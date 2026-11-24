# Federation UI, Status & Down-Resilience — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dashboard config UI + status page + views-cache survival for the federation plugin, per the approved spec `docs/pi/specs/2026-11-22-ui-and-status-design.md`.

**Note:** Task order deviates from the spec's phase order (UI first) on purpose — the reporter/cache/status groundwork (Tasks 1–4) lands first because the UI and endpoints depend on it. See the spec's "Implementation order" and "Deviations" sections.

**Architecture:** Config read/write reuses the host's plugin-config API (`GET/POST /Plugins/{id}/Configuration`, admin-gated) from an embedded dashboard page (`IHasWebPages`, host-served, no iframe, `window.ApiClient`/`window.Dashboard` globals — verified in jellyfin-web master: `window.ApiClient` set in `ServerConnections.setLocalApiClient`, `window.Dashboard` in `src/utils/dashboard.js`). Status and test-connection live in a plugin MVC controller (`ControllerBase`, auto-registered via `GetApiPluginAssemblies`). A `FederationStatusService` (`IHostedService`) probes remotes **unauthenticated** (`GET /System/Info/Public`, `PublicSystemInfo` = `MediaBrowser.Model/System/PublicSystemInfo.cs` — fields `ServerName`, `Version`). Survival uses a views-cache JSON: startup login fails transiently → build channels from cache with a token-less client that self-heals via the existing 401-re-login.

**Tech Stack:** .NET 10, ASP.NET Core MVC (host-loaded shared framework), System.Text.Json, vanilla JS, xUnit + `FakeHttpMessageHandler`.

**Build environment:** `dotnet` is not on PATH; export first:
```bash
export DOTNET_ROOT=/nix/store/47cpgncd1v5ilpans6n6rnzjhfsrmsv7-dotnet-sdk-10.0.300/share/dotnet
export PATH="$DOTNET_ROOT:$PATH"
```
**Verification standard:** after every task, run `dotnet build -c Release` AND `dotnet build -c Release container/Jellyfin.Plugin.Federation.Container.csproj` — both targets must build (AGENTS.md). Run `dotnet test -c Release` for the task's test. Use the eval `commit` tool (not `git add`) so unrelated work in `dist/` stays untouched.

**Code style (AGENTS.md, non-negotiable):**
- Every public type/member in `src/` gets an XML doc comment (`GenerateDocumentationFile` is on).
- `async`/`await` + `.ConfigureAwait(false)` on every `await` in `src/`.
- Log messages start with `Federation:`.
- Tests: `[Fact]` methods first, helpers/fake types last in the file.

---

### Task 1: `RemoteApiClient` — token-less mode, `lastAuthenticatedUtc`, reporting callback

**Files:**
- Modify: `src/RemoteApiClient.cs`
- Test: `tests/Jellyfin.Plugin.Federation.Tests/RemoteApiClientTests.cs` (append facts + fake at file end)

**Design:** Today `ConfigureAsync(alias, serverUrl, username, password, ct)` sets up credentials **and logs in**. The views-cache fallback (Task 3) needs a client with credentials but no token. Split: `ConfigureAsync` becomes `Prepare(alias, serverUrl, username, password)` (no login, no throw) + existing `LoginAsync` flow. The factory calls `Prepare` + `LoginAsync` sequence exactly as before (same retry semantics). Token-less self-heal (Task 3 fallback path): the first authenticated call logs in **proactively, single-flight under `_loginLock`**, so no request is ever sent with an empty `ApiKey` (the mid-run 401 → `RefreshTokenAsync` path is unchanged and still handles token revocation). Add `LastAuthenticatedUtc` (DateTimeOffset?; set on every successful authenticated GET in `GetJsonAsync`) and a reporting callback:

```csharp
/// <summary>Receives request outcomes for the status page.</summary>
public void SetReporter(Action<RemoteApiReport>? reporter);
```

```csharp
/// <summary>One authenticated-call outcome (first line of the status service data).</summary>
/// <param name="Succeeded">Whether an authenticated call to the remote succeeded.</param>
/// <param name="Error">Sanitized short error (no stack, no query strings), null on success.</param>
/// <param name="AuthFailed">True when the error is a definitive 401/403.</param>
public sealed record RemoteApiReport(bool Succeeded, string? Error, bool AuthFailed);
```

Error sanitization rule (implemented by `SanitizeError` below): report **fixed category strings only, never raw exception text** — `FederationException` with status code → `remote returned {code}` (never include the URL: it may carry `ApiKey`), `FederationException` without status → `remote error`, `TaskCanceledException` → `timeout`, anything else (`HttpRequestException` et al.) → `unreachable`. The status service adds `probe failed` for its own probe errors. Keep the raw exception out of the report (invariant 9 — the status surface is logged).

- [ ] **Step 1: Write failing tests** (append to `RemoteApiClientTests.cs`, before existing helper classes at the end of the file; check the file first for where facts end). Also add the `ThrowingHandler` shown below at the end of the **same file, inside its `namespace Jellyfin.Plugin.Federation.Tests;` block** (alongside the existing `FakeHttpMessageHandler` helper) — Tasks 4 and 5 reference it from the same namespace.

```csharp
// Note: these tests reference RemoteApiReport (defined in the src namespace
// Jellyfin.Plugin.Federation). Add this using at the top of RemoteApiClientTests.cs:
//   using Jellyfin.Plugin.Federation;
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
        Assert.False(reported!.Value.Succeeded);
        Assert.True(reported.Value.AuthFailed);
        Assert.DoesNotContain("secret", reported.Value.Error, StringComparison.Ordinal);
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
        Assert.False(reported!.Value.Succeeded);
        Assert.True(reported.Value.AuthFailed);
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
        Assert.False(reported!.Value.Succeeded);
        Assert.False(reported.Value.AuthFailed);
    }
}

/// <summary>Shared handler that always throws HttpRequestException (remote down). Top-level, shared by Tasks 1, 4, 5.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("connection refused");
}
```

