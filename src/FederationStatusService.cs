using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Reachability state of one federation instance.
/// </summary>
public enum FederationInstanceState
{
    /// <summary>Never probed yet.</summary>
    Unknown,

    /// <summary>Answers /System/Info/Public.</summary>
    Online,

    /// <summary>Probe failed (connection/timeout/5xx).</summary>
    Offline,

    /// <summary>Authenticating as the configured user failed with 401/403.</summary>
    InvalidCredentials
}

/// <summary>
/// In-memory per-instance status. Written by the probe loop and by request-outcome
/// reports; read by the status API. The password is never part of this type.
/// </summary>
public class InstanceStatus
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceStatus"/> class.
    /// </summary>
    /// <param name="alias">Instance alias from the config.</param>
    public InstanceStatus(string alias)
    {
        Alias = alias;
        Channels = Array.Empty<string>();
    }

    /// <summary>Gets the instance alias from the config.</summary>
    public string Alias { get; private set; } = string.Empty;

    /// <summary>Gets or sets the current reachability state.</summary>
    public FederationInstanceState State { get; set; }

    /// <summary>Gets or sets the last error category (fixed sanitized string, never raw exception text).</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the UTC time of the last probe.</summary>
    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>Gets or sets the UTC time of the last successful authenticated request (null if none succeeded yet).</summary>
    public DateTimeOffset? LastAuthenticatedUtc { get; set; }

    /// <summary>Gets or sets the remote server name from public info.</summary>
    public string? RemoteServerName { get; set; }

    /// <summary>Gets or sets the remote server version from public info.</summary>
    public string? RemoteVersion { get; set; }

    /// <summary>Gets or sets the configured remote base URL (trimmed; set at registration).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Gets or sets the configured remote username (set at registration; never the password).</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the names of the channels this instance contributed (non-null).</summary>
    public IReadOnlyList<string> Channels { get; set; }

    /// <summary>Gets or sets the consecutive probe failure count (drives the backoff step).</summary>
    internal int ConsecutiveFailures { get; set; }
}

