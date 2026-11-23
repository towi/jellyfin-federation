using System;
using System.IO;
using Jellyfin.Plugin.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationViewsCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var cache = new FederationViewsCache(_dir, NullLogger.Instance);
        var views = new[]
        {
            new CachedView("f1e2d3c4b5a60718293a4b5c6d7e8f90", "Filme", "movies", "paul · Filme"),
            new CachedView("f1e2d3c4b5a60718293a4b5c6d7e8f91", "Serien", "tvshows", "paul · Serien (f1e2d3c4)")
        };

        cache.Save("paul", "https://remote.example.com:8920", "paul", views);
        var loaded = cache.Load("paul", "https://remote.example.com:8920", "paul");

        Assert.Equal(views, loaded);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var cache = new FederationViewsCache(_dir, NullLogger.Instance);

        Assert.Empty(cache.Load("paul", "https://remote.example.com:8920", "paul"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var cache = new FederationViewsCache(_dir, NullLogger.Instance);
        cache.Save("paul", "https://remote.example.com:8920", "paul", Array.Empty<CachedView>());
        var path = Directory.GetFiles(_dir, "views.json", SearchOption.AllDirectories)[0];
        File.WriteAllText(path, "{ not json");

        Assert.Empty(cache.Load("paul", "https://remote.example.com:8920", "paul"));
    }

    [Fact]
    public void FilePath_IgnoresTrailingSlash_DoesNotDuplicateEntries()
    {
        // Invariant 1 in config form: a trailing slash in the URL must not create a
        // second cache entry.
        var cache = new FederationViewsCache(_dir, NullLogger.Instance);
        cache.Save("paul", "https://remote.example.com:8920/", "paul", new[] { new CachedView("a", "Filme", "movies", "paul · Filme") });

        Assert.Single(cache.Load("paul", "https://remote.example.com:8920", "paul"));
    }

    [Fact]
    public void Load_DifferentAliasOrUrl_IsIsolated()
    {
        var cache = new FederationViewsCache(_dir, NullLogger.Instance);
        cache.Save("paul", "https://remote.example.com:8920", "paul", new[] { new CachedView("a", "Filme", "movies", "paul · Filme") });

        Assert.Empty(cache.Load("paul", "https://other.example.com:8920", "paul"));
        Assert.Empty(cache.Load("paulzwei", "https://remote.example.com:8920", "paul"));
        // Credential rotation: a different username is a different cache entry.
        Assert.Empty(cache.Load("paul", "https://remote.example.com:8920", "otheruser"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
