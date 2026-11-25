using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    /// Fixed stamp assigned to every item's <see cref="ChannelItemInfo.DateModified"/>
    /// so the host's forceUpdate path persists the info-panel display media source
    /// for items hydrated before it existed (one-shot; see the assignment below).
    /// </summary>
    private static readonly DateTime DisplaySourceBackfillStamp = new(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);

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

        // Display-only media source for the host's item info panel: it reads the
        // *static* (persisted) sources, which for channel items are normally an
        // empty stub because media info only arrives at playback time via
        // IRequiresMediaInfoCallback. The source must therefore be safe to persist
        // and safe to serve statically:
        //  - Id and Path stay null. The host normalizes the id to the local item
        //    id (keeps it in the per-user static list) and sets item.Path from the
        //    first source's path - a stream URL here would persist the remote
        //    access token and a remote disk path into the local database.
        //  - Type = Placeholder: the host's playback path filters Placeholder
        //    sources out (SortMediaSources), so the live source from
        //    IRequiresMediaInfoCallback stays the only playable source. The info
        //    panel renders Placeholder sources and their streams.
        //  - Streams are scrubbed the same way as in MediaSourceMapper: external
        //    subtitles point at files on the remote disk and are dropped.
        // Media items only: folders never carry a playable source, and their
        // ChannelItemInfo.MediaSources must stay empty (the host reads its
        // first entry for item.Path).
        if (info.Type == ChannelItemType.Media
            && info.MediaType is ChannelMediaType.Video or ChannelMediaType.Audio)
        {
            info.MediaSources.Add(CreateDisplayMediaSource(dto));
        }

        // The host persists MediaSources only when (re)creating an item or on
        // forceUpdate. Items hydrated before the display source existed would
        // otherwise never pick it up. The host's own "info.DateModified >
        // item.DateModified" trigger provides a one-shot backfill: the stamp
        // exceeds every stored date, the first pass stores the stamp, and every
        // later load compares equal (no-op).
        info.DateModified = DisplaySourceBackfillStamp;

        return info;
    }

    private static MediaSourceInfo CreateDisplayMediaSource(BaseItemDto dto)
    {
        // Prefer the remote item's own media source (from Fields=MediaSources);
        // it carries the exact container, size and streams the info panel should
        // show. Fall back to the item-level container when it is absent.
        var remote = dto.MediaSources?.FirstOrDefault();

        var source = new MediaSourceInfo
        {
            Type = MediaSourceType.Placeholder,
            Container = remote?.Container ?? dto.Container,
            Size = remote?.Size,
            RunTimeTicks = remote?.RunTimeTicks ?? dto.RunTimeTicks,
            // Playability mirrors the dynamic source from MediaSourceMapper so the
            // display source matches what playback actually offers (direct play is
            // off there: the client must not open the remote URL itself).
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true
        };

        var mediaStreams = remote?.MediaStreams ?? dto.MediaStreams;

        if (mediaStreams is not null)
        {
            // External subtitle streams point at files on the remote disk and are
            // dropped (same rule as MediaSourceMapper). Each stream is cloned
            // before scrubbing so the (host-shared) DTO instance is never mutated.
            var streams = mediaStreams
                .Where(s => s.Type != MediaStreamType.Subtitle || !s.IsExternal)
                .Select(s =>
                {
                    var clone = JsonSerializer.Deserialize<MediaStream>(
                        JsonSerializer.Serialize(s, FederationJsonOptions.Options), FederationJsonOptions.Options)
                        ?? throw new InvalidOperationException("JSON round-trip of a media stream produced null.");

                    // Remote paths/URLs are unusable from this server and would leak
                    // the remote disk layout (and token-bearing URLs) through the item
                    // API, so they are not forwarded.
                    clone.Path = null;
                    clone.DeliveryUrl = null;
                    return clone;
                })
                .ToList();

            source.MediaStreams = streams;
            source.InferTotalBitrate();
        }

        return source;
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
