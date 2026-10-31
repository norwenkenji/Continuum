using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Continuum.Stopped;
using Continuum.Presentation;

namespace Continuum.Presentation;

/// <summary>
/// Модель представления главного окна: сводка последней сессии и вердикт
/// «Где я остановился?» (бишдинги XAML, code-behind только про трей/закрытие).
/// Обновление - командой; ViewModel тестируется без UI.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly IRepository _repository;
    private readonly StoppedEngine _stopped;

    private string _verdictText = "Нет данных";
    private string? _projectText;
    private string? _lastFileText;
    private string? _gitText;
    private string _sessionSummaryText = "Сессий ещё не было";
    private string? _verdictDetail;

    public MainViewModel(IRepository repository, StoppedEngine stopped)
    {
        _repository = repository;
        _stopped = stopped;
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());
    }

    /// <summary>Вердикт статуса: «Работа, возможно, не завершена» и т.п.</summary>
    public string VerdictText
    {
        get => _verdictText;
        private set => SetField(ref _verdictText, value);
    }

    /// <summary>Проект вердикта: имя и корень.</summary>
    public string? ProjectText
    {
        get => _projectText;
        private set => SetField(ref _projectText, value);
    }

    /// <summary>Последний файл сессии.</summary>
    public string? LastFileText
    {
        get => _lastFileText;
        private set => SetField(ref _lastFileText, value);
    }

    /// <summary>Ветка/HEAD живого репозитория.</summary>
    public string? GitText
    {
        get => _gitText;
        private set => SetField(ref _gitText, value);
    }

    /// <summary>Сводка сессии: длительность, приложений, файлов.</summary>
    public string SessionSummaryText
    {
        get => _sessionSummaryText;
        private set => SetField(ref _sessionSummaryText, value);
    }

    /// <summary>Расшифровка вердикта (какие факты двигали решение).</summary>
    public string? VerdictDetail
    {
        get => _verdictDetail;
        private set => SetField(ref _verdictDetail, value);
    }

    public RelayCommand RefreshCommand { get; }

    /// <summary>Собирает сводку и вердикт заново. Никогда не бросает.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            await RefreshSessionAsync(ct).ConfigureAwait(false);
            await RefreshVerdictAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // UI не падает от сбоя чтения БД - остаётся прежнее состояние
        }
    }

    private async Task RefreshSessionAsync(CancellationToken ct)
    {
        var session = await _repository.GetLastSessionAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            SessionSummaryText = "Сессий ещё не было";
            return;
        }

        var events = await _repository.GetSessionEventsAsync(session.Id, ct).ConfigureAwait(false);
        var files = await _repository.GetSessionFileActivityAsync(session.Id, ct).ConfigureAwait(false);

        var duration = session.EndedAt is { } end
            ? FormatDuration(end - session.StartedAt)
            : FormatDuration(DateTimeOffset.UtcNow - session.StartedAt) + " (идёт)";
        var apps = events.Select(e => e.ApplicationId).Where(id => id is not null).Distinct().Count();
        SessionSummaryText =
            $"Длительность: {duration}, приложений: {apps}, затронуто файлов: {files.Select(f => f.Path).Distinct().Count()}";
    }

    private async Task RefreshVerdictAsync(CancellationToken ct)
    {
        var verdict = await _stopped.EvaluateAsync(ct: ct).ConfigureAwait(false);

        VerdictText = verdict.Status switch
        {
            StoppedStatus.Completed => "Работа завершена",
            StoppedStatus.MaybeUnfinished => "Работа, возможно, не завершена",
            _ => "Статус неясен",
        };
        ProjectText = verdict.ProjectName is null ? null : $"{verdict.ProjectName}";
        LastFileText = verdict.LastFilePath is null ? null : System.IO.Path.GetFileName(verdict.LastFilePath);
        GitText = verdict.Branch is null ? null : $"{verdict.Branch} @ {(verdict.HeadCommit?[..7] ?? "?")}";
        VerdictDetail = verdict.Status switch
        {
            StoppedStatus.Completed => $"Последнее событие - коммит, незакоммиченных файлов: {verdict.DirtyCount}",
            StoppedStatus.MaybeUnfinished => verdict.DirtyCount > 0
                ? $"Незакоммиченных файлов: {verdict.DirtyCount}"
                : "Последнее событие - правка файла",
            _ => verdict.ProjectName is null ? "Нет проектных событий в сессии" : "Git-репозиторий не найден",
        };
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes} мин"
                : $"{(int)span.TotalSeconds} с";
}
