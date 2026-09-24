using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Continuum.Infrastructure.Database;

/// <summary>
/// Загружает миграции из встроенных ресурсов сборки.
/// Соглашение об именовании файлов: V{номер}__{имя}.sql, например V001__init.sql.
/// Файлы лежат в Infrastructure/Database/Migrations и встраиваются в сборку,
/// поэтому работают и после folder-publish на чистой машине.
/// </summary>
public static partial class EmbeddedMigrationLoader
{
    public const string ResourcePrefix = "Continuum.Infrastructure.Database.Migrations.";

    public static IReadOnlyList<Migration> Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var result = new List<Migration>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            if (!TryParseFileName(fileName, out var version, out var name))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Ресурс миграции недоступен: {resourceName}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result.Add(new Migration(version, name, reader.ReadToEnd()));
        }

        return result.OrderBy(m => m.Version).ToArray();
    }

    /// <summary>Разбирает имя файла миграции: V001__init.sql → (1, "init").</summary>
    public static bool TryParseFileName(string fileName, out int version, out string name)
    {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            version = 0;
            name = string.Empty;
            return false;
        }

        version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        name = match.Groups[2].Value;
        return true;
    }

    [GeneratedRegex(@"^V(\d+)__(.+)\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();
}