- [ ] **Step 2: Run tests — expect compile failure** (`Prepare`/`SetReporter`/`LastAuthenticatedUtc` don't exist)

```bash
dotnet test -c Release --filter "FullyQualifiedName~RemoteApiClientPrepareTests"
```
Expected: **compile error** CS1061 (`Prepare` not found).

- [ ] **Step 3: Implement in `src/RemoteApiClient.cs`**

Split `ConfigureAsync` into `Prepare` (all of the current body except the final `return await LoginAsync(...)`; make it **synchronous**, return `void`) and keep `ConfigureAsync` as:

```csharp
/// <summary>
/// Prepares credentials and logs in (startup path). A failed login is
/// reported through the outcome reporter — startup 401s must surface as
/// InvalidCredentials, not a green Online probe.
/// </summary>
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
```

— so the reporter also sees **startup** login failures (the factory wires it via `clientSetup` before calling `ConfigureAsync`; the retry loop's `IsDefinitiveAuthFailure` filter guarantees a 401 is reported exactly once, no retry churn).

In `GetJsonAsync`, **before sending**: if `_accessToken` is empty → login proactively, single-flight under the existing `_loginLock` (the same pattern as `RefreshTokenAsync`; concurrent first callers share one login — a duplicate login would create an extra device row on the remote, invariant 2 territory):

```csharp
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
```

Then continue with the existing snapshot/401/refresh logic. Because the login is proactive, the first request never carries an empty `ApiKey` — no dead 401 round-trip.

Reporter wiring in `GetJsonAsync` (and only there — login failures during requests are reported through it):

```csharp
private Action<RemoteApiReport>? _reporter;
private DateTimeOffset? _lastAuthenticatedUtc;

/// <summary>Gets the UTC time of the last successful authenticated request, or null if none succeeded yet.</summary>
public DateTimeOffset? LastAuthenticatedUtc => _lastAuthenticatedUtc;

/// <summary>Sets the outcome reporter invoked after every authenticated request; null removes it.</summary>
public void SetReporter(Action<RemoteApiReport>? reporter) => _reporter = reporter;

private void Report(Exception ex)
{
    _reporter?.Invoke(new RemoteApiReport(
        Succeeded: false,
        Error: SanitizeError(ex),
        AuthFailed: ex is FederationException { StatusCode: 401 or 403 }));
}

private static string SanitizeError(Exception ex)
    => ex switch
    {
        FederationException f => f.StatusCode is null ? "remote error" : $"remote returned {f.StatusCode}",
        TaskCanceledException => "timeout",
        _ => "unreachable"
    };
```

Call `Report(ex)` in the existing catch-free flow by wrapping: simplest correct shape is to add try/catch around the whole `GetJsonAsync` body:

```csharp
private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
{
    try
    {
        // empty-token login (above) + existing snapshot/401/refresh body
        ...
        _lastAuthenticatedUtc = DateTimeOffset.UtcNow;
        _reporter?.Invoke(new RemoteApiReport(true, null, false));
        return result; // (set by the existing body)
    }
    catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
    {
        Report(ex);
        throw;
    }
}
```

Note the reporter is invoked *before* rethrowing — the channel's own catch/log flow stays unchanged.

- [ ] **Step 4: Run the new tests**

```bash
dotnet test -c Release --filter "FullyQualifiedName~RemoteApiClientPrepareTests"
```
Expected: 7 PASS.

- [ ] **Step 5: Full regression** — `dotnet test -c Release` (44 existing + 7 new = 51 pass). Existing tests reference `ConfigureAsync` — they keep working since it still exists. Then build both targets. Commit:

```python
# eval commit tool
d = await commit.draft()
await d.commit("feat: remote client prepare mode, auth tracking, outcome reporter", ["- Prepare(alias, url, user, password) stores credentials without login;", "  views-cache fallback (later task) builds token-less channels that self-heal", "  via the existing 401 re-login path.", "- GetJsonAsync: token-less first request triggers exactly one login.", "- LastAuthenticatedUtc + sanitized outcome reporter feed the status service."])
```

---

### Task 2: `FederationViewsCache` — persist and load per-instance view sets

**Files:**
- Create: `src/FederationViewsCache.cs`
- Create: `tests/Jellyfin.Plugin.Federation.Tests/FederationViewsCacheTests.cs`
- Modify: both csproj (no change needed — SDK default glob picks up new `.cs` in `src/`; container csproj has `../src/**/*.cs` glob. Verify with the builds.)

**Design:** One JSON file per instance under `{DataPath}/federation/{sha256(alias + "|" + serverUrl + "|" + username)[..16]}/views.json` (username in the key: a credential rotation must not rebuild the *previous* user's library set). Content: the view list with final channel names (dedup suffix included) so a cache-built channel is byte-identical to a live one. Written **only from the factory's successful startup path** (never from TestCredentials — unsaved credentials must not pollute the cache; spec line 123). Corrupt/missing file → empty list (treated as absent), never throws. A failed write (disk full, permissions) is logged and swallowed — a cache problem must never kill live channel creation or be misdiagnosed as a connectivity failure.

```csharp
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
```

- [ ] **Step 1: Write failing tests** — `tests/Jellyfin.Plugin.Federation.Tests/FederationViewsCacheTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests — expect compile failure**

```bash
dotnet test -c Release --filter "FullyQualifiedName~FederationViewsCacheTests"
```
Expected: CS0246 (`CachedView`/`FederationViewsCache` not found).

- [ ] **Step 3: Create `src/FederationViewsCache.cs`** with the code above.

- [ ] **Step 4: Run tests — expect 5 PASS.** Build both targets (glob check: new file appears in both). Commit:

```python
d = await commit.draft()
await d.commit("feat: per-instance views cache for down-at-startup channel rebuild", [])
```

---

### Task 3: Factory — cache write on success, cache fallback on transient failure

**Files:**
- Modify: `src/FederationChannelFactory.cs`
- Modify: `src/FederationChannelOptions.cs` (no change — "from cache" is a startup detail logged by the factory, not channel state)
- Create: `tests/Jellyfin.Plugin.Federation.Tests/FederationChannelFactoryCacheTests.cs` (new file; `ThrowingHandler` not needed here)

**Design:** Factory gets an optional `FederationViewsCache` constructor parameter (default null = cache disabled, existing tests unaffected). Flow per instance:

1. Live path (unchanged): retry login+views (4 attempts, 3 s, definitive 401/403 excluded — invariant 3). On success: build channels from live views (existing dedup loop), then `cache.Save(alias, serverUrl, username, cachedViews)` where `cachedViews` mirrors the built channels (including dedup suffixes), and `status.RegisterChannels(alias, names)`.
2. Fallback: when the retry loop gives up with a **transient** error (`!IsDefinitiveAuthFailure(ex)`) and the cache has views → `client.Prepare(...)` (token-less) and build channels from the cached `ChannelName`/`RemoteLibraryId` values directly (no live views needed; **names come from the cache**, not recomputed — byte-stability). Log `LogWarning("Federation: {Alias} unreachable at startup; using cached views for {Count} channels", ...)`.
3. Definitive 401/403 or no cache → current behavior (skip, `LogError`).

Extract the per-view channel construction so both paths share it. New factory signature:

```csharp
public FederationChannelFactory(
    Func<RemoteApiClient> clientFactory,
    ILogger logger,
    FederationViewsCache? viewsCache = null,
    TimeSpan? loginRetryDelay = null)
```

(Keep parameter order backward-compatible: existing call sites use `(clientFactory, logger)` and `(clientFactory, logger, TimeSpan)` — the third parameter changes meaning! **Check existing callers:** registrator uses `(clientFactory, logger)`, tests use `(clientFactory, logger, TimeSpan)`. Change the tests' constructor to named argument `loginRetryDelay:` — or put `viewsCache` last. **Decision: put `viewsCache` last** to avoid touching all test call sites:

```csharp
public FederationChannelFactory(
    Func<RemoteApiClient> clientFactory,
    ILogger logger,
    TimeSpan? loginRetryDelay = null,
    FederationViewsCache? viewsCache = null,
    Action<RemoteApiClient, string>? clientSetup = null,
    FederationStatusService? status = null)
{
    // ... existing assignments ...
    _viewsCache = viewsCache;
    _clientSetup = clientSetup; // invoked right after _clientFactory() in both live and fallback paths
    _status = status;
}
```

All parameters default, so every existing test call site keeps compiling (the existing `(clientFactory, logger, TimeSpan)` sites are unaffected — `loginRetryDelay` keeps its position).

Fallback-detection detail: the retry loop's exception after `MaxLoginAttempts` needs classification. The existing catch-all at the bottom (`catch (Exception ex) { _logger.LogError(...) }`) becomes: record `lastError`; after the loop, if `viewsCache` has views for the instance and `!IsDefinitiveAuthFailure(lastError)` → build from cache, else rethrow/skip as today.

Implementation sketch (replace the per-instance `try` block body — keep the dedup suffix logic in a helper so both paths use it; the *live* path must keep its exact current name algorithm, tests pin it):

```csharp
// inside the existing foreach (var instance in configuration.Instances):
var serverUrl = instance.ServerUrl.TrimEnd('/');
RemoteApiClient? client = null;
Exception? lastError = null;
QueryResult<BaseItemDto>? views = null;
Guid remoteUserId = Guid.Empty;

client = _clientFactory();
_clientSetup?.Invoke(client, instance.Alias); // wire the outcome reporter (Task 6)
// existing retry loop assigns remoteUserId + views, or leaves lastError set

if (views is not null)
{
    var channelsForInstance = new List<(string ChannelName, string RemoteLibraryId, string LibraryName, string? CollectionType)>();
    foreach (var view in views.Items.Where(v => v.Type == BaseItemKind.CollectionFolder))
    {
        if (view.Name is null) { _logger.LogInformation(...); continue; }
        if (view.CollectionType is null || !SupportedCollectionTypes.Contains(view.CollectionType.Value)) { _logger.LogInformation(...); continue; }
        var channelName = RegisterUniqueChannelName(seenNames, instance.Alias, view.Name, view.Id);
        channelsForInstance.Add((channelName, view.Id.ToString("N"), view.Name, view.CollectionType.Value.ToString()));
        channels.Add(new FederationChannel(new FederationChannelOptions(instance.Alias, channelName, view.Id.ToString("N"), serverUrl), client, _logger));
    }

    _viewsCache?.Save(instance.Alias, serverUrl, instance.Username, channelsForInstance
        .Select(c => new CachedView(c.RemoteLibraryId, c.LibraryName, c.CollectionType, c.ChannelName))
        .ToList());
    _status?.RegisterChannels(instance.Alias, channelsForInstance.Select(c => c.ChannelName).ToArray());
}
else if (lastError is not null && !IsDefinitiveAuthFailure(lastError))
{
    var cached = _viewsCache?.Load(instance.Alias, serverUrl, instance.Username) ?? Array.Empty<CachedView>();
    if (cached.Count > 0)
    {
        _logger.LogWarning(lastError,
            "Federation: {Alias} ({Url}) unreachable at startup; rebuilding {Count} channels from cached views",
            instance.Alias, serverUrl, cached.Count);
        var builtNames = new List<string>(cached.Count);
        foreach (var view in cached)
        {
            if (!seenNames.Add(view.ChannelName))
            {
                continue; // defensive: cache already contains unique names
            }

            channels.Add(new FederationChannel(
                new FederationChannelOptions(instance.Alias, view.ChannelName, view.RemoteLibraryId, serverUrl),
                client,
                _logger));
            builtNames.Add(view.ChannelName);
        }

        // Prepare only if the client has no token yet (all logins failed:
        // remote down). If a login had succeeded (views fetch failed), the token
        // is already set and re-rolling the DeviceId would invalidate it
        // (invariant 2).
        if (string.IsNullOrEmpty(client.AccessToken))
        {
            client.Prepare(instance.Alias, serverUrl, instance.Username, instance.Password);
        }

        _status?.RegisterChannels(instance.Alias, builtNames.ToArray());
    }
    else
    {
        _logger.LogError(lastError, "Federation: cannot connect to {Alias} ({Url})", instance.Alias, serverUrl);
    }
}
else if (lastError is not null)
{
    _logger.LogError(lastError, "Federation: cannot connect to {Alias} ({Url})", instance.Alias, serverUrl);
}
```

Extract `RegisterUniqueChannelName(HashSet<string> seenNames, string alias, string libraryName, Guid viewId)` containing the current dedup block verbatim (returns the unique name). The fallback path uses the *cached* names directly (already deduped when written), re-adding them to `seenNames` keeps later live instances from colliding.

Careful with the client lifecycle: in the fallback path `client.Prepare` must happen **before** any channel uses it; the channels hold the client reference. In the live path, `ConfigureAsync` already prepared it.

- [ ] **Step 1: Write failing tests** — `tests/Jellyfin.Plugin.Federation.Tests/FederationChannelFactoryCacheTests.cs` (new file; copy the `CreateHandler`/`CreateConfig`/`CreateValidInstance` helpers from `FederationChannelFactoryTests.cs`):

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
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
```

- [ ] **Step 2: Run — expect compile failure** (no `viewsCache`/`status` ctor params).
- [ ] **Step 3: Implement** per the sketch. Move the current dedup block into `RegisterUniqueChannelName` verbatim; keep the live path behavior byte-identical (existing tests must pass unchanged).
- [ ] **Step 4: Run new tests (5 PASS) + full suite (all pass). Build both targets.** Commit:

```python
d = await commit.draft()
await d.commit("feat: factory falls back to cached views when remote unreachable at startup", ["- Live path unchanged; cache written after a successful views fetch.", "- Transient failure + cache: token-less channels rebuilt with byte-stable names.", "- Definitive 401/403 never falls back (invariant 3)."])
```

---

### Task 4: `FederationStatusService` — unauthenticated reachability loop

**Files:**
- Create: `src/FederationStatusService.cs`
- Create: `tests/Jellyfin.Plugin.Federation.Tests/FederationStatusServiceTests.cs`

**Design:** In-memory per-instance state + background loop. **Invariant 11: never authenticates** — probe is `GET {url}/System/Info/Public` with a plain `HttpClient` (5 s timeout), parsed as `PublicSystemInfo` (`MediaBrowser.Model/System`, fields `ServerName`, `Version`). Backoff: online → 30 s; offline → 30/60/120/240/480/600 s (cap). Injected `TimeProvider`-like abstraction for tests: constructor takes `Func<DateTimeOffset>` clock and a `Func<TimeSpan, CancellationToken, Task>` delay (defaults: real). Remote client failures reported by `RemoteApiClient` (Task 1) update the same state.

```csharp
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Reachability state of one federation instance.
/// </summary>
public enum FederationInstanceState
{
    /// <summary>Never probed yet.</summary>
    Unknown,

    /// <summary>Answers /System/Info/Public.</summary>
    Online,

    /// <summary>Probe failed (connection/timeout/5xx).</summary>
    Offline,

    /// <summary>Authenticating as the configured user failed with 401/403.</summary>
    InvalidCredentials
}

/// <summary>
/// In-memory per-instance status. Written by the probe loop and by request-outcome
/// reports; read by the status API.
/// </summary>
public class FederationStatusService : IHostedService
{
    private readonly ConcurrentDictionary<string, InstanceStatus> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextProbe = new(StringComparer.Ordinal); // alias → earliest next probe
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan[] _backoffSteps = { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(600) };
    private readonly Func<PluginConfiguration> _configProvider;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationStatusService"/> class.
    /// </summary>
    /// <param name="configProvider">Provides the loaded configuration (fixed at startup).</param>
    /// <param name="httpClient">Dedicated probe client (5 s timeout, no auth header).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Time source (injectable for deterministic backoff tests).</param>
    /// <param name="delay">Loop delay (injectable; defaults to real Task.Delay).</param>
    public FederationStatusService(
        Func<PluginConfiguration> configProvider,
        HttpClient httpClient,
        ILogger logger,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _configProvider = configProvider;
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    /// <summary>Registers one instance (called once at startup with the loaded config; gives rows their URL/username).</summary>
    public void RegisterInstance(FederationInstance instance)
        => _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a)
        {
            ServerUrl = instance.ServerUrl.TrimEnd('/'),
            Username = instance.Username
        });

    /// <summary>Records the channel names an instance contributes (called by the factory after channel creation).</summary>
    public void RegisterChannels(string alias, string[] channelNames)
        => _states.GetOrAdd(alias, a => new InstanceStatus(a)).Channels = channelNames;

    /// <summary>True if <paramref name="alias"/> was registered at startup (has a configured instance).</summary>
    public bool IsTracked(string? alias) => !string.IsNullOrWhiteSpace(alias) && _states.ContainsKey(alias!);

    /// <summary>Records an authenticated-call outcome from a RemoteApiClient (Task 1 reporter).</summary>
    public void ReportOutcome(string alias, RemoteApiReport report)
    {
        var state = _states.GetOrAdd(alias, a => new InstanceStatus(a));
        if (report.Succeeded)
        {
            state.State = FederationInstanceState.Online; // an authenticated call succeeded → remote is up and credentials work
            state.LastError = null;
        }
        else if (report.AuthFailed)
        {
            state.State = FederationInstanceState.InvalidCredentials;
            state.LastError = report.Error;
        }
        else
        {
            // Any other failure (unreachable, timeout, 5xx/4xx from the remote):
            // record it. InvalidCredentials is sticky — only a success clears it.
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Offline;
            }

            state.LastError = report.Error;
        }

        state.LastAuthenticatedUtc = report.Succeeded ? _clock() : state.LastAuthenticatedUtc;
    }

    /// <summary>Gets a snapshot of all instance states (status API reads this).</summary>
    public IReadOnlyList<InstanceStatus> Snapshot() => _states.Values.OrderBy(s => s.Alias).ToList();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ProbeLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected at shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Probes one instance once (testable entry point of the loop body):
    /// unauthenticated GET /System/Info/Public, updates state and the due-time map.
    /// </summary>
    /// <param name="instance">The instance to probe.</param>
    /// <param name="ct">Cancellation.</param>
    public Task ProbeOnceAsync(FederationInstance instance, CancellationToken ct)
        => ProbeInstanceAsync(instance, ct);

    /// <summary>Gets when the given alias is due for its next probe (test surface for backoff assertions).</summary>
    /// <param name="alias">Instance alias.</param>
    /// <returns>The earliest allowed next probe time, or null if never probed.</returns>
    public DateTimeOffset? NextProbeDue(string alias)
        => _nextProbe.TryGetValue(alias, out var due) ? due : null;

    /// <summary>Probes one instance; updates the state and the per-alias due time.</summary>
    private async Task ProbeInstanceAsync(FederationInstance instance, CancellationToken ct)
    {
        var serverUrl = instance.ServerUrl.TrimEnd('/');
        try
        {
            // Invariant 11: unauthenticated probe only.
            using var response = await _httpClient.GetAsync(serverUrl + "/System/Info/Public", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A non-2xx from a reachable remote is "remote returned {code}",
                // distinct from "unreachable" (no connection at all). The
                // two-arg ctor sets StatusCode (the one-arg one does not).
                throw new FederationException($"Remote server returned {(int)response.StatusCode} for {serverUrl}/System/Info/Public", (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var info = await JsonSerializer.DeserializeAsync<PublicSystemInfo>(stream, FederationJsonOptions.Options, ct).ConfigureAwait(false);
            var state = _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a));
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Online;
            }

            state.RemoteServerName = info?.ServerName;
            state.RemoteVersion = info?.Version;
            state.LastCheckedUtc = _clock();
            state.ConsecutiveFailures = 0;
            _nextProbe[instance.Alias] = _clock() + _backoffSteps[0];
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var state = _states.GetOrAdd(instance.Alias, a => new InstanceStatus(a));
            if (state.State != FederationInstanceState.InvalidCredentials)
            {
                state.State = FederationInstanceState.Offline;
            }

            state.LastError = ex switch
            {
                FederationException { StatusCode: not null } f => $"remote returned {f.StatusCode}",
                TaskCanceledException => "timeout",
                HttpRequestException => "unreachable",
                _ => "probe failed"
            };
            state.LastCheckedUtc = _clock();
            // Back off on every failure kind (refused, timeout, 5xx), not just some:
            // a non-2xx remote that never recovers must not be re-probed every 5 s.
            var step = Math.Min(state.ConsecutiveFailures, _backoffSteps.Length - 1);
            _nextProbe[instance.Alias] = _clock() + _backoffSteps[step];
            state.ConsecutiveFailures++;
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        // Per-instance offset so instances don't synchronize (spec): a never-seen
        // alias gets its due time seeded with a hash-derived offset and is
        // skipped this cycle (first real probe on the next one).
        while (!ct.IsCancellationRequested)
        {
            foreach (var instance in _configProvider().Instances.Where(i => !string.IsNullOrWhiteSpace(i.ServerUrl)))
            {
                if (ct.IsCancellationRequested) { break; }

                if (!_nextProbe.TryGetValue(instance.Alias, out var due))
                {
                    var offset = TimeSpan.FromSeconds(Math.Abs(instance.Alias.GetHashCode()) % 15);
                    _nextProbe[instance.Alias] = _clock() + offset;
                    continue;
                }

                if (_clock() < due)
                {
                    continue;
                }

                try
                {
                    await ProbeInstanceAsync(instance, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }

            await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); // loop granularity; per-instance due times gate the probes
        }
    }
}
```

`InstanceStatus` (same file; constructor `public InstanceStatus(string alias)` sets `Alias = alias; Channels = Array.Empty<string>();`): `Alias`, `State`, `LastError` (string?), `LastCheckedUtc` (DateTimeOffset?), `LastAuthenticatedUtc` (DateTimeOffset?), `RemoteServerName`, `RemoteVersion` (string?), `ServerUrl` (string?, from `RegisterInstance`), `Username` (string?, from `RegisterInstance`), `Channels` (IReadOnlyList<string>, non-null, from `RegisterChannels`), `ConsecutiveFailures` (int, internal). XML-doc everything public.

Per-instance probe offset (spec: "per-instance offset so instances don't synchronize"): implemented in `ProbeLoopAsync` only — when an alias has no entry in `_nextProbe` yet, seed it with `_clock() + offset` where `offset = TimeSpan.FromSeconds(Math.Abs(alias.GetHashCode()) % 15)` and **skip** the probe that cycle (the instance gets probed on the next one). `ProbeInstanceAsync`/`ProbeOnceAsync` therefore never apply the offset, keeping the tests' exact due-time assertions valid (`clock.Now + 30 s` after first failure, `+ 60 s` after the second, …).

- [ ] **Step 1: Write failing tests** — `FederationStatusServiceTests.cs`:

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationStatusServiceTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static FederationInstance Instance(string alias = "paul", string url = "https://remote.example.com:8920")
        => new() { Alias = alias, ServerUrl = url, Username = "paul", Password = "x" };

    private static FederationStatusService Create(FakeHttpMessageHandler handler, out FakeClock clock)
    {
        clock = new FakeClock(DateTimeOffset.Parse("2026-11-22T10:00:00Z"));
        var config = new PluginConfiguration();
        config.Instances.Add(Instance());
        // Injected delay: the loop never waits 5 s in tests (instant completion is
        // fine — StartStop_SmokeTest only proves the lifecycle does not throw).
        var service = new FederationStatusService(() => config, new HttpClient(handler), Logger, () => clock.Now, (_, _) => Task.CompletedTask);
        service.RegisterInstance(Instance());
        return service;
    }

    [Fact]
    public async Task ProbeOnce_Online_SetsStateAndPublicInfo()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"paul-server","Version":"10.10.0"}""");
        var service = Create(handler, out _);

        await service.ProbeOnceAsync(Instance(), CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Online, state.State);
        Assert.Equal("paul-server", state.RemoteServerName);
        Assert.Equal("10.10.0", state.RemoteVersion);
        // Invariant 11: the probe must never authenticate.
        var probe = Assert.Single(handler.Requests);
        Assert.Equal("/System/Info/Public", probe.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("ApiKey=", probe.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeOnce_Failure_MarksOfflineAndBacksOff()
    {
        var service = Create(new ThrowingHandler(), out var clock);
        var instance = Instance();

        await service.ProbeOnceAsync(instance, CancellationToken.None);
        var firstDue = service.NextProbeDue("paul");
        await service.ProbeOnceAsync(instance, CancellationToken.None);
        var secondDue = service.NextProbeDue("paul");
        await service.ProbeOnceAsync(instance, CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Offline, state.State);
        Assert.Equal("unreachable", state.LastError);
        // Backoff grows: 30s → 60s → 120s (cap 600s).
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(30), firstDue);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(60), secondDue);
    }

    [Fact]
    public async Task ProbeOnce_503_MarksOfflineAndBacksOffLikeRefused()
    {
        // Spec: 5xx from the probe also backs off — a non-2xx remote must not
        // be re-probed on every loop tick, and it is classified as a remote
        // error ("remote returned 503"), not "unreachable".
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            string.Empty,
            HttpStatusCode.ServiceUnavailable);
        var service = Create(handler, out _);
        var instance = Instance();

        await service.ProbeOnceAsync(instance, CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Offline, state.State);
        Assert.Equal("remote returned 503", state.LastError);
        Assert.NotNull(service.NextProbeDue("paul"));
    }

    [Fact]
    public async Task ProbeOnce_Success_DoesNotClearInvalidCredentials()
    {
        // 401 from an authenticated call is definitive: a green public-info probe
        // must NOT flip the state back to Online (the user must see the credential
        // problem, not a green dot).
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"paul-server","Version":"10.10.0"}""");
        var service = Create(handler, out _);

        service.ReportOutcome("paul", new RemoteApiReport(false, "remote returned 401", true));
        await service.ProbeOnceAsync(Instance(), CancellationToken.None);

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.InvalidCredentials, state.State);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public void ReportOutcome_Success_RecordsLastAuthenticated()
    {
        var service = Create(new FakeHttpMessageHandler(), out var clock);

        service.ReportOutcome("paul", new RemoteApiReport(true, null, false));

        var state = service.Snapshot().Single();
        Assert.Equal(FederationInstanceState.Online, state.State);
        Assert.Equal(clock.Now, state.LastAuthenticatedUtc);
    }

    [Fact]
    public async Task StartStop_SmokeTest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.RequestUri!.AbsolutePath == "/System/Info/Public",
            """{"ServerName":"s","Version":"1"}""");
        var service = Create(handler, out _);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        Assert.NotEmpty(service.Snapshot());
    }

    /// <summary>Fixed clock for deterministic backoff assertions; stays put unless `Advance` is called — `ProbeOnceAsync` must never advance it.</summary>
    internal sealed class FakeClock
    {
        public FakeClock(DateTimeOffset start) => Now = start;

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan span) => Now += span;
    }

}
```

(`ThrowingHandler` is already defined top-level in `RemoteApiClientTests.cs` by Task 1 — the same test assembly/namespace, so it is visible here.)

Notes:
- `FederationStatusService` constructor for production (Task 6 wiring): `(Func<PluginConfiguration>, HttpClient, ILogger)` with optional `Func<DateTimeOffset>? clock = null` and `Func<TimeSpan, CancellationToken, Task>? delay = null`. The loop's delay is injectable so `StartStop_SmokeTest` completes in milliseconds (a 5 s real delay would tax every test run; a cancelled delay throws OCE immediately, which `StopAsync` already swallows).
- `ProbeOnceAsync` and `NextProbeDue` are **public** (the repo has no `InternalsVisibleTo`; public surface of the service class is fine — the controller lives in the same assembly). The due-time map is a **member** (`ConcurrentDictionary<string, DateTimeOffset>`), not loop-local — `ProbeOnceAsync` reads/writes it, `NextProbeDue` exposes it, and the sketch's loop-local `nextProbe` variable must be replaced by that member.

- [ ] **Step 2: Run — expect compile failure.**
- [ ] **Step 3: Implement** `src/FederationStatusService.cs` (code sketch above; refine to the `ProbeOnceAsync` shape for testability).
- [ ] **Step 4: Run new tests (≥4 PASS) + full suite + both builds.** Commit:

```python
d = await commit.draft()
await d.commit("feat: unauthenticated reachability status service (invariant 11)", ["- GET /System/Info/Public only; never AuthenticateByName, never ApiKey.", "- Exponential backoff 30s..600s; InvalidCredentials is sticky against probe success."])
```

---

### Task 5: `FederationController` — status + test-credentials endpoints

**Files:**
- Create: `src/FederationController.cs`
- Create: `tests/Jellyfin.Plugin.Federation.Tests/FederationControllerTests.cs`
- Modify: `container/Jellyfin.Plugin.Federation.Container.csproj` (add `FrameworkReference`)

**Design:** Plain `ControllerBase` (the host auto-registers plugin assemblies containing `ControllerBase`-derived public non-abstract types). All endpoints `[Authorize(Policy = Policies.RequiresElevation)]` (constant from `MediaBrowser.Common.Api`). `TestCredentials` creates a throwaway `RemoteApiClient` (fresh `HttpClient` over a handler injected for tests), one login + views fetch, disposes. The persistent clients are never touched.

```csharp
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// Federation status and connection-test endpoints. Admin-only: instance URLs and
/// usernames are not for regular users (the host's default policy would allow any
/// authenticated user — elevation is explicit).
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("[controller]")] // → /Federation
public class FederationController : ControllerBase
{
    private readonly FederationStatusService _status;
    private readonly Func<RemoteApiClient> _testClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationController"/> class.
    /// </summary>
    /// <param name="status">Status service singleton.</param>
    /// <param name="testClientFactory">Creates a throwaway client for connection tests.</param>
    public FederationController(FederationStatusService status, Func<RemoteApiClient> testClientFactory)
    {
        _status = status;
        _testClientFactory = testClientFactory;
    }

