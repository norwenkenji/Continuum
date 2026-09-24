using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Continuum.Core.Abstractions;

namespace Continuum.Infrastructure.Privacy;

/// <summary>
/// Маскирует секреты в командных строках и URL до записи в БД.
/// Спецификация: docs/specs/privacy-pipeline.md, §5.
/// Маска всегда ровно «***» — длина секрета в выходе не раскрывается.
/// Остальные виды Observable возвращаются без изменений (§5.3).
/// </summary>
public sealed class SecretSanitizer : ISanitizer
{
    /// <summary>Маска фиксированной длины: по ней нельзя восстановить длину секрета.</summary>
    public const string Mask = "***";

    // Страховка от патологических входных строк: регекс не имеет права виснуть
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // §5.1: пары «параметр=значение» (--password=X, AWS_SECRET_ACCESS_KEY=X).
    // Имя параметра сохраняется, значение заменяется маской.
    // Lookahead проверяет наличие ключевого слова в любом месте имени —
    // в том числе в самом начале («password=...»).
    private static readonly Regex SensitiveAssignment = new(
        @"(?<name>(?=[A-Za-z0-9_-]*(?:password|passwd|pwd|pass|secret|token|api[-_]?key|access[-_]?key|auth|credentials?))[A-Za-z_][A-Za-z0-9_-]*)\s*=\s*(?<value>""[^""]*""|'[^']*'|[^\s]+)",
        RegexOptions.IgnoreCase,
        RegexTimeout);

    // §5.1: пары «флаг значение» (-p X, --token X)
    private static readonly Regex SensitiveFlag = new(
        @"(?<flag>--?(?:password|passwd|pwd|pass|token|secret|api[-_]?key|access[-_]?key|auth|credentials?|key|p))\s+(?<value>""[^""]*""|'[^']*'|[^\s]+)",
        RegexOptions.IgnoreCase,
        RegexTimeout);

    // §5.1: PEM-блок private key целиком
    private static readonly Regex PemBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.None,
        RegexTimeout);

    // §5.1: токены по форме. Список — данные, а не хардкод: пользовательские
    // паттерны добавляются через конструктор, без правки кода.
    private static readonly Regex[] DefaultTokenPatterns =
    [
        new(@"ghp_[A-Za-z0-9]{36}", RegexOptions.IgnoreCase, RegexTimeout),  // GitHub PAT
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.IgnoreCase, RegexTimeout),  // API-ключ по форме
        new(@"xox[baprs]-[A-Za-z0-9-]+", RegexOptions.None, RegexTimeout),   // Slack-токен
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.None, RegexTimeout),           // AWS access key
    ];

    // §5.2: query-параметры, чьи значения маскируются (имя параметра сохраняется)
    private static readonly HashSet<string> SensitiveQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "key", "sig", "auth", "code", "password", "secret",
    };

    private static readonly char[] AuthorityTerminators = ['/', '?'];

    private readonly IReadOnlyList<Regex> _tokenPatterns;

    public SecretSanitizer(IEnumerable<string>? extraTokenPatterns = null)
    {
        var patterns = new List<Regex>(DefaultTokenPatterns);
        if (extraTokenPatterns is not null)
        {
            foreach (var pattern in extraTokenPatterns)
            {
                patterns.Add(new Regex(pattern, RegexOptions.None, RegexTimeout));
            }
        }

        _tokenPatterns = patterns;
    }

    public string Sanitize(string value, ObservableKind kind)
    {
        ArgumentNullException.ThrowIfNull(value);

        return kind switch
        {
            ObservableKind.CommandLine => SanitizeCommandLine(value),
            ObservableKind.Url => SanitizeUrl(value),
            // §5.3: пути и заголовки не маскируются — они и есть ценность контекста
            _ => value,
        };
    }

    private string SanitizeCommandLine(string value)
    {
        var result = value;

        // Порядок важен: сначала PEM-блок целиком, затем пары параметр/значение,
        // в конце — голые токены по форме (часть из них уже накрыта флагами)
        result = PemBlock.Replace(result, Mask);
        result = SensitiveAssignment.Replace(result, "${name}=" + Mask);
        result = SensitiveFlag.Replace(result, "${flag} " + Mask);
        foreach (var pattern in _tokenPatterns)
        {
            result = pattern.Replace(result, Mask);
        }

        return result;
    }

    private static string SanitizeUrl(string value)
    {
        var result = value;

        // Fragment удаляется целиком: SPA-роуты могут нести данные сессии
        var hashIndex = result.IndexOf('#');
        if (hashIndex >= 0)
        {
            result = result[..hashIndex];
        }

        result = RemoveUserInfo(result);
        result = MaskQueryParameters(result);
        return result;
    }

    private static string RemoveUserInfo(string url)
    {
        // user:password@host → host; '@' вне authority (в пути или query) не трогаем
        var schemeIndex = url.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeIndex >= 0 ? schemeIndex + 3 : 0;
        var atIndex = url.IndexOf('@', authorityStart);
        if (atIndex < 0)
        {
            return url;
        }

        var authorityEnd = url.IndexOfAny(AuthorityTerminators, authorityStart);
        if (authorityEnd >= 0 && atIndex > authorityEnd)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, authorityStart), url.AsSpan(atIndex + 1));
    }

    private static string MaskQueryParameters(string url)
    {
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= url.Length - 1)
        {
            return url;
        }

        var parts = url[(queryIndex + 1)..].Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var equalsIndex = parts[i].IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue; // параметр без значения — не секрет
            }

            var name = parts[i][..equalsIndex];
            if (SensitiveQueryParameters.Contains(name))
            {
                parts[i] = name + "=" + Mask;
            }
        }

        return string.Concat(url.AsSpan(0, queryIndex + 1), string.Join('&', parts));
    }
}
