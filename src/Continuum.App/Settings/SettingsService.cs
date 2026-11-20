using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Continuum.Infrastructure.Privacy;
using Microsoft.Extensions.Logging;

namespace Continuum.Settings;

/// <summary>
/// Настройки приложения (шаг 9, ФТ-2.3 и ФТ-7.2): чтение и запись в таблицу
/// settings, применение к privacy-набору и порогу простоя.
/// Ключи: privacy.apps / privacy.dirs / privacy.never / retention.days /
/// session.idle_minutes. Значения - JSON; битое значение трактуется как
/// умолчание (никогда не бросает, конвейер не должен падать из-за настроек).
/// После Load/Save набор исключений применяется сразу - без перезапуска.
/// </summary>
public sealed class SettingsService : IIdleThresholdProvider
{
    public const string AppsKey = "privacy.apps";
    public const string DirsKey = "privacy.dirs";
    public const string NeverKey = "privacy.never";
    public const string RetentionKey = "retention.days";
    public const string IdleKey = "session.idle_minutes";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IRepository _repository;
    private readonly ExclusionSet _exclusions;
    private readonly ILogger<SettingsService> _logger;

    private AppSettings _settings = AppSettings.Default;

    public SettingsService(
        IRepository repository,
        ExclusionSet exclusions,
        ILogger<SettingsService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Текущие настройки (снимок; меняется только через Load/Save).</summary>
    public AppSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Порог простоя для супервизора сессии (ФТ-3.1).</summary>
    TimeSpan IIdleThresholdProvider.Current => Settings.IdleThreshold;

    /// <summary>Настройки изменились (для переприменения зависимых компонентов).</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>
    /// Читает настройки из БД, применяет к исключениям и возвращает их.
    /// Отсутствующие или битые значения - умолчания.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var apps = await ReadListAsync(AppsKey, ct).ConfigureAwait(false);
        var dirs = await ReadListAsync(DirsKey, ct).ConfigureAwait(false);
        var rules = await ReadRulesAsync(ct).ConfigureAwait(false);
        var retention = await ReadIntAsync(RetentionKey, ct).ConfigureAwait(false);
        var idle = await ReadIntAsync(IdleKey, ct).ConfigureAwait(false);

        var settings = new AppSettings(
            ExcludedApplications: apps,
            ExcludedDirectories: dirs,
            NeverRecordRules: rules,
            RetentionDays: Math.Clamp(retention ?? 0, 0, AppSettings.MaxRetentionDays),
            IdleThresholdMinutes: Math.Clamp(
                idle ?? AppSettings.DefaultIdleThresholdMinutes,
                AppSettings.MinIdleThresholdMinutes,
                AppSettings.MaxIdleThresholdMinutes));

        Apply(settings);
        return settings;
    }

    /// <summary>Сохраняет настройки и применяет их сразу (без перезапуска).</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings with
        {
            ExcludedApplications = Clean(settings.ExcludedApplications),
            ExcludedDirectories = Clean(settings.ExcludedDirectories),
            NeverRecordRules = settings.NeverRecordRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => r with { Value = r.Value.Trim() })
                .ToList(),
            RetentionDays = Math.Clamp(settings.RetentionDays, 0, AppSettings.MaxRetentionDays),
            IdleThresholdMinutes = Math.Clamp(
                settings.IdleThresholdMinutes,
                AppSettings.MinIdleThresholdMinutes,
                AppSettings.MaxIdleThresholdMinutes),
        };

        await _repository.SetSettingAsync(AppsKey, JsonSerializer.Serialize(normalized.ExcludedApplications, JsonOptions), ct).ConfigureAwait(false);
        await _repository.SetSettingAsync(DirsKey, JsonSerializer.Serialize(normalized.ExcludedDirectories, JsonOptions), ct).ConfigureAwait(false);
        await _repository.SetSettingAsync(NeverKey, JsonSerializer.Serialize(normalized.NeverRecordRules, JsonOptions), ct).ConfigureAwait(false);
        await _repository.SetSettingAsync(RetentionKey, normalized.RetentionDays.ToString(), ct).ConfigureAwait(false);
        await _repository.SetSettingAsync(IdleKey, normalized.IdleThresholdMinutes.ToString(), ct).ConfigureAwait(false);

        Apply(normalized);
    }

    private void Apply(AppSettings settings)
    {
        Volatile.Write(ref _settings, settings);
        try
        {
            _exclusions.Apply(settings);
        }
        catch (Exception ex)
        {
            // Набор исключений не применился - работаем с прежним; не роняем UI
            _logger.LogWarning(ex, "Не удалось применить настройки исключений");
        }

        try
        {
            Changed?.Invoke(this, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Подписчик на изменение настроек упал");
        }
    }

    private async Task<IReadOnlyList<string>> ReadListAsync(string key, CancellationToken ct)
    {
        var raw = await _repository.GetSettingAsync(key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Настройка {Key} содержит битый JSON - взято умолчание", key);
            return [];
        }
    }

    private async Task<IReadOnlyList<NeverRecordRule>> ReadRulesAsync(CancellationToken ct)
    {
        var raw = await _repository.GetSettingAsync(NeverKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<NeverRecordRule>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Настройка {Key} содержит битый JSON - взято умолчание", NeverKey);
            return [];
        }
    }

    private async Task<int?> ReadIntAsync(string key, CancellationToken ct)
    {
        var raw = await _repository.GetSettingAsync(key, ct).ConfigureAwait(false);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
