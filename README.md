# Jellyfin Federation Plugin

Connects two Jellyfin servers: the remote server's libraries appear in your
server's sidebar, and **every stream is proxied through your server** — so
any client of yours (the TV in the living room, a phone without a route to
the other machine) plays the remote media as if it were local.

```
clients (TV, phone) ──▶ Server A ──▶ Server B
                        (plugin)     (user "paul")
```

- On startup the plugin logs into B with a regular user account
  (username + password — no admin, no API key) and creates one channel per
  library that user can see: `paul · Movies`, `paul · Series`, …
- Browsing is a live query against B's `Items` API, fed into the host's
  standard channel machinery (hydration, search, playback state).
- Streaming: A rewrites each media source to B's stream endpoint and lets
  its own ffmpeg pull (or pipe) the bytes. Transcoding runs on A; clients
  never talk to B.

## Installation

The story below: two local Jellyfin servers, A (local, gets the plugin) and
B (remote, shares its libraries), running on different ports with separate
data directories. Run them however you normally run Jellyfin.

> Note: In practice you will run the two Jellyfin instances on different
> servers with different IPs, maybe connected via a SSH Tunnel or
> any other VPN solution like WireGuard or Tailscale.

### 1. Create a user on B

In B's web UI: **Users → Add user → `paul`**, and grant that user access
to the libraries you want to share. That user's access defines what shows
up on A.

### 2. Build the plugin

The plugin builds against a Jellyfin **source checkout**. Clone both as
siblings (the plugin expects the server checkout in `../jellyfin`):

```
git clone https://github.com/jellyfin/jellyfin.git
git clone https://github.com/towi/jellyfin-federation.git
cd jellyfin-federation && dotnet build -c Release
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the
target framework is `net10.0`).

> **Running Jellyfin in a Docker container instead?** The
> [container build](AGENTS.md#building-for-a-container-install) compiles the
> same code against the exact assemblies of the image you run. The deploy
> steps below are identical.

### 3. Deploy to A

```
mkdir -p {dataDir}/plugins/Jellyfin.Plugin.Federation {dataDir}/plugins/configurations
cp bin/Release/net10.0/Jellyfin.Plugin.Federation.dll {dataDir}/plugins/Jellyfin.Plugin.Federation/
```

`{dataDir}` is A's `JELLYFIN_DATA_DIR` (e.g. `/var/lib/jellyfin`).

### 4. Configure A

Create `{dataDir}/plugins/configurations/Jellyfin.Plugin.Federation.xml`
and lock it down — it holds a password
(`chmod 600`, owner = the user running Jellyfin):

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Instances>
    <FederationInstance>
      <Alias>paul</Alias>
      <ServerUrl>http://192.168.1.50:8097</ServerUrl>
      <Username>paul</Username>
      <Password>secret</Password>
    </FederationInstance>
  </Instances>
</PluginConfiguration>
```

| Field          | Meaning                                                          |
|----------------|------------------------------------------------------------------|
| `Alias`        | Short name; shown in the channel names (`paul · Movies`)          |
| `ServerUrl`    | B's address as seen from A, with port                            |
| `Username`     | The B user from step 1                                           |
| `Password`     | That user's password (stored in plaintext — see `chmod 600`)     |

More `<FederationInstance>` blocks connect more remote servers.

### 5. Restart A and browse

`paul · Movies`, `paul · Series`, … appear in A's sidebar. Play anything
from any client of A.

> B must be **reachable when A starts**: the login happens during startup.
> If B is still down, the channels are missing until the next restart of A.

### If the servers are not on one network

Everything above assumes A and B reach each other directly. If they sit in
different locations, put a tunnel between them — an SSH tunnel, WireGuard,
or Tailscale — and point `ServerUrl` at the tunnel endpoint. Nothing else
changes.

## Limitations (V1)

- The remote must be a **separate** server: self-federation is not
  supported (A answers its own startup login with 503).
- The remote must be reachable **at A's startup**; see above.
- Play state stays local; B does not see playback.
- Config changes require a restart of A.
- Search finds remote items only after they have been browsed once.
- The remote access token is part of the stream URLs, so it appears in
  ffmpeg command lines, A's logs, and `PlaybackInfo` responses (visible to
  local users who can play the item). Treat A's logs as secret material
  and use a least-privilege user on B.
- Each start of A creates a new device row on B ("Jellyfin Federation");
  older rows must **not** be deleted while their images are in use — see
  invariant 2 in [AGENTS.md](AGENTS.md#architecture-invariants).

## Development

Build, test, container builds and the architecture invariants live in
[AGENTS.md](AGENTS.md). Design notes: [docs/design.md](docs/design.md).

## License

[MIT](LICENSE)
