using System;
using Continuum.Core.Domain;
using Continuum.Infrastructure.Database;
using Xunit;

namespace Continuum.Tests.Database;

public class EnumCodecTests
{
    [Theory]
    [InlineData(SessionStatus.Running, "running")]
    [InlineData(SessionEndReason.CrashRecovered, "crash_recovered")]
    [InlineData(EventKind.AppFocused, "app_focused")]
    [InlineData(FileActivitySource.VsCodeHistory, "vs_code_history")]
    [InlineData(RestoreStrategyKind.OpenUrl, "open_url")]
    public void ToDb_writes_snake_case<TEnum>(TEnum value, string expected) where TEnum : struct, Enum
    {
        Assert.Equal(expected, EnumCodec.ToDb(value));
    }

    [Fact]
    public void Roundtrip_all_domain_enums()
    {
        AssertRoundtrip<SessionStatus>();
        AssertRoundtrip<SessionEndReason>();
        AssertRoundtrip<EventKind>();
        AssertRoundtrip<FileChangeKind>();
        AssertRoundtrip<FileActivitySource>();
        AssertRoundtrip<SnapshotReason>();
        AssertRoundtrip<TerminalSource>();
        AssertRoundtrip<BrowserTabSource>();
        AssertRoundtrip<RestorePlanStatus>();
        AssertRoundtrip<RestoreStepResult>();
        AssertRoundtrip<RestoreStrategyKind>();
    }

    private static void AssertRoundtrip<TEnum>() where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var db = EnumCodec.ToDb(value);
            var back = EnumCodec.FromDb<TEnum>(db);
            Assert.Equal(value, back);
        }
    }
}
