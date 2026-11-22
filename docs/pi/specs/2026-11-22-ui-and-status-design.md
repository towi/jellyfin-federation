# Jellyfin Federation — UI, Status & Down-Resilience

**Date:**    2026-11-22
**Branch:**  `feat/ui-and-status`
**Status:**  Approved design (2026-11-22), not yet implemented
**Target:**  ../jellyfin master / 13.0.0-dev (7c2c5948c6); linuxserver/jellyfin 12.2.0 (container build)

## Goal

Three features for the federation plugin, all without host changes:

1. **Config via UI** — manage federation instances (alias, server URL, username,
   password) from the Jellyfin dashboard instead of hand-editing the XML.
2. **Down-safety** — a remote that is unreachable must not silently degrade:
   reachability is monitored, channels created earlier keep working and
   self-heal when the remote returns, and invalid credentials are surfaced.
3. **Status page** — per-remote live state (reachable, remote version, last
   error, channels, last authenticated contact) in the same dashboard page.

Scope decision (user, 2026-11-22): *all three phases in one spec*; restart model
*save + explicit restart button*; UI placed *only under Dashboard → Plugins*
(no main-menu entry); spec language English.

## Verified host facts (all checked against ../jellyfin)

| Fact                                                                                        | Evidence                                                                                          |
|---------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------|
| Plugins expose dashboard pages via `IHasWebPages`/`PluginPageInfo`; the host serves them as embedded resources        | `Jellyfin.Api/Controllers/DashboardController.cs:73-95` (`GET web/ConfigurationPage?name=…`)   |
| jellyfin-web loads plugin pages **inline** into the dashboard (not an iframe); the page JS runs with the authenticated dashboard `ApiClient` | `src/components/ServerContentPage.tsx` + `apps/dashboard/routes/routes.tsx:48-49` (`view='/web/configurationpage'`) |
| The host serves plugin config via `GET/POST /Plugins/{id}/Configuration`, persisting through `BasePlugin.SaveConfiguration` (same XML file this plugin already uses) | `Jellyfin.Api/Controllers/PluginsController.cs:164-213`; `MediaBrowser.Common/Plugins/BasePluginOfT.cs:155-176` |
| Plugin assemblies are automatically registered as MVC application parts; a plugin `ControllerBase` subclass just works  | `Emby.Server.Implementations/ApplicationHost.cs:1010` (`GetApiPluginAssemblies`); `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:158-161` |
| The host's plugin-config endpoints are **admin-only**: `PluginsController` carries a class-level `[Authorize(Policy = Policies.RequiresElevation)]` covering `GET/POST /Plugins/{id}/Configuration` | `Jellyfin.Api/Controllers/PluginsController.cs:25-27`                                                          |
| The default auth policy requires authentication only — **not** admin; elevation needs an explicit policy | `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:63-78`; `Jellyfin.Api/Auth/.../DefaultAuthorizationHandler.cs:47-70` |
| `POST /System/Restart` exists (`LocalAccessOrRequiresElevation`)                        | `Jellyfin.Api/Controllers/SystemController.cs:114-124`                                            |
| `ChannelManager` receives `IEnumerable<IChannel>` once in its constructor; **no** runtime add/remove API → no hot channel registration | `src/Jellyfin.LiveTv/Channels/ChannelManager.cs:81-95` (`internal IChannel[] Channels`)       |
| Plugin services registered via `IPluginServiceRegistrator` land in the same `IServiceCollection` the generic host builds → plugin `IHostedService`s run | `Emby.Server.Implementations/ApplicationHost.cs:490-492`; `Jellyfin.Server/Program.cs:178-193` |
| `MediaBrowser.Model`/`MediaBrowser.Common` carry `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, which flows through **project** references only | `MediaBrowser.Common/MediaBrowser.Common.csproj:17`; **probe-verified**: a `ControllerBase` compiles in the source-checkout target but fails with CS0234 in the container target until the container csproj gains an explicit `FrameworkReference` |
| Host `GetDashboardConfigurationPage` has **no** `[Authorize]` attribute (any authenticated user can fetch plugin page HTML); page HTML must therefore contain no secrets, real data comes only through admin-gated API calls | `DashboardController.cs:73-95`                                                                   |

## Non-goals

- No hot-reload of channels: `ChannelManager` is fixed at startup (see facts).
  Config changes (including newly added remotes) always apply after a restart.
  Making this work would require a host change — out of scope by project rule.
- No automatic restart after saving (deliberate user decision).
- No self-federation, no play-state sync, no remote transcoding (unchanged V1
  limitations).
- The reachability monitor never authenticates (see invariant 11) — credential
  validity is only tested on demand.
- No localization of the plugin page (English UI strings, like the host's own
  plugin pages).

## Architecture

```mermaid
flowchart TB
  subgraph UI["Dashboard (admin, embedded page)"]
    C["config.html (single page, inline JS)<br/>status panel + settings form"]
  end
  subgraph API["FederationController (plugin MVC, admin-only)"]
    T["POST Federation/TestCredentials"]
    S["GET Federation/Status"]
  end
  subgraph RT["Runtime (DI singletons)"]
    R["FederationStatusService (IHostedService)<br/>unauthenticated GET /System/Info/Public,<br/>backoff, state machine"]
    F["FederationChannelFactory<br/>(+ views-cache fallback)"]
    VC["FederationViewsCache (views.json)"]
    CA["FederationChannel + RemoteApiClient<br/>(existing; 401 self-heal)"]
  end
  B["Remote B"]

  C -- "ApiClient (authenticated, same-origin)" --> T
  C -- "poll every 5 s" --> S
  S --> R
  T -. "throwaway client, exactly 1 login, disposed" .-> B
  R -. "no auth, 5 s timeout" .-> B
  F -- "startup: login + views;<br/>fallback: views.json" --> CA
  F -- "write after successful views fetch" --> VC
  CA -. "401 -> re-login (existing)" .-> B
