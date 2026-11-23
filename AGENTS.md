# AGENTS.md

This file provides guidance for coding agents working in this repository.

## Project Overview

Jellyfin Federation is a [Jellyfin](https://jellyfin.org/) server plugin that
connects one Jellyfin server ("A", the local one) to another ("B", the remote
one). You use a user (e.g. `paul`) on B to connect to, and their
libraries show up in A's sidebar as channels (`paul · Movies`),
and all media is **proxied through A** — clients (a Samsung TV, a phone)
stream from A and never contact B. Transcoding happens on A.

This is *not* a fork of Jellyfin and not a standalone app. It is a single
class library loaded by the host server, plus a test project. Everything it
does is built on the host's existing **channel** infrastructure
(`IChannel`/`ChannelManager` in `src/Jellyfin.LiveTv`): the host handles
the sidebar, item hydration, folder hierarchy, ID derivation, search
indexing, playback state and cleanup. The plugin only supplies data:

- `Channel.GetChannelItems` → remote `Items` query → `ChannelItemInfo[]`
- `IRequiresMediaInfoCallback` → remote `PlaybackInfo` → `MediaSourceInfo[]`
  whose `Path` is B's stream endpoint URL.

## Project Structure

```
jellyfin-federation/
├── Jellyfin.Plugin.Federation.csproj      # The plugin (net10.0); builds against
│                                          # a sibling ../jellyfin source checkout
├── Jellyfin.Plugin.Federation.slnx        # Solution incl. the ../jellyfin projects
├── src/                                   # Plugin source (all namespaces: Jellyfin.Plugin.Federation)
│   ├── Configuration/PluginConfiguration.cs
│   ├── Plugin.cs                          # Entry point; registers services + config
│   ├── FederationPluginServiceRegistrator.cs
│   ├── RemoteApiClient.cs                 # Login + typed remote API (views, items, playbackinfo)
│   ├── FederationChannelFactory.cs        # One channel per supported remote library
│   ├── FederationChannel.cs               # IChannel implementation
│   ├── FederationChannelOptions.cs
│   ├── ItemMapper.cs                      # Remote BaseItemDto → ChannelItemInfo
│   ├── MediaSourceMapper.cs               # Remote PlaybackInfo → MediaSourceInfo (proxied URL)
│   ├── DiagnosticSink.cs                  # DiagnosticTraceListener (ffmpeg et al. → logger)
│   ├── FederationJsonOptions.cs           # JSON options matching the remote API
│   ├── FederationException.cs
│   ├── FederationViewsCache.cs            # Per-instance last-known-views fallback (DataPath)
│   ├── FederationStatusService.cs         # Unauthenticated reachability loop (IHostedService)
│   ├── FederationController.cs            # Admin-gated status + test-credentials endpoints
│   ├── RemoteApiReport.cs                 # Outcome report from RemoteApiClient
│   └── Web/config.html                    # Embedded dashboard page (IHasWebPages)
├── tests/Jellyfin.Plugin.Federation.Tests/  # xUnit; FakeHttpMessageHandler-based, no network
├── container/                             # Alternative csproj: compiles the SAME src/
│                                          # against the exact DLLs of a Docker image
├── container-refs/                        # Those reference DLLs (from the linuxserver image)
└── docs/design.md                         # Design notes (original German spec, English testing section)
```

We need an original `../jellyfin` checkout (or a fork) as a build dependency; 
it is not part of this repo. Keep all plugin code in this repo; 
never modify the server checkout.

## Building

Two targets, both compile `src/`:

1. **Against the source checkout** (default): `Jellyfin.Plugin.Federation.csproj`
   references `../jellyfin/MediaBrowser.{Controller,Model,Common}` and
   `../jellyfin/src/Jellyfin.Extensions`. Requires the sibling checkout.

2. **Against container binaries** (`container/Jellyfin.Plugin.Federation.Container.csproj`):
   references the exact assemblies shipped in the target Docker image
   (currently linuxserver/jellyfin 12.2.0) from `container-refs/`.
   This is the DLL that actually gets deployed when Jellyfin runs in a
   container — the source-checkout build is for development.

```
dotnet build -c Release
dotnet build -c Release container/Jellyfin.Plugin.Federation.Container.csproj
dotnet test  -c Release
```

Requires the **.NET 10 SDK** (target framework `net10.0`); no other global
tools are needed.

Both targets must build. If a change compiles only against one of them, the
other target's Jellyfin version has a different API — that is a defect to
report, not a target to drop.

### Building for a container install

Extract the reference assemblies from a running container (linuxserver image;
paths differ per distro image — locate `MediaBrowser.Controller.dll` first):

```
docker cp jellyfin:/usr/lib/jellyfin/bin/MediaBrowser.Controller.dll container-refs/controller.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/MediaBrowser.Model.dll        container-refs/model.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/MediaBrowser.Common.dll       container-refs/common.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/Jellyfin.Data.dll             container-refs/data.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/Jellyfin.Extensions.dll       container-refs/extensions.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/Microsoft.Extensions.Logging.Abstractions.dll        container-refs/Logging.Abstractions.dll
docker cp jellyfin:/usr/lib/jellyfin/bin/Microsoft.Extensions.DependencyInjection.Abstractions.dll container-refs/DependencyInjection.Abstractions.dll
```

Then `dotnet build -c Release container/...` and deploy
`container/bin/Release/net10.0/Jellyfin.Plugin.Federation.dll` to the host's
`{dataDir}/plugins/Jellyfin.Plugin.Federation/` (see README), and restart the
container. If the image is upgraded, re-extract: the DLL must match the
running server's assemblies or loading fails.

## Code Style

- C# 12 / net10.0, `Nullable` enabled, `ImplicitUsings` enabled.
- `async`/`await` everywhere in the network path;
  `.ConfigureAwait(false)` on every `await` in `src/`.
- `ILogger` via constructor injection. Log messages start with
  `Federation:` so they are greppable in host logs.
- No `Console.WriteLine`, no `Thread.Sleep`, no blocking calls in the DI
  registration path *except* the one documented `GetAwaiter().GetResult()`
  in `FederationPluginServiceRegistrator` (the host's `RegisterServices`
  is synchronous by API contract — do not "fix" it into async).
- No new dependencies. The plugin may only use what the host already loads
  at runtime (`System.*`, `MediaBrowser.*`, `Jellyfin.Data`,
  `Microsoft.Extensions.*.Abstractions`); see both csproj files for the
  allowed reference set. The ASP.NET Core shared framework
  (`Microsoft.AspNetCore.App`) is referenced for the plugin's MVC controller
  and hosted service — it is loaded by the host at runtime, and the container
  csproj must declare the FrameworkReference explicitly (HintPath DLL
  references do not flow FrameworkReferences).
- Tests are xUnit, use `FakeHttpMessageHandler` for HTTP, and must run
  without network access. Add a test for every behavior change; the test
  must fail without the fix.
- Keep the two csproj files in sync: the container project globs
  `src/**/*.cs` explicitly, the main project uses the SDK default glob and
  must exclude any directory that contains generated C# (`tests/`,
  `container/`). A new top-level folder with `.cs` files or generated
  `obj/` output silently ends up compiled into the other project.

