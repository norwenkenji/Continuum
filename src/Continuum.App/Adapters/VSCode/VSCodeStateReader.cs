using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Continuum.Adapters.VSCode;

/// <summary>Файл из User\History: путь отслеживаемого файла и время последнего сохранения (null - entries пуст).</summary>
internal sealed record HistoryEntry(string Path, DateTimeOffset? LastSave);

/// <summary>Открытый файл с позицией курсора из workbench.state.</summary>
internal sealed record WorkbenchLine(string Uri, int Line);

/// <summary>
/// Чтение локальных хранилищ VS Code (data-sources §7): User\History\*\entries.json,
/// workspaceStorage\state.vscdb (workbench.state), globalStorage\state.vscdb
/// (history.recentlyOpenedPathsList). Только чтение: state.vscdb открывается в
/// Mode=ReadOnly (VS Code держит файл открытым), при отказе - копия в %TEMP%;
/// не вышло и это - тихий пропуск. Битые файлы пропускаются, исключения наружу
/// не выходят: класс источника B, недоступность - норма, не ошибка.
/// </summary>
internal static class VSCodeStateReader
{
    private const string WorkbenchStateKey = "workbench.state";
    private const string RecentlyOpenedKey = "history.recentlyOpenedPathsList";

    /// <summary>JSON-островки в блобе workbench.state: "resource":"..." и "selectionStartLineNumber":N.</summary>
    private static readonly Regex WorkbenchTokenRegex = new(
        "\"resource\"\\s*:\\s*\"(?<resource>[^\"]+)\"|\"selectionStartLineNumber\"\\s*:\\s*(?<line>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string TempCopyFolder = Path.Combine(Path.GetTempPath(), "continuum-vscode");

    /// <summary>
    /// Файлы из History корня: перечисляет User\History\*\entries.json, для каждого -
    /// путь (resource) и max mtime файлов-снапшотов из entries. Битые записи
    /// пропускаются; несуществующий корень - пустой список.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> ReadHistory(string root)
    {
        var result = new List<HistoryEntry>();
        var historyDir = Path.Combine(root, "User", "History");
        if (!Directory.Exists(historyDir))
        {
            return result;
        }

        foreach (var hashDir in EnumerateDirectoriesSafe(historyDir))
        {
            var entriesPath = Path.Combine(hashDir, "entries.json");
            if (!File.Exists(entriesPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(entriesPath));
                if (!document.RootElement.TryGetProperty("resource", out var resourceElement))
                {
                    continue;
                }

                var path = VSCodePathUtil.TryUriToLocalPath(resourceElement.GetString());
                if (path is null)
                {
                    continue;
                }

                result.Add(new HistoryEntry(path, ReadLastSave(hashDir, document.RootElement)));
            }
            catch
            {
                // Битый entries.json (не JSON, нет resource) - молча пропустить (спека §7.2)
            }
        }

        return result;
    }

    /// <summary>
    /// Пары (resource-URI, строка курсора) из workbench.state хранилища workspace.
    /// Блоб читается как UTF-8 текст и разбирается регексами best-effort:
    /// последняя selectionStartLineNumber перед следующим resource относится
    /// к этому resource. Не распарсилось - пустой список, это не ошибка.
    /// </summary>
    public static IReadOnlyList<WorkbenchLine> ReadWorkbenchState(string dbPath)
    {
        var text = TryReadItemTableValue(dbPath, WorkbenchStateKey);
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<WorkbenchLine>();
        }

        var linesByUri = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentUri = null;
        int? currentLine = null;
        foreach (Match match in WorkbenchTokenRegex.Matches(text))
        {
            if (match.Groups["resource"].Success)
            {
                Flush();
                currentUri = match.Groups["resource"].Value;
                currentLine = null;
            }
            else if (match.Groups["line"].Success && currentUri is not null)
            {
                currentLine = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            }
        }

        Flush();
        return linesByUri.Select(pair => new WorkbenchLine(pair.Key, pair.Value)).ToList();

        void Flush()
        {
            if (currentUri is not null && currentLine is not null)
            {
                linesByUri[currentUri] = currentLine.Value;
            }
        }
    }

    /// <summary>fileUri из history.recentlyOpenedPathsList (недавние файлы; folderUri пропускаются).</summary>
    public static IReadOnlyList<string> ReadRecentlyOpenedFileUris(string dbPath)
    {
        var result = new List<string>();
        var text = TryReadItemTableValue(dbPath, RecentlyOpenedKey);
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("entries", out var entriesElement)
                || entriesElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entry in entriesElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("fileUri", out var fileUriElement))
                {
                    var fileUri = fileUriElement.GetString();
                    if (!string.IsNullOrEmpty(fileUri))
                    {
                        result.Add(fileUri);
                    }
                }
            }
        }
        catch
        {
            // Битый JSON - нет недавних файлов, это не ошибка
        }

        return result;
    }

    /// <summary>
    /// Значение ключа из ItemTable state.vscdb (data-sources §7.2): сначала
    /// прямой ReadOnly-доступ; если VS Code держит файл и прямой доступ не
    /// удался - файл копируется в %TEMP% и читается копия. Совсем не вышло -
    /// null (тихий пропуск, исключения наружу не выходят).
    /// </summary>
    private static string? TryReadItemTableValue(string dbPath, string key)
    {
        if (!File.Exists(dbPath))
        {
            return null;
        }

        if (TryReadValue(dbPath, key, out var direct))
        {
            return direct;
        }

        var copy = TryCopyToTemp(dbPath);
        if (copy is null)
        {
            return null;
        }

        try
        {
            return TryReadValue(copy, key, out var fromCopy) ? fromCopy : null;
        }
        finally
        {
            try
            {
                File.Delete(copy);
            }
            catch
            {
                // Неудалённая копия - мусор в %TEMP%, не ошибка
            }
        }
    }

    /// <summary>
    /// Читает значение ключа. false - файл не открылся (занят VS Code или
    /// таблицы нет): нужен путь через копию. true - открылся, даже если ключа нет.
    /// </summary>
    private static bool TryReadValue(string dbPath, string key, out string? value)
    {
        value = null;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, // файл не удерживается после закрытия - копию в %TEMP% можно удалить
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = @key";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@key";
            parameter.Value = key;
            command.Parameters.Add(parameter);

            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return true; // файл открыт, ключа нет - это не отказ доступа, копия не нужна
            }

            value = reader.GetValue(0) switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryCopyToTemp(string dbPath)
    {
        try
        {
            Directory.CreateDirectory(TempCopyFolder);
            var copyPath = Path.Combine(TempCopyFolder, Guid.NewGuid().ToString("N") + ".vscdb");
            File.Copy(dbPath, copyPath, true);
            return copyPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Max mtime файлов-снапшотов из entries (лежат в той же папке, что и entries.json).</summary>
    private static DateTimeOffset? ReadLastSave(string hashDir, JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("entries", out var entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        DateTimeOffset? lastSave = null;
        foreach (var entry in entriesElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = idElement.GetString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var snapshotPath = Path.Combine(hashDir, id);
            if (!File.Exists(snapshotPath))
            {
                continue;
            }

            var mtime = new DateTimeOffset(File.GetLastWriteTime(snapshotPath));
            if (lastSave is null || mtime > lastSave)
            {
                lastSave = mtime;
            }
        }

        return lastSave;
    }

    /// <summary>Перечисление подкаталогов без исключений (каталог может исчезнуть между проверкой и чтением).</summary>
    internal static IReadOnlyList<string> EnumerateDirectoriesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
