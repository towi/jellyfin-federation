using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Maps remote BaseItemDto to ChannelItemInfo.
/// </summary>
public static class ItemMapper
{
    /// <summary>
    /// Converts one remote item DTO into channel item info.
    /// </summary>
    /// <param name="dto">Remote item.</param>
    /// <param name="baseUrl">Remote base URL without trailing slash.</param>
    /// <param name="accessToken">Access token from the remote login.</param>
    /// <returns>Channel item info.</returns>
    public static ChannelItemInfo ToChannelItemInfo(BaseItemDto dto, string baseUrl, string accessToken)
    {
        var remoteId = dto.Id.ToString("N");
        var info = new ChannelItemInfo
        {
            Id = remoteId,
            Name = dto.Name ?? remoteId,
            Type = dto.IsFolder == true ? ChannelItemType.Folder : ChannelItemType.Media,
            Overview = dto.Overview,
            CommunityRating = dto.CommunityRating,
            RunTimeTicks = dto.RunTimeTicks,
            OfficialRating = dto.OfficialRating,
            OriginalTitle = dto.OriginalTitle,
            Genres = dto.Genres is null ? new List<string>() : new List<string>(dto.Genres),
            Studios = NameGuidPairsToNames(dto.Studios),
            Tags = new List<string>(),
            ProviderIds = dto.ProviderIds is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dto.ProviderIds, StringComparer.OrdinalIgnoreCase),
            PremiereDate = dto.PremiereDate,
            ProductionYear = dto.ProductionYear,
            IndexNumber = dto.IndexNumber,
            ParentIndexNumber = dto.ParentIndexNumber,
            SeriesName = dto.SeriesName,
            DateCreated = dto.DateCreated,
            // PersonInfo.Id stays the generated value: the host resolves people by
            // name (PeopleRepository dedupes, SavePeopleMetadataAsync reuses local
            // person entities) and a remote person id would be meaningless here.
            People = MapPeople(dto.People),
            Artists = dto.Artists is null ? new List<string>() : new List<string>(dto.Artists)
        };

        if (dto.ImageTags is not null && dto.ImageTags.TryGetValue(ImageType.Primary, out var tag))
        {
            // Image is fetched server-side by the client server; ApiKey in query is the required (non-legacy) way for item images.
            info.ImageUrl = $"{baseUrl}/Items/{remoteId}/Images/Primary?tag={tag}&ApiKey={Uri.EscapeDataString(accessToken)}";
        }

        switch (dto.Type)
        {
            case BaseItemKind.Series:
                info.FolderType = ChannelFolderType.Series;
                break;
            case BaseItemKind.Season:
                info.FolderType = ChannelFolderType.Season;
                break;
            case BaseItemKind.MusicAlbum:
                info.FolderType = ChannelFolderType.MusicAlbum;
                break;
            case BaseItemKind.MusicArtist:
                info.FolderType = ChannelFolderType.MusicArtist;
                break;
            case BaseItemKind.PhotoAlbum:
                info.FolderType = ChannelFolderType.PhotoAlbum;
                break;
            case BaseItemKind.Episode:
                info.ContentType = ChannelMediaContentType.Episode;
                break;
            case BaseItemKind.Movie:
                info.ContentType = ChannelMediaContentType.Movie;
                break;
        }

        if (dto.Type is BaseItemKind.Audio)
        {
            info.MediaType = ChannelMediaType.Audio;
            info.AlbumArtists = NameGuidPairsToNames(dto.AlbumArtists);
        }
        else if (dto.Type is BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.Video or BaseItemKind.MusicVideo or BaseItemKind.Trailer)
        {
            info.MediaType = ChannelMediaType.Video;
        }

        // MediaSources: set at playback time via IRequiresMediaInfoCallback, not here (keep hydrated entities lean).

        return info;
    }

    private static List<PersonInfo> MapPeople(BaseItemPerson[]? people)
    {
        var result = new List<PersonInfo>();
        if (people is null)
        {
            return result;
        }

        foreach (var person in people)
        {
            result.Add(new PersonInfo
            {
                Name = person.Name,
                Role = person.Role,
                Type = person.Type
            });
        }

        return result;
    }

    private static List<string> NameGuidPairsToNames(NameGuidPair[]? pairs)
    {
        var result = new List<string>();
        if (pairs is null)
        {
            return result;
        }

        foreach (var pair in pairs)
        {
            result.Add(pair.Name);
        }

        return result;
    }
}
