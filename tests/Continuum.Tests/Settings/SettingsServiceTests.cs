using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Privacy;
using Continuum.Settings;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Settings;

/// <summary>
/// Настройки (шаг 9): чтение и запись, умолчания при отсутствии и битых
/// значениях, ограничение диапазонов и главное требование ФТ-2.3 -
/// исключение из настроек действует сразу, без перезапуска.
/// </summary>
public class SettingsServiceTests
{
    private readonly FakeRepository _repository = new();
    private readonly ExclusionSet _exclusions = new();

    private SettingsService CreateService() => new(
        _repository, _exclusions, NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task Empty_database_gives_defaults()
    {
        var service = CreateService();

        var settings = await service.LoadAsync();

        Assert.Empty(settings.ExcludedApplications);
        Assert.Empty(settings.ExcludedDirectories);
        Assert.Empty(settings.NeverRecordRules);
        Assert.Equal(0, settings.RetentionDays);
        Assert.Equal(AppSettings.DefaultIdleThresholdMinutes, settings.IdleThresholdMinutes);
        Assert.Null(settings.RetentionPeriod);
    }

    [Fact]
    public async Task Saved_settings_are_persisted_and_applied_without_restart()
    {
        var service = CreateService();
        var settings = new AppSettings(
            ExcludedApplications: ["mysecretapp"],
            ExcludedDirectories: [@"C:\work\private"],
            NeverRecordRules: [new NeverRecordRule(NeverRecordRuleKind.ByHost, "bank.example.com")],
            RetentionDays: 30,
            IdleThresholdMinutes: 5);

        await service.SaveAsync(settings);

        // Перечитали из хранилища - значения на месте (JSON-ключи в settings)
        var reloaded = await CreateService().LoadAsync();
        Assert.Equal(["mysecretapp"], reloaded.ExcludedApplications);
        Assert.Equal([@"C:\work\private"], reloaded.ExcludedDirectories);
        Assert.Single(reloaded.NeverRecordRules);
        Assert.Equal(NeverRecordRuleKind.ByHost, reloaded.NeverRecordRules[0].Kind);
        Assert.Equal(30, reloaded.RetentionDays);
        Assert.Equal(5, reloaded.IdleThresholdMinutes);
        Assert.Equal(TimeSpan.FromDays(30), reloaded.RetentionPeriod);

        // ФТ-2.3: исключение уже действует - перезапуска не было
        Assert.True(_exclusions.IsApplicationExcluded("mysecretapp", null));
        Assert.True(_exclusions.IsDirectoryExcluded(@"C:\work\private\sub\file.txt"));
        Assert.True(_exclusions.IsNeverRecorded(new Continuum.Core.Abstractions.Observable(
            Continuum.Core.Abstractions.ObservableKind.Url,
            "https://bank.example.com/pay",
            null, null, null, DateTimeOffset.UnixEpoch, null)));
    }

    [Fact]
    public async Task Defaults_keep_working_after_user_settings_applied()
    {
        var service = CreateService();
        await service.SaveAsync(new AppSettings(["notepad"], [], [], 0, 15));

        // Встроенные исключения неудаляемы: менеджеры паролей на месте
        Assert.True(_exclusions.IsApplicationExcluded("keepass", null));
        Assert.True(_exclusions.IsApplicationExcluded("notepad", null));
    }

    [Fact]
    public async Task Corrupted_values_fall_back_to_defaults()
    {
        await _repository.SetSettingAsync(SettingsService.AppsKey, "{не json");
        await _repository.SetSettingAsync(SettingsService.NeverKey, "42");
        await _repository.SetSettingAsync(SettingsService.RetentionKey, "много");
        await _repository.SetSettingAsync(SettingsService.IdleKey, "9999");

        var settings = await CreateService().LoadAsync();

        Assert.Empty(settings.ExcludedApplications);
        Assert.Empty(settings.NeverRecordRules);
        Assert.Equal(0, settings.RetentionDays);
        Assert.Equal(AppSettings.MaxIdleThresholdMinutes, settings.IdleThresholdMinutes);
    }

    [Fact]
    public async Task Ranges_are_clamped_on_save()
    {
        var service = CreateService();

        await service.SaveAsync(new AppSettings([], [], [], RetentionDays: 99999, IdleThresholdMinutes: 0));

        var settings = service.Settings;
        Assert.Equal(AppSettings.MaxRetentionDays, settings.RetentionDays);
        Assert.Equal(AppSettings.MinIdleThresholdMinutes, settings.IdleThresholdMinutes);
    }

    [Fact]
    public async Task Changed_event_is_raised_after_save()
    {
        var service = CreateService();
        AppSettings? observed = null;
        service.Changed += (_, settings) => observed = settings;

        await service.SaveAsync(new AppSettings([], [], [], 7, 10));

        Assert.NotNull(observed);
        Assert.Equal(7, observed!.RetentionDays);
    }
}
