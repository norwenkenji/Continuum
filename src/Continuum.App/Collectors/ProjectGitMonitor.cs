using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Continuum.Collectors;

/// <summary>
/// Монитор dirty-состояния зарегистрированных проектов. Каждый тик опрашивает
/// корни из IProjectRegistry через IGitClient (data-sources §4: только
/// читающие команды, таймауты на стороне клиента). Эмитит по одному
/// Observation на появившийся/изменившийся dirty-файл; исчезновение из
/// dirty и первый тик по корню событиями не являются. Недоступный git -
/// пропуск корня с сохранением предыдущего состояния, монитор живёт дальше.
/// </summary>
public sealed class ProjectGitMonitor : IObservationSource, IDisposable
{
    /// <summary>Интервал опроса по умолчанию (бюджет: git 15-60 с, здесь чаще - локальные команды дешёвые).</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly IProjectRegistry _registry;
    private readonly IGitClient _git;
    private readonly IClock _clock;
    private readonly ILogger<ProjectGitMonitor> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    // Корень проекта → его диффер. Ключи уже нормализованы реестром к одному регистру
    private readonly Dictionary<string, GitStateDiffer> _differs = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProjectGitMonitor(
        IProjectRegistry registry,
        IGitClient git,
        IClock clock,
        ILogger<ProjectGitMonitor> logger,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Интервал опроса должен быть положительным.");
        }

        _registry = registry;
        _git = git;
        _clock = clock;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <inheritdoc/>
    public event EventHandler<Observation>? Observed;

    /// <summary>Запускает цикл опроса. Идемпотентно: повторный Start - пустая операция.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            _differs.Clear(); // перезапуск - новая точка отсчёта по всем корням

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }
    }

    /// <summary>Останавливает цикл опроса. Идемпотентно.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Фоновая задача не должна ронять Stop
        }

        cts.Dispose();
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Любая ошибка тика - пропускаем тик, монитор продолжает работу
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка через Stop
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var roots = _registry.RegisteredRoots;

        // Корни, ушедшие из реестра, забываем: возврат корня начнётся с базового тика
        foreach (var known in _differs.Keys.ToArray())
        {
            if (!roots.Contains(known))
            {
                _differs.Remove(known);
            }
        }

        foreach (var root in roots)
        {
            GitStateInfo state;
            try
            {
                state = await _git.GetStateAsync(root, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Ошибка конкретного корня - пропуск корня, предыдущее состояние сохраняется
                _logger.LogDebug(ex, "Опрос git для корня {Root} завершился ошибкой, корень пропущен", root);
                continue;
            }

            if (!state.Available)
            {
                continue; // git недоступен/репозиторий удалён - состояние не трогаем
            }

            if (!_differs.TryGetValue(root, out var differ))
            {
                differ = new GitStateDiffer();
                _differs[root] = differ;
            }

            var snapshot = new Dictionary<string, FileChangeKind>(StringComparer.Ordinal);
            foreach (var file in state.DirtyFiles)
            {
                snapshot[file.Path] = file.Kind;
            }

            foreach (var change in differ.Process(snapshot))
            {
                Emit(new Observation(
                    new Observable(
                        Kind: ObservableKind.FilePath,
                        Value: CombineAbsolute(root, change.Path),
                        ProcessId: null,
                        ApplicationName: null,
                        ApplicationExePath: null,
                        Timestamp: _clock.UtcNow,
                        ProjectRootPath: root),
                    EventKind.FileChanged,
                    FileChange: change.Kind,
                    FileSource: FileActivitySource.Git));
            }
        }
    }

    /// <summary>Склейка относительного пути git ('/'-разделители) с нормализованным корнем.</summary>
    private static string CombineAbsolute(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', '\\'));

    /// <summary>Эмитит наблюдение: ошибка одного подписчика не ломает остальных и сам коллектор.</summary>
    private void Emit(Observation observation)
    {
        var handlers = Observed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<Observation> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, observation);
            }
            catch
            {
                // Подписчик не должен получать исключения из недр коллектора -
                // и коллектор не должен падать от исключений подписчика
            }
        }
    }
}
