using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Logs into each configured federation instance and creates one channel per supported remote library.
/// </summary>
public class FederationChannelFactory
{
    private static readonly HashSet<CollectionType> SupportedCollectionTypes = new()
    {
        CollectionType.movies,
        CollectionType.tvshows,
        CollectionType.music
    };

    private readonly Func<RemoteApiClient> _clientFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _loginRetryDelay;
    private readonly FederationViewsCache? _viewsCache;
    private readonly Action<RemoteApiClient, string>? _clientSetup;
    private readonly FederationStatusService? _status;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationChannelFactory"/> class.
    /// </summary>
    /// <param name="clientFactory">Creates one remote API client (with its own HttpClient) per instance.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loginRetryDelay">Delay between login attempts.</param>
    /// <param name="viewsCache">Optional per-instance views cache; null disables cache write and fallback.</param>
    /// <param name="clientSetup">Optional hook invoked right after each client is created (e.g. wiring the outcome reporter).</param>
    /// <param name="status">Optional status service to register instances and channel names on.</param>
    public FederationChannelFactory(
        Func<RemoteApiClient> clientFactory,
        ILogger logger,
        TimeSpan? loginRetryDelay = null,
        FederationViewsCache? viewsCache = null,
        Action<RemoteApiClient, string>? clientSetup = null,
        FederationStatusService? status = null)
    {
        _clientFactory = clientFactory;
        _logger = logger;
        _loginRetryDelay = loginRetryDelay ?? TimeSpan.FromSeconds(3);
        _viewsCache = viewsCache;
        _clientSetup = clientSetup;
        _status = status;
    }

    private const int MaxLoginAttempts = 4;

    /// <summary>
    /// Logs into every configured instance and builds one channel per remote library
    /// with a supported collection type (movies, tvshows, music).
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel list; failed instances are skipped.</returns>
    public async Task<IReadOnlyList<FederationChannel>> CreateChannelsAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var channels = new List<FederationChannel>();

