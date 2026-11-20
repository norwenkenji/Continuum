using System.Collections.ObjectModel;
using Continuum.Core.Domain;

namespace Continuum.Presentation;

/// <summary>Правило «не записывать никогда» в списке окна настроек.</summary>
public sealed class NeverRuleItem
{
    public NeverRuleItem(NeverRecordRuleKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public NeverRecordRuleKind Kind { get; }

    public string Value { get; }

    public string Display => Kind switch
    {
        NeverRecordRuleKind.ByAppName => $"Приложение: {Value}",
        NeverRecordRuleKind.ByHost => $"Хост: {Value}",
        _ => $"Подстрока пути: {Value}",
    };

    public NeverRecordRule ToRule() => new(Kind, Value);
}

/// <summary>Вариант выбора вида правила для ComboBox.</summary>
public sealed class NeverRuleKindOption
{
    public NeverRuleKindOption(NeverRecordRuleKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public NeverRecordRuleKind Kind { get; }

    public string Title { get; }

    public override string ToString() => Title;

    public static IReadOnlyList<NeverRuleKindOption> All { get; } =
    [
        new(NeverRecordRuleKind.ByAppName, "Имя приложения"),
        new(NeverRecordRuleKind.ByHost, "Хост URL"),
        new(NeverRecordRuleKind.ByPathSubstring, "Подстрока пути"),
    ];
}
