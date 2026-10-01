namespace Continuum.Runtime;

/// <summary>Точка доступа к текущей сессии для пайплайна наблюдений.</summary>
public interface ISessionContext
{
    /// <summary>Id текущей сессии; 0 - сессия ещё не создана.</summary>
    long CurrentSessionId { get; }
}