```

## Phase 1 — Config UI

Mechanism: **reuse the host's plugin-page pattern** (TMDb/MusicBrainz style).
No custom endpoint for config read/write, no auth plumbing, no token handling.

| Piece                          | Design                                                                                                                                                |
|--------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Plugin : IHasWebPages`        | `GetPages()` returns one `PluginPageInfo`: `Name = "Jellyfin Federation"`, `DisplayName = "Jellyfin Federation"`, `EmbeddedResourcePath = "Jellyfin.Plugin.Federation.Web.config.html"`, `EnableInMainMenu = false` (appears under Dashboard → Plugins only). |
| `src/Web/config.html`          | Single self-contained page (inline JS, no separate `config.js` — the host's own plugins keep JS inline in the html). `data-role="page"`, `data-require="emby-input,emby-button"`, `pageshow` handler. Loads via `ApiClient.getPluginConfiguration(pluginId)`, saves via `ApiClient.updatePluginConfiguration(pluginId, config)`. Layout: status panel on top, then the instances form (a single scrolling page rather than separate tabs — scope decision, see "Deviations" note). Vanilla JS, no framework, no external CDN (must work in offline/Tailscale setups). |
| Settings tab                   | One form block per instance: `Alias`, `ServerUrl`, `Username`, `Password` (masked input; value round-trips through the host config API like all host plugin pages — admin-only surface). Buttons: add instance, remove instance, per-instance **Test connection**, **Save**. Client-side validation: http/https URL parses, alias/username non-empty, duplicate alias → warning. The saved XML stays byte-compatible with the hand-edited format; the file remains the single source of truth (`BasePlugin.SaveConfiguration`). |
| Test connection                | `POST Federation/TestCredentials` (our own controller, introduced with this phase). Uses a **throwaway** `RemoteApiClient` (fresh `HttpClient`, fresh DeviceId suffix), performs exactly **one** login plus views fetch, then disposes. Never touches the persistent per-instance client — a login on the persistent client would roll a new DeviceId and orphan tokens baked into persisted image URLs (invariant 2). Result JSON: `{ ok: true, libraryCount: n }` or `{ ok: false, reason: "Unreachable" \| "InvalidCredentials" \| "ServerError" }` (auth failure = 401/403 from the login attempt; unreachable = connection/timeout; server error = any other non-success login status). |
| Save → restart                 | After `updatePluginConfiguration` succeeds, the page shows "Saved — restart required to apply" and a **Restart now** button that calls `POST System/Restart` through the authenticated dashboard client. No auto-restart, no `NotifyPendingRestart` (user decision). |

## Phase 2 — Status

| Piece                                | Design                                                                                                                                                                                                                                                                                                                                                             |
|--------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `FederationController : ControllerBase` | Plugin MVC controller (auto-registered as application part). All endpoints carry `[Authorize(Policy = Policies.RequiresElevation)]` — the host's *default* policy only requires authentication (verified), which would expose instance URLs to every logged-in user. `Policies` comes from `MediaBrowser.Common.Api` (referenced in both build targets). |
| `GET Federation/Status`              | Per instance: `alias`, `serverUrl`, `username` (**never** password), `state` ∈ `Unknown \| Online \| Offline \| InvalidCredentials`, `lastError` (a fixed sanitized category string — `unreachable` / `timeout` / `remote returned {code}` / `remote error` / `probe failed` — never raw exception text, so no stack, no query strings, no tokens can leak; invariant 9), `lastCheckedUtc`, `remoteVersion` + `remoteServerName` (from public info), `channels` (names of the channels this instance contributed), `lastAuthenticatedUtc` (last successful authenticated request of the persistent client, `null` if B was down at startup and it never logged in). State transitions: `InvalidCredentials` is set only by a failed login (401/403) — via the persistent client's re-login failure or the Test button; it clears back to `Online` on the next successful authenticated request or successful test. |
| `POST Federation/TestCredentials`    | See phase 1. Also admin-gated. Exactly one login attempt — a wrong password must feed the remote's lockout counter exactly once (invariant 3 extension to the UI path).                                                                                                  |
| `FederationStatusService`           | `IHostedService`, registered by `FederationPluginServiceRegistrator` (plugin registrations land in the host's service collection — verified). Own `HttpClient` (5 s timeout, **no** `Authorization` header). Per instance: `GET {serverUrl}/System/Info/Public`. Online: 30 s interval. Offline: exponential backoff 30 s → 60 s → 120 s → … capped at 10 min, per-instance offset so instances don't synchronize. |
| Status panel                     | Same embedded page, panel above the settings form (see "Deviations"). Polls `Federation/Status` every 5 s while visible. Renders per remote: colored state dot, remote name/version, last error, last authenticated contact, channel list; credential testing is the per-instance Test button in the settings form.                                                                                                       |

### New invariant 11 — the monitor never authenticates

`FederationStatusService` must never call `AuthenticateByName` or send the
access token. Reasons:

- A login per check would create a device row on the remote per check (with a
  fresh DeviceId) or — with a stable DeviceId — revoke the token baked into
  persisted image URLs (invariants 2, 3).
- Unauthenticated `/System/Info/Public` answers "is B up" plus version at
  near-zero cost and zero credential risk.

Credential validity (`InvalidCredentials`) enters the status only through (a)
the Test-connection button and (b) the persistent client's 401-re-login failure
(the existing single-shot path reports auth failure to the status service).

## Phase 3 — Survival (remote down)

| Piece                       | Design                                                                                                                                                                                                                                                                                                                                                                  |
|-----------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `FederationViewsCache`      | JSON file per instance: `{DataPath}/federation/{sha256(alias + "\|" + serverUrl)[..16]}/views.json`, content: per view — remote library id, name, collection type, and the **final channel name** including dedup suffix (recomputed identically on cache use so names stay byte-stable). Written **only after a successful startup login's views fetch** (the authoritative saved-credentials path — never from a Test-connection click, which may use unsaved credentials). Corrupt file → treated as absent (log warning). |
| Factory fallback            | If the startup login fails with a **transient** error (connection refused, timeout, 5xx/503 — **not** 401/403, which stay definitive per invariant 3) and a views cache exists for the instance, the factory builds the channels **from the cache**: same `FederationChannelOptions` (alias, channel name, remote library id, trimmed server URL), same `RemoteApiClient` instance, but **without a token**. |
| Self-healing                | A token-less client keeps credentials in memory. The first authenticated request triggers exactly one **proactive** login before sending — single-flight under the existing `_loginLock`, so concurrent callers share one login (a duplicate login would create an extra device row, invariant 2) — and no request is ever sent with an empty `ApiKey` (no dead 401 round-trip). The channel heals with zero new machinery when B returns. Mid-run token revocation/remote restarts keep using the existing single-shot 401 re-login path (`RefreshTokenAsync`). |
| DataVersion                 | Unchanged: `SHA256(alias \| remoteLibraryId \| serverUrl)` (invariant 10). Cache-built and live channels produce identical `DataVersion` → no cache churn, no re-hydration storm. |
| Documented expectations    | B down at A's start **with** cache: channels present in the sidebar; their content is bounded by the host's channel-item file cache (3 h), then empty until B is back. B down at A's start **without** cache (new instance, never seen): no channels — the status page shows `Offline`, plus the hint that a restart creates them once B is reachable. New/renamed libraries on B require an A restart (no runtime channel add — host constraint, see facts). |

## Error handling

- **Startup, remote down (no cache):** as today — instance skipped, `LogError`,
  server starts; additionally status row `Offline` with last error.
- **Startup, remote down (with cache):** channels from cache, `LogWarning`
  "using cached views from {date}", status `Offline`.
- **Browsing while B is down:** unchanged — the channel manager swallows
  provider exceptions as empty results; the plugin logs (`LogError`) and the
  status service records the failure — the reporting callback lives on
  `RemoteApiClient` itself (set once per instance by the factory), so every
  authenticated call path (browse, playback info, views) reports outcomes
  uniformly and `FederationChannel` stays unchanged. The status page thus
  reflects real request outcomes, not just probe results.
- **Credentials changed on B (mid-run):** request 401 → re-login → if that
  login also fails 401/403: single attempt, no retry loop (lockout), status
  becomes `InvalidCredentials`, channel errors surface to the client as today.
  The admin sees it on the status page, fixes the password via the UI, tests,
  saves, restarts.
- **Status API itself:** never throws on remote failure; unreachable remotes
  are data (`Offline` + `lastError`), not errors.

## Security review

- All new endpoints admin-only (`RequiresElevation`); the embedded page HTML
  contains no secrets (it is fetchable by any authenticated user — verified
  host behavior — so it only renders data it pulls from admin-gated APIs).
- No password, token, or `ApiKey`-bearing URL ever appears in `GET Status`
  responses or in the page; `lastError` strings are sanitized (invariant 9
  extends to the status surface).
- Test-connection and the monitor are lockout-safe: one login per click, zero
  logins from the loop (invariants 3 + 11).
- New device rows on B remain bounded as today: one per plugin start plus one
  per test click; the monitor adds none (invariant 2 consequence unchanged).
- The config file continues to hold the password in plaintext (`chmod 600`);
  the UI changes nothing about that (documented limitation stays).

## Testing (xUnit, `FakeHttpMessageHandler`, fully offline)

| Test                                                                                          | Pins                                                        |
|-----------------------------------------------------------------------------------------------|--------------------------------------------------------------|
| Status service: public info 200 → `Online` + version captured; 503 → backoff sequence 30/60/120 s (fake clock); connection refused → `Offline`; **no `AuthenticateByName` request in the whole handler log** | invariant 11, backoff                                        |
| TestCredentials: 200 → `ok + libraryCount`; 401 → `InvalidCredentials` with **exactly one** login request; unreachable → `Unreachable`                                        | single-shot, no retry (invariant 3 on the UI path)          |
| Factory fallback: transient failure + cache present → channels built from cache with identical names/`DataVersion`; transient failure without cache → no channels; 401 + cache → **no** channels (definitive); successful login → cache written; alias/URL change → cache ignored                                     | phase 3, invariant 10                                        |
| Token-less client: first `GetChannelItems` on a prepared client triggers exactly one **proactive** login (empty-token path), then the request goes out with the fresh token — exactly one login request in the whole handler log; concurrent calls share one login (existing `_loginLock`)                                                   | self-healing, invariant on the refresh race                  |
| Controller: `GET Status` response contains no password/token/ApiKey substring; `RequiresElevation` attribute present on all endpoints                                        | invariants 9, admin-gating                                   |
| Views cache: write/read round-trip; corrupt JSON → absent; final channel name (dedup suffix) survives round-trip                                                              | cache stability                                              |
| Config round-trip: `PluginConfiguration` round-trips through the host's JSON contract (`JsonDefaults.Options`, PascalCase) — the exact property keys the page JS reads/writes are pinned, so a rename in either direction fails the test; the host's `XmlSerializer` persistence path is pinned separately by the existing `LoadConfig` tests | phase 1 |

## Files

**New** (all under `src/`, namespace `Jellyfin.Plugin.Federation`):

- `Web/config.html` — embedded dashboard page (single self-contained page, inline JS; no separate `config.js`)
- `FederationController.cs` — `Status`, `TestCredentials` (anonymous response types; no `StatusModels.cs` — single consumer)
- `FederationStatusService.cs` — `IHostedService` reachability loop + in-memory state
- `FederationViewsCache.cs` — views cache load/save
- Tests: `FederationStatusServiceTests.cs`, `FederationControllerTests.cs`, `FederationViewsCacheTests.cs`, factory/channel additions to existing test files

**Changed**:

- `Plugin.cs` — implement `IHasWebPages`
- `RemoteApiClient.cs` — credential-without-token mode (factory fallback), `lastAuthenticatedUtc` tracking, and a reporting callback into the status service for request outcomes (success → `lastAuthenticatedUtc`, failure → `lastError`, 401/403 on re-login → `InvalidCredentials`)
- `FederationChannelFactory.cs` — cache fallback + cache write + status hooks
- `FederationPluginServiceRegistrator.cs` — register status service (hosted), controller dependencies, status singleton
- `Jellyfin.Plugin.Federation.csproj` — `<EmbeddedResource Include="Web/config.html" />` (+ js)
- `container/Jellyfin.Plugin.Federation.Container.csproj` — same `EmbeddedResource`; **`<FrameworkReference Include="Microsoft.AspNetCore.App" />`** (probe-verified: required for `ControllerBase`; the source-checkout target inherits it transitively through the `MediaBrowser.Common` project reference, the HintPath-DLL container target does not). One reference covers everything: `IHostedService` (`Microsoft.Extensions.Hosting.Abstractions`) and `AddHostedService` (`Microsoft.Extensions.DependencyInjection`) ship in the same shared framework (verified against the SDK ref pack) — no additional extracted container-refs DLLs needed.

**Docs**:

- `README.md` — configuration section becomes "via dashboard (or XML file, unchanged format)"; limitations list replaces "remote must be reachable at startup" with the survival behavior; new Status section
- `AGENTS.md` — new invariants 11 (monitor never authenticates) and 12 (views cache is the only path to channels without a live login; only transient failures qualify); allowed-reference note for the container `FrameworkReference`
- `docs/design.md` — link this spec, status "Extended (2026-11-22)"

## Implementation order

The plan deviates from this spec's phase order deliberately: the **reporter +
views-cache + status-service groundwork lands first** (plan Tasks 1–4) because
the UI and the status endpoints depend on them, and each task stays independently
green. Concretely:

1. **Phase 3 groundwork + token-less client** (plan Tasks 1–3: `RemoteApiClient`
   prepare/reporter, `FederationViewsCache`, factory fallback) — the
   self-healing client and cache the status page will display.
2. **Phase 2** (plan Tasks 4–5: status service, status + test-credentials
   endpoints) — makes phase 3 observable.
3. **Phase 1 UI** (plan Task 7: embedded page + `IHasWebPages`) — reads the
   already-existing endpoints; wiring (Task 6) and docs (Task 8) interleave.

Each task lands green: both csproj targets build, `dotnet test` passes, and the
task's tests fail without the task's change.

## Deviations from this approved spec

- **Single page, not tabs:** the plan ships one scrolling page (status panel on
  top, instances form below) instead of two tabs. Scope decision; the per-row
  Test button lives in the instances form (each instance row has its own
  "Test connection" button), so the status surface still offers per-remote
  credential testing. The `GET Federation/Status` contract (incl. `channels`)
  is implemented regardless.
- **Anonymous response types:** the controller returns anonymous types instead
  of a `StatusModels.cs` (single consumer; the response shape is pinned by the
  controller tests).
- **Inline JS:** the page keeps its JavaScript inline in `config.html` (host
  plugins do the same); no separate `config.js` file.

## Risks

- **Container `FrameworkReference`**: compile-time-only; the shared framework ships in every Jellyfin image (the host is an ASP.NET Core app). Probe-verified both directions (fails without, compiles with). Runtime DLL-availability risk: none — same assemblies the host already loads.
- **Plugin-page JS contract** (`ApiClient` globals, `emby-*` elements): pattern is stable across the host's own plugins (TMDb, MusicBrainz, ListenBrainz use it today), but needs a manual browser verification against the target dashboard once — part of phase 1 verification.
- **`GetDashboardConfigurationPage` is only default-policy protected** (any authenticated user): mitigated by keeping the page secret-free (no inline data).
- **`IHostedService` activation for plugins**: plugin registrations provably land in the host's service collection (verified call chain), but the first live run must confirm `StartAsync` fires (phase 2 verification step).
- **Inline-loading of plugin pages depends on jellyfin-web's view manager**: the deployed dashboard must keep `configurationpage` routing (present in current jellyfin-web master; verified). Older dashboards that iframe the page also work — the page uses no parent-frame access.
