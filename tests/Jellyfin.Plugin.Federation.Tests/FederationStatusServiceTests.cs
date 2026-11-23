using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationStatusServiceTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static FederationInstance Instance(string alias = "paul", string url = "https://remote.example.com:8920")
        => new() { Alias = alias, ServerUrl = url, Username = "paul", Password = "x" };

    private static FederationStatusService Create(HttpMessageHandler handler, out FakeClock clock)
    {
        clock = new FakeClock(DateTimeOffset.Parse("2026-11-22T10:00:00Z"));
        var clockRef = clock; // local: out params cannot be captured in lambdas (CS1628)
        var config = new PluginConfiguration();
        config.Instances.Add(Instance());
        // Injected delay: the loop never waits 5 s in tests (instant completion is
        // fine — StartStop_SmokeTest only proves the lifecycle does not throw).
        var service = new FederationStatusService(() => config, new HttpClient(handler), Logger, () => clockRef.Now, (_, _) => Task.CompletedTask);
        service.RegisterInstance(Instance());
        return service;
    }

    [Fact]
    public async Task ProbeOnce_Online_SetsStateAndPublicInfo()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"paul-server","Version":"10.10.0"}""");
        var service = Create(handler, out _);

        await service.ProbeOnceAsync(Instance(), CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Online, state.State);
        Assert.Equal("paul-server", state.RemoteServerName);
        Assert.Equal("10.10.0", state.RemoteVersion);
        // Invariant 11: the probe must never authenticate.
        var probe = Assert.Single(handler.Requests);
        Assert.Equal("/System/Info/Public", probe.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("ApiKey=", probe.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeOnce_Failure_MarksOfflineAndBacksOff()
    {
        var service = Create(new ThrowingHandler(), out var clock);
        var instance = Instance();

        await service.ProbeOnceAsync(instance, CancellationToken.None);
        var firstDue = service.NextProbeDue("paul");
        await service.ProbeOnceAsync(instance, CancellationToken.None);
        var secondDue = service.NextProbeDue("paul");
        await service.ProbeOnceAsync(instance, CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Offline, state.State);
        Assert.Equal("unreachable", state.LastError);
        // Backoff grows: 30s → 60s → 120s (cap 600s).
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(30), firstDue);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(60), secondDue);
    }

    [Fact]
    public async Task ProbeOnce_503_MarksOfflineAndBacksOffLikeRefused()
    {
        // Spec: 5xx from the probe also backs off — a non-2xx remote must not
        // be re-probed on every loop tick, and it is classified as a remote
        // error ("remote returned 503"), not "unreachable".
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            string.Empty,
            HttpStatusCode.ServiceUnavailable);
        var service = Create(handler, out _);
        var instance = Instance();

        await service.ProbeOnceAsync(instance, CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Offline, state.State);
        Assert.Equal("remote returned 503", state.LastError);
        Assert.NotNull(service.NextProbeDue("paul"));
    }

    [Fact]
    public async Task ProbeOnce_Success_DoesNotClearInvalidCredentials()
    {
        // 401 from an authenticated call is definitive: a green public-info probe
        // must NOT flip the state back to Online (the user must see the credential
        // problem, not a green dot).
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"paul-server","Version":"10.10.0"}""");
        var service = Create(handler, out _);

        service.ReportOutcome("paul", new RemoteApiReport(false, "remote returned 401", true));
        await service.ProbeOnceAsync(Instance(), CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.InvalidCredentials, state.State);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public void ReportOutcome_Success_RecordsLastAuthenticated()
    {
        var service = Create(new FakeHttpMessageHandler(), out var clock);

        service.ReportOutcome("paul", new RemoteApiReport(true, null, false));

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Online, state.State);
        Assert.Equal(clock.Now, state.LastAuthenticatedUtc);
    }

    [Fact]
    public async Task StartStop_SmokeTest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"s","Version":"1"}""");
        var service = Create(handler, out _);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        Assert.NotEmpty(service.Snapshot());
    }

    /// <summary>Fixed clock for deterministic backoff assertions; stays put unless `Advance` is called — `ProbeOnceAsync` must never advance it.</summary>
    internal sealed class FakeClock
    {
        public FakeClock(DateTimeOffset start) => Now = start;

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan span) => Now += span;
    }
}
