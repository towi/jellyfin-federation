using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// One remote Jellyfin library exposed as a channel.
/// </summary>
public class FederationChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly RemoteApiClient _client;
    private readonly FederationChannelOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationChannel"/> class.
    /// </summary>
    /// <param name="options">Channel options.</param>
    /// <param name="client">Configured remote API client (already logged in by the factory).</param>
    /// <param name="logger">Logger; defaults to a no-op logger.</param>
    public FederationChannel(FederationChannelOptions options, RemoteApiClient client, ILogger? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Name => _options.ChannelName;

    /// <inheritdoc />
    public string Description => $"Remote library from {_options.Alias}";

    /// <inheritdoc />
    public string HomePageUrl => _options.ServerUrl;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>
    /// Bumped when the shape of the channel data changes (the info-panel display
    /// media source was added in this build), so the host's per-folder item cache
    /// (keyed by this hash) is invalidated exactly once and existing items are
    /// re-hydrated.
    /// </summary>
    private const string DataFormatSalt = "m1";

    /// <summary>
    /// Gets a hash over the stable configuration so the channel manager invalidates caches on config change.
    /// The access token is deliberately excluded: it changes on every login and would
    /// invalidate the channel item caches on each plugin restart.
    /// </summary>
    public string DataVersion
    {
        get
        {
            var raw = string.Join('|', _options.Alias, _options.RemoteLibraryId, _options.ServerUrl, DataFormatSalt);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
        => new()
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Audio, ChannelMediaType.Video },
            ContentTypes = new List<ChannelMediaContentType>
            {
                ChannelMediaContentType.Movie, ChannelMediaContentType.Episode, ChannelMediaContentType.Trailer
            },
            MaxPageSize = null,
            SupportsSortOrderToggle = false,
            SupportsContentDownloading = false
        };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // FolderId is the ExternalId of the parent (null = channel root -> the remote library itself).
        var remoteParent = query.FolderId ?? _options.RemoteLibraryId;

        // Load the full folder; ChannelManager applies paging/filter on the hydrated entities.
        QueryResult<BaseItemDto> page;
        try
        {
            page = await _client.GetItemsAsync(remoteParent, startIndex: null, limit: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The channel manager swallows provider exceptions as empty results,
            // expecting the provider to have logged the failure itself. Timeouts
            // surface as TaskCanceledException and are logged like any error.
            _logger.LogError(ex, "Federation: cannot load items of {Channel} (remote parent {ParentId})", _options.ChannelName, remoteParent);
            throw;
        }

        var items = page.Items
            .Where(i => i.Type != BaseItemKind.CollectionFolder
                && !(i.IsFolder == true && i.Type == BaseItemKind.Folder))
            .Select(i => ItemMapper.ToChannelItemInfo(i, _options.ServerUrl, _client.AccessToken))
            .ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = page.TotalRecordCount
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        PlaybackInfoResponse playbackInfo;
        try
        {
            playbackInfo = await _client.GetPlaybackInfoAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogError(ex, "Federation: cannot get playback info of {Channel} (remote item {ItemId})", _options.ChannelName, id);
            throw;
        }

        if (playbackInfo.MediaSources is null)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        return playbackInfo.MediaSources
            .Select(s => MediaSourceMapper.ToMediaSourceInfo(s, id, _options.ServerUrl, _client.AccessToken, IsAudioSource(s)))
            .ToList();
    }

    /// <summary>
    /// Returns true if the source has at least one media stream and none of them is a video stream.
    /// </summary>
    private static bool IsAudioSource(MediaSourceInfo source)
        => source.MediaStreams is { Count: > 0 }
            && source.MediaStreams.All(s => s.Type != MediaStreamType.Video);

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Enumerable.Empty<ImageType>();
}