    /// <summary>
    /// Gets the per-instance status snapshot.
    /// </summary>
    /// <returns>Status rows; never fails on remote errors (they are data).</returns>
    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
        => Ok(_status.Snapshot()
            .Select(s => new
            {
                s.Alias,
                s.ServerUrl,
                s.Username,
                s.State,
                s.LastError,
                s.LastCheckedUtc,
                s.LastAuthenticatedUtc,
                s.RemoteServerName,
                s.RemoteVersion,
                s.Channels
            })
            .ToList());

    /// <summary>
    /// Tests credentials against one remote: exactly one login and views fetch with
    /// a throwaway client (never the persistent one — invariant 2). The outcome is
    /// also fed to the status service (a failed test sets InvalidCredentials; a
    /// successful test clears it).
    /// </summary>
    /// <param name="request">Instance data (alias/server/username/password as entered, not necessarily saved).</param>
    /// <returns>ok + libraryCount, or a reason; wrong credentials count exactly one failed login on the remote.</returns>
    [HttpPost("TestCredentials")]
    public async Task<ActionResult<object>> TestCredentials([FromBody] TestCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ServerUrl)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { ok = false, reason = "ServerError" });
        }

        using var client = _testClientFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // bounded: a stalled remote must not hang the dashboard
        try
        {
            var userId = await client.ConfigureAsync(request.Alias ?? "federation", request.ServerUrl.TrimEnd('/'), request.Username, request.Password, cts.Token).ConfigureAwait(false);
            var views = await client.GetViewsAsync(userId, cts.Token).ConfigureAwait(false);
            var libraryCount = views.Items.Count(v => v.Type == MediaBrowser.Model.Dto.BaseItemKind.CollectionFolder);
            ReportTestOutcome(request.Alias, true, null);
            return Ok(new { ok = true, libraryCount });
        }
        catch (FederationException ex) when (ex.StatusCode is 401 or 403)
        {
            ReportTestOutcome(request.Alias, false, "InvalidCredentials");
            return Ok(new { ok = false, reason = "InvalidCredentials" });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ReportTestOutcome(request.Alias, false, "Unreachable");
            return Ok(new { ok = false, reason = "Unreachable" });
        }
        catch (Exception)
        {
            // Catch-all: any other failure (e.g. FederationException with null
            // StatusCode — "Login ... returned no access token", malformed JSON,
            // invalid Uri) becomes a clean JSON error, not a raw 500.
            ReportTestOutcome(request.Alias, false, "ServerError");
            return Ok(new { ok = false, reason = "ServerError" });
        }
    }

    private void ReportTestOutcome(string? alias, bool ok, string? reason)
    {
        // TestCredentials may run for an unsaved (new) alias — report only for
        // aliases the status service already tracks, so a typo or not-yet-saved
        // instance cannot create a ghost status row.
        if (!_status.IsTracked(alias))
        {
            return;
        }

        _status.ReportOutcome(
            alias,
            ok
                ? new RemoteApiReport(true, null, false)
                : new RemoteApiReport(false, reason, reason == "InvalidCredentials"));
    }
}