        // The host keys channel entities on the channel name (GetInternalChannelId);
        // duplicate names across all instances collapse into one entity and the
        // other library becomes unreachable. Names are disambiguated globally.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in configuration.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Alias)
                || string.IsNullOrWhiteSpace(instance.ServerUrl)
                || string.IsNullOrWhiteSpace(instance.Username)
                || string.IsNullOrWhiteSpace(instance.Password))
            {
                _logger.LogWarning("Federation: skipping instance with missing Alias/ServerUrl/Username/Password");
                continue;
            }

            // One client per instance: an HttpClient can only hold one BaseAddress
            // and each instance keeps its own login access token.
            var client = _clientFactory();
            _clientSetup?.Invoke(client, instance.Alias); // wire the outcome reporter (Task 6)

            // The config may carry a trailing slash (it is a URL, not a path).
            // Every URL built for this instance (images, streams) derives from the
            // trimmed base: a second "/" after the host makes the remote return 404.
            var serverUrl = instance.ServerUrl.TrimEnd('/');

            Exception? lastError = null;
            QueryResult<BaseItemDto>? views = null;

            try
            {
                // Login and the views fetch both run during DI setup, before the
                // app is ready. A remote that is still booting answers 503 (app
                // gate) or refuses the connection entirely, so both steps are
                // retried on transient failures. (A retry after a failed views
                // fetch re-logs in: harmless, the previous token stays valid.)
                Guid remoteUserId;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        remoteUserId = await client.ConfigureAsync(instance.Alias, serverUrl, instance.Username, instance.Password, cancellationToken).ConfigureAwait(false);
                        views = await client.GetViewsAsync(remoteUserId, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (attempt < MaxLoginAttempts
                        && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                        && !IsDefinitiveAuthFailure(ex))
                    {
                        lastError = ex;
                        _logger.LogWarning(ex,
                            "Federation: cannot connect to {Alias} ({Url}), attempt {Attempt} of {Max}; retrying in {Delay}s",
                            instance.Alias, serverUrl, attempt, MaxLoginAttempts, (int)_loginRetryDelay.TotalSeconds);
                        await Task.Delay(_loginRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // The retry loop exhausted its attempts (or a definitive failure
                // escaped): remember the last error for the cache fallback below.
                lastError = ex;
            }

            if (views is not null)
            {
                var channelsForInstance = new List<(string ChannelName, string RemoteLibraryId, string LibraryName, string? CollectionType)>();
                foreach (var view in views.Items.Where(v => v.Type == BaseItemKind.CollectionFolder))
                {
                    if (view.Name is null)
                    {
                        _logger.LogInformation("Federation: skipping unnamed library {LibraryId} of {Alias}", view.Id, instance.Alias);
                        continue;
                    }

                    if (view.CollectionType is null || !SupportedCollectionTypes.Contains(view.CollectionType.Value))
                    {
                        _logger.LogInformation("Federation: skipping library {Library} of {Alias} (unsupported collection type {CollectionType})", view.Name, instance.Alias, view.CollectionType);
                        continue;
                    }

                    var channelName = RegisterUniqueChannelName(seenNames, instance.Alias, view.Name, view.Id);
                    channelsForInstance.Add((channelName, view.Id.ToString("N"), view.Name, view.CollectionType.Value.ToString()));
                    channels.Add(new FederationChannel(
                        new FederationChannelOptions(instance.Alias, channelName, view.Id.ToString("N"), serverUrl),
                        client,
                        _logger));
                }

                // Only the successful startup path writes the cache (spec): it is
                // the authoritative saved-credentials view set, names included.
                _viewsCache?.Save(instance.Alias, serverUrl, instance.Username, channelsForInstance
                    .Select(c => new CachedView(c.RemoteLibraryId, c.LibraryName, c.CollectionType, c.ChannelName))
                    .ToList());
                _status?.RegisterChannels(instance.Alias, channelsForInstance.Select(c => c.ChannelName).ToArray());
            }
            else if (lastError is not null && !IsDefinitiveAuthFailure(lastError)
                     && _viewsCache?.Load(instance.Alias, serverUrl, instance.Username) is { Count: > 0 } cached)
            {
                // Transient failure (remote down at startup) with a cache: rebuild
                // the channels token-less, names from the cache (byte-stable,
                // invariant 10). Definitive 401/403 never falls back — wrong
                // credentials must not surface stale libraries (invariant 3).
                _logger.LogWarning(lastError,
                    "Federation: {Alias} ({Url}) unreachable at startup; rebuilding {Count} channels from cached views",
                    instance.Alias, serverUrl, cached.Count);
                var builtNames = new List<string>(cached.Count);
                foreach (var view in cached)
                {
                    if (!seenNames.Add(view.ChannelName))
                    {
                        continue; // defensive: the cache already contains unique names
                    }

                    channels.Add(new FederationChannel(
                        new FederationChannelOptions(instance.Alias, view.ChannelName, view.RemoteLibraryId, serverUrl),
                        client,
                        _logger));
                    builtNames.Add(view.ChannelName);
                }

                // Prepare only if the client has no token yet (all logins failed:
                // remote down). If a login had succeeded (views fetch failed), the
                // token is already set and re-rolling the DeviceId would invalidate
                // it (invariant 2).
                if (string.IsNullOrEmpty(client.AccessToken))
                {
                    client.Prepare(instance.Alias, serverUrl, instance.Username, instance.Password);
                }

                _status?.RegisterChannels(instance.Alias, builtNames.ToArray());
            }
            else if (lastError is not null)
            {
                _logger.LogError(lastError, "Federation: cannot connect to {Alias} ({Url})", instance.Alias, serverUrl);
            }
        }

        return channels;
    }

    /// <summary>
    /// Computes the unique channel name for one library, registering every
    /// candidate until it is unique (invariant 4): with the same alias repeated,
    /// the view ids (and hence the first suffix) collide too, so fall back to a
    /// counter. Shared by the live and the cache-fallback paths.
    /// </summary>
    /// <param name="seenNames">Global set of already-registered channel names.</param>
    /// <param name="alias">Instance alias.</param>
    /// <param name="libraryName">Remote library name.</param>
    /// <param name="viewId">Remote library item id.</param>
    /// <returns>The unique channel name, registered in <paramref name="seenNames"/>.</returns>
    private string RegisterUniqueChannelName(HashSet<string> seenNames, string alias, string libraryName, Guid viewId)
    {
        var channelName = $"{alias} · {libraryName}";
        if (!seenNames.Add(channelName))
        {
            var idHex = viewId.ToString("N");
            var n = 1;
            string candidate;
            do
            {
                candidate = n == 1
                    ? $"{channelName} ({idHex[..8]})"
                    : $"{channelName} ({idHex[..8]} #{n})";
                n++;
            }
            while (!seenNames.Add(candidate));

            _logger.LogWarning("Federation: duplicate library name {Name} of {Alias}; using {ChannelName}", libraryName, alias, candidate);
            channelName = candidate;
        }

        return channelName;
    }

    /// <summary>
    /// A 401/403 from the remote is a definitive credential problem: retrying
    /// cannot change the outcome, and every attempt counts against the remote
    /// user's failed-login counter (which can lock the account out when the
    /// remote has lockout configured).
    /// </summary>
    private static bool IsDefinitiveAuthFailure(Exception ex)
        => ex is FederationException { StatusCode: 401 or 403 };
}
