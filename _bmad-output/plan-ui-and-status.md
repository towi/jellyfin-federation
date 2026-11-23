---
title: 'Federation UI, Status & Down-Resilience'
type: feature
ticket: ''
created: '2026-11-23'
status: in-progress
baseline_revision: 'c9d3ea522ebb272d4a897b2388e5d22f6f117bf7'
route: full
route_source: auto
risk: medium
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - docs/pi/plans/2026-11-22-ui-and-status.md
  - docs/pi/specs/2026-11-22-ui-and-status-design.md
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The plugin has no dashboard: no way to see whether each configured remote is reachable or its credentials valid, no way to test unsaved credentials before they hit the startup path, and when a remote is down at startup its channels silently disappear for the whole session (login fails, nothing is registered).

**Approach:** One embedded dashboard page (status panel + settings form, per-instance Test button, restart confirmation) backed by two admin-only controller endpoints (`GET Federation/Status`, `POST Federation/TestCredentials`), an `IHostedService` reachability probe (unauthenticated `GET /System/Info/Public` only, invariant 11), a per-instance views cache so channels survive a remote being down at startup, and token-less clients that self-heal with a single-flight proactive login when the remote returns.

## Boundaries & Constraints

**Always:**
- The reviewed plan in `context:` is the sole source of truth for all code sketches, test code, task order (Tasks 1→9), commit messages, and the final `config.html`; implement it verbatim, refining only where it explicitly says to.
- Both csproj targets build (`dotnet build -c Release` for both) and `dotnet test -c Release` passes (44 existing + 32 new = 76) after every task.
- AGENTS.md rules: `.ConfigureAwait(false)` in `src/`, `Federation:` log prefix, no new dependencies, XML doc on all public members, tests offline via `FakeHttpMessageHandler`.
- Invariants 1–11 unchanged (URL trim, DeviceId suffix, bounded retries, channel-name dedup, proxied stream path, people guard, no image self-healing, no self-federation, token in URLs, DataVersion); the plan adds invariant 12 (username in cache key) to AGENTS.md in Task 8.

**Never:**
- No authentication or `ApiKey` in the probe (invariant 11) — no `AuthenticateByName`, no login, ever, from the status service.
- No hot reload: config changes require a server restart (the restart button exists for that).
- No changes to the `../jellyfin` checkout; no new NuGet packages; no tabs (single scrolling page); no `config.js`.
- Never log or report raw exception text, query strings, or tokens — fixed sanitized categories only (invariant 9).
- The views cache is written only from the factory's successful startup path, never from TestCredentials (unsaved credentials must not pollute it).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Probe online | `GET /System/Info/Public` → 200 | State `Online`, version/name captured, next probe +30 s | — |
| Probe 503 | non-2xx from reachable remote | State `Offline`, `lastError` = `remote returned {code}`, backoff 30→600 s | No retry churn: backoff applies to every failure kind |
| Probe refused/timeout | connection error / 5 s timeout | State `Offline`, `lastError` = `unreachable` / `timeout`, backoff | — |
| Probe vs bad credentials | login previously 401 (sticky) | probe success does not clear `InvalidCredentials` | only an authenticated success clears it |
| Test credentials | throwaway client, one login + views fetch | `{ ok: true, libraryCount: n }`; throwaway disposed, persistent client untouched | 401/403 → `InvalidCredentials`; connection/timeout → `Unreachable`; anything else → `ServerError` (never raw text); unsaved alias → no status row created |
| Token-less client first call | prepared client, empty token, B back up | exactly one proactive login (single-flight, `_loginLock`), then request with fresh token; no request ever sent with empty `ApiKey` | login 401 → reported `AuthFailed`, status → `InvalidCredentials`, no retry |
| Startup, remote down | login fails transiently (refused/timeout/5xx), views cache exists | channels built from cache: same names/options, token-less client, same `DataVersion` | 401/403 login stays definitive (invariant 3): no cache, no channels; corrupt/missing cache file → empty list, never throws |
| Status response | any state | `alias`, `serverUrl`, `username`, `state`, `lastError`, `lastCheckedUtc`, `remoteVersion`, `remoteServerName`, `channels`, `lastAuthenticatedUtc` — never password, never token | — |
| Config round-trip | page saves via host plugin-config JSON (PascalCase, `JsonDefaults.Options`) | all four instance fields survive; XML persistence path unchanged | — |

