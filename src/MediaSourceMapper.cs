using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Maps a remote media source to a locally consumable one with a proxied stream URL.
/// </summary>
public static class MediaSourceMapper
{
    /// <summary>
    /// Builds the media source this server offers for playback of remote media.
    /// The stream URL points at the remote server's static stream endpoint so ffmpeg
    /// (or a remote pipe) can open it directly; the access token is passed as
    /// <c>ApiKey</c> in the query because ffmpeg does not send authentication headers.
    /// </summary>
    /// <param name="remoteSource">Media source from the remote PlaybackInfo.</param>
    /// <param name="remoteItemId">Item id on the remote server.</param>
    /// <param name="baseUrl">Remote base URL.</param>
    /// <param name="accessToken">Access token from the remote login.</param>
    /// <param name="isAudio">Whether the item is audio (endpoint /Audio vs /Videos).</param>
    /// <returns>Mapped media source.</returns>
    public static MediaSourceInfo ToMediaSourceInfo(MediaSourceInfo remoteSource, string remoteItemId, string baseUrl, string accessToken, bool isAudio)
    {
        var endpoint = isAudio ? "Audio" : "Videos";
        var streamUrl = $"{baseUrl}/{endpoint}/{remoteItemId}/stream"
            + $"?MediaSourceId={Uri.EscapeDataString(remoteSource.Id)}"
            + "&Static=true"
            + $"&ApiKey={Uri.EscapeDataString(accessToken)}";

        // External subtitle streams point at files on the *remote* disk; this server
        // cannot read those paths, so forwarding them would make subtitle selection
        // and burn-in fail. Only embedded subtitle streams are usable through a pipe.
        var streams = remoteSource.MediaStreams?
            .Where(s => s.Type != MediaStreamType.Subtitle || !s.IsExternal)
            .ToList();

        // Reset a default subtitle selection that was dropped above.
        var defaultSubtitleStreamIndex = remoteSource.DefaultSubtitleStreamIndex;
        if (defaultSubtitleStreamIndex is int index
            && streams is not null
            && streams.All(s => !(s.Type == MediaStreamType.Subtitle && s.Index == index)))
        {
            defaultSubtitleStreamIndex = null;
        }

        return new MediaSourceInfo
        {
            // Keep the remote id: NormalizeMediaSources keeps non-empty ids untouched,
            // and the client round-trips this id back on stream requests.
            Id = remoteSource.Id,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiresOpening = false,
            RequiresClosing = false,
            Container = remoteSource.Container,
            MediaStreams = streams,
            RunTimeTicks = remoteSource.RunTimeTicks,
            Size = remoteSource.Size,
            Bitrate = remoteSource.Bitrate,
            DefaultAudioStreamIndex = remoteSource.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = defaultSubtitleStreamIndex
        };
    }
}
