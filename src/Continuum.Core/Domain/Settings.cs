namespace Continuum.Core.Domain;

/// <summary>Вид правила «не записывать никогда» (docs/specs/privacy-pipeline.md, §4.3).</summary>
public enum NeverRecordRuleKind
{
    /// <summary>Совпадение с именем приложения.</summary>
    ByAppName,

    /// <summary>Хост URL совпадает с доменом или его суффиксом.</summary>
    ByHost,

    /// <summary>Подстрока в пути (например, «\bank\»).</summary>
    ByPathSubstring,
}

/// <summary>Правило «не записывать никогда»: вид и значение.</summary>
public sealed record NeverRecordRule(NeverRecordRuleKind Kind, string Value);

/// <summary>
/// Пользовательские настройки приложения (окно настроек, ФТ-2.3 и ФТ-7.2):
/// исключения приложений и каталогов, правила «не записывать никогда»,
/// срок хранения данных и порог простоя для границ сессии.
/// Хранятся в таблице settings отдельными ключами; значения - JSON.
/// </summary>
public sealed record AppSettings(
    IReadOnlyList<string> ExcludedApplications,
    IReadOnlyList<string> ExcludedDirectories,
    IReadOnlyList<NeverRecordRule> NeverRecordRules,
    int RetentionDays,
    int IdleThresholdMinutes)
{
    /// <summary>Порог простоя по умолчанию - 15 минут.</summary>
    public const int DefaultIdleThresholdMinutes = 15;

    public const int MinIdleThresholdMinutes = 1;

    public const int MaxIdleThresholdMinutes = 240;

    /// <summary>Верхняя граница срока хранения (10 лет) - защита от опечатки.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>Настройки по умолчанию: без пользовательских исключений, хранение бессрочное.</summary>
    public static AppSettings Default { get; } = new(
        ExcludedApplications: [],
        ExcludedDirectories: [],
        NeverRecordRules: [],
        RetentionDays: 0,
        IdleThresholdMinutes: DefaultIdleThresholdMinutes);

    /// <summary>Срок хранения в виде интервала; null - хранить бессрочно.</summary>
    public TimeSpan? RetentionPeriod => RetentionDays > 0 ? TimeSpan.FromDays(RetentionDays) : null;

    /// <summary>Порог простоя, приведённый к допустимому диапазону.</summary>
    public TimeSpan IdleThreshold => TimeSpan.FromMinutes(
        Math.Clamp(IdleThresholdMinutes, MinIdleThresholdMinutes, MaxIdleThresholdMinutes));
}