### File Layout

Organize for top-to-bottom reading: important things first.

- **Usings**, then **fields** (`private static readonly` constants first),
  then the **constructor**, then **public members**, then **private helpers**
  (below the methods that call them; meatier logic above leaf formatters).
- Public API of every type and member in `src/` gets a XML doc comment
  (`GenerateDocumentationFile` is on; the container build suppresses CS1591
  only because it shares the same sources with a different warning set).
- In test files: `[Fact]` methods first, helpers and fake types last.

## Architecture Invariants

These are load-bearing decisions, several of them counter-intuitive.
Verify against the host source (`../jellyfin`) before changing any of them;
each one failed in production or review before being pinned this way.

1. **`ServerUrl` is trimmed exactly once, in the factory.**
   `RemoteApiClient` and both mappers build URLs by string concatenation
   from `BaseUrl`; a trailing slash in the config yields `host//Videos/…`
   and the remote Kestrel answers **404** (the API client itself survives
   it, because `Uri` resolution normalizes — which is why the bug is silent
   in login and loud in playback). If you touch URL construction, keep the
   trim central and test it from a trailing-slash config.

2. **DeviceId = alias prefix + per-`ConfigureAsync`-call random suffix**
   (`{sanitized-alias}-{8 hex}`, set in `RemoteApiClient.ConfigureAsync`).
   Do not make it stable (no `SHA256(alias)`-only form): re-login with the
   same device id makes the host **delete the old device row and revoke its
   access token**, and those old tokens are baked into persisted state
   (channel image URLs in `BaseItemImageInfos` embed `?ApiKey=…`). A stable
   id would 401 every previously-hydrated image after each restart.
   (The startup retry loop re-calls `ConfigureAsync` on each attempt, so it
   re-rolls the suffix — harmless: a failed login issued no token yet.)
   Consequence to preserve: every server start adds one device row
   ("Jellyfin Federation") on the remote. Users must **not delete** older
   rows while their images are in use (deletion = token revocation).
   The sanitized prefix (only `[a-z0-9-]`, max 16 chars) is what makes the
   device identifiable in the remote's device list.

