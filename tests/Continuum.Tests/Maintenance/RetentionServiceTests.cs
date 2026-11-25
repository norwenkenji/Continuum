using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Database;
using Continuum.Infrastructure.Privacy;
using Continuum.Maintenance;
using Continuum.Settings;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Maintenance;

/// <summary>
/// Срок хранения (ФТ-7.2): старые сессии удаляются вместе с дочерними
/// данными, свежие остаются, «0 дней» - бессрочное хранение.
/// </summary>
public class RetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static async Task<(FakeRepository Repository, SettingsService Settings, RetentionService Service)> CreateAsync(int retentionDays)
    {
        var repository = new FakeRepository();
        var exclusions = new ExclusionSet();
        var settings = new SettingsService(repository, exclusions, NullLogger<SettingsService>.Instance);
        await settings.SaveAsync(new AppSettings([], [], [], retentionDays, 15));
        var service = new RetentionService(
            repository, settings, new FixedClock(Now), NullLogger<RetentionService>.Instance);
        return (repository, settings, service);
    }

    private static async Task<long> AddSessionAsync(FakeRepository repository, DateTimeOffset endedAt, string filePath)
    {
        var sessionId = await repository.CreateSessionAsync(
            new Session(0, endedAt.AddHours(-2), null, SessionStatus.Running, null, 900));
        await repository.EndSessionAsync(sessionId, endedAt, SessionEndReason.User);
        var projectId = await repository.UpsertProjectAsync(
            new Project(0, "proj", "c:/work/proj", null, endedAt, endedAt));
        await repository.AppendEventAsync(new Event(0, sessionId, endedAt, EventKind.FileChanged, null, projectId, null, filePath, null, null));
        await repository.AppendFileActivityAsync(new FileActivity(0, sessionId, endedAt, projectId, filePath, FileChangeKind.Modified, FileActivitySource.Watcher));
        var snapshotId = await repository.SaveSnapshotAsync(new Snapshot(
            0, sessionId, endedAt, SnapshotReason.SessionEnd, 1, "{}")
        {
            OpenFiles = [new OpenFile(0, 0, filePath, projectId, null, 10, endedAt, "vscode_state")],
        });
        await repository.CreateRestorePlanAsync(new RestorePlan(
            0, snapshotId, endedAt, false, RestorePlanStatus.Success, endedAt, endedAt)
        {
            Steps = [new RestoreStep(0, 0, 1, RestoreStrategyKind.OpenFile, filePath, null, RestoreStepResult.Success, null, 5)],
        });
        return sessionId;
    }

    [Fact]
    public async Task Old_sessions_are_deleted_with_children_and_fresh_ones_keep()
    {
        var (repository, _, service) = await CreateAsync(retentionDays: 7);
        var oldId = await AddSessionAsync(repository, Now.AddDays(-10), "c:/work/proj/old.cs");
        var freshId = await AddSessionAsync(repository, Now.AddDays(-1), "c:/work/proj/new.cs");

        var deleted = await service.RunOnceAsync();

        Assert.Equal(1, deleted);
        Assert.DoesNotContain(repository.Sessions, s => s.Id == oldId);
        Assert.Contains(repository.Sessions, s => s.Id == freshId);
        Assert.DoesNotContain(repository.Events, e => e.SessionId == oldId);
        Assert.DoesNotContain(repository.FileActivities, f => f.SessionId == oldId);
        Assert.DoesNotContain(repository.Snapshots, s => s.SessionId == oldId);
        Assert.DoesNotContain(repository.RestorePlans, p => p.SnapshotId == 0);
        Assert.Single(repository.RestorePlans); // план свежей сессии на месте
        Assert.Contains(repository.Events, e => e.SessionId == freshId);
    }

    [Fact]
    public async Task Zero_retention_means_keep_forever()
    {
        var (repository, _, service) = await CreateAsync(retentionDays: 0);
        await AddSessionAsync(repository, Now.AddDays(-3650), "c:/work/proj/ancient.cs");

        var deleted = await service.RunOnceAsync();

        Assert.Equal(0, deleted);
        Assert.Single(repository.Sessions);
    }

    [Fact]
    public async Task New_retention_setting_applies_immediately()
    {
        var (repository, settings, service) = await CreateAsync(retentionDays: 0);
        await AddSessionAsync(repository, Now.AddDays(-10), "c:/work/proj/old.cs");

        // Пользователь выставил срок хранения в окне настроек - без перезапуска
        await settings.SaveAsync(new AppSettings([], [], [], 7, 15));
        await Task.Delay(150); // фоновая реакция на изменение настроек

        Assert.Empty(repository.Sessions);
        Assert.Equal(0, await service.RunOnceAsync()); // повтор удалять нечего
    }

    [Fact]
    public async Task Open_sessions_are_never_deleted()
    {
        var (repository, _, service) = await CreateAsync(retentionDays: 1);
        await repository.CreateSessionAsync(new Session(
            0, Now.AddDays(-10), null, SessionStatus.Running, null, 900));

        var deleted = await service.RunOnceAsync();

        Assert.Equal(0, deleted);
        Assert.Single(repository.Sessions);
    }
}
