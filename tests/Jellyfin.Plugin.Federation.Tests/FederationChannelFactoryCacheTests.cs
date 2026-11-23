using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationChannelFactoryCacheTests
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private const string BaseUrl = "https://remote.example.com:8920";

    private static FederationInstance CreateValidInstance(string alias = "paul")
        => new() { Alias = alias, ServerUrl = BaseUrl, Username = "paul", Password = "secret" };

    private static PluginConfiguration CreateConfig(params FederationInstance[] instances)
    {
        var config = new PluginConfiguration();
        config.Instances.AddRange(instances);
        return config;
    }

    private static FakeHttpMessageHandler CreateHandler()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            File.ReadAllText("Data/views-response.json"));
        return handler;
    }

    [Fact]
    public async Task CreateChannels_WritesCacheOnSuccess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FederationViewsCache(dir, Logger);
            var handler = CreateHandler();
            var factory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(handler)), Logger, TimeSpan.FromMilliseconds(1), cache);

            var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            Assert.Equal(3, channels.Count);
            var cached = cache.Load("paul", BaseUrl, "paul");
            Assert.Equal(3, cached.Count);
            Assert.Contains(cached, c => c.ChannelName == "paul · Filme" && c.RemoteLibraryId == "f1e2d3c4b5a60718293a4b5c6d7e8f90");
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task CreateChannels_TransientFailureWithCache_RebuildsFromCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FederationViewsCache(dir, Logger);
            cache.Save("paul", BaseUrl, "paul", new[]
            {
                new CachedView("f1e2d3c4b5a60718293a4b5c6d7e8f90", "Filme", "movies", "paul · Filme"),
                new CachedView("f1e2d3c4b5a60718293a4b5c6d7e8f91", "Serien", "tvshows", "paul · Serien")
            });
            var handler = new FakeHttpMessageHandler();
            handler.Add(
                req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
                string.Empty,
                HttpStatusCode.ServiceUnavailable); // persistent transient failure
            var factory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(handler)), Logger, TimeSpan.FromMilliseconds(1), cache);

            var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            Assert.Equal(2, channels.Count);
            Assert.Contains(channels, c => c.Name == "paul · Filme");
            Assert.Contains(channels, c => c.Name == "paul · Serien");
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task CreateChannels_TransientFailureWithoutCache_SkipsInstance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FederationViewsCache(dir, Logger);
            var handler = new FakeHttpMessageHandler();
            handler.Add(
                req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
                string.Empty,
                HttpStatusCode.ServiceUnavailable);
            var factory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(handler)), Logger, TimeSpan.FromMilliseconds(1), cache);

            var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            Assert.Empty(channels);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task CreateChannels_DefinitiveAuthFailure_NeverUsesCache()
    {
        // 401 is definitive (invariant 3): no channel is built, even with a cache —
        // wrong credentials must not surface stale libraries.
        var dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FederationViewsCache(dir, Logger);
            cache.Save("paul", BaseUrl, "paul", new[] { new CachedView("a", "Filme", "movies", "paul · Filme") });
            var handler = new FakeHttpMessageHandler();
            handler.Add(
                req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
                string.Empty,
                HttpStatusCode.Unauthorized);
            var factory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(handler)), Logger, TimeSpan.FromMilliseconds(1), cache);

            var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            Assert.Empty(channels);
            Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Post));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task CreateChannels_CachedChannelHasIdenticalDataVersionToLiveChannel()
    {
        // Invariant 10: cache-built and live channels must produce the same
        // DataVersion so the host cache is not churned between restarts.
        var dir = Path.Combine(Path.GetTempPath(), "fedcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FederationViewsCache(dir, Logger);
            cache.Save("paul", BaseUrl, "paul", new[] { new CachedView("f1e2d3c4b5a60718293a4b5c6d7e8f90", "Filme", "movies", "paul · Filme") });
            var downHandler = new FakeHttpMessageHandler();
            downHandler.Add(
                req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
                string.Empty,
                HttpStatusCode.ServiceUnavailable);
            var downFactory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(downHandler)), Logger, TimeSpan.FromMilliseconds(1), cache);

            var cachedChannels = await downFactory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            var upFactory = new FederationChannelFactory(() => new RemoteApiClient(new HttpClient(CreateHandler())), Logger, TimeSpan.FromMilliseconds(1));
            var liveChannels = await upFactory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

            Assert.Equal(
                liveChannels.Single(c => c.Name == "paul · Filme").DataVersion,
                cachedChannels.Single(c => c.Name == "paul · Filme").DataVersion);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }
}
