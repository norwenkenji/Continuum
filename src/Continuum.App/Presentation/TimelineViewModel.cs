using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Interfaces;
using Continuum.Presentation;

namespace Continuum.Presentation;

/// <summary>
/// Модель представления хронологии (шаг 8): события последней сессии
/// списком «время - описание». Загрузка - командой/методом LoadAsync,
/// ViewModel тестируется без UI (IRepository - за конструктором).
/// </summary>
public sealed class TimelineViewModel : ViewModelBase
{
    private readonly IRepository _repository;

    private string _summaryText = "Событий нет";

    public TimelineViewModel(IRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        Events = [];
    }

    /// <summary>Строки хронологии последней сессии (готовые к отображению).</summary>
    public ObservableCollection<string> Events { get; }

    /// <summary>Сводка: «Сессия: 45 событий, 19:02 - 21:07».</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    /// <summary>Загружает события последней сессии. Никогда не бросает.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await _repository.GetLastSessionAsync(ct).ConfigureAwait(false);
            if (session is null)
            {
                Events.Clear();
                SummaryText = "Сессий ещё не было";
                return;
            }

            var events = await _repository.GetSessionEventsAsync(session.Id, ct).ConfigureAwait(false);
            var lines = events
                .OrderBy(e => e.Ts)
                .Select(TimelineFormatter.Format)
                .ToList();

            Events.Clear();
            foreach (var line in lines)
            {
                Events.Add(line);
            }

            SummaryText = lines.Count == 0
                ? "Сессия без событий"
                : $"Сессия: {lines.Count} событий, {session.StartedAt.ToLocalTime():HH:mm} - " +
                  (session.EndedAt?.ToLocalTime().ToString("HH:mm") ?? "идёт");
        }
        catch
        {
            // UI не падает от сбоя чтения БД - остаётся прежнее состояние
        }
    }
}