/// <summary>
/// Connection-test request payload.
/// </summary>
public sealed record TestCredentialsRequest(string? Alias, string ServerUrl, string Username, string Password);
```

Notes:
- `InstanceStatus` carries `ServerUrl` and `Username` (set in `RegisterInstance`, Task 4); the snapshot response includes them; the **password is never part of `InstanceStatus`**. No `GetServerUrl` lookup exists.
- `Ok(new {...})` anonymous types: the spec's DTO requirement — anonymous types serialize fine and keep the response stable enough for our own page; a named DTO adds no value here (single consumer). Keep anonymous.
- `[ApiController]` requires the ASP.NET Core framework reference in the **container** csproj (probe-verified). Add to `container/Jellyfin.Plugin.Federation.Container.csproj` after the Compile glob:

```xml
    <!-- The source-checkout target inherits Microsoft.AspNetCore.App transitively
         through the MediaBrowser.Common project reference; plain HintPath DLL
         references do not flow FrameworkReferences, so the container target needs
         this explicitly. Compile-time only — the host loads the shared framework. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```

- `TestCredentialsRequest` JSON casing: MVC uses the host's configured JSON (camelCase for model binding is case-insensitive by default) — fine.

- [ ] **Step 1: Write failing tests** — `FederationControllerTests.cs`. The controller is testable by direct instantiation (plain `ControllerBase`, no host needed):

```csharp
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationControllerTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static FederationStatusService CreateStatus()
    {
        var handler = new FakeHttpMessageHandler();
        var config = new PluginConfiguration();
        var service = new FederationStatusService(() => config, new HttpClient(handler), Logger);
        return service;
    }

    [Fact]
    public void GetStatus_ReturnsSnapshotWithoutSecrets()
    {
        var status = CreateStatus();
        status.RegisterInstance(new FederationInstance { Alias = "paul", ServerUrl = "https://remote.example.com:8920", Username = "paul", Password = "secret-token" });
        var controller = new FederationController(status, () => throw new InvalidOperationException("not used"));

        var result = controller.GetStatus();

        var json = JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value!, FederationJsonOptions.Options);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestCredentials_Success_ReturnsLibraryCount()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            File.ReadAllText("Data/authenticate-response.json"));
        handler.Add(
            req => req.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal),
            File.ReadAllText("Data/views-response.json"));
        var controller = new FederationController(CreateStatus(), () => new RemoteApiClient(new HttpClient(handler)));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://remote.example.com:8920", "paul", "secret"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("\"ok\":true", json.Replace("True", "true"), StringComparison.OrdinalIgnoreCase);
        // Exactly one login (lockout safety).
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"));
    }

    [Fact]
    public async Task TestCredentials_401_ReturnsInvalidCredentials_SingleLogin()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Add(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/Users/AuthenticateByName",
            string.Empty,
            HttpStatusCode.Unauthorized);
        var status = CreateStatus();
        status.RegisterInstance(new FederationInstance { Alias = "paul", ServerUrl = "https://remote.example.com:8920", Username = "paul", Password = "x" });
        var controller = new FederationController(status, () => new RemoteApiClient(new HttpClient(handler)));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://remote.example.com:8920", "paul", "wrong"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("InvalidCredentials", json, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests.Count);
        // The failed test also updates the status service (spec phase 2).
        var row = status.Snapshot().Single(s => s.Alias == "paul");
        Assert.Equal(FederationInstanceState.InvalidCredentials, row.State);
    }

    [Fact]
    public async Task TestCredentials_Unreachable_ReturnsUnreachable()
    {
        var controller = new FederationController(CreateStatus(), () => new RemoteApiClient(new HttpClient(new ThrowingHandler())));

        var result = await controller.TestCredentials(new TestCredentialsRequest("paul", "https://down.example.com", "paul", "x"));

        var value = Assert.IsType<OkObjectResult>(result.Result);
        var json = JsonSerializer.Serialize(value.Value!, FederationJsonOptions.Options);
        Assert.Contains("Unreachable", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_AreAdminOnly()
    {
        // The host's default policy only requires authentication; our endpoints
        // must pin RequiresElevation explicitly (verified host behavior).
        var authorize = typeof(FederationController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("RequiresElevation", authorize!.Policy);
    }

}
```

- [ ] **Step 2: Run — compile failure.**
- [ ] **Step 3: Implement** controller + `FrameworkReference` in the container csproj. Note `using System.Reflection;` for the attribute test.
- [ ] **Step 4: Run new tests (5 PASS) + full suite + BOTH builds — the container build is the critical verification for this task.** Commit:

```python
d = await commit.draft()
await d.commit("feat: admin-gated status + test-credentials endpoints", ["- ControllerBase auto-registered by the host (GetApiPluginAssemblies).", "- TestCredentials: throwaway client, exactly one login, disposed.", "- Container csproj gains explicit AspNetCore FrameworkReference (probe-verified)."])
```

---

### Task 6: Registrator — wire status service, cache, reporters

**Files:**
- Modify: `src/FederationPluginServiceRegistrator.cs`
- Test: none new (wiring; the pieces are covered). Manual verification in Task 9.

**Design:** The registrator builds `FederationViewsCache` (with `appPaths.DataPath`), constructs `FederationStatusService` with the loaded config provider and a probe `HttpClient` (5 s timeout), registers all instances on it (so status rows carry `ServerUrl`/`Username` from startup), and passes the cache + status to the factory (Task 3 added the `viewsCache`/`clientSetup`/`status` constructor parameters). The `clientSetup` hook wires each client's outcome reporter to `status.ReportOutcome(alias, report)`. After channel creation, it registers the status service in DI (as singleton + `IHostedService`) and the throwaway-client factory for the controller's TestCredentials:

```csharp
// In RegisterServices, after var config = LoadConfig(...):
var viewsCache = new FederationViewsCache(appPaths.DataPath, logger);

var status = new FederationStatusService(
    () => config, // config is fixed at startup (restart-on-change semantics), so a closure is correct
    new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
    logger);

// Register every instance so status rows carry ServerUrl/Username from startup
// (the probe loop alone would only give it state, not the identifying fields).
foreach (var instance in config.Instances)
{
    status.RegisterInstance(instance);
}

var factory = new FederationChannelFactory(
    () => new RemoteApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }),
    logger,
    viewsCache: viewsCache,
    clientSetup: (client, alias) => client.SetReporter(report => status.ReportOutcome(alias, report)),
    status: status); // so the factory can feed channel names + cache paths into the status service

