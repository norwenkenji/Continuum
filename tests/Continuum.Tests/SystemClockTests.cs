using Continuum.Core.Abstractions;
using Continuum.Core.Time;
using Xunit;

namespace Continuum.Tests;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_returns_utc()
    {
        // Arrange: создаём системные часы
        var clock = new SystemClock();

        // Act: получаем текущее время
        var now = clock.UtcNow;

        // Assert: время в UTC и близко к эталонному DateTimeOffset.UtcNow
        Assert.Equal(DateTimeKind.Utc, now.UtcDateTime.Kind);
        Assert.Equal(TimeSpan.Zero, now.Offset);

        // Разница с эталоном должна быть меньше 5 секунд
        var delta = (DateTimeOffset.UtcNow - now).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SystemClock_implements_IClock()
    {
        // Проверяем, что SystemClock реализует контракт IClock
        Assert.IsAssignableFrom<IClock>(new SystemClock());
    }
}
