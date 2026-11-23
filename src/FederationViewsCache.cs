using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// One cached remote library: enough to rebuild a channel without a live login.
/// </summary>
/// <param name="RemoteLibraryId">Remote library item id (hex string, N-format).</param>
/// <param name="LibraryName">Remote library name ("Filme").</param>
/// <param name="CollectionType">Collection type string ("movies"/"tvshows"/"music"); null for unsupported.</param>
/// <param name="ChannelName">Final channel name including dedup suffix, byte-stable across restarts.</param>
public sealed record CachedView(string RemoteLibraryId, string LibraryName, string? CollectionType, string ChannelName);

/// <summary>
/// Persists the last successfully fetched views per federation instance so channels
/// can be rebuilt when the remote is unreachable at startup (spec phase 3).
/// One JSON file per instance under {DataPath}/federation/{key}/views.json.
/// </summary>
public class FederationViewsCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _rootPath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationViewsCache"/> class.
    /// </summary>
    /// <param name="dataPath">Host data path (IApplicationPaths.DataPath).</param>
    /// <param name="logger">Logger.</param>
    public FederationViewsCache(string dataPath, ILogger logger)
    {
        _rootPath = Path.Combine(dataPath, "federation");
        _logger = logger;
    }

    /// <summary>
    /// Loads the cached views of one instance. Missing key or corrupt file → empty list.
    /// </summary>
    /// <param name="alias">Instance alias.</param>
    /// <param name="serverUrl">Remote base URL (any trailing slash tolerated).</param>
    /// <param name="username">Configured remote username (part of the key).</param>
    /// <returns>The cached views, empty when none are usable.</returns>
    public IReadOnlyList<CachedView> Load(string alias, string serverUrl, string username)
    {
        var path = FilePath(alias, serverUrl, username);
        if (!File.Exists(path))
        {
            return Array.Empty<CachedView>();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var views = JsonSerializer.Deserialize<List<CachedView>>(stream, Options);
            return views ?? new List<CachedView>();
        }
        catch (Exception ex)
        {
            // A corrupt cache must never break startup — treat as absent.
            _logger.LogWarning(ex, "Federation: ignoring corrupt views cache {Path}", path);
            return Array.Empty<CachedView>();
        }
    }

    /// <summary>
    /// Persists the views of one instance (atomic: temp file + move).
    /// Never throws: a failed write is logged and swallowed.
    /// </summary>
    /// <param name="alias">Instance alias.</param>
    /// <param name="serverUrl">Remote base URL.</param>
    /// <param name="username">Configured remote username (part of the key).</param>
    /// <param name="views">Views to store.</param>
    public void Save(string alias, string serverUrl, string username, IReadOnlyList<CachedView> views)
    {
        var path = FilePath(alias, serverUrl, username);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(views, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A local disk problem must never kill channel creation or be
            // misdiagnosed as a remote-connectivity failure.
            _logger.LogWarning(ex, "Federation: cannot write views cache {Path}; continuing without cache", path);
        }
    }

    private string FilePath(string alias, string serverUrl, string username)
    {
        var raw = alias.Trim() + "|" + serverUrl.Trim().TrimEnd('/') + "|" + username;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..16];
        return Path.Combine(_rootPath, key, "views.json");
    }
}