// after channels were created (existing code):
serviceCollection.AddSingleton(status);
serviceCollection.AddSingleton<IHostedService>(status);

// The controller's test-credentials factory: a throwaway client per test
// (fresh DeviceId via ConfigureAsync — invariant 2 is satisfied).
serviceCollection.AddSingleton<Func<RemoteApiClient>>(
    sp => () => new RemoteApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
```

`IHostedService` requires `using Microsoft.Extensions.Hosting;` — available through the shared framework (source target: transitive; container target: after Task 5's FrameworkReference).

- [ ] **Step 1: Implement** (wiring only; the constructor signature change from Task 3 gains the `clientSetup` parameter — add it there or here consistently; do it here as one edit, keeping default null so all tests keep compiling).
- [ ] **Step 2: Build both targets + full test suite passes.** Commit:

```python
d = await commit.draft()
await d.commit("feat: wire status service, views cache and outcome reporters into DI", [])
```

---

### Task 7: Embedded dashboard pages (settings + status)

> **Deviation (implemented):** the final build ships **two** embedded pages, not
> one. `config.html` is the settings page only (instances form, no status
> panel) with `EnableInMainMenu = false`; a new `status.html` is the main-menu
> entry (`EnableInMainMenu = true`) that polls `Federation/Status/Public`.
> `config.html` also dropped the admin-detection branching (`getCurrentUser`):
> it is the admin-only settings surface and always renders the form. See the
> spec's "Deviations" (Two pages, not one) and the spec's phase 1/2 tables,
> which are the authoritative design. The verbatim `config.html` below is the
> originally-planned single-page content; treat the settings-only + status.html
> split as the shipped reality.

**Files:**
- Create: `src/Web/config.html` (settings page, inline JS) + `src/Web/status.html` (status page, inline JS)
- Modify: `src/Plugin.cs` (implement `IHasWebPages`, return two `PluginPageInfo`s)
- Modify: `Jellyfin.Plugin.Federation.csproj` + `container/...csproj` (embedded resource, both pages)
- Test: `tests/Jellyfin.Plugin.Federation.Tests/PluginPageTests.cs`

**Design:** Host page pattern (verified from `MusicBrainz/Configuration/config.html`): `data-role="page"` root, `data-require="emby-input,emby-button"` (settings page only), `pageshow` → `ApiClient.getPluginConfiguration(pluginId)`, form submit → `ApiClient.updatePluginConfiguration(pluginId, config).then(Dashboard.processPluginConfigurationUpdateResult)`. The **settings** page (`config.html`) has no status panel and no admin-branching. The **status** page (`status.html`) polls `Federation/Status/Public` (reduced, any authenticated user) every 5 s via the ApiClient AJAX helper `ApiClient.ajax({ url: ApiClient.getUrl('Federation/Status/Public'), type: 'GET' })`. Restart (settings page): `ApiClient.ajax({ url: ApiClient.getUrl('System/Restart'), type: 'POST' })`.

`config.html` complete content:

```html
<!DOCTYPE html>
<html>
<head>
    <title>Jellyfin Federation</title>
</head>
<body>
    <div id="federationConfigPage" data-role="page" class="page type-interior pluginConfigurationPage federationConfigPage" data-require="emby-input,emby-button">
        <div data-role="content">
            <div class="content-primary">
                <h1>Jellyfin Federation</h1>

                <div id="federationStatusPanel" class="verticalSection">
                    <h2>Status</h2>
                    <div id="federationStatusTable"></div>
                </div>

                <div class="verticalSection">
                    <h2>Instances</h2>
                    <div class="fieldDescription" style="margin-bottom:1em;">
                        Each instance connects to one remote Jellyfin server as the given user.
                        Changes take effect after a server restart.
                    </div>
                    <div id="federationInstances"></div>
                    <button is="emby-button" type="button" id="addInstanceBtn" class="raised block"><span>Add instance</span></button>
                </div>

                <div id="federationSaveSection" class="verticalSection">
                    <div id="saveError" class="fieldDescription" style="color:#f44336;"></div>
                    <form class="configForm">
                        <div>
                            <button is="emby-button" type="submit" class="raised button-submit block"><span>Save</span></button>
                        </div>
                    </form>
                </div>
                <div id="restartSection" style="display:none; margin-top:1em;">
                    <div class="fieldDescription" style="margin-bottom:.5em;">Saved — restart the server to apply the changes.</div>
                    <button is="emby-button" type="button" id="restartNowBtn" class="raised block"><span>Restart now</span></button>
                </div>
            </div>
        </div>
        <script type="text/javascript">
            var FederationPluginConfig = { uniquePluginId: '30c06888-1907-48cc-957a-6d83e86a39d7' };

            function el(tag, attrs, text) {
                var e = document.createElement(tag);
                Object.keys(attrs || {}).forEach(function (k) { e.setAttribute(k, attrs[k]); });
                if (text) { e.textContent = text; }
                return e;
            }

            function instanceRow(instance) {
                var container = el('div', { 'class': 'instanceContainer', 'style': 'border:1px solid rgba(128,128,128,.3); border-radius:.3em; padding:1em; margin-bottom:1em;' });
                ['Alias', 'ServerUrl', 'Username', 'Password'].forEach(function (field) {
                    var wrap = el('div', { 'class': 'inputContainer' });
                    var input = el('input', {
                        'is': 'emby-input',
                        'type': field === 'Password' ? 'password' : 'text',
                        'id': 'fed-' + field + '-' + instance.__idx,
                        'label': field === 'ServerUrl' ? 'Server URL' : field
                    });
                    input.value = instance[field] || '';
                    wrap.appendChild(input);
                    container.appendChild(wrap);
                });
                var testBtn = el('button', { 'is': 'emby-button', 'type': 'button', 'class': 'raised', 'data-idx': instance.__idx });
                testBtn.textContent = 'Test connection';
                testBtn.addEventListener('click', function () {
                    testConnection(parseInt(this.getAttribute('data-idx'), 10));
                });
                container.appendChild(testBtn);
                var removeBtn = el('button', { 'is': 'emby-button', 'type': 'button', 'class': 'raised', 'data-idx': instance.__idx });
                removeBtn.textContent = 'Remove';
                removeBtn.addEventListener('click', function () {
                    // Re-sync from the DOM first so unsaved edits in any row are
                    // kept, and the splice index (valid for the current render)
                    // matches the collected array.
                    window.__fedInstances = collectInstances();
                    container.parentNode.removeChild(container);
                    window.__fedInstances.splice(instance.__idx, 1);
                    renderInstances();
                });
                container.appendChild(removeBtn);
                var result = el('div', { 'class': 'testResult fieldDescription', 'id': 'fed-test-' + instance.__idx });
                container.appendChild(result);
                return container;
            }

            function renderInstances() {
                var host = document.getElementById('federationInstances');
                host.innerHTML = '';
                window.__fedInstances.forEach(function (instance, idx) {
                    instance.__idx = idx;
                    host.appendChild(instanceRow(instance));
                });
            }

            function collectInstances() {
                return window.__fedInstances.map(function (instance, idx) {
                    return {
                        Alias: document.getElementById('fed-Alias-' + idx).value.trim(),
                        ServerUrl: document.getElementById('fed-ServerUrl-' + idx).value.trim(),
                        Username: document.getElementById('fed-Username-' + idx).value.trim(),
                        Password: document.getElementById('fed-Password-' + idx).value
                    };
                });
            }

            function validate(instances) {
                for (var i = 0; i < instances.length; i++) {
                    var inst = instances[i];
                    if (!inst.Alias || !inst.Username) { return 'Alias and username are required.'; }
                    if (inst.Alias.indexOf('\u00b7') !== -1) { return 'Alias must not contain "\u00b7".'; }
                    if (!/^https?:\/\/.+/.test(inst.ServerUrl)) { return 'Server URL must start with http:// or https://'; }
                }
                var aliases = instances.map(function (i) { return i.Alias.toLowerCase(); });
                var dup = aliases.some(function (a, idx) { return aliases.indexOf(a) !== idx; });
                if (dup) { return 'Duplicate aliases are not allowed.'; }
                return null;
            }

            function testConnection(idx) {
                var instances = collectInstances();
                var inst = instances[idx];
                var resultEl = document.getElementById('fed-test-' + idx);
                if (!inst.ServerUrl) { resultEl.textContent = 'Server URL is required.'; return; }
                var error = validate([inst]);
                if (error) { resultEl.textContent = error; return; }
                resultEl.textContent = 'Testing...';
                ApiClient.ajax({
                    url: ApiClient.getUrl('Federation/TestCredentials'),
                    type: 'POST',
                    data: JSON.stringify({ Alias: inst.Alias, ServerUrl: inst.ServerUrl, Username: inst.Username, Password: inst.Password }),
                    contentType: 'application/json'
                }).then(function (result) {
                    if (result.ok) { resultEl.textContent = 'OK — ' + result.libraryCount + ' libraries visible.'; }
                    else if (result.reason === 'InvalidCredentials') { resultEl.textContent = 'Login failed: wrong username or password.'; }
                    else if (result.reason === 'Unreachable') { resultEl.textContent = 'Server unreachable.'; }
                    else { resultEl.textContent = 'Server error.'; }
                }, function () { resultEl.textContent = 'Request failed.'; });
            }

            function renderStatus(rows) {
                var host = document.getElementById('federationStatusTable');
                host.innerHTML = '';
                var table = el('div', { 'class': 'itemsContainer vertical-wrap centered' });
                rows.forEach(function (row) {
                    var line = el('div', { 'style': 'display:flex; gap:1em; align-items:center; padding:.2em 0;' });
                    var dot = el('span', {
                        'style': 'width:.8em; height:.8em; border-radius:50%; display:inline-block; margin-right:.4em; background:'
                            + (row.State === 'Online' ? '#4caf50' : row.State === 'InvalidCredentials' ? '#f44336' : '#ff9800')
                    });
                    line.appendChild(dot);
                    line.appendChild(el('strong', {}, row.Alias));
                    var meta = row.State + (row.RemoteServerName ? ' — ' + row.RemoteServerName + ' ' + (row.RemoteVersion || '') : '')
                        + (row.LastError ? ' (last error: ' + row.LastError + ')' : '')
                        + (row.LastAuthenticatedUtc ? ' · last authenticated: ' + row.LastAuthenticatedUtc : '');
                    // Spec phase 3: no channels yet + remote not online → hint that
                    // a restart creates them once B is reachable.
                    if ((!row.Channels || !row.Channels.length) && row.State !== 'Online' && row.State !== 'InvalidCredentials') {
                        meta += ' — no channels yet; restart A once B is reachable to create them';
                    }
                    line.appendChild(el('span', { 'class': 'fieldDescription' }, meta));
                    table.appendChild(line);
                    if (row.Channels && row.Channels.length) {
                        table.appendChild(el('div', { 'class': 'fieldDescription', 'style': 'margin-left:1.2em;' },
                            row.Channels.join(', ')));
                    }
                });
                if (!rows.length) { table.appendChild(el('div', { 'class': 'fieldDescription' }, 'No instances configured.')); }
                host.appendChild(table);
            }

            function refreshStatus() {
                ApiClient.ajax({ url: ApiClient.getUrl('Federation/Status'), type: 'GET' })
                    .then(renderStatus, function () {});
            }

            document.querySelector('.federationConfigPage')
                .addEventListener('pageshow', function () {
                    // Clear a previous poller (revisits must not stack intervals).
                    if (window.__fedStatusTimer) { window.clearInterval(window.__fedStatusTimer); }
                    refreshStatus();
                    window.__fedStatusTimer = window.setInterval(refreshStatus, 5000);
                    Dashboard.showLoadingMsg();
                    ApiClient.getPluginConfiguration(FederationPluginConfig.uniquePluginId).then(function (config) {
                        window.__fedInstances = (config.Instances || []).map(function (i) {
                            return { Alias: i.Alias, ServerUrl: i.ServerUrl, Username: i.Username, Password: i.Password };
                        });
                        renderInstances();
                        Dashboard.hideLoadingMsg();
                    }, function () {
                        Dashboard.hideLoadingMsg();
                        document.getElementById('saveError').textContent = 'Failed to load configuration.';
                    });
                });


            document.getElementById('addInstanceBtn')
                .addEventListener('click', function () {
                    // Keep any unsaved edits in the currently rendered rows.
                    window.__fedInstances = collectInstances();
                    window.__fedInstances.push({ Alias: '', ServerUrl: '', Username: '', Password: '' });
                    renderInstances();
                });

            document.querySelector('.configForm')
                .addEventListener('submit', function (e) {
                    var instances = collectInstances();
                    var error = validate(instances);
                    var msg = document.getElementById('saveError');
                    if (error) {
                        msg.textContent = error;
                        e.preventDefault();
                        return false;
                    }

                    Dashboard.showLoadingMsg();
                    ApiClient.getPluginConfiguration(FederationPluginConfig.uniquePluginId).then(function (config) {
                        config.Instances = instances;
                        ApiClient.updatePluginConfiguration(FederationPluginConfig.uniquePluginId, config).then(function () {
                            Dashboard.processPluginConfigurationUpdateResult();
                            document.getElementById('restartSection').style.display = '';
                        }, function () {
                            Dashboard.hideLoadingMsg();
                            msg.textContent = 'Save failed.';
                        });
                    }, function () {
                        Dashboard.hideLoadingMsg();
                        msg.textContent = 'Save failed.';
                    });

                    e.preventDefault();
                    return false;
                });

            document.getElementById('restartNowBtn')
                .addEventListener('click', function () {
                    Dashboard.confirm('Restart the server now?', 'Restart').then(function () {
                        ApiClient.ajax({ url: ApiClient.getUrl('System/Restart'), type: 'POST' });
                    });
                });
        </script>
    </div>
</body>
</html>
```

The `saveError` div is included in the HTML above; the status-poll interval intentionally has no `pagebeforehide` cleanup (the event is unreliable in the new dashboard; the interval is cheap and dies with the SPA session). The HTML above is final — no post-hoc edits.

`Plugin.cs` change:

```csharp
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // ... existing members ...

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
        => new[]
        {
            // Settings: Dashboard → Plugins only.
            new PluginPageInfo
            {
                Name = "Jellyfin Federation",
                DisplayName = "Jellyfin Federation",
                EmbeddedResourcePath = GetType().Assembly.GetName().Name + ".Web.config.html",
                EnableInMainMenu = false
            },
            // Status: main menu entry; polls Federation/Status/Public.
            new PluginPageInfo
            {
                Name = "Jellyfin Federation Status",
                DisplayName = "Jellyfin Federation Status",
                EmbeddedResourcePath = GetType().Assembly.GetName().Name + ".Web.status.html",
                EnableInMainMenu = true
            }
        };
}
```

(usings: `MediaBrowser.Model.Plugins`; the resource name for a file at `src/Web/config.html` in an SDK project with default root namespace is `Jellyfin.Plugin.Federation.Web.config.html` — verify via the resource test below.)

csproj additions (both targets; main csproj — the SDK glob does **not** auto-embed `.html`):

```xml
  <ItemGroup>
    <EmbeddedResource Include="src/Web/config.html" />
    <EmbeddedResource Include="src/Web/status.html" />
  </ItemGroup>
