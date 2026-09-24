using System;
using System.IO;
using Continuum.Core.Abstractions;

namespace Continuum.Infrastructure.Privacy;

/// <summary>
/// Решает, разрешено ли наблюдение. false — отбросить целиком,
/// не записывать ничего (ни события, ни поля в снапшоте).
/// Порядок правил: docs/specs/privacy-pipeline.md, §2; секретные
/// файловые артефакты — §5.3.
/// </summary>
public sealed class PrivacyFilter : IPrivacyFilter
{
    private readonly IExclusionSet _exclusions;

    public PrivacyFilter(IExclusionSet exclusions)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        _exclusions = exclusions;
    }

    public bool Allow(Observable o)
    {
        ArgumentNullException.ThrowIfNull(o);

        // 1. Исключённое приложение — отбрасываются все виды наблюдений (§4.1)
        if (!string.IsNullOrEmpty(o.ApplicationName) || !string.IsNullOrEmpty(o.ApplicationExePath))
        {
            if (_exclusions.IsApplicationExcluded(o.ApplicationName ?? string.Empty, o.ApplicationExePath))
            {
                return false;
            }
        }

        // 2. Исключённая директория — только для видов, где Value является путём (§4.2)
        if (o.Kind is ObservableKind.FilePath or ObservableKind.Cwd
            && !string.IsNullOrEmpty(o.Value)
            && _exclusions.IsDirectoryExcluded(o.Value))
        {
            return false;
        }

        // 3. Правила «не записывать никогда» (§4.3)
        if (_exclusions.IsNeverRecorded(o))
        {
            return false;
        }

        // 4. Секретный файловый артефакт (§5.3) — отбрасывается событие целиком
        if (o.Kind is ObservableKind.FilePath && IsSecretArtifact(o.Value))
        {
            return false;
        }

        return true;
    }

    // §5.3: точечные имена секретных артефактов
    private static readonly string[] SecretFileNames =
    [
        ".env", ".npmrc", ".netrc", "credentials.json", "id_rsa", "id_ed25519",
    ];

    // §5.3: расширения секретных артефактов
    private static readonly string[] SecretFileExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".kdbx",
    ];

    private static bool IsSecretArtifact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        foreach (var name in SecretFileNames)
        {
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var extension in SecretFileExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