3. **Retries are bounded and exclude definitive auth failures.**
   The startup retry loop (`FederationChannelFactory`) retries connection
   errors and 503 (a remote still booting answers 503 until
   `Core startup complete`) up to `MaxLoginAttempts` (4) with a 3 s delay,
   but **never** retries 401/403: a wrong password retried four times
   feeds the remote's account lockout counter. The filter lives in
   `IsDefinitiveAuthFailure`; a re-login mid-run (401 after token
   revocation) is a separate, single-shot path in `RemoteApiClient`.

4. **Channel names are deduplicated globally, registered until unique.**
   The host keys channel entities on the channel name
   (`GetInternalChannelId`); two channels with the same name collapse into
   one entity and the second library becomes unreachable. The factory keeps
   a `HashSet` across *all* instances and appends id-suffixes
   (`(…)` / `(#n)`) until the name is registered — a single check is not
   enough (n identical instances produce the same id-suffix). Keep the
   register-every-candidate pattern and its triple-collision test.

5. **`MediaSourceInfo.Path` is the remote stream URL, with
   `IsRemote = false`, `Protocol = Http`.**
   `IsRemote = true` makes host code (e.g. `UniversalAudioController`,
   `MediaInfoHelper`) redirect the client to that URL instead of serving
   through the local pipeline — which would break clients without a route
   to B. `IsRemote = false` keeps ffmpeg on A as the single byte mover.
   Do not "simplify" by returning a local-looking path: ffmpeg pulls the
   remote URL directly, including Range requests for seeking.

6. **People/Artists are mapped with a null/blank-Name guard.**
   The host's `PeopleRepository.UpdatePeople` calls `person.Name.Trim()`
   unconditionally during hydration; a remote person entry without a name
   NREs the item's hydration path. Filter such entries in `ItemMapper`.

7. **Image self-healing does not exist for channel items — design around
   it.** The host skips scheduled image refresh for non-library sources
   (`LibraryManager.UpdateImagesAsync`, the `isLiveTvShow` gate
   `parentItem.SourceType != SourceType.Library` — the parent of a channel
   item is the `Channel` entity, so the gate is true and processing is
   skipped). A dead remote image URL (e.g. from a revoked token) stays
   dead until the items are re-hydrated. This is a host constraint;
   document it, don't try to work around it from the plugin.

8. **No self-federation.** The factory runs during the host's
   `RegisterServices`, before the app is ready; a `ServerUrl` pointing at
   the local server gets 503 and (by invariant 3) retries four times, then
   the instance is skipped. Support is out of scope; do not add it.