```

For the container csproj the paths are relative to its own directory (`container/`): `<EmbeddedResource Include="../src/Web/config.html" LogicalName="Jellyfin.Plugin.Federation.Web.config.html" />` and the same for `status.html`. The manifest resource name derives from the **RootNamespace + logical path**: both csprojs set `RootNamespace` to `Jellyfin.Plugin.Federation` — for a file *outside* the project cone (container), MSBuild computes the resource name from `RootNamespace` + the relative path from the project directory including `..` — this yields `Jellyfin.Plugin.Federation..src.Web.config.html` (dots for `..`)! **Mitigation: set `LogicalName` explicitly** on both so the manifest name is identical and stable:

```xml
<EmbeddedResource Include="src/Web/config.html" LogicalName="Jellyfin.Plugin.Federation.Web.config.html" />
<EmbeddedResource Include="src/Web/status.html" LogicalName="Jellyfin.Plugin.Federation.Web.status.html" />
```

- [ ] **Step 1: Write failing tests** — `PluginPageTests.cs` (add the config round-trip test, see note below):


```csharp
using System.IO;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Federation;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PluginPageTests
{
    [Fact]
    public void Plugin_ImplementsIHasWebPages()
    {
        Assert.Contains(typeof(MediaBrowser.Model.Plugins.IHasWebPages), typeof(Plugin).GetInterfaces());
    }

    [Fact]
    public void EmbeddedResource_ExistsInAssembly()
    {
        var assembly = typeof(Plugin).Assembly;
        var names = assembly.GetManifestResourceNames();

        Assert.Contains("Jellyfin.Plugin.Federation.Web.config.html", names);
        Assert.Contains("Jellyfin.Plugin.Federation.Web.status.html", names);
    }

    [Fact]
    public void EmbeddedResource_ContainsNoPluginIdPlaceholder()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Federation.Web.config.html");
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains("30c06888-1907-48cc-957a-6d83e86a39d7", html); // uniquePluginId matches Plugin.Id
        Assert.DoesNotContain("TBD", html, StringComparison.Ordinal);
    }

    // Spec "Testing" table: config round-trip. The host's plugin-config endpoints
    // (PluginsController) serialize with JsonDefaults.Options — PascalCase
    // (PropertyNamingPolicy = null). The page JS must use exactly those keys;
    // a rename in either direction would silently produce an empty form or drop
    // fields on save, so pin the contract here.
    [Fact]
    public void PluginConfiguration_RoundTripsThroughHostJsonContract()
    {
        var config = new PluginConfiguration();
        config.Instances.Add(new FederationInstance
        {
            Alias = "paul",
            ServerUrl = "https://remote.example.com:8920",
            Username = "paul",
            Password = "secret"
        });

        var json = JsonSerializer.Serialize(config, Jellyfin.Extensions.Json.JsonDefaults.Options);

        // The keys the page JS reads and writes (config.Instances / i.Alias / ...).
        Assert.Contains("\"Instances\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Alias\":\"paul\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ServerUrl\":\"https://remote.example.com:8920\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Username\":\"paul\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Password\":\"secret\"", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<PluginConfiguration>(json, Jellyfin.Extensions.Json.JsonDefaults.Options);
        Assert.NotNull(roundTripped);
        var inst = Assert.Single(roundTripped!.Instances);
        Assert.Equal("paul", inst.Alias);
        Assert.Equal("https://remote.example.com:8920", inst.ServerUrl);
        Assert.Equal("paul", inst.Username);
        Assert.Equal("secret", inst.Password);
    }
}
```

- [ ] **Step 2: Run — fail** (no `IHasWebPages`, no resource).
- [ ] **Step 3: Implement** — create `src/Web/config.html` (content above, verbatim and final), modify `Plugin.cs`, add `EmbeddedResource` items with `LogicalName` to both csprojs.
- [ ] **Step 4: Run new tests (4 PASS) + full suite + both builds.** Commit:

```python
d = await commit.draft()
await d.commit("feat: embedded dashboard page — settings, status, test, restart", ["- Host plugin-page pattern (TMDb/MusicBrainz); inline loading, ApiClient globals.", "- Polls Federation/Status every 5s; Test uses throwaway endpoint; restart via System/Restart."])
```

---

### Task 8: Documentation — README, AGENTS.md, docs/design.md

**Files:**
- Modify: `README.md`, `AGENTS.md`, `docs/design.md`

- [ ] **Step 1: README.md** — replace section "### 4. Configure A" intro paragraph: keep the XML instructions (file still works) and add after them:

```markdown
### 4. Configure A — dashboard (recommended)

