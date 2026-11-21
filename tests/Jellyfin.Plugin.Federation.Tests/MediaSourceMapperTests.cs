using System;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class MediaSourceMapperTests
{
    private const string BaseUrl = "https://remote.example.com:8920";
    private const string AccessToken = "testtoken123";
    private const string RemoteItemId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Fact]
    public void Map_SetsRemoteStreamUrlAndSafeFlags()
    {
        var remoteSource = new MediaSourceInfo
        {
            Id = "abc123",
            Path = "/media/movie.mkv",
            Protocol = MediaProtocol.File,
            Container = "mkv",
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0, Width = 1920, Height = 1080 },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "eac3", Index = 1, Channels = 6 }
            },
            RunTimeTicks = 72000000000
        };

        var source = MediaSourceMapper.ToMediaSourceInfo(remoteSource, RemoteItemId, BaseUrl, AccessToken, isAudio: false);

        Assert.StartsWith(BaseUrl + "/Videos/", source.Path, StringComparison.Ordinal);
        Assert.Contains("/" + RemoteItemId + "/stream", source.Path);
        Assert.EndsWith("&Static=true&ApiKey=testtoken123", source.Path, StringComparison.Ordinal);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.IsRemote);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal("mkv", source.Container);
        Assert.Equal(2, source.MediaStreams!.Count);
        Assert.False(source.RequiresOpening);
    }

    [Fact]
    public void Map_AudioUsesAudioEndpoint()
    {
        var remoteSource = new MediaSourceInfo
        {
            Id = "abc123", Path = "/media/track.flac", Protocol = MediaProtocol.File, Container = "flac"
        };

        var source = MediaSourceMapper.ToMediaSourceInfo(remoteSource, RemoteItemId, BaseUrl, AccessToken, isAudio: true);

        Assert.StartsWith(BaseUrl + "/Audio/", source.Path, StringComparison.Ordinal);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
    }

    [Fact]
    public void Map_KeepsRemoteSourceId()
    {
        var remoteSource = new MediaSourceInfo
        {
            Id = "abc123",
            Path = "/media/movie.mkv",
            Protocol = MediaProtocol.File,
            Container = "mkv"
        };

        var source = MediaSourceMapper.ToMediaSourceInfo(remoteSource, RemoteItemId, BaseUrl, AccessToken, isAudio: false);

        // The id is passed through so the client round-trips it back on stream requests.
        Assert.Equal(remoteSource.Id, source.Id);
        // MediaSourceId in the URL references the remote media source id, not the item id.
        Assert.Contains("?MediaSourceId=" + Uri.EscapeDataString(remoteSource.Id), source.Path);
    }

    [Fact]
    public void Map_DropsExternalSubtitleStreamsAndTheirDefaultSelection()
    {
        var remoteSource = new MediaSourceInfo
        {
            Id = "abc123",
            Path = "/media/movie.mkv",
            Protocol = MediaProtocol.File,
            Container = "mkv",
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "eac3", Index = 1 },
                new MediaStream { Type = MediaStreamType.Subtitle, Codec = "subrip", Index = 2, IsExternal = false },
                new MediaStream { Type = MediaStreamType.Subtitle, Codec = "subrip", Index = 3, IsExternal = true, Path = "/media/movie.en.srt" }
            },
            DefaultSubtitleStreamIndex = 3
        };

        var source = MediaSourceMapper.ToMediaSourceInfo(remoteSource, RemoteItemId, BaseUrl, AccessToken, isAudio: false);

        Assert.Equal(3, source.MediaStreams!.Count);
        Assert.DoesNotContain(source.MediaStreams, s => s.IsExternal);
        Assert.Null(source.DefaultSubtitleStreamIndex);
    }

    [Fact]
    public void Map_KeepsEmbeddedDefaultSubtitleSelection()
    {
        var remoteSource = new MediaSourceInfo
        {
            Id = "abc123",
            Path = "/media/movie.mkv",
            Protocol = MediaProtocol.File,
            Container = "mkv",
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 },
                new MediaStream { Type = MediaStreamType.Subtitle, Codec = "subrip", Index = 2, IsExternal = false }
            },
            DefaultSubtitleStreamIndex = 2
        };

        var source = MediaSourceMapper.ToMediaSourceInfo(remoteSource, RemoteItemId, BaseUrl, AccessToken, isAudio: false);

        Assert.Equal(2, source.DefaultSubtitleStreamIndex);
        Assert.Equal(2, source.MediaStreams!.Count);
    }
}
