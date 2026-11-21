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
}
