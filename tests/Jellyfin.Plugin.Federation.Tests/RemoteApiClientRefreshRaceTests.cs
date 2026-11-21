using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RemoteApiClientRefreshRaceTests
{
    private const string BaseUrl = "https://remote.example.com:8920";
    private static readonly Guid UserId = Guid.Parse("7a0b0e34-1234-4321-b2c3-d4e5f6071829");

    [Fact]
    public async Task ConcurrentRetry_DoesNotReloginWithTokenRefreshedByAnotherRequest()
    {
        using var handler = new SlowItemsHandler();
        using var client = new RemoteApiClient(new HttpClient(handler));
        await client.ConfigureAsync("paul", BaseUrl, "paul", "secret", CancellationToken.None); // login 1 -> token-a

        // Request A is sent with token-a; its 401 response is held back.
        var slow = client.GetItemsAsync("a1b2c3d4e5f60718293a4b5c6d7e8f90", null, null, CancellationToken.None);

        // Request B also uses token-a, gets 401, refreshes (login 2 -> token-b) and succeeds.
        await client.GetViewsAsync(UserId, CancellationToken.None);
        Assert.Equal("token-b", client.AccessToken);

        // A's stale 401 now arrives. Its snapshot is token-a, so the fresh token-b
        // is not treated as stale and no third login revokes it.
        handler.ReleaseSlowRequest();
        await slow;

        Assert.Equal(2, handler.LoginCount);
        Assert.Equal("token-b", client.AccessToken);
    }

    private sealed class SlowItemsHandler : HttpMessageHandler
    {
        private static readonly string AuthenticateBody = File.ReadAllText("Data/authenticate-response.json");
        private static readonly string ItemsBody = File.ReadAllText("Data/items-response.json");
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _logins;

        public int LoginCount => Volatile.Read(ref _logins);

        public void ReleaseSlowRequest() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;

            if (request.Method == HttpMethod.Post)
            {
                var login = Interlocked.Increment(ref _logins);
                var token = login == 1 ? "token-a" : "token-b";
                return Json(AuthenticateBody.Replace("remote-access-token-xyz", token, StringComparison.Ordinal));
            }

            if (path.EndsWith("/Views", StringComparison.Ordinal))
            {
                return query.Contains("ApiKey=token-a", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json("{}");
            }

            if (path.StartsWith("/Items", StringComparison.Ordinal))
            {
                if (query.Contains("ApiKey=token-a", StringComparison.Ordinal))
                {
                    // Hold the stale request's 401 until the other request has refreshed.
                    await _release.Task.ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return Json(ItemsBody);
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
