using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationChannelFactoryTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private const string BaseUrl = "https://remote.example.com:8920";

    private static PluginConfiguration CreateConfig(params FederationInstance[] instances)
    {
        var config = new PluginConfiguration();
        config.Instances.AddRange(instances);
        return config;
    }

    private static FederationInstance CreateValidInstance(string alias = "paul")
        => new() { Alias = alias, ServerUrl = BaseUrl, Username = "paul", Password = "secret" };

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

    private static FederationChannelFactory CreateFactory(FakeHttpMessageHandler handler)
        => new(() => new RemoteApiClient(new HttpClient(handler)), Logger, TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task CreateChannels_LoginViewsAndBuildChannels()
    {
        var handler = CreateHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        // Playlists (playlists) and the library without CollectionType are filtered out.
        Assert.Equal(3, channels.Count);
        Assert.Contains(channels, c => c.Name == "paul · Filme");
        Assert.Contains(channels, c => c.Name == "paul · Serien");
        Assert.Contains(channels, c => c.Name == "paul · Musik");

        // Views were fetched for the remote user id from the login, with the ApiKey.
        var viewsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal));
        Assert.Contains("7a0b0e3412344321b2c3d4e5f6071829", viewsRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("ApiKey=remote-access-token-xyz", viewsRequest.RequestUri!.Query, StringComparison.Ordinal);

        // RemoteLibraryId is the view id in N format: the root query uses it as ParentId.
        var moviesChannel = channels.Single(c => c.Name == "paul · Filme");
        await moviesChannel.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.NewGuid() }, CancellationToken.None);
        var itemsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Contains("ParentId=f1e2d3c4b5a60718293a4b5c6d7e8f90", itemsRequest.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateChannels_InvalidInstanceSkipped()
    {
        var handler = CreateHandler();
        var factory = CreateFactory(handler);
        var invalid = CreateValidInstance();
        invalid.Username = string.Empty;

        var channels = await factory.CreateChannelsAsync(CreateConfig(invalid), CancellationToken.None);

        Assert.Empty(channels);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task CreateChannels_LoginFailureSkipsInstanceButContinues()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
                && req.RequestUri!.Host == "bad.example.com",
            string.Empty,
            HttpStatusCode.NotFound);
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            File.ReadAllText("Data/views-response.json"));
        var factory = CreateFactory(handler);

        var bad = new FederationInstance { Alias = "kaputt", ServerUrl = "https://bad.example.com:8920", Username = "paul", Password = "wrong" };
        var config = CreateConfig(bad, CreateValidInstance());

        var channels = await factory.CreateChannelsAsync(config, CancellationToken.None);

        Assert.Equal(3, channels.Count);
        Assert.All(channels, c => Assert.StartsWith("paul · ", c.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateChannels_UnnamedLibrarySkipped()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            """
        {
          "Items": [
            { "Id": "f1e2d3c4b5a60718293a4b5c6d7e8f95", "Type": "CollectionFolder", "IsFolder": true, "CollectionType": "movies" }
          ],
          "TotalRecordCount": 1,
          "StartIndex": 0
        }
        """);
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        Assert.Empty(channels);
    }

    [Fact]
    public async Task CreateChannels_CreatesOneClientPerInstance()
    {
        var handler = CreateHandler();
        var created = 0;
        var factory = new FederationChannelFactory(
            () =>
            {
                created++;
                return new RemoteApiClient(new HttpClient(handler));
            },
            Logger);
        var second = CreateValidInstance("paulzwei");
        second.ServerUrl = "https://other.example.com:8920";

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance(), second), CancellationToken.None);

        Assert.Equal(2, created);
        Assert.Equal(6, channels.Count);
        Assert.Equal(2, channels.Select(c => c.Name.Split(' ')[0]).Distinct().Count());
    }

    [Fact]
    public async Task CreateChannels_RetriesFailedLoginAtStartup()
    {
        var handler = new FakeHttpMessageHandler();
        var loginCount = 0;
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName" && loginCount++ < 2,
            string.Empty,
            HttpStatusCode.ServiceUnavailable);
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            File.ReadAllText("Data/views-response.json"));
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        Assert.Equal(3, channels.Count);
        Assert.Equal(3, loginCount);
    }

    [Fact]
    public async Task CreateChannels_TrimsTrailingSlashFromConfiguredUrl()
    {
        // The config is a URL and may carry a trailing slash. Every URL built for
        // this instance (images, streams) derives from the trimmed base; a second
        // "/" after the host makes the remote return 404 (playback broke on this).
        var handler = CreateHandler();
        var instance = CreateValidInstance();
        instance.ServerUrl = BaseUrl + "/";
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(instance), CancellationToken.None);

        Assert.Equal(3, channels.Count);
        Assert.All(channels, c => Assert.Equal(BaseUrl, c.HomePageUrl));
    }

    [Fact]
    public async Task CreateChannels_RetriesConnectionFailuresAtStartup()
    {
        // A remote that is still booting refuses the connection (HttpRequestException),
        // not just HTTP 503. Both must be retried.
        var handler = new ThrowingLoginHandler(2); // first two logins throw, third succeeds
        var factory = new FederationChannelFactory(
            () => new RemoteApiClient(new HttpClient(handler)),
            Logger,
            TimeSpan.FromMilliseconds(1));

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        Assert.Equal(3, channels.Count);
        Assert.Equal(3, handler.LoginAttempts);
    }

    [Fact]
    public async Task CreateChannels_DisambiguatesDuplicateChannelNamesAcrossInstances()
    {
        // The host keys channel entities on the name; two instances exposing the same
        // view name would otherwise collapse into one entity and hide the second library.
        var handler = CreateHandler();
        var factory = CreateFactory(handler);
        var a = CreateValidInstance();
        var b = CreateValidInstance(); // same alias + same views -> identical names

        var channels = await factory.CreateChannelsAsync(CreateConfig(a, b), CancellationToken.None);

        Assert.Equal(6, channels.Count);
        // Every channel name is unique (collisions get a short id suffix).
        Assert.Equal(6, channels.Select(c => c.Name).Distinct().Count());
        // The first occurrence keeps the plain name; the duplicate is suffixed.
        Assert.Contains(channels, c => c.Name == "paul · Filme");
        Assert.Contains(channels, c => c.Name.StartsWith("paul · Filme (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateChannels_DisambiguatesTripleCollisionWithIdenticalIds()
    {
        // Three instances of the same alias: the view ids (and thus the first
        // id suffix) collide too, so the dedup must fall back to a counter to
        // keep every name unique.
        var handler = CreateHandler();
        var factory = CreateFactory(handler);
        var a = CreateValidInstance();
        var b = CreateValidInstance();
        var c = CreateValidInstance();

        var channels = await factory.CreateChannelsAsync(CreateConfig(a, b, c), CancellationToken.None);

        Assert.Equal(9, channels.Count);
        Assert.Equal(9, channels.Select(ch => ch.Name).Distinct().Count());
    }

    [Fact]
    public async Task CreateChannels_DoesNotRetryDefinitiveAuthFailures()
    {
        // A 401 (wrong credentials) is permanent; retrying only counts against the
        // remote user's failed-login counter (possible lockout). Expect exactly one
        // login attempt and no channels.
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.Unauthorized);
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        var logins = handler.Requests.Count(r => r.Method == HttpMethod.Post);
        Assert.Empty(channels);
        Assert.Equal(1, logins);
    }

    [Fact]
    public async Task CreateChannels_GivesUpAfterMaxLoginAttempts()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.ServiceUnavailable);
        var factory = CreateFactory(handler);

        var channels = await factory.CreateChannelsAsync(CreateConfig(CreateValidInstance()), CancellationToken.None);

        Assert.Empty(channels);
    }

    /// <summary>
    /// Handler that throws HttpRequestException (connection refused) for the first
    /// <paramref name="throwingLogins"/> login attempts, then answers successfully.
    /// </summary>
    private sealed class ThrowingLoginHandler : HttpMessageHandler
    {
        private readonly int _throwingLogins;

        public ThrowingLoginHandler(int throwingLogins)
        {
            _throwingLogins = throwingLogins;
        }

        public int LoginAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName")
            {
                LoginAttempts++;
                if (LoginAttempts <= _throwingLogins)
                {
                    throw new HttpRequestException("connection refused");
                }

                return Task.FromResult(Json(File.ReadAllText("Data/authenticate-response.json")));
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(File.ReadAllText("Data/views-response.json")));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
