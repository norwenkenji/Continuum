using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Adapters.VSCode;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Time;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Adapters;

/// <summary>
/// VSCodeAdapter на фикстурах в %TEMP% (реальный VS Code на машине не нужен):
/// фейковые корни с User\History (entries.json + файлы-снапшоты с mtime),
/// User\workspaceStorage (workspace.json + state.vscdb с блобом workbench.state)
/// и User\globalStorage (state.vscdb с history.recentlyOpenedPathsList).
/// state.vscdb создаётся через Microsoft.Data.Sqlite как его пишет VS Code
/// (ItemTable: key TEXT, value BLOB). Ожидания - WaitFor-циклом с таймаутом,
/// по образцу RecentFilesMonitorTests.
/// </summary>
public class VSCodeAdapterTests : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "continuum-vscode-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Observation> _events = new();

    public VSCodeAdapterTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void First_poll_is_baseline_and_silent()
    {
        var root = CreateRoot();
        AddHistory(root, "file:///c%3A/proj/readme.md", DateTime.Now.AddMinutes(-5), "AAA111.md");
        using var adapter = StartWithEvents(root);

        Thread.Sleep(500); // несколько тиков: файл уже был в базе

        lock (_events)
        {
            Assert.Empty(_events);
        }
    }

    [Fact]
    public void New_save_after_baseline_emits_single_modified_event()
    {
        var root = CreateRoot();
        var snapshot = AddHistory(root, "file:///c%3A/proj/readme.md", DateTime.Now.AddMinutes(-10), "AAA111.md");
        var expectedPath = @"c:\proj\readme.md";
        using var adapter = StartWithEvents(root);

        Thread.Sleep(700); // базовый тик гарантированно прошёл
        File.SetLastWriteTime(snapshot, DateTime.Now.AddMinutes(1)); // новое сохранение

        Assert.True(WaitFor(() => CountFor(expectedPath) >= 1), "событие сохранения не пришло за 15 секунд");
        Thread.Sleep(300); // ещё несколько тиков: повторного эмита быть не должно
        Assert.Equal(1, CountFor(expectedPath));

        lock (_events)
        {
            var observation = _events.Single(o => o.Observable.Value == expectedPath);
            Assert.Equal(EventKind.FileChanged, observation.EventKind);
            Assert.Equal(FileChangeKind.Modified, observation.FileChange);
            Assert.Equal(FileActivitySource.VsCodeHistory, observation.FileSource);
            Assert.Equal(expectedPath, observation.Observable.Value);
            Assert.Null(observation.Observable.ProjectRootPath);
            Assert.Null(observation.Observable.ProcessId);
            Assert.Null(observation.Observable.ApplicationName);
            Assert.Null(observation.Observable.ApplicationExePath);
        }
    }

    [Fact]
    public void Cyrillic_resource_uri_decodes_to_russian_path()
    {
        var root = CreateRoot();
        var snapshot = AddHistory(
            root, "file:///c%3A/Users/u/%D0%BE%D1%82%D1%87%D1%91%D1%82.md", DateTime.Now.AddMinutes(-10), "AAA111.md");
        var expectedPath = @"c:\Users\u\отчёт.md";
        using var adapter = StartWithEvents(root);

        Thread.Sleep(700);
        File.SetLastWriteTime(snapshot, DateTime.Now.AddMinutes(1));

        Assert.True(WaitFor(() => CountFor(expectedPath) >= 1), "событие по кириллическому пути не пришло");
        lock (_events)
        {
            Assert.Equal(expectedPath, _events.Single().Observable.Value);
        }
    }

    [Fact]
    public void No_changes_after_baseline_stay_silent()
    {
        var root = CreateRoot();
        AddHistory(root, "file:///c%3A/proj/readme.md", DateTime.Now.AddMinutes(-5), "AAA111.md");
        using var adapter = StartWithEvents(root);

        Thread.Sleep(900); // база + ещё несколько тиков без изменений

        lock (_events)
        {
            Assert.Empty(_events);
        }
    }

    [Fact]
    public async Task GetOpenFiles_returns_line_path_source_and_last_modified()
    {
        var root = CreateRoot();
        var savedAt = DateTime.Now.AddHours(-1);
        AddHistory(root, "file:///c%3A/proj/Program.cs", savedAt, "CCC333.cs");
        AddWorkspace(root, "file:///c%3A/proj",
            "editors:[{\"resource\":\"file:///c%3A/proj/Program.cs\",\"assoc\":null,\"selection\":{\"selectionStartLineNumber\":42,\"selectionStartColumn\":3}}]");
        using var adapter = CreateAdapter(root);

        var files = await adapter.GetOpenFilesAsync();

        var file = Assert.Single(files);
        Assert.Equal(@"c:\proj\Program.cs", file.Path);
        Assert.Equal(42, file.Line);
        Assert.Equal("vscode_state", file.Source);
        Assert.NotNull(file.LastModified);
        Assert.Equal(savedAt, file.LastModified!.Value.LocalDateTime);
        Assert.Null(file.ProjectId);
        Assert.Null(file.ApplicationId);
        Assert.Equal(0, file.Id);
        Assert.Equal(0, file.SnapshotId);
    }

    [Fact]
    public async Task GetOpenFiles_falls_back_to_recently_opened_files()
    {
        var root = CreateRoot();
        // workspaceStorage отсутствует - только недавние пути в globalStorage;
        // folderUri (папка) в open_files не попадает, только fileUri
        AddGlobalRecent(
            root,
            new[] { "file:///c%3A/proj/readme.md" },
            new[] { "file:///c%3A/proj" });
        using var adapter = CreateAdapter(root);

        var files = await adapter.GetOpenFilesAsync();

        var file = Assert.Single(files);
        Assert.Equal(@"c:\proj\readme.md", file.Path);
        Assert.Null(file.Line);
        Assert.Equal("vscode_state", file.Source);
        Assert.Null(file.LastModified);
    }

    [Fact]
    public async Task Missing_root_returns_empty_without_throwing()
    {
        var missing = Path.Combine(_tempRoot, "no-such-vscode");
        using var adapter = StartWithEvents(missing);

        var files = await adapter.GetOpenFilesAsync();
        Assert.Empty(files);

        Thread.Sleep(300); // цикл по несуществующему корню не падает и не эмитит
        lock (_events)
        {
            Assert.Empty(_events);
        }
    }

    [Fact]
    public void Broken_entries_json_is_skipped()
    {
        var root = CreateRoot();
        var snapshot = AddHistory(root, "file:///c%3A/proj/good.md", DateTime.Now.AddMinutes(-10), "AAA111.md");

        // Битый entries.json: не JSON - молча пропускается
        var brokenDir = Path.Combine(root, "User", "History", "h" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "entries.json"), "это не json");

        using var adapter = StartWithEvents(root);

        Thread.Sleep(700);
        File.SetLastWriteTime(snapshot, DateTime.Now.AddMinutes(1));

        Assert.True(WaitFor(() => CountFor(@"c:\proj\good.md") >= 1), "событие по валидному файлу не пришло");
        Thread.Sleep(300);
        lock (_events)
        {
            Assert.Single(_events); // битая запись не дала ни события, ни исключения
        }
    }

    [Fact]
    public async Task Opened_state_vscdb_is_readable_via_readonly_mode()
    {
        var root = CreateRoot();
        var stateDb = AddWorkspace(root, "file:///c%3A/proj",
            "editors:[{\"resource\":\"file:///c%3A/proj/Program.cs\",\"selection\":{\"selectionStartLineNumber\":42}}]");
        using var adapter = CreateAdapter(root);

        // Занятый файл в стиле VS Code: открыт на чтение-запись, чужие чтения
        // разрешены. Прямой Mode=ReadOnly с таким локом справляется без копии
        // (проверено экспериментально на Windows: e_sqlite3 открывает файл
        // при чужом ReadWrite-хендле с FileShare.Read)
        using (var hold = new FileStream(stateDb, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var files = await adapter.GetOpenFilesAsync();

            var file = Assert.Single(files);
            Assert.Equal(@"c:\proj\Program.cs", file.Path);
            Assert.Equal(42, file.Line);
        }
    }

    [Fact]
    public async Task Exclusively_locked_state_vscdb_returns_empty_without_throwing()
    {
        var root = CreateRoot();
        var workspaceDb = AddWorkspace(root, "file:///c%3A/proj",
            "editors:[{\"resource\":\"file:///c%3A/proj/Program.cs\",\"selection\":{\"selectionStartLineNumber\":42}}]");
        var globalDb = AddGlobalRecent(root, new[] { "file:///c%3A/proj/readme.md" });
        using var adapter = CreateAdapter(root);

        // Эксклюзивный лок: не открывается ни ReadOnly-режим, ни копия в %TEMP%
        // (FileShare.None запрещает чужие чтения целиком) - тихий пустой список
        using var workspaceHold = new FileStream(workspaceDb, FileMode.Open, FileAccess.Read, FileShare.None);
        using var globalHold = new FileStream(globalDb, FileMode.Open, FileAccess.Read, FileShare.None);

        var files = await adapter.GetOpenFilesAsync();
        Assert.Empty(files);
    }

    [Fact]
    public async Task Two_roots_are_both_read()
    {
        var rootA = CreateRoot("Code");
        var rootB = CreateRoot("Cursor");
        var snapshotA = AddHistory(rootA, "file:///c%3A/projA/a.cs", DateTime.Now.AddMinutes(-10), "AAA111.cs");
        var snapshotB = AddHistory(rootB, "file:///c%3A/projB/b.cs", DateTime.Now.AddMinutes(-10), "BBB222.cs");
        AddWorkspace(rootA, "file:///c%3A/projA",
            "editors:[{\"resource\":\"file:///c%3A/projA/x.cs\",\"selection\":{\"selectionStartLineNumber\":5}}]");
        AddWorkspace(rootB, "file:///c%3A/projB",
            "editors:[{\"resource\":\"file:///c%3A/projB/y.cs\",\"selection\":{\"selectionStartLineNumber\":7}}]");
        using var adapter = StartWithEvents(rootA, rootB);

        Thread.Sleep(700);
        File.SetLastWriteTime(snapshotA, DateTime.Now.AddMinutes(1));
        File.SetLastWriteTime(snapshotB, DateTime.Now.AddMinutes(1));

        Assert.True(WaitFor(() => CountFor(@"c:\projA\a.cs") >= 1), "событие из корня A не пришло");
        Assert.True(WaitFor(() => CountFor(@"c:\projB\b.cs") >= 1), "событие из корня B не пришло");
        Thread.Sleep(300);
        lock (_events)
        {
            Assert.Equal(2, _events.Count);
        }

        var files = await adapter.GetOpenFilesAsync();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Path == @"c:\projA\x.cs" && f.Line == 5);
        Assert.Contains(files, f => f.Path == @"c:\projB\y.cs" && f.Line == 7);
    }

    /// <summary>Создаёт фейковый корень VS Code (с пустым User\History) и возвращает его путь.</summary>
    private string CreateRoot(string name = "Code")
    {
        var root = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(root, "User", "History"));
        return root;
    }

    /// <summary>
    /// Добавляет отслеживаемый файл в History: entries.json (resource + ids) и
    /// файлы-снапшоты с заданным mtime. Возвращает путь первого снапшота - его
    /// mtime и поднимается в тестах как «новое сохранение».
    /// </summary>
    private string AddHistory(string root, string resourceUri, DateTime lastSave, params string[] snapshotIds)
    {
        var ids = snapshotIds.Length > 0 ? snapshotIds : new[] { "AAA111.md" };
        var dir = Path.Combine(root, "User", "History", "h" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "entries.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            resource = resourceUri,
            entries = ids.Select(id => new { id, source = "bounded" }).ToArray(),
        }));
        foreach (var id in ids)
        {
            var snapshotPath = Path.Combine(dir, id);
            File.WriteAllText(snapshotPath, "снимок");
            File.SetLastWriteTime(snapshotPath, lastSave);
        }

        return Path.Combine(dir, ids[0]);
    }

    /// <summary>Добавляет workspace-хранилище: workspace.json + state.vscdb с блобом workbench.state. Возвращает путь к базе.</summary>
    private string AddWorkspace(string root, string folderUri, string workbenchState)
    {
        var dir = Path.Combine(root, "User", "workspaceStorage", "w" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.json"), JsonSerializer.Serialize(new { folder = folderUri }));
        var dbPath = Path.Combine(dir, "state.vscdb");
        CreateStateDb(dbPath, "workbench.state", workbenchState);
        return dbPath;
    }

    /// <summary>Добавляет globalStorage\state.vscdb с history.recentlyOpenedPathsList. Возвращает путь к базе.</summary>
    private string AddGlobalRecent(string root, string[] fileUris, string[]? folderUris = null)
    {
        var entries = new List<object>();
        foreach (var folder in folderUris ?? Array.Empty<string>())
        {
            entries.Add(new { folderUri = folder });
        }

        foreach (var file in fileUris)
        {
            entries.Add(new { fileUri = file });
        }

        var dbPath = Path.Combine(root, "User", "globalStorage", "state.vscdb");
        CreateStateDb(dbPath, "history.recentlyOpenedPathsList", JsonSerializer.Serialize(new { entries }));
        return dbPath;
    }

    /// <summary>Создаёт state.vscdb в формате VS Code: ItemTable(key TEXT PRIMARY KEY, value BLOB).</summary>
    private static void CreateStateDb(string dbPath, string key, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // файл не остаётся в пуле - фикстуру можно удалить
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var table = connection.CreateCommand())
        {
            table.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value BLOB)";
            table.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO ItemTable (key, value) VALUES (@key, @value)";
        var keyParameter = insert.CreateParameter();
        keyParameter.ParameterName = "@key";
        keyParameter.Value = key;
        var valueParameter = insert.CreateParameter();
        valueParameter.ParameterName = "@value";
        valueParameter.Value = Encoding.UTF8.GetBytes(value);
        insert.Parameters.Add(keyParameter);
        insert.Parameters.Add(valueParameter);
        insert.ExecuteNonQuery();
    }

    private VSCodeAdapter CreateAdapter(params string[] roots) =>
        new(new SystemClock(), NullLogger<VSCodeAdapter>.Instance, roots, resolver: null, pollInterval: PollInterval);

    private VSCodeAdapter StartWithEvents(params string[] roots)
    {
        var adapter = CreateAdapter(roots);
        adapter.Observed += (_, observation) =>
        {
            lock (_events)
            {
                _events.Add(observation);
            }
        };
        adapter.Start();
        return adapter;
    }

    private int CountFor(string path)
    {
        lock (_events)
        {
            return _events.Count(o => o.Observable.Value == path);
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    public void Dispose()
    {
        // Пул SQLite держит дескрипторы даже после Dispose соединений -
        // без очистки каталог фикстур не удалится
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        // Служебная папка адаптера для копий занятых state.vscdb
        try
        {
            var tempCopies = Path.Combine(Path.GetTempPath(), "continuum-vscode");
            if (Directory.Exists(tempCopies))
            {
                Directory.Delete(tempCopies, recursive: true);
            }
        }
        catch
        {
            // Параллельный тест мог держать копию - не страшно
        }
    }
}