In A's dashboard under **Plugins → Jellyfin Federation** you can manage instances
(alias, server URL, username, password), test each connection, and save. Saving
writes the same XML file shown above; **changes apply after a server restart** —
the page offers a *Restart now* button. The same page shows a live status table
(reachability, remote version, last error, last authenticated contact) for every
configured instance.
```

Replace in **Limitations (V1)** the lines:
- `- The remote must be reachable **at A's startup**; see above.` → `- A remote that is unreachable at A's startup still shows its channels (from the last known libraries) and starts working as soon as it is reachable again. A **newly added** remote that has never been reachable produces no channels until the next restart.`
- Add: `- The status monitor never logs in to the remote; it only calls the unauthenticated /System/Info/Public. Credential problems are detected through failed authenticated requests or the page's Test button.`
- Config line stays (`- Config changes require a restart of A.`).

- [ ] **Step 2: AGENTS.md** — after invariant 10 add:

```markdown
11. **The status monitor never authenticates.** `FederationStatusService` only
    calls the remote's unauthenticated `/System/Info/Public`. A login per probe
    would create device rows per cycle or (stable DeviceId) revoke tokens baked
    into persisted image URLs — invariants 2 and 3 apply to *every* code path that
    talks to the remote, not just startup. Credential validity reaches the status
    only via failed authenticated requests (reported through `RemoteApiClient`'s
    outcome reporter) or the UI's one-shot Test button.

12. **The views cache is the only path to channels without a live login.**
    `FederationViewsCache` persists the last successful view set (including the
    final deduped channel names) and the factory falls back to it **only** when
    the startup login fails transiently — never on definitive 401/403, never
    from TestCredentials (unsaved credentials must not pollute the cache). Cache
    entries are keyed by `sha256(alias|trimmed serverUrl|username)` — the
    username is in the key so a credential rotation does not rebuild the
    *previous* user's library set.
```

