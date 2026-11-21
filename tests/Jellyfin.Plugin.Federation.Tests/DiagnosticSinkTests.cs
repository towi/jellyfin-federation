using System;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class DiagnosticSinkTests
{
    [Fact]
    public void Attach_ReplaysBufferedEntriesInOrder()
    {
        var sink = new DiagnosticSink();
        sink.LogWarning("first");
        sink.LogError(new InvalidOperationException("boom"), "second");

        var target = new CapturingLogger();
        sink.Attach(target);

        Assert.Collection(
            target.Entries,
            e => Assert.Equal((LogLevel.Warning, "first"), e),
            e => Assert.Equal((LogLevel.Error, "second"), e));
    }

    [Fact]
    public void Log_AfterAttach_ForwardsDirectly()
    {
        var sink = new DiagnosticSink();
        var target = new CapturingLogger();
        sink.Attach(target);

        sink.LogInformation("after");

        var entry = Assert.Single(target.Entries);
        Assert.Equal((LogLevel.Information, "after"), entry);
    }

    [Fact]
    public void Attach_SecondLogger_DoesNotReplayAgain()
    {
        var sink = new DiagnosticSink();
        sink.LogInformation("buffered");

        var first = new CapturingLogger();
        sink.Attach(first);
        var second = new CapturingLogger();
        sink.Attach(second);

        Assert.Single(first.Entries);
        Assert.Empty(second.Entries);
    }
}
