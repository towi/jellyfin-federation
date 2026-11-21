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

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationChannelFactory"/> class.
    /// </summary>
    /// <param name="clientFactory">Creates one remote API client (with its own HttpClient) per instance.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loginRetryDelay">Delay between login attempts.</param>
    public FederationChannelFactory(Func<RemoteApiClient> clientFactory, ILogger logger, TimeSpan? loginRetryDelay = null)
    {
        _clientFactory = clientFactory;
        _logger = logger;
        _loginRetryDelay = loginRetryDelay ?? TimeSpan.FromSeconds(3);
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

            // The config may carry a trailing slash (it is a URL, not a path).
            // Every URL built for this instance (images, streams) derives from the
            // trimmed base: a second "/" after the host makes the remote return 404.
            var serverUrl = instance.ServerUrl.TrimEnd('/');

            try
            {
                // Login and the views fetch both run during DI setup, before the
                // app is ready. A remote that is still booting answers 503 (app
                // gate) or refuses the connection entirely, so both steps are
                // retried on transient failures. (A retry after a failed views
                // fetch re-logs in: harmless, the previous token stays valid.)
                Guid remoteUserId;
                QueryResult<BaseItemDto> views;
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
                        _logger.LogWarning(ex,
                            "Federation: cannot connect to {Alias} ({Url}), attempt {Attempt} of {Max}; retrying in {Delay}s",
                            instance.Alias, serverUrl, attempt, MaxLoginAttempts, (int)_loginRetryDelay.TotalSeconds);
                        await Task.Delay(_loginRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }

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

                    var channelName = $"{instance.Alias} · {view.Name}";
                    if (!seenNames.Add(channelName))
                    {
                        // Register every candidate until it is unique: with the
                        // same alias repeated, the view ids (and hence the first
                        // suffix) collide too, so fall back to a counter.
                        var idHex = view.Id.ToString("N");
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

                        _logger.LogWarning("Federation: duplicate library name {Name} of {Alias}; using {ChannelName}", view.Name, instance.Alias, candidate);
                        channelName = candidate;
                    }

                    channels.Add(new FederationChannel(
                        new FederationChannelOptions(instance.Alias, channelName, view.Id.ToString("N"), serverUrl),
                        client,
                        _logger));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Federation: cannot connect to {Alias} ({Url})", instance.Alias, serverUrl);
            }
        }

        return channels;
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