9. **The remote access token is in the stream URLs by necessity.**
   Every URL to B carries `?ApiKey={token}` (the only non-legacy-gated auth
   mechanism — `X-Emby-Token` and lowercase `api_key` are legacy-gated on
   the remote). Therefore the token ends up in ffmpeg command lines, the
   host's ffmpeg logs, and `PlaybackInfo` responses. Do not "fix" this by
   moving to header auth; it is documented as a limitation. If you change
   anything that surfaces URLs in logs (see `DiagnosticSink`), re-check
   that no *new* secret material is added to log lines.

10. **`DataVersion` = `SHA256(alias | remoteLibraryId | serverUrl)`**
    (no library name). The host's per-channel cache path already includes
    the channel name (which carries the view name), so a remote view rename
    rotates the cache via the name; keeping the name out of the hash stops
    the dedup suffixes (invariant 4) from churning it. Vary these three
    components in the `DataVersion` tests, not the library name.

11. **The status monitor never authenticates.** `FederationStatusService`
    only calls the remote's unauthenticated `/System/Info/Public`. A login
    per probe would create device rows per cycle or (stable DeviceId) revoke
    tokens baked into persisted image URLs — invariants 2 and 3 apply to
    *every* code path that talks to the remote, not just startup. Credential
    validity reaches the status only via failed authenticated requests
    (reported through `RemoteApiClient`'s outcome reporter) or the UI's
    one-shot Test button.

12. **The views cache is the only path to channels without a live login.**
    `FederationViewsCache` persists the last successful view set (including
    the final deduped channel names) and the factory falls back to it
    **only** when the startup login fails transiently — never on definitive
    401/403, never from TestCredentials (unsaved credentials must not
    pollute the cache). Cache entries are keyed by
    `sha256(alias|trimmed serverUrl|username)` — the username is in the key
    so a credential rotation does not rebuild the *previous* user's library
    set.

## Testing

```
dotnet test -c Release        # 77 tests, all offline
```

- HTTP behavior is tested through `FakeHttpMessageHandler`
  (matcher → response) and, for the retry/throwing cases, dedicated
  `HttpMessageHandler` subclasses. **Matcher lambdas may close over state
  (e.g. a counter), but the counter must be incremented in the matcher,
  not in the response builder** — matchers are evaluated in registration
  order and short-circuit; a response-builder side effect fires late or
  not at all.
- Fixture JSON lives in `tests/.../Data/`; extend the fixtures before
  adding network-dependent tests (there are none — keep it that way).
- A behavior change ships with a test that fails without it. If a host
  contract (invariants 1–10) changes, the test that pins it changes with it.

## Deployment Notes

- Deploy the **container** build DLL for container hosts, the
  source-checkout DLL for source builds. Same plugin directory name in
  both: `{dataDir}/plugins/Jellyfin.Plugin.Federation/`.
- The config file is `{dataDir}/plugins/configurations/Jellyfin.Plugin.Federation.xml`
  (plain file, loaded with `XmlSerializer`; the plugin does not use the
  host's `BasePlugin` configuration persistence — `Plugin.Instance` is
  never touched on the registration path). It holds a password:
  `chmod 600`.
- After a deploy that changes persisted-state formats (stream/image URLs,
  channel names), old rows in the local DB (`BaseItemImageInfos`,
  `MediaSources`) still carry the old values until items are re-hydrated
  (see invariants 2 and 7). If a user reports "it works, except X", suspect
  stale persisted state before the new code.

## Markdown

- **Tables must be space-padded so columns align in plain text.** Markdown
  tables are read raw (terminals, diffs, editors) at least as often as
  rendered. Pad every cell to its column width:

  ```markdown
  | Field   | Meaning                                  |
  |---------|------------------------------------------|
  | `Alias` | Short name (`paul · Movies`)             |
  ```

  Not packed-tight tables with varying widths per row.
- Doc comments and commit messages in this repo are English.

## Known Limitations (V1)

See the README's Limitations section for the user-facing list. Internal
additions:

- Search only finds remote items after they have been browsed (host
  channel hydration drives the search index).
- Config changes require a server restart (no hot reload).
- External subtitle files on B are hidden (they reference paths on B's
  disk); only embedded subtitle streams are offered, so selection cannot
  fail mid-playback.