</frozen-after-approval>

## Code Map

- `src/RemoteApiClient.cs` — add token-less `Prepare(alias, url, user, password)`, split of `ConfigureAsync`, empty-token single-flight login in `GetJsonAsync`, `SetReporter`/`LastAuthenticatedUtc`, `Report`/`SanitizeError` (fixed categories) — Task 1
- `src/RemoteApiReport.cs` — new `record RemoteApiReport(bool Succeeded, string? Error, bool AuthFailed)` — Task 1
- `src/FederationViewsCache.cs` — new: `{DataPath}/federation/{sha256(alias|url|username)[..16]}/views.json`, `Save`/`Load`, corrupt→empty, never throws — Task 2
- `src/FederationChannelFactory.cs` — ctor gains `viewsCache`/`clientSetup`/`status` (all defaulted); cache `Save` after successful login; transient-failure fallback builds channels from cache via token-less client — Task 3
- `src/FederationStatusService.cs` — new `IHostedService`: probe loop (offset-staggered, injectable clock/delay), `RegisterInstance`/`RegisterChannels`/`ReportOutcome`/`IsTracked`/`ProbeOnceAsync`/`NextProbeDue`/`Snapshot`; own `HttpClient` (5 s, no auth) — Task 4
- `src/FederationController.cs` — new `ControllerBase`, `[Authorize(Policy = "RequiresElevation")]`: `GET Status`, `POST TestCredentials` (throwaway client, 10 s CTS, catch-all → `ServerError`) — Task 5
- `src/FederationPluginServiceRegistrator.cs` — wire cache (`appPaths.DataPath`), status service (DI singleton + `IHostedService`), `clientSetup` → `status.ReportOutcome`, test-client factory — Task 6
- `src/Web/config.html` — new embedded resource (`LogicalName` in both csprojs); `src/Plugin.cs` implements `IHasWebPages` (`Name = "Jellyfin Federation"`, `EnableInMainMenu = false`) — Task 7
- `README.md`, `AGENTS.md` (invariant 12), `docs/design.md` — docs — Task 8
- `tests/Jellyfin.Plugin.Federation.Tests/` — 5 new test files (32 tests total) + additions to existing factory tests; all code in the plan, verbatim — Tasks 1–7
- Host reference (read-only): `../jellyfin/Jellyfin.Api/Controllers/PluginsController.cs` (config JSON = `JsonDefaults.Options`, PascalCase), MusicBrainz/TMDb page pattern

## Tasks & Acceptance

