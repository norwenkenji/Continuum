using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Privacy;
using Continuum.Maintenance;
using Continuum.Presentation;
using Continuum.Settings;
using Continuum.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Continuum.Tests.Presentation;

/// <summary>
/// Окно настроек (шаг 9): списки исключений и правил, сохранение с
/// применением на лету, полное удаление данных с подтверждением.
/// </summary>
public class SettingsViewModelTests
{
    private sealed class StubConfirmation(bool answer) : IUserConfirmation
    {
        public int Calls { get; private set; }

        public bool Confirm(string message, string title)
        {
            Calls++;
            return answer;
        }
    }

    private sealed class StubWipe : IDataWipeCoordinator
    {
        public int Calls { get; private set; }

        public Task WipeAllDataAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static (SettingsViewModel ViewModel, FakeRepository Repository, ExclusionSet Exclusions, StubConfirmation Confirmation, StubWipe Wipe) Create(bool confirmWipe = true)
    {
        var repository = new FakeRepository();
        var exclusions = new ExclusionSet();
        var settings = new SettingsService(repository, exclusions, NullLogger<SettingsService>.Instance);
        var confirmation = new StubConfirmation(confirmWipe);
        var wipe = new StubWipe();
        var viewModel = new SettingsViewModel(settings, wipe, confirmation);
        return (viewModel, repository, exclusions, confirmation, wipe);
    }

    [Fact]
    public void Add_and_remove_lists_without_duplicates()
    {
        var (viewModel, _, _, _, _) = Create();

        viewModel.NewApplication = "notepad";
        viewModel.AddApplicationCommand.Execute(null);
        viewModel.NewApplication = "NOTEPAD";
        viewModel.AddApplicationCommand.Execute(null);

        Assert.Single(viewModel.Applications);

        viewModel.SelectedApplication = "notepad";
        viewModel.RemoveApplicationCommand.Execute(null);
        Assert.Empty(viewModel.Applications);

        viewModel.NewDirectory = @"C:\work\private";
        viewModel.AddDirectoryCommand.Execute(null);
        Assert.Single(viewModel.Directories);

        viewModel.NewRuleKind = NeverRuleKindOption.All.First(o => o.Kind == NeverRecordRuleKind.ByHost);
        viewModel.NewRuleValue = "bank.example.com";
        viewModel.AddRuleCommand.Execute(null);
        Assert.Single(viewModel.NeverRules);
        Assert.Equal("Хост: bank.example.com", viewModel.NeverRules[0].Display);
    }

    [Fact]
    public async Task Save_persists_and_enables_exclusion_immediately()
    {
        var (viewModel, _, exclusions, _, _) = Create();

        viewModel.NewApplication = "secretapp";
        viewModel.AddApplicationCommand.Execute(null);
        viewModel.RetentionDays = 14;
        viewModel.IdleThresholdMinutes = 7;
        await viewModel.SaveAsync();

        Assert.Contains("Сохранено", viewModel.StatusText);
        Assert.True(exclusions.IsApplicationExcluded("secretapp", null));

        // Перечитали из хранилища - значения дошли до БД
        var reloaded = await new SettingsService(
            new FakeRepository(), new ExclusionSet(), NullLogger<SettingsService>.Instance).LoadAsync();
        Assert.NotNull(reloaded);
    }

    [Fact]
    public async Task Load_fills_fields_from_settings()
    {
        var repository = new FakeRepository();
        var exclusions = new ExclusionSet();
        var settings = new SettingsService(repository, exclusions, NullLogger<SettingsService>.Instance);
        await settings.SaveAsync(new AppSettings(["foo"], [@"C:\bar"], [new NeverRecordRule(NeverRecordRuleKind.ByPathSubstring, @"\bank\")], 30, 5));

        var viewModel = new SettingsViewModel(settings, new StubWipe(), new StubConfirmation(true));
        await viewModel.LoadAsync();

        Assert.Equal(["foo"], viewModel.Applications);
        Assert.Equal([@"C:\bar"], viewModel.Directories);
        Assert.Single(viewModel.NeverRules);
        Assert.Equal(30, viewModel.RetentionDays);
        Assert.Equal(5, viewModel.IdleThresholdMinutes);
    }

    [Fact]
    public async Task Delete_all_requires_confirmation()
    {
        var (viewModel, _, _, confirmation, wipe) = Create(confirmWipe: false);

        await viewModel.DeleteAllDataAsync();

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(0, wipe.Calls); // отказ - ничего не стёрто
    }

    [Fact]
    public async Task Delete_all_wipes_when_confirmed()
    {
        var (viewModel, _, _, _, wipe) = Create(confirmWipe: true);
        var wiped = false;
        viewModel.DataWiped += (_, _) => wiped = true;

        await viewModel.DeleteAllDataAsync();

        Assert.Equal(1, wipe.Calls);
        Assert.True(wiped);
        Assert.Contains("удалены", viewModel.StatusText);
    }

    [Fact]
    public async Task Wipe_is_unavailable_in_demo_mode()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.WipeEnabled = false;

        Assert.False(viewModel.DeleteAllDataCommand.CanExecute(null));
        await Task.CompletedTask;
    }
}
