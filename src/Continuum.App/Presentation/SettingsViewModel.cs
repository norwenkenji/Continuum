using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Maintenance;
using Continuum.Settings;

namespace Continuum.Presentation;

/// <summary>
/// Модель представления окна настроек (шаг 9, ФТ-2.3 и ФТ-7.2):
/// исключения приложений и каталогов, правила «не записывать никогда»,
/// срок хранения данных, порог простоя сессии, полное удаление данных.
/// Сохранение применяет настройки сразу - без перезапуска (ChangeSet
/// обновляет набор исключений и порог простоя).
/// Как и прочие модели представления, тестируется без UI.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDataWipeCoordinator _maintenance;
    private readonly IUserConfirmation _confirmation;

    private string _newApplication = string.Empty;
    private string _newDirectory = string.Empty;
    private string _newRuleValue = string.Empty;
    private string? _selectedApplication;
    private string? _selectedDirectory;
    private NeverRuleItem? _selectedRule;
    private NeverRuleKindOption _newRuleKind = NeverRuleKindOption.All[0];
    private int _retentionDays;
    private int _idleThresholdMinutes = AppSettings.DefaultIdleThresholdMinutes;
    private string _statusText = "Настройки не изменялись";
    private bool _isBusy;

    public SettingsViewModel(
        SettingsService settings,
        IDataWipeCoordinator maintenance,
        IUserConfirmation confirmation)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));

        AddApplicationCommand = new RelayCommand(AddApplication);
        RemoveApplicationCommand = new RelayCommand(RemoveApplication, () => SelectedApplication is not null);
        AddDirectoryCommand = new RelayCommand(AddDirectory);
        RemoveDirectoryCommand = new RelayCommand(RemoveDirectory, () => SelectedDirectory is not null);
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => SelectedRule is not null);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => !IsBusy);
        DeleteAllDataCommand = new RelayCommand(async () => await DeleteAllDataAsync(), () => !IsBusy && WipeEnabled);
    }

    public ObservableCollection<string> Applications { get; } = [];

    public ObservableCollection<string> Directories { get; } = [];

    public ObservableCollection<NeverRuleItem> NeverRules { get; } = [];

    public IReadOnlyList<NeverRuleKindOption> RuleKinds => NeverRuleKindOption.All;

    public RelayCommand AddApplicationCommand { get; }

    public RelayCommand RemoveApplicationCommand { get; }

    public RelayCommand AddDirectoryCommand { get; }

    public RelayCommand RemoveDirectoryCommand { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand RemoveRuleCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand DeleteAllDataCommand { get; }

    /// <summary>Полное удаление данных выполнено (окно и главное окно обновляются).</summary>
    public event EventHandler? DataWiped;

    /// <summary>В демо-режиме стирать демо-данные нельзя: кнопка недоступна.</summary>
    public bool WipeEnabled { get; set; } = true;

    public string NewApplication
    {
        get => _newApplication;
        set => SetField(ref _newApplication, value);
    }

    public string NewDirectory
    {
        get => _newDirectory;
        set => SetField(ref _newDirectory, value);
    }

    public string NewRuleValue
    {
        get => _newRuleValue;
        set => SetField(ref _newRuleValue, value);
    }

    public NeverRuleKindOption NewRuleKind
    {
        get => _newRuleKind;
        set => SetField(ref _newRuleKind, value);
    }

    public string? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (SetField(ref _selectedApplication, value))
            {
                RemoveApplicationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedDirectory
    {
        get => _selectedDirectory;
        set
        {
            if (SetField(ref _selectedDirectory, value))
            {
                RemoveDirectoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public NeverRuleItem? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetField(ref _selectedRule, value))
            {
                RemoveRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Срок хранения в днях; 0 - хранить бессрочно.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => SetField(ref _retentionDays, value);
    }

    /// <summary>Порог простоя сессии в минутах (ФТ-3.1).</summary>
    public int IdleThresholdMinutes
    {
        get => _idleThresholdMinutes;
        set => SetField(ref _idleThresholdMinutes, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                DeleteAllDataCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Загружает настройки в поля окна.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            Applications.Clear();
            foreach (var application in settings.ExcludedApplications)
            {
                Applications.Add(application);
            }

            Directories.Clear();
            foreach (var directory in settings.ExcludedDirectories)
            {
                Directories.Add(directory);
            }

            NeverRules.Clear();
            foreach (var rule in settings.NeverRecordRules)
            {
                NeverRules.Add(new NeverRuleItem(rule.Kind, rule.Value));
            }

            RetentionDays = settings.RetentionDays;
            IdleThresholdMinutes = settings.IdleThresholdMinutes;
            StatusText = "Настройки загружены";
        }
        catch
        {
            StatusText = "Не удалось загрузить настройки - показаны значения по умолчанию";
        }
    }

    /// <summary>Сохраняет настройки и применяет их сразу (без перезапуска).</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var settings = new AppSettings(
                ExcludedApplications: Applications.ToList(),
                ExcludedDirectories: Directories.ToList(),
                NeverRecordRules: NeverRules.Select(r => r.ToRule()).ToList(),
                RetentionDays: Math.Clamp(RetentionDays, 0, AppSettings.MaxRetentionDays),
                IdleThresholdMinutes: Math.Clamp(
                    IdleThresholdMinutes,
                    AppSettings.MinIdleThresholdMinutes,
                    AppSettings.MaxIdleThresholdMinutes));

            await _settings.SaveAsync(settings, ct).ConfigureAwait(false);

            // Поля нормализованы - показываем то, что реально сохранилось
            RetentionDays = settings.RetentionDays;
            IdleThresholdMinutes = settings.IdleThresholdMinutes;
            StatusText = $"Сохранено в {DateTime.Now:HH:mm} - действует сразу, перезапуск не нужен";
        }
        catch
        {
            StatusText = "Не удалось сохранить настройки";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Полное удаление данных с подтверждением (ФТ-7.2).</summary>
    public async Task DeleteAllDataAsync(CancellationToken ct = default)
    {
        var confirmed = _confirmation.Confirm(
            "Все записанные данные будут стёрты без возможности восстановления:\n" +
            "хронология, снапшоты, планы восстановления и настройки.\n\nПродолжить?",
            "Удаление всех данных");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _maintenance.WipeAllDataAsync(ct).ConfigureAwait(false);
            await LoadAsync(ct).ConfigureAwait(false);
            StatusText = "Все данные удалены, база создана заново";
            DataWiped?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            StatusText = "Не удалось удалить данные";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddApplication()
    {
        var value = NewApplication.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (!Applications.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)))
        {
            Applications.Add(value);
        }

        NewApplication = string.Empty;
    }

    private void RemoveApplication()
    {
        if (SelectedApplication is { } selected)
        {
            Applications.Remove(selected);
            SelectedApplication = null;
        }
    }

    private void AddDirectory()
    {
        var value = NewDirectory.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (!Directories.Any(d => string.Equals(d, value, StringComparison.OrdinalIgnoreCase)))
        {
            Directories.Add(value);
        }

        NewDirectory = string.Empty;
    }

    private void RemoveDirectory()
    {
        if (SelectedDirectory is { } selected)
        {
            Directories.Remove(selected);
            SelectedDirectory = null;
        }
    }

    private void AddRule()
    {
        var value = NewRuleValue.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (!NeverRules.Any(r => r.Kind == NewRuleKind.Kind
                && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            NeverRules.Add(new NeverRuleItem(NewRuleKind.Kind, value));
        }

        NewRuleValue = string.Empty;
    }

    private void RemoveRule()
    {
        if (SelectedRule is { } selected)
        {
            NeverRules.Remove(selected);
            SelectedRule = null;
        }
    }
}
