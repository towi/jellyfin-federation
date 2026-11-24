using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Typed client for the remote Jellyfin HTTP API.
/// The HttpClient (including Timeout, e.g. 15s) is supplied by the caller.
/// </summary>
public class RemoteApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string _accessToken = string.Empty;
    private string _deviceId = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private Action<RemoteApiReport>? _reporter;
    private DateTimeOffset? _lastAuthenticatedUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    public RemoteApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Prepares the client for one federation instance without logging in:
    /// stores the base URL, device id, and credentials. The views-cache fallback
    /// builds channels with this alone (the remote may be down); the login then
    /// happens proactively on the first authenticated request (GetJsonAsync).
    /// </summary>
    /// <param name="alias">Instance alias from the config; part of the device id.</param>
    /// <param name="serverUrl">Remote base URL (a trailing slash is tolerated).</param>
    /// <param name="username">Remote user name.</param>
    /// <param name="password">Remote password.</param>
    public void Prepare(string alias, string serverUrl, string username, string password)
    {
        BaseUrl = serverUrl.TrimEnd('/');

        // The device id is unique per plugin run: when a token dies (remote
        // logout, DB reset, password change) the mid-run re-login then creates a
        // new device row instead of revoking the old token. That matters because
        // previously issued tokens are embedded in persisted URLs: channel item
        // images (the host downloads them once and serves the local copy) and
        // the channel manager's 5-minute media source cache. A stable device id
        // would revoke the old token on every re-login and break those URLs.
        // The alias is the only stable, human-readable part; keep just the
        // characters safe inside the Authorization header (the host treats a
        // double-quote as an escape toggle and URL-decodes the value), and add a
        // per-run random suffix for uniqueness.
        var aliasTag = SanitizeDeviceIdPart(alias);
        _deviceId = $"{aliasTag}-{Guid.NewGuid().ToString("N")[..8]}";

        // HttpClient.BaseAddress can only be set before the first request, and this
        // client serves exactly one instance, so set it once and keep it.
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl + "/");
        }

        // Login: POST /Users/AuthenticateByName (Jellyfin.Api/Controllers/UserController.cs:209-236).
        // The session manager requires client identification from the standard
        // Authorization header (AuthorizationContext.GetAuthorizationDictionary reads
        // HeaderNames.Authorization; the "MediaBrowser" scheme is NOT legacy-gated).
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add(
            "Authorization",
            $"MediaBrowser Client=\"Jellyfin Federation\", Device=\"Jellyfin Federation\", DeviceId=\"{_deviceId}\", Version=\"1.0\"");

        // The credentials are kept in memory only so a revoked or expired token
        // can be renewed mid-run (see GetJsonAsync) without a server restart.
        _username = username;
        _password = password;
    }

    /// <summary>
    /// Prepares credentials and logs in (startup path). A failed login is
    /// reported through the outcome reporter — startup 401s must surface as
    /// InvalidCredentials, not a green Online probe.
    /// </summary>
    /// <param name="alias">Instance alias from the config; part of the device id.</param>
    /// <param name="serverUrl">Remote base URL (a trailing slash is tolerated).</param>
    /// <param name="username">Remote user name.</param>
    /// <param name="password">Remote password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote user id of the logged in user.</returns>
    public async Task<Guid> ConfigureAsync(string alias, string serverUrl, string username, string password, CancellationToken cancellationToken)
    {
        Prepare(alias, serverUrl, username, password);
        try
        {
            return await LoginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Report(ex);
            throw;
        }
    }

    private static string SanitizeDeviceIdPart(string alias)
    {
        // Keep only characters that are safe both inside the quoted Authorization
        // header value (no quotes/backslashes) and after the host's URL-decode
        // step (no '%'). Fall back to a generic tag if the alias yields nothing.
        var sanitized = new string(
            [.. alias.Trim().ToLowerInvariant()
                .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                .Take(16)
                .ToArray()]);
        return sanitized.Length > 0 ? sanitized : "federation";
    }

    /// <summary>
    /// Logs in with the stored credentials and replaces the access token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote user id of the logged in user.</returns>
    private async Task<Guid> LoginAsync(CancellationToken cancellationToken)
    {
        // The access token from the response authenticates every following request via
        // the ApiKey query parameter (AuthorizationContext.cs:103-106 — NOT legacy-gated).
        var body = JsonSerializer.Serialize(new { Username = _username, Pw = _password });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("Users/AuthenticateByName", content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new FederationException($"Login to {BaseUrl} failed: {(int)response.StatusCode}", (int)response.StatusCode);
        }

        var authResult = await response.Content.ReadFromJsonAsync<AuthenticationResult>(FederationJsonOptions.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new FederationException($"Empty login response from {BaseUrl}");
        _accessToken = authResult.AccessToken ?? throw new FederationException($"Login to {BaseUrl} returned no access token");
        return authResult.User.Id;
    }

    /// <summary>
    /// Renews the access token after a 401. Concurrent callers share one login:
    /// the first refreshes, the others reuse the new token.
    /// </summary>
    /// <param name="staleToken">Token that was rejected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current access token.</returns>
    private async Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_accessToken, staleToken, StringComparison.Ordinal))
            {
                return _accessToken;
            }

            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return _accessToken;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Gets the configured base URL.
    /// </summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the access token from the login (in-memory only).
    /// </summary>
    public string AccessToken => _accessToken;

    /// <summary>
    /// Gets the UTC time of the last successful authenticated request, or null if none succeeded yet.
    /// </summary>
    public DateTimeOffset? LastAuthenticatedUtc => _lastAuthenticatedUtc;

    /// <summary>
    /// Sets the outcome reporter invoked after every authenticated request; null removes it.
    /// </summary>
    /// <param name="reporter">The reporter, or null.</param>
    public void SetReporter(Action<RemoteApiReport>? reporter) => _reporter = reporter;

    /// <summary>
    /// Gets one page of items under a remote parent.
    /// </summary>
    /// <param name="remoteParentId">Remote parent item id (hex string).</param>
    /// <param name="startIndex">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result page.</returns>
    public async Task<QueryResult<BaseItemDto>> GetItemsAsync(
        string remoteParentId,
        int? startIndex,
        int? limit,
        CancellationToken cancellationToken)
    {
        // Only the fields ItemMapper consumes. MediaSources and Path are deliberately
        // not requested: media sources arrive at playback time via PlaybackInfo, and
        // the physical path is on the remote disk — both would bloat every folder
        // load for no consumer.
        var url = $"Items?ParentId={remoteParentId}"
            + "&Fields=Overview,Genres,Studios,People,ProviderIds,OriginalTitle,PremiereDate,ProductionYear,RunTimeTicks,CommunityRating,ChildCount"
            + "&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary"
            + "&SortBy=SortName&SortOrder=Ascending"
            + (startIndex.HasValue ? $"&StartIndex={startIndex.Value}" : string.Empty)
            + (limit.HasValue ? $"&Limit={limit.Value}" : string.Empty);

        return await GetJsonAsync<QueryResult<BaseItemDto>>(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the libraries visible to the logged-in user.
    /// </summary>
    /// <param name="remoteUserId">Remote user id from login.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with CollectionFolder items.</returns>
    public async Task<QueryResult<BaseItemDto>> GetViewsAsync(Guid remoteUserId, CancellationToken cancellationToken)
    {
        return await GetJsonAsync<QueryResult<BaseItemDto>>($"Users/{remoteUserId:N}/Views", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets remote playback info for one item.
    /// </summary>
    /// <param name="remoteItemId">Remote item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Playback info response.</returns>
    public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string remoteItemId, CancellationToken cancellationToken)
    {
        return await GetJsonAsync<PlaybackInfoResponse>($"Items/{remoteItemId}/PlaybackInfo", cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        try
        {
            // Token-less self-heal (views-cache fallback, Task 3): a prepared but
            // not-logged-in client logs in proactively before the first request,
            // single-flight under _loginLock so concurrent first callers share one
            // login (a duplicate login would create a second device row on the
            // remote, invariant 2). No request is ever sent with an empty ApiKey.
            if (string.IsNullOrEmpty(_accessToken))
            {
                await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (string.IsNullOrEmpty(_accessToken))
                    {
                        await LoginAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _loginLock.Release();
                }
            }

            // Snapshot the token used for this request: it is the only one that can be
            // the rejected one. Reading the field again after the round trip could pick
            // up a token another thread refreshed in the meantime and re-login again,
            // which (same DeviceId) would invalidate that fresh token.
            var token = _accessToken;
            var response = await SendGetAsync(url, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Token revoked or expired (remote logout, password change, DB reset):
                // log in once and repeat the request. The fresh request is never retried again.
                response.Dispose();
                var refreshedToken = await RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
                response = await SendGetAsync(url, refreshedToken, cancellationToken).ConfigureAwait(false);
            }

            T result;
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new FederationException($"Remote server returned {(int)response.StatusCode} for {url}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                result = await JsonSerializer.DeserializeAsync<T>(stream, FederationJsonOptions.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new FederationException($"Empty response from remote server for {url}");
            }

            _lastAuthenticatedUtc = DateTimeOffset.UtcNow;
            _reporter?.Invoke(new RemoteApiReport(true, null, false));
            return result;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The reporter is invoked before rethrowing — the channel's own
            // catch/log flow stays unchanged.
            Report(ex);
            throw;
        }
    }

    private void Report(Exception ex)
    {
        _reporter?.Invoke(new RemoteApiReport(
            Succeeded: false,
            Error: SanitizeError(ex),
            AuthFailed: ex is FederationException { StatusCode: 401 or 403 }));
    }

    // Fixed category strings only, never raw exception text (invariant 9 — the
    // status surface is logged): the URL may carry ApiKey, so it is never included.
    private static string SanitizeError(Exception ex)
        => ex switch
        {
            FederationException f => f.StatusCode is null ? "remote error" : $"remote returned {f.StatusCode}",
            TaskCanceledException => "timeout",
            _ => "unreachable"
        };

    private Task<HttpResponseMessage> SendGetAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        // ApiKey (access token from login) as query parameter on every request —
        // NOT legacy-gated, unlike X-Emby-Token header and lowercase api_key.
        var separator = url.Contains('?') ? "&" : "?";
        return _httpClient.GetAsync(
            $"{url}{separator}ApiKey={Uri.EscapeDataString(accessToken)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loginLock.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
