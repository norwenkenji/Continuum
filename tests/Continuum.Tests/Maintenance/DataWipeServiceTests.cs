using System;
using System.IO;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Infrastructure.Database;
using Continuum.Maintenance;
using Continuum.Settings;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Maintenance;

/// <summary>
/// Полное удаление данных (ФТ-7.2): файл базы и служебные -wal/-shm
/// стираются, схема создаётся заново, настройки возвращаются к умолчаниям,
/// записи предыдущих сессий не остаётся.
/// </summary>
public sealed class DataWipeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "continuum-wipe-tests", Guid.NewGuid().ToString("N"));

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeRepository _unused = new();

    public DataWipeServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "continuum.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        new DatabaseInitializer(_factory).Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // уборка - best effort
        }
    }

    private SettingsService CreateSettings(FakeRepository repository)
        => new(repository, new Infrastructure.Privacy.ExclusionSet(), NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task Wipe_deletes_database_files_and_recreates_schema()
    {
        var repository = new SqliteRepository(_factory, new Core.Time.SystemClock());
        var sessionId = await repository.CreateSessionAsync(
            new Core.Domain.Session(0, DateTimeOffset.UtcNow, null, Core.Domain.SessionStatus.Running, null, 900));
        await repository.AppendEventAsync(new Core.Domain.Event(
            0, sessionId, DateTimeOffset.UtcNow, Core.Domain.EventKind.SessionStart, null, null, null, null, null, null));

        var settings = CreateSettings(_unused);
        var wipe = new DataWipeService(_factory, new DatabaseInitializer(_factory), settings, NullLogger<DataWipeService>.Instance);

        var deletedFiles = await wipe.WipeAsync();

        Assert.NotEmpty(deletedFiles);
        Assert.True(File.Exists(_dbPath), "база должна быть создана заново");
        Assert.False(await repository.GetLastSessionAsync() is { } leftover && leftover.Id == sessionId);
        Assert.Null(await repository.GetLastSessionAsync());

        // Схема создана заново: миграции на месте, писать можно
        var newSessionId = await repository.CreateSessionAsync(
            new Core.Domain.Session(0, DateTimeOffset.UtcNow, null, Core.Domain.SessionStatus.Running, null, 900));
        Assert.True(newSessionId > 0);
    }

    [Fact]
    public async Task Wipe_resets_settings_to_defaults()
    {
        var repository = new SqliteRepository(_factory, new Core.Time.SystemClock());
        // настройки живут в той же базе: после вайпа читаются умолчания
        var settings = new SettingsService(
            repository, new Infrastructure.Privacy.ExclusionSet(), NullLogger<SettingsService>.Instance);
        await settings.SaveAsync(new Core.Domain.AppSettings(["secretapp"], [], [], 30, 5));

        var wipe = new DataWipeService(_factory, new DatabaseInitializer(_factory), settings, NullLogger<DataWipeService>.Instance);
        await wipe.WipeAsync();

        Assert.Empty(settings.Settings.ExcludedApplications);
        Assert.Equal(0, settings.Settings.RetentionDays);
    }
}