/// <summary>
/// Background reachability probe for every configured instance. Invariant 11:
/// the probe is an unauthenticated GET /System/Info/Public — it never calls
/// AuthenticateByName and never sends an ApiKey, so it must not be able to
/// create device rows or trip the remote's failed-login counter.
/// </summary>
public class FederationStatusService : IHostedService
{
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(240),
        TimeSpan.FromSeconds(480),
        TimeSpan.FromSeconds(600)
    };

    private readonly Func<PluginConfiguration> _configProvider;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, InstanceStatus> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextProbe = new(StringComparer.Ordinal); // alias → earliest next probe
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationStatusService"/> class.
    /// </summary>
    /// <param name="configProvider">Provides the loaded configuration (fixed at startup).</param>
    /// <param name="httpClient">Dedicated probe client (5 s timeout, no auth header).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Time source (injectable for deterministic backoff tests).</param>
    /// <param name="delay">Loop delay (injectable; defaults to real Task.Delay).</param>
    public FederationStatusService(
        Func<PluginConfiguration> configProvider,
        HttpClient httpClient,
        ILogger logger,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _configProvider = configProvider;
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    /// <summary>Registers one instance (called once at startup with the loaded config; gives rows their URL/username).</summary>
    /// <param name="instance">The configured instance.</param>
    public void RegisterInstance(FederationInstance instance)
        => _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a)
        {
            ServerUrl = instance.ServerUrl.TrimEnd('/'),
            Username = instance.Username
        });

    /// <summary>Records the channel names an instance contributes (called by the factory after channel creation).</summary>
    /// <param name="alias">Instance alias.</param>
    /// <param name="channelNames">Channel names contributed by this instance.</param>
    public void RegisterChannels(string alias, string[] channelNames)
        => _states.GetOrAdd(alias, a => new InstanceStatus(a)).Channels = channelNames;

    /// <summary>Gets whether <paramref name="alias"/> was registered at startup (has a configured instance).</summary>
    /// <param name="alias">Instance alias.</param>
    /// <returns>True when the alias is tracked.</returns>
    public bool IsTracked(string? alias) => !string.IsNullOrWhiteSpace(alias) && _states.ContainsKey(alias!);

    /// <summary>Records an authenticated-call outcome from a RemoteApiClient (Task 1 reporter).</summary>
    /// <param name="alias">Instance alias.</param>
    /// <param name="report">The outcome.</param>
    public void ReportOutcome(string alias, RemoteApiReport report)
    {
        var state = _states.GetOrAdd(alias, a => new InstanceStatus(a));
        if (report.Succeeded)
        {
            state.State = FederationInstanceState.Online; // an authenticated call succeeded → remote is up and credentials work
            state.LastError = null;
        }
        else if (report.AuthFailed)
        {
            state.State = FederationInstanceState.InvalidCredentials;
            state.LastError = report.Error;
        }
        else
        {
            // Any other failure (unreachable, timeout, 5xx/4xx from the remote):
            // record it. InvalidCredentials is sticky — only a success clears it.
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Offline;
            }

            state.LastError = report.Error;
        }

        state.LastAuthenticatedUtc = report.Succeeded ? _clock() : state.LastAuthenticatedUtc;
    }

    /// <summary>Gets a snapshot of all instance states (status API reads this).</summary>
    /// <returns>One entry per known instance, ordered by alias.</returns>
    public IReadOnlyList<InstanceStatus> Snapshot() => _states.Values.OrderBy(s => s.Alias).ToList();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ProbeLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected at shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Probes one instance once (testable entry point of the loop body):
    /// unauthenticated GET /System/Info/Public, updates state and the due-time map.
    /// </summary>
    /// <param name="instance">The instance to probe.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the probe is done.</returns>
    public Task ProbeOnceAsync(FederationInstance instance, CancellationToken ct)
        => ProbeInstanceAsync(instance, ct);

    /// <summary>Gets when the given alias is due for its next probe (test surface for backoff assertions).</summary>
    /// <param name="alias">Instance alias.</param>
    /// <returns>The earliest allowed next probe time, or null if never probed.</returns>
    public DateTimeOffset? NextProbeDue(string alias)
        => _nextProbe.TryGetValue(alias, out var due) ? due : null;

    /// <summary>Probes one instance; updates the state and the per-alias due time.</summary>
    /// <param name="instance">The instance to probe.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the probe is done.</returns>
    private async Task ProbeInstanceAsync(FederationInstance instance, CancellationToken ct)
    {
        var serverUrl = instance.ServerUrl.TrimEnd('/');
        try
        {
            // Invariant 11: unauthenticated probe only.
            using var response = await _httpClient.GetAsync(serverUrl + "/System/Info/Public", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A non-2xx from a reachable remote is "remote returned {code}",
                // distinct from "unreachable" (no connection at all). The
                // two-arg ctor sets StatusCode (the one-arg one does not).
                throw new FederationException($"Remote server returned {(int)response.StatusCode} for {serverUrl}/System/Info/Public", (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var info = await JsonSerializer.DeserializeAsync<PublicSystemInfo>(stream, FederationJsonOptions.Options, ct).ConfigureAwait(false);
            var state = _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a));
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Online;
            }

            state.RemoteServerName = info?.ServerName;
            state.RemoteVersion = info?.Version;
            state.LastCheckedUtc = _clock();
            state.ConsecutiveFailures = 0;
            _nextProbe[instance.Alias] = _clock() + BackoffSteps[0];
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var state = _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a));
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Offline;
            }

            state.LastError = ex switch
            {
                FederationException { StatusCode: not null } f => $"remote returned {f.StatusCode}",
                TaskCanceledException => "timeout",
                HttpRequestException => "unreachable",
                _ => "probe failed"
            };
            state.LastCheckedUtc = _clock();
            // Back off on every failure kind (refused, timeout, 5xx), not just some:
            // a non-2xx remote that never recovers must not be re-probed every 5 s.
            var step = Math.Min(state.ConsecutiveFailures, BackoffSteps.Length - 1);
            _nextProbe[instance.Alias] = _clock() + BackoffSteps[step];
            state.ConsecutiveFailures++;
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        // Per-instance offset so instances don't synchronize (spec): a never-seen
        // alias gets its due time seeded with a hash-derived offset and is
        // skipped this cycle (first real probe on the next one).
        while (!ct.IsCancellationRequested)
        {
            foreach (var instance in _configProvider().Instances.Where(i => !string.IsNullOrWhiteSpace(i.ServerUrl)))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!_nextProbe.TryGetValue(instance.Alias, out var due))
                {
                    var offset = TimeSpan.FromSeconds(Math.Abs(instance.Alias.GetHashCode()) % 15);
                    _nextProbe[instance.Alias] = _clock() + offset;
                    continue;
                }

                if (_clock() < due)
                {
                    continue;
                }

                try
                {
                    await ProbeInstanceAsync(instance, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }

            await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); // loop granularity; per-instance due times gate the probes
        }
    }
}
