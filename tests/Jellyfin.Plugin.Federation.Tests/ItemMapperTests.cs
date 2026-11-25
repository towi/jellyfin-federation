using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ItemMapperTests
{
    private const string BaseUrl = "https://remote.example.com:8920";
    private const string AccessToken = "testtoken123";

    [Fact]
    public void Map_Movie_BecomesMediaMovie()
    {
        var dto = new BaseItemDto
        {
            Id = new Guid("a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90"),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false,
            Overview = "Overview",
            ProductionYear = 2021,
            RunTimeTicks = 72000000000,
            CommunityRating = 7.5f,
            Genres = new[] { "Drama" },
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "imgtag1" }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        Assert.Equal(dto.Id.ToString("N"), info.Id);
        Assert.Equal(ChannelItemType.Media, info.Type);
        Assert.Equal(ChannelMediaType.Video, info.MediaType);
        Assert.Equal(ChannelMediaContentType.Movie, info.ContentType);
        Assert.Equal("Remote Movie", info.Name);
        Assert.Contains("imgtag1", info.ImageUrl, StringComparison.Ordinal);
        Assert.Contains("ApiKey=testtoken123", info.ImageUrl, StringComparison.Ordinal);
        Assert.Equal(7.5f, info.CommunityRating);
    }

    [Fact]
    public void Map_Series_BecomesFolderSeries()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Series",
            Type = BaseItemKind.Series,
            IsFolder = true,
            ChildCount = 2
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        Assert.Equal(ChannelItemType.Folder, info.Type);
        Assert.Equal(ChannelFolderType.Series, info.FolderType);
        Assert.Equal("Remote Series", info.Name);
    }

    [Fact]
    public void Map_SeasonAndEpisode_WireUpHierarchy()
    {
        var season = ItemMapper.ToChannelItemInfo(new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Season 1",
            Type = BaseItemKind.Season,
            IsFolder = true,
            ParentIndexNumber = 1
        }, BaseUrl, AccessToken);

        var episode = ItemMapper.ToChannelItemInfo(new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Ep 1",
            Type = BaseItemKind.Episode,
            IsFolder = false,
            SeriesName = "Remote Series",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            RunTimeTicks = 18000000000
        }, BaseUrl, AccessToken);

        Assert.Equal(ChannelFolderType.Season, season.FolderType);
        Assert.Equal(ChannelMediaType.Video, episode.MediaType);
        Assert.Equal(ChannelMediaContentType.Episode, episode.ContentType);
        Assert.Equal("Remote Series", episode.SeriesName);
    }

    [Fact]
    public void Map_MusicAlbumAndAudio_BecomeMusicTypes()
    {
        var album = ItemMapper.ToChannelItemInfo(new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Album",
            Type = BaseItemKind.MusicAlbum,
            IsFolder = true
        }, BaseUrl, AccessToken);

        var track = ItemMapper.ToChannelItemInfo(new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            Type = BaseItemKind.Audio,
            IsFolder = false,
            AlbumArtists = new[] { new NameGuidPair { Name = "Artist" } },
            MediaType = Jellyfin.Data.Enums.MediaType.Audio,
            RunTimeTicks = 2400000000
        }, BaseUrl, AccessToken);

        Assert.Equal(ChannelItemType.Folder, album.Type);
        Assert.Equal(ChannelFolderType.MusicAlbum, album.FolderType);
        Assert.Equal(ChannelMediaType.Audio, track.MediaType);
        Assert.Contains("Artist", track.AlbumArtists, StringComparer.Ordinal);
        Assert.Equal(2400000000, track.RunTimeTicks);
    }

    [Fact]
    public void Map_PeopleAndArtists_AreForwarded()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Movie with cast",
            Type = BaseItemKind.Movie,
            People =
            [
                new BaseItemPerson { Name = "Actor", Role = "Director", Type = PersonKind.Director },
                new BaseItemPerson { Name = "Star", Type = PersonKind.Actor }
            ],
            Artists = ["The Artist", "Another Artist"]
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        Assert.Equal(2, info.People.Count);
        Assert.Equal("Actor", info.People[0].Name);
        Assert.Equal("Director", info.People[0].Role);
        Assert.Equal(PersonKind.Director, info.People[0].Type);
        Assert.Contains("The Artist", info.Artists);
    }

    [Fact]
    public void Map_NullPeopleAndArtists_BecomeEmptyLists()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Bare item",
            Type = BaseItemKind.Movie
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        Assert.Empty(info.People);
        Assert.Empty(info.Artists);
    }

    [Fact]
    public void Map_MediaItem_ContainsDisplayOnlyMediaSource()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false,
            Container = "mkv",
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Container = "mkv",
                    MediaStreams = new[]
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Codec = "hevc",
                            Width = 1920,
                            Height = 1080,
                            Path = "/media/movie.mkv",
                            Index = 0
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Codec = "eac3",
                            Channels = 6,
                            Path = "/media/movie.mkv",
                            Index = 1
                        }
                    }
                }
            }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var source = Assert.Single(info.MediaSources);

        // The host persists this source and sets item.Path from it: a stream URL
        // or remote disk path here would persist the remote access token / disk
        // layout into the local database.
        Assert.Null(source.Id);
        Assert.Null(source.Path);
        // Placeholder keeps it out of the host's playback list (SortMediaSources
        // filters it) while the item info panel still renders it and its streams.
        Assert.Equal(MediaSourceType.Placeholder, source.Type);
        Assert.Equal("mkv", source.Container);
        // Mirrors the dynamic source: direct play is off so no client can open
        // the remote URL directly.
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);

        var streams = source.MediaStreams;
        Assert.NotNull(streams);
        Assert.Equal(2, streams.Count);
        Assert.Equal("hevc", streams[0].Codec);
        Assert.Equal("eac3", streams[1].Codec);
        foreach (var stream in streams)
        {
            Assert.Null(stream.Path);
            Assert.Null(stream.DeliveryUrl);
        }
    }

    [Fact]
    public void Map_MediaItem_FallsBackToItemLevelContainerWithoutRemoteSource()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false,
            Container = "mp4",
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 }
            }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var source = Assert.Single(info.MediaSources);

        Assert.Equal("mp4", source.Container);
        var streams = source.MediaStreams;
        Assert.NotNull(streams);
        Assert.Single(streams);
        Assert.Equal("hevc", streams[0].Codec);
    }

    [Fact]
    public void Map_MediaItem_PrefersRemoteSourceContainerAndSize()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false,
            Container = "mp4",
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Container = "mov,mp4,m4a,3gp,3g2,mj2",
                    Size = 123456789,
                    MediaStreams = new[]
                    {
                        new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 }
                    }
                }
            }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var source = Assert.Single(info.MediaSources);

        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", source.Container);
        Assert.Equal(123456789, source.Size);
        Assert.NotNull(source.MediaStreams);
    }

    [Fact]
    public void Map_MediaItem_DropsExternalSubtitlesAndScrubsDto()
    {
        var external = new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            IsExternal = true,
            Path = "/subs/movie.en.srt",
            Index = 2
        };

        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false,
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    MediaStreams = new[]
                    {
                        new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 },
                        external
                    }
                }
            }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var source = Assert.Single(info.MediaSources);
        var streams = source.MediaStreams;

        // External subtitle streams point at files on the remote disk.
        Assert.NotNull(streams);
        Assert.Single(streams);
        Assert.Equal(MediaStreamType.Video, streams[0].Type);

        // The DTO comes from the host-shared deserialization pool: it must not be
        // mutated by the mapping.
        Assert.Equal("/subs/movie.en.srt", external.Path);
    }

    [Fact]
    public void Map_MediaItem_WithoutStreams_HasNoMediaStreams()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var source = Assert.Single(info.MediaSources);

        Assert.Empty(source.MediaStreams);
        Assert.Null(source.Bitrate);
    }

    [Fact]
    public void Map_Folder_KeepsMediaSourcesEmpty()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Series",
            Type = BaseItemKind.Series,
            IsFolder = true,
            MediaSources = new[]
            {
                new MediaSourceInfo { Container = "mkv" }
            }
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        Assert.Empty(info.MediaSources);
    }

    [Fact]
    public void Map_SetsFixedDateModifiedForDisplaySourceBackfill()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Remote Movie",
            Type = BaseItemKind.Movie,
            IsFolder = false
        };

        var info = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);
        var again = ItemMapper.ToChannelItemInfo(dto, BaseUrl, AccessToken);

        // One-shot backfill of the persisted display source for items hydrated
        // before it existed: the host's "info.DateModified > item.DateModified"
        // trigger re-saves MediaSources once, then the comparison saturates.
        Assert.Equal(DateTimeKind.Utc, info.DateModified.Kind);
        Assert.True(info.DateModified > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(info.DateModified, again.DateModified);
    }
}
