using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Continuum.Core.Abstractions;
using Continuum.Core.Domain;
using Continuum.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Continuum.Projects;

/// <summary>
/// Сессионный реестр проектов: путь → внешний git root (через IProjectResolver)
/// → id в БД (upsert при первом появлении). Потокобезопасен: регистрировать
/// могут и коллекторы, и пайплайн.
/// </summary>
public sealed class ProjectRegistry : IProjectRegistry
{
    private readonly IProjectResolver _resolver;
    private readonly IRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<ProjectRegistry> _logger;

    // Нормализованный root → id проекта. StringComparer.Ordinal: корни уже
    // нормализованы резолвером к одному регистру
    private readonly ConcurrentDictionary<string, long> _rootToId = new(StringComparer.Ordinal);

    // Серийный замок upsert'ов: два потока не должны конкурировать за один корень
    private readonly SemaphoreSlim _upsertLock = new(1, 1);

    public ProjectRegistry(
        IProjectResolver resolver,
        IRepository repository,
        IClock clock,
        ILogger<ProjectRegistry> logger)
    {
        _resolver = resolver;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public IReadOnlyCollection<string> RegisteredRoots => [.. _rootToId.Keys];

    public long? GetProjectId(string normalizedRoot)
        => _rootToId.TryGetValue(normalizedRoot, out var id) ? id : null;

    public async Task<long?> RegisterPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? root;
        try
        {
            root = _resolver.ResolveRoot(path);
        }
        catch (Exception ex)
        {
            // Резолвер не должен бросать, но реестр - не место для падений
            _logger.LogWarning(ex, "Не удалось определить проект для пути {Path}", path);
            return null;
        }

        if (root is null)
        {
            return null; // вне git-репозитория - не проект, не ошибка
        }

        if (_rootToId.TryGetValue(root, out var cached))
        {
            return cached;
        }

        await _upsertLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rootToId.TryGetValue(root, out cached))
            {
                return cached;
            }

            var now = _clock.UtcNow;
            var id = await _repository.UpsertProjectAsync(
                new Project(
                    Id: 0,
                    Name: Path.GetFileName(root) is { Length: > 0 } name ? name : root,
                    RootPath: root,
                    GitRemote: null, // remote - дополнительный признак, читается со снапшотами (шаг 4)
                    FirstSeen: now,
                    LastSeen: now),
                ct).ConfigureAwait(false);

            _rootToId[root] = id;
            _logger.LogInformation("Зарегистрирован проект {Name} ({Root})", Path.GetFileName(root), root);
            return id;
        }
        finally
        {
            _upsertLock.Release();
        }
    }
}