**Execution:** (each task = the matching `### Task N` section of the context plan, TDD: failing test → run → implement → green + full suite + both builds → commit with the plan's message)

- [ ] Task 1: `src/RemoteApiClient.cs`, `src/RemoteApiReport.cs` + `RemoteApiClientPrepareTests.cs` (7 tests) — token-less mode, reporting, proactive login
- [ ] Task 2: `src/FederationViewsCache.cs` + `FederationViewsCacheTests.cs` (5) — per-instance views cache under `DataPath`
- [ ] Task 3: `src/FederationChannelFactory.cs` + `FederationChannelFactoryCacheTests.cs` (5) — cache write + down-at-startup fallback
- [ ] Task 4: `src/FederationStatusService.cs` + `FederationStatusServiceTests.cs` (6) — reachability probe loop + state machine
- [ ] Task 5: `src/FederationController.cs` + `FederationControllerTests.cs` (5) — status + test-credentials endpoints (admin-only)
- [ ] Task 6: `src/FederationPluginServiceRegistrator.cs` — DI wiring (no new tests; pieces covered)
- [ ] Task 7: `src/Web/config.html`, `src/Plugin.cs`, both csprojs + `PluginPageTests.cs` (4) — embedded dashboard page
- [ ] Task 8: `README.md`, `AGENTS.md`, `docs/design.md` — docs + invariant 12
- [ ] Task 9: end-to-end verification per the plan (container build deploy path, dashboard smoke)

**Acceptance Criteria:**
- Given the dashboard is open, when a remote is up, then its row shows `Online` with remote version, channel names, and last-authenticated time; when it is down, `Offline` with a fixed-category `lastError` and no raw exception text.
- Given unsaved credentials in the form, when Test is clicked, then a throwaway client reports `libraryCount` or a fixed reason; the persistent client and any saved status row are untouched; a typo'd alias creates no status row.
- Given a remote is down at startup but a views cache exists, when the host starts, then the channels appear (token-less) and self-heal via exactly one login when the remote returns.
- Given `dotnet test -c Release`, when run, then 77 tests pass offline; both csproj targets build.

## Implementation Notes

- Task 1: plan test code used `reported!.Value.Succeeded` / `reported.Value.X` (Nullable-style); `RemoteApiReport` is a class record, so `.Value` does not exist — removed it in the three affected tests (assertions otherwise verbatim).
- Task order: Task 4 before Task 3 (compile dependency — Task 3's factory ctor takes `FederationStatusService? status = null`, which does not exist until Task 4). Everything else in Task order 1→9 unchanged.
- Task 4: plan test `Create` took `FakeHttpMessageHandler` but one test passes `ThrowingHandler` (different class) and captured the `out` param `clock` in a lambda (CS1628). Fixed: `Create` now takes `HttpMessageHandler` and captures `clock` in a local before the lambda. Assertions unchanged.
- Task 4 (container target): `IHostedService` lives in `Microsoft.Extensions.Hosting.Abstractions`, which the container csproj did not reference (plain HintPath refs don't flow FrameworkReferences). Added `container-refs/Hosting.Abstractions.dll` (from the .NET 10 shared framework the image ships) + a Reference item with the docker-cp re-extraction note.
- Task 5: added one test beyond the plan (`TestCredentialsRequest_BindsPascalCaseBody`): the page POSTs PascalCase bodies; a silent casing mismatch would 400 every Test click. Controller count 5→6 (total 44+33=77 after Task 7).
- Task 9 smoke (two local source-build instances A:8096 / B:8097, `--nowebclient`, plugin DLL deployed to A):
  - Startup with B up: status `Online`, `mustrum 13.0.0`, channel `paul · Movies` in A; item `fed-test` listed through the channel.
  - `POST Federation/TestCredentials`: correct pw → `{"ok":true,"libraryCount":1}`; wrong pw → `{"ok":false,"reason":"InvalidCredentials"}`.
  - B stopped, A restarted: log `paul (...) unreachable at startup; rebuilding 1 channels from cached views`; channel still present; status `Offline` / `lastError: unreachable`.
  - B restarted, A untouched: probe loop flipped the row to `Online` within ~30 s (5 s interval), no A restart.
  - Second never-reached instance (`quinn`, 8098, no cache): 4 bounded login retries then skipped, no channel, status `Offline` / `unreachable` / empty `Channels` (the page renders the "no channels yet; restart A once B is reachable" hint for this row).
  - Host plugin-config API (what the page load/save use): `GET /Plugins/{id}/Configuration` returns PascalCase; `POST` round-trips a second instance into the XML in hand-edited byte-compatible form.
  - Known cosmetic (matches plan code verbatim): a successful probe does not clear a stale `LastError`; it is cleared on the next successful authenticated request (e.g. first browse/playback) or a successful Test click.
  - Browser-only checks not run (source build ships no `jellyfin-web`): page rendering, 5 s poll interval, Restart-now button.

## Plan Change Log

- Reorder Task 3 ↔ Task 4 during implementation: Task 3's new factory signature references `FederationStatusService` (Task 4), so implementing Task 3 first cannot compile. Known-bad state avoided: red build with CS0246 on `FederationStatusService`. KEEP: the Task 3/Task 4 content itself (sketches, tests) unchanged.

## Review Triage Log

## Verification

**Commands:**
- `dotnet build -c Release` -- expected: 0 errors (source-checkout target)
- `dotnet build -c Release container/Jellyfin.Plugin.Federation.Container.csproj` -- expected: 0 errors (container target)
- `dotnet test -c Release` -- expected: 77 pass (44 existing + 33 new), offline

**Manual checks (if no CLI):**
- Task 9 of the context plan: deploy container build to a test host, open the dashboard, verify status rows, Test button, and restart flow.
