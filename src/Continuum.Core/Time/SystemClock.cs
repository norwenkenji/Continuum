using Continuum.Core.Abstractions;

namespace Continuum.Core.Time;

/// <summary>Системные часы: текущее время в UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
