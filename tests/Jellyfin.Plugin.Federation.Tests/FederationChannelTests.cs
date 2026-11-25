using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationChannelTests
{
    private const string BaseUrl = "https://remote.example.com:8920";
    private const string LibraryId = "lib123";
    private static readonly string MovieId = Guid.Parse("a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90").ToString("N");

    private static FederationChannel CreateChannel(out FakeHttpMessageHandler handler)
    {
        handler = new FakeHttpMessageHandler();
        handler.Add(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(req => req.RequestUri!.AbsolutePath.Contains("PlaybackInfo", StringComparison.Ordinal),
            File.ReadAllText("Data/playbackinfo-response.json"));
        handler.Add(req => req.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal),
            File.ReadAllText("Data/items-response.json"));
        var client = new RemoteApiClient(new HttpClient(handler));
        _ = client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None).GetAwaiter().GetResult();
        handler.Token = client.AccessToken;

        return new FederationChannel(
            new FederationChannelOptions("paul", "Filme", LibraryId, BaseUrl),
            client);
    }

    [Fact]
    public async Task GetChannelItems_Root_ReturnsRemoteLibraryChildren()
    {
        var channel = CreateChannel(out var handler);

        var result = await channel.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.NewGuid() }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal(ChannelItemType.Media, result.Items[0].Type);
        Assert.Equal(ChannelItemType.Folder, result.Items[1].Type);

        var itemsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        var query = itemsRequest.RequestUri!.Query;
        Assert.Contains($"ParentId={LibraryId}", query);
        Assert.Contains($"ApiKey={handler.Token}", query);
    }

    [Fact]
    public async Task GetChannelItems_WithFolderId_UsesExternalParent()
    {
        var channel = CreateChannel(out var handler);

        var result = await channel.GetChannelItems(
            new InternalChannelItemQuery { UserId = Guid.NewGuid(), FolderId = "abc123" },
            CancellationToken.None);

        Assert.Equal(2, result!.Items.Count);

        var itemsRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Contains("ParentId=abc123", itemsRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_ReturnsProxiedSource()
    {
        var channel = CreateChannel(out var handler);

        var sources = await channel.GetChannelItemMediaInfo(MovieId, CancellationToken.None);

        var source = Assert.Single(sources);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.IsRemote);
        Assert.StartsWith($"{BaseUrl}/Videos/", source.Path, StringComparison.Ordinal);
        Assert.EndsWith($"ApiKey={handler.Token}", source.Path, StringComparison.Ordinal);
        Assert.Equal(MovieId, source.Id);

        var playbackRequest = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("PlaybackInfo", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Get, playbackRequest.Method);
        Assert.Equal($"/Items/{MovieId}/PlaybackInfo", playbackRequest.RequestUri!.AbsolutePath);
        Assert.Contains($"ApiKey={handler.Token}", playbackRequest.RequestUri!.Query);
    }

    [Fact]
    public void DataVersion_IsStableHashOfConfiguration()
    {
        var first = new FederationChannel(
            new FederationChannelOptions("paul", "paul · Filme", LibraryId, BaseUrl),
            new RemoteApiClient(new HttpClient(new FakeHttpMessageHandler())));
        var second = new FederationChannel(
            new FederationChannelOptions("paul", "paul · Filme", LibraryId, BaseUrl),
            new RemoteApiClient(new HttpClient(new FakeHttpMessageHandler())));

        Assert.Equal(first.DataVersion, second.DataVersion);

        // 64 hex chars = SHA-256
        Assert.Matches("^[0-9a-f]{64}$", first.DataVersion);

        // The hash includes a data-format salt, so a persisted-state format
        // change can invalidate the host's item cache by bumping it in one
        // place (see DataFormatSalt).
        Assert.Equal(
            "7486bd11c254ce1706f770b376295b0d3f0db00014b3a2ea67b1e30fe04f05f3",
            first.DataVersion);

        // The display name is NOT part of the version (it can change on dedup
        // without the remote library changing); the remote library id is.
        var renamed = new FederationChannel(
            new FederationChannelOptions("paul", "paul · Filme (a1b2c3d4)", LibraryId, BaseUrl),
            new RemoteApiClient(new HttpClient(new FakeHttpMessageHandler())));
        Assert.Equal(first.DataVersion, renamed.DataVersion);

        var otherLibrary = new FederationChannel(
            new FederationChannelOptions("paul", "paul · Serien", "0123456789abcdef0123456789abcdef", BaseUrl),
            new RemoteApiClient(new HttpClient(new FakeHttpMessageHandler())));
        Assert.NotEqual(first.DataVersion, otherLibrary.DataVersion);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_UsesRefreshedTokenInStreamUrl()
    {
        // The remote revokes the startup token: PlaybackInfo with token-a is 401,
        // the client re-logs in (token-b) and the stream URL must carry token-b.
        var handler = new FakeHttpMessageHandler();
        var playbackCalls = 0;
        var loginCalls = 0;
        handler.Add(
            req => req.RequestUri!.AbsolutePath.Contains("PlaybackInfo", StringComparison.Ordinal) && playbackCalls++ == 0,
            string.Empty,
            System.Net.HttpStatusCode.Unauthorized);
        handler.Add(req => req.Method == HttpMethod.Post && loginCalls++ == 0,
            "{\"User\":{\"Id\":\"7a0b0e34-1234-4321-b2c3-d4e5f6071829\",\"Name\":\"paul\"},\"AccessToken\":\"token-a\"}");
        handler.Add(
            req => req.Method == HttpMethod.Post,
            "{\"User\":{\"Id\":\"7a0b0e34-1234-4321-b2c3-d4e5f6071829\",\"Name\":\"paul\"},\"AccessToken\":\"token-b\"}");
        handler.Add(
            req => req.RequestUri!.AbsolutePath.Contains("PlaybackInfo", StringComparison.Ordinal),
            File.ReadAllText("Data/playbackinfo-response.json"));
        var client = new RemoteApiClient(new HttpClient(handler));
        _ = client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None).GetAwaiter().GetResult();
        var channel = new FederationChannel(new FederationChannelOptions("paul", "Filme", LibraryId, BaseUrl), client);

        var sources = await channel.GetChannelItemMediaInfo(MovieId, CancellationToken.None);

        var source = Assert.Single(sources);
        Assert.Equal("token-b", client.AccessToken);
        Assert.EndsWith("ApiKey=token-b", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChannelItems_LogsTimeout()
    {
        var handler = new SlowItemsHandler();
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) };
        using var client = new RemoteApiClient(httpClient);
        _ = client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None).GetAwaiter().GetResult();
        var logger = new CapturingLogger();
        var channel = new FederationChannel(new FederationChannelOptions("paul", "Filme", LibraryId, BaseUrl), client, logger);

        // An HttpClient timeout surfaces as TaskCanceledException; it must be logged,
        // because the channel manager turns the rethrown exception into an empty result.
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => channel.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.NewGuid() }, CancellationToken.None));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("cannot load items", entry.Message, StringComparison.Ordinal);
    }

    private sealed class SlowItemsHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(File.ReadAllText("Data/authenticate-response.json"), Encoding.UTF8, "application/json")
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Name_IsFactorySuppliedChannelName()
    {
        var channel = new FederationChannel(
            new FederationChannelOptions("paul", "paul · Filme", LibraryId, BaseUrl),
            new RemoteApiClient(new HttpClient(new FakeHttpMessageHandler())));

        // The factory builds "{Alias} · {Library}" and guarantees uniqueness
        // (the host keys channel entities on the name); the channel uses it verbatim.
        Assert.Equal("paul · Filme", channel.Name);
        Assert.Equal(BaseUrl, channel.HomePageUrl);
        Assert.Equal(ChannelParentalRating.GeneralAudience, channel.ParentalRating);
    }
}
