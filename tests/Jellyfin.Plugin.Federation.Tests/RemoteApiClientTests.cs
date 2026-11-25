using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RemoteApiClientTests
{
    private const string BaseUrl = "https://remote.example.com:8920";
    private const string AccessToken = "remote-access-token-xyz";
    private static readonly Guid UserId = Guid.Parse("7a0b0e34-1234-4321-b2c3-d4e5f6071829");

    [Fact]
    public void DeserializeItemsResponse_ProducesTypedItems()
    {
        var json = File.ReadAllText("Data/items-response.json");
        var result = JsonSerializer.Deserialize<QueryResult<BaseItemDto>>(json, FederationJsonOptions.Options);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalRecordCount);
        var movie = Assert.Single(result.Items, i => i.Type == Jellyfin.Data.Enums.BaseItemKind.Movie);
        Assert.Equal("Remote Movie", movie.Name);
        Assert.False(movie.IsFolder);
        Assert.Equal("A movie on the remote server", movie.Overview);
        Assert.Equal(2021, movie.ProductionYear);
        Assert.Equal(72000000000, movie.RunTimeTicks);
        Assert.Equal("tt0000001", movie.ProviderIds["Imdb"]);
        Assert.Equal(7.5f, movie.CommunityRating!.Value, 3);
        Assert.Equal("imgtag1", movie.ImageTags[MediaBrowser.Model.Entities.ImageType.Primary]);
        Assert.Equal("Drama", movie.Genres[0]);
        Assert.NotNull(movie.PremiereDate);
        Assert.Equal(2021, movie.PremiereDate.Value.Year);

        var source = Assert.Single(movie.MediaSources!);
        Assert.Equal("mkv", source.Container);
        Assert.Equal("/media/movie.mkv", source.Path);
        Assert.Equal(2, source.MediaStreams!.Count);
        Assert.Equal("hevc", source.MediaStreams[0].Codec);
        Assert.Equal(1920, source.MediaStreams[0].Width);
        Assert.Equal("eac3", source.MediaStreams[1].Codec);
        Assert.Equal(6, source.MediaStreams[1].Channels);

        var series = Assert.Single(result.Items, i => i.Type == Jellyfin.Data.Enums.BaseItemKind.Series);
        Assert.True(series.IsFolder);
        Assert.Equal(2, series.ChildCount);
    }

    [Fact]
    public void DeserializeItemsResponse_ParsesGuidIdsWithoutHyphens()
    {
        // The real remote API uses N-format Guids (32 hex chars without dashes).
        var json = File.ReadAllText("Data/items-response.json");
        var result = JsonSerializer.Deserialize<QueryResult<BaseItemDto>>(json, FederationJsonOptions.Options);

        Assert.NotNull(result);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90"), result!.Items[0].Id);
        Assert.Equal(Guid.Parse("b1b2c3d4-e5f6-0718-293a-4b5c6d7e8f91"), result.Items[1].Id);
    }

    [Fact]
    public async Task GetItemsAsync_AppendsApiKeyQueryParameter()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(req => req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);
        var result = await client.GetItemsAsync(Guid.Parse("a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90").ToString("N"), 0, 50, CancellationToken.None);

        Assert.Equal(2, result.TotalRecordCount);
        var itemsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        var query = itemsRequest.RequestUri!.Query;
        Assert.Contains($"ApiKey={AccessToken}", query);
        Assert.Contains("ParentId=a1b2c3d4e5f60718293a4b5c6d7e8f90", query);
        Assert.Contains("StartIndex=0", query);
        Assert.Contains("Limit=50", query);
        Assert.Contains("Fields=", query);
        Assert.Contains("MediaSources", query);
        Assert.Contains("EnableImages=true", query);
        Assert.Contains("SortBy=SortName", query);
    }

    [Fact]
    public async Task ConfigureAsync_LoginStoresTokenAndReturnsUserId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        var userId = await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);

        Assert.Equal(UserId, userId);
        Assert.Equal(AccessToken, client.AccessToken);

        var loginRequest = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/Users/AuthenticateByName", loginRequest.RequestUri!.AbsolutePath);
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"Pw\"", body);
        Assert.Contains("secret", body);
        Assert.Contains("paul", body);

        // The session manager requires client identification via the standard
        // Authorization header (scheme MediaBrowser) — without it, login fails.
        Assert.True(loginRequest.Headers.Contains("Authorization"));
        Assert.Contains("MediaBrowser", loginRequest.Headers.GetValues("Authorization").Single());
        Assert.Contains("DeviceId=", loginRequest.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ConfigureAsync_DerivesDeviceIdFromAliasAndIsUniquePerRun()
    {
        var handler1 = new FakeHttpMessageHandler();
        handler1.Add(req => req.Method == HttpMethod.Post, File.ReadAllText("Data/authenticate-response.json"));
        var handler2 = new FakeHttpMessageHandler();
        handler2.Add(req => req.Method == HttpMethod.Post, File.ReadAllText("Data/authenticate-response.json"));
        using var client1 = new RemoteApiClient(new HttpClient(handler1));
        using var client2 = new RemoteApiClient(new HttpClient(handler2));

        await client1.ConfigureAsync("Paul", BaseUrl, "paul", "secret", CancellationToken.None);
        await client2.ConfigureAsync("Paul", BaseUrl, "paul", "secret", CancellationToken.None);

        static string DeviceIdOf(FakeHttpMessageHandler handler)
            => handler.Requests.Single(r => r.Method == HttpMethod.Post)
                .Headers.GetValues("Authorization").Single()
                .Split("DeviceId=\"")[1].Split('"')[0];

        var id1 = DeviceIdOf(handler1);
        var id2 = DeviceIdOf(handler2);

        // Alias-based (normalized), not a hash of the server URL.
        Assert.StartsWith("paul-", id1, StringComparison.Ordinal);
        Assert.Matches(@"^paul-[0-9a-f]{8}$", id1);
        // Unique per plugin run: a mid-run re-login must NOT revoke the token that
        // persisted URLs (images, media source cache) still carry.
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Relogin_KeepsTheDeviceIdFromTheOriginalLogin()
    {
        var handler = new FakeHttpMessageHandler();
        var logins = 0;
        handler.Add(
            req => req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
                && logins++ == 0,
            File.ReadAllText("Data/authenticate-response.json").Replace("remote-access-token-xyz", "token-a", StringComparison.Ordinal));
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json").Replace("remote-access-token-xyz", "token-b", StringComparison.Ordinal));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal)
                && req.RequestUri!.Query.Contains("ApiKey=token-a", StringComparison.Ordinal),
            string.Empty,
            HttpStatusCode.Unauthorized);
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);
        await client.GetItemsAsync("a1b2c3d4e5f60718293a4b5c6d7e8f90", null, null, CancellationToken.None);

        var loginDeviceIds = handler.Requests
            .Where(r => r.Method == HttpMethod.Post)
            .Select(r => r.Headers.GetValues("Authorization").Single().Split("DeviceId=\"")[1].Split('"')[0])
            .Distinct()
            .ToList();

        // Both the initial login and the 401 re-login used the same device, so the
        // re-login created a new token WITHOUT deleting the first one.
        Assert.Single(loginDeviceIds);
        Assert.Equal("token-b", client.AccessToken);
    }

    [Fact]
    public async Task GetViewsAsync_UsesUserIdAndApiKey()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(req => req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(req => req.RequestUri!.AbsolutePath.StartsWith($"/Users/{UserId:N}/Views", StringComparison.Ordinal),
            "{}");
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);
        await client.GetViewsAsync(UserId, CancellationToken.None);

        var viewsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/Users/", StringComparison.Ordinal) && r.Method == HttpMethod.Get);
        Assert.Contains($"ApiKey={AccessToken}", viewsRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task GetPlaybackInfoAsync_ReturnsPlaybackInfoWithMediaSources()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(req => req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        const string itemId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        handler.Add(req => req.RequestUri!.AbsolutePath == $"/Items/{itemId}/PlaybackInfo",
            File.ReadAllText("Data/playbackinfo-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);
        var playbackInfo = await client.GetPlaybackInfoAsync(itemId, CancellationToken.None);

        var source = Assert.Single(playbackInfo.MediaSources!);
        Assert.Equal("/media/movie.mkv", source.Path);
        Assert.Equal("mkv", source.Container);
        Assert.Equal(2, source.MediaStreams!.Count);

        var playbackRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("PlaybackInfo", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Get, playbackRequest.Method);
        Assert.Contains($"ApiKey={AccessToken}", playbackRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task GetItemsAsync_ReloginsOnceWhenTokenWasRevoked()
    {
        var handler = new FakeHttpMessageHandler();
        var loginCount = 0;
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal)
                && req.RequestUri.Query.Contains("ApiKey=token-a", StringComparison.Ordinal),
            string.Empty,
            HttpStatusCode.Unauthorized);
        handler.Add(
            req => req.Method == HttpMethod.Post && loginCount++ == 0,
            "{\"User\":{\"Id\":\"7a0b0e34-1234-4321-b2c3-d4e5f6071829\",\"Name\":\"paul\"},\"AccessToken\":\"token-a\"}");
        handler.Add(
            req => req.Method == HttpMethod.Post,
            "{\"User\":{\"Id\":\"7a0b0e34-1234-4321-b2c3-d4e5f6071829\",\"Name\":\"paul\"},\"AccessToken\":\"token-b\"}");
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);
        var result = await client.GetItemsAsync("a1b2c3d4e5f60718293a4b5c6d7e8f90", null, null, CancellationToken.None);

        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal("token-b", client.AccessToken);
        var itemsRequests = handler.Requests.FindAll(r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Equal(2, itemsRequests.Count);
        Assert.Contains("ApiKey=token-a", itemsRequests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("ApiKey=token-b", itemsRequests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItemsAsync_ThrowsWhenStillUnauthorizedAfterRelogin()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            string.Empty,
            HttpStatusCode.Unauthorized);
        handler.Add(
            req => req.Method == HttpMethod.Post,
            File.ReadAllText("Data/authenticate-response.json"));
        using var client = new RemoteApiClient(new HttpClient(handler));

        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None);

        await Assert.ThrowsAsync<FederationException>(
            () => client.GetItemsAsync("a1b2c3d4e5f60718293a4b5c6d7e8f90", null, null, CancellationToken.None));

        // Exactly one retry — the refreshed request is not retried again.
        var itemsRequests = handler.Requests.FindAll(r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Equal(2, itemsRequests.Count);
        Assert.Equal(2, handler.Requests.FindAll(r => r.Method == HttpMethod.Post).Count);
    }
}

// Note: these tests reference RemoteApiReport (defined in the src namespace
// Jellyfin.Plugin.Federation), reachable from this namespace without a using.
public class RemoteApiClientPrepareTests
{
    private static FakeHttpMessageHandler CreateHandler()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        return handler;
    }

    [Fact]
    public void Prepare_StoresCredentialsWithoutLogin()
    {
        // Prepare must not log in: the views-cache fallback builds channels for a
        // remote that is down; the login happens later via the proactive
        // empty-token path in GetJsonAsync (first authenticated call).
        var handler = CreateHandler();
        var client = new RemoteApiClient(new HttpClient(handler));

        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");

        Assert.Equal("https://remote.example.com:8920", client.BaseUrl);
        Assert.Equal(string.Empty, client.AccessToken);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetItems_WithTokenLessClient_LogsInOnceThenFetches()
    {
        // Self-heal: a request on a prepared-but-not-logged-in client triggers one
        // proactive login (empty-token path), then sends the request with the fresh
        // token — no dead 401 round-trip with an empty ApiKey.
        var handler = CreateHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        var client = new RemoteApiClient(new HttpClient(handler));
        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");

        var result = await client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None);

        Assert.NotNull(result);
        var logins = handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName");
        Assert.Equal(1, logins);
        var itemsRequest = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Contains("ApiKey=remote-access-token-xyz", itemsRequest.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItems_ConcurrentFirstCalls_ShareOneLogin()
    {
        // Spec: single-flight under _loginLock — concurrent first callers of a
        // token-less client share one login (a duplicate login creates a second
        // device row on the remote, invariant 2).
        var handler = CreateHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        var client = new RemoteApiClient(new HttpClient(handler));
        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");

        await Task.WhenAll(
            client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None),
            client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None),
            client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None));

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"));
    }

    [Fact]
    public async Task GetItems_ReportsSuccessWithTimestamp()
    {
        var handler = CreateHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        var client = new RemoteApiClient(new HttpClient(handler));
        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");
        var before = DateTimeOffset.UtcNow;

        await client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None);

        Assert.NotNull(client.LastAuthenticatedUtc);
        Assert.True(client.LastAuthenticatedUtc >= before);
    }

    [Fact]
    public async Task GetItems_ReportsSanitizedFailureAndSetsAuthFailedOn401Login()
    {
        // Wrong password on a token-less client: the login 401s, the request is not
        // retried, and the reporter sees AuthFailed so the status can show
        // InvalidCredentials instead of an endless retry loop.
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.Unauthorized);
        var client = new RemoteApiClient(new HttpClient(handler));
        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");
        RemoteApiReport? reported = null;
        client.SetReporter(r => reported = r);

        await Assert.ThrowsAsync<FederationException>(
            () => client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None));

        Assert.NotNull(reported);
        Assert.False(reported!.Succeeded);
        Assert.True(reported.AuthFailed);
        Assert.DoesNotContain("secret", reported.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // exactly one login attempt
    }

    [Fact]
    public async Task ConfigureAsync_ReportsAuthFailureOn401Login()
    {
        // Startup with a wrong password: the login 401s and the reporter sees
        // AuthFailed — otherwise the unauthenticated probe would show Online
        // while every channel call fails.
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.Unauthorized);
        var client = new RemoteApiClient(new HttpClient(handler));
        RemoteApiReport? reported = null;
        client.SetReporter(r => reported = r);

        await Assert.ThrowsAsync<FederationException>(
            () => client.ConfigureAsync("paul", "https://remote.example.com:8920", "paul", "wrong", CancellationToken.None));

        Assert.NotNull(reported);
        Assert.False(reported!.Succeeded);
        Assert.True(reported.AuthFailed);
        Assert.Single(handler.Requests); // no retry on definitive failure
    }

    [Fact]
    public async Task GetItems_ReportsConnectionFailureWithoutAuthFlag()
    {
        var handler = new ThrowingHandler();
        var client = new RemoteApiClient(new HttpClient(handler));
        client.Prepare("paul", "https://remote.example.com:8920", "paul", "secret");
        RemoteApiReport? reported = null;
        client.SetReporter(r => reported = r);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetItemsAsync("f1e2d3c4b5a60718293a4b5c6d7e8f90", null, null, CancellationToken.None));

        Assert.NotNull(reported);
        Assert.False(reported!.Succeeded);
        Assert.False(reported.AuthFailed);
    }
}

/// <summary>Shared handler that always throws HttpRequestException (remote down). Top-level, shared by Tasks 1, 4, 5.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("connection refused");
}