Also extend the "No new dependencies" sentence in Code Style with: `...; the ASP.NET Core shared framework (Microsoft.AspNetCore.App) is referenced for the plugin's MVC controller and hosted service — it is loaded by the host at runtime, and the container csproj must declare the FrameworkReference explicitly (HintPath DLL references do not flow FrameworkReferences).`

- [ ] **Step 3: docs/design.md** — at the end of the "Fehlerbehandlung" section add:

```markdown
- **Status & UI (2026-11-22, feat/ui-and-status):** Dashboard page (IHasWebPages,
  host config API), admin-only Federation controller (status snapshot, one-shot
  TestCredentials), unauthenticated reachability loop (invariant 11), views cache
  fallback (invariant 12). Details: docs/pi/specs/2026-11-22-ui-and-status-design.md
```

- [ ] **Step 4: Commit**

```python
d = await commit.draft()
await d.commit("docs: UI/status/down-resilience in README, AGENTS invariants 11-12", [])
```

---

### Task 9: End-to-end verification

- [ ] **Step 1: Full suite + both builds**

```bash
export DOTNET_ROOT=/nix/store/47cpgncd1v5ilpans6n6rnzjhfsrmsv7-dotnet-sdk-10.0.300/share/dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet test -c Release
dotnet build -c Release
dotnet build -c Release container/Jellyfin.Plugin.Federation.Container.csproj
```
Expected: all tests pass, both builds 0 errors.

- [ ] **Step 2: Manual smoke (web surface)** — deploy the container DLL per AGENTS.md to the test server, restart, open Dashboard → Plugins → Jellyfin Federation. Verify: instance form loads saved config; Test returns library count; Save → restart banner appears; Restart now triggers a restart; status panel shows state/version/channels; after stopping the remote, Status shows Offline within ~30–60 s and the channels still appear in the sidebar (cached views); an instance that is down at startup **without** a cache shows the "no channels yet; restart A once B is reachable" hint. Record deviations; fix and re-run.

- [ ] **Step 3: Final commit** of any fixes; then update `docs/design.md` status line if needed.
