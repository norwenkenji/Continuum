using System;
using Continuum.Core.Domain;

namespace Continuum.Collectors;

/// <summary>
/// Событие диффа HEAD/ветки: вид события (GitCommit или GitBranchSwitch)
/// и готовое значение для title наблюдения.
/// </summary>
public sealed record GitHeadChange(EventKind Kind, string Value);

/// <summary>
/// Чистая логика диффа HEAD и ветки без git (как GitStateDiffer для dirty):
/// вход - очередное состояние (branch, headCommit, headSubject), выход -
/// не более одного события. Первый вызов после создания или
/// <see cref="Reset"/> - базовая точка отсчёта без события.
///
/// Семантика переходов detached HEAD (осознанное решение):
/// уход в detached (ветка стала null) - не событие, состояние запоминается;
/// выход из detached (ветка появилась, прежняя была null) - тоже не событие:
/// назвать «с чего» переключились нельзя. Только смена одной именованной
/// ветки на другую - GitBranchSwitch.
/// </summary>
public sealed class GitHeadDiffer
{
    private string? _branch;
    private string? _headCommit;
    private bool _baselineTaken;

    /// <summary>
    /// Сравнивает очередное состояние с предыдущим и запоминает его.
    /// Если в одном тике сменились и ветка, и хэш - только GitBranchSwitch:
    /// переключение ветки уже объясняет смену HEAD.
    /// </summary>
    public GitHeadChange? Process(string? branch, string? headCommit, string? headSubject)
    {
        if (!_baselineTaken)
        {
            Remember(branch, headCommit);
            return null;
        }

        GitHeadChange? change = null;

        var branchChanged = !string.Equals(_branch, branch, StringComparison.Ordinal);
        if (branchChanged)
        {
            // Обе ветки именованные - иначе это уход в detached или выход из него
            if (_branch is not null && branch is not null)
            {
                change = new GitHeadChange(EventKind.GitBranchSwitch, $"{_branch} -> {branch}");
            }
        }
        else
        {
            var commitChanged = !string.Equals(_headCommit, headCommit, StringComparison.Ordinal);
            if (commitChanged && _headCommit is not null && headCommit is not null)
            {
                change = new GitHeadChange(EventKind.GitCommit, FormatCommit(headCommit, headSubject));
            }
        }

        Remember(branch, headCommit);
        return change;
    }

    /// <summary>Сбрасывает базу: следующий вызов снова отдаст null.</summary>
    public void Reset() => _baselineTaken = false;

    /// <summary>«short7 subject»; без subject - только короткий хэш.</summary>
    private static string FormatCommit(string headCommit, string? headSubject)
    {
        var shortHash = headCommit.Length > 7 ? headCommit[..7] : headCommit;
        return string.IsNullOrEmpty(headSubject) ? shortHash : $"{shortHash} {headSubject}";
    }

    private void Remember(string? branch, string? headCommit)
    {
        _branch = branch;
        _headCommit = headCommit;
        _baselineTaken = true;
    }
}
