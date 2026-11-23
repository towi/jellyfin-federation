# Jellyfin Federation — Design

**Datum:** 2026-11-19
**Status:** Implemented (2026-11-19)
**Ziel-Jellyfin:** master / 13.0.0-dev (Commit 7c2c5948c6)

## Ziel

Ein Jellyfin-Server („Client") bindet einen oder mehrere ferne Jellyfin-Server („Remote", z.B. „paul", via Tailscale) ein:

- Remote-Bibliotheken erscheinen als eigene Sidebar-Einträge und sind browsbar.
- Alle Clients (inkl. Samsung-TV) sprechen **nur** mit dem Client-Server; dieser ist Stream-Proxy.
- Konfiguration rein per Datei, noch kein UI.
- **Keine Änderungen am Jellyfin-Server**: Umsetzung als getrenntes Plugin `Jellyfin.Plugin.Federation`.

## Nicht-Ziele (V1)

- Play-State-Sync zum Remote-Server (bleibt lokal — User-Entscheidung).
- Vollständige Remote-Suche (nur bereits gehydratete Items werden lokal gefunden).
- Remote-Transcoding (Transcodes laufen auf dem Client-Server, siehe Trade-off).
- Admin-UI/Maske für die Konfiguration.

## Architektur

```
Samsung TV / Web ──HTTP──> Client-Jellyfin (Plugin geladen)
                              │
                              ├─ Sidebar: Channel-Mechanik (UserViewManager,
                              │  existierend, keine Core-Änderung)
                              ├─ Browsen: /Channels/{id}/Items → Plugin →
                              │  Remote-API → ChannelManager hydratiert lokale
                              │  Entities (existierende Mechanik)
                              ├─ PlaybackInfo: Plugin liefert MediaSource via
                              │  IRequiresMediaInfoCallback
                              └─ Stream: existierende Pipe-Zweige
                                 (Video: VideosController.cs:456,
                                  Audio: AudioHelper.cs:116) bzw. lokales
                                 ffmpeg mit HTTP-Input bei Transcode
                              │
                              ▼ (nur Client-Server, via Tailscale, Login als Remote-User)
                           Remote-Jellyfin („paul")
```

### Warum Channel-Mechanik

Verifiziert am Codebestand: `ChannelManager` (src/Jellyfin.LiveTv/Channels/ChannelManager.cs) ist der existierende Präzedenzfall für „externer Content + Server als Stream-Proxy":

- Sidebar: `UserViewManager.GetUserViews` (Emby.Server.Implementations/Library/UserViewManager.cs:118-127) fügt Channels bei `IncludeExternalContent` hinzu — null Änderung.
- Browsen: `Channel.GetItemsInternal` (MediaBrowser.Controller/Channels/Channel.cs:48-63) → `ChannelManager.GetChannelItemsInternal` (ChannelManager.cs:709-779) inkl. File-Cache (3h), Dead-Item-Cleanup, DB-Hydration.
- Hierarchie: `ChannelFolderType.Series/Season/MusicAlbum/MusicArtist/PhotoAlbum` → Entities `Series/Season/MusicAlbum/...` (ChannelManager.cs:986-1014).
- IDs: deterministisch via `GetNewItemId(remoteId + channelName + "16", type)` (ChannelManager.cs:941-976); Remote-ID in `BaseItemEntity.ExternalId` (existierende Spalte, keine Migration).
- MediaSources: Plugin-seitig via `IRequiresMediaInfoCallback.GetChannelItemMediaInfo` → `GetDynamicMediaSources` (ChannelManager.cs:384-417), 5-Min-MemoryCache.
- Sichtbarkeit: `Channel.IsVisible` + `EnableAllChannels` (default true für neue User, Jellyfin.Data/UserEntityExtensions.cs:192) → per User abschaltbar ohne neue UI.
- Plugin-Registrierung: `IPluginServiceRegistrator` (MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs, entdeckt in PluginManager.cs:208-238) registriert Services; `ChannelManager` nimmt `IEnumerable<IChannel>` per DI (ChannelManager.cs:81-92).

## Plugin-Komponenten

Neue Assembly `Jellyfin.Plugin.Federation` (eigenes Repo/Verzeichnis neben dem Server-Repo), deployed nach `{dataDir}/plugins/Jellyfin.Plugin.Federation/`.

| Komponente | Aufgabe |
|---|---|
| `Plugin : BasePlugin<PluginConfiguration>` | Manifest, Guid, Config-Zugriff |
| `Configuration/FederationPluginConfiguration` | Instanzen-Liste; Persistenz via `BasePlugin` → `plugins/configurations/Jellyfin.Plugin.Federation.xml` (XML, hand-editierbar) |
| `FederationChannel : IChannel, IRequiresMediaInfoCallback` | eine Instanz pro Remote-Bibliothek; `GetChannelItems` → Remote-API → `ChannelItemInfo[]`; `GetChannelItemMediaInfo` → Remote-PlaybackInfo → `MediaSourceInfo[]` |
| `RemoteApiClient` | Typed HTTP (NamedClient.Default via IHttpClientFactory), Auth per `POST /Users/AuthenticateByName` → `AccessToken`, der als `ApiKey`-Query-Parameter an jeder Request-URL hängt; JSON via `JsonDefaults.Options` (nutzt `BaseItemDto`/`QueryResult<>`/`PlaybackInfoResponse` aus Jellyfin.Model) |
| `FederationPluginServiceRegistrator` | DI: `RemoteApiClient`, `IChannel`-Instanzen aus Plugin-Config |

**Channel-Naming:** `{alias} · {bibliotheksname}` (z.B. `paul · Filme`), `DataVersion` = Hash aus Config (erzwingt Re-Hydration bei Config-Änderung).

### Konfiguration (Beispiel)

```xml
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Instances>
    <FederationInstance>
      <Alias>paul</Alias>
      <ServerUrl>http://paul.tail1234.ts.net:8096</ServerUrl>
      <Username>paul</Username>
      <Password>geheim</Password>
    </FederationInstance>
  </Instances>
</PluginConfiguration>
```

### Authentifizierung (Username/Password statt API-Key)

API-Keys kann nur ein Admin des Remote-Servers anlegen — ein normaler Remote-User (z.B. „paul" auf seinem eigenen Server) hat keinen Zugriff auf das Dashboard-API-Key-Feature. Deshalb V1: **Username + Passwort in der Config-Datei** (Datei liegt im Plugin-Config-Verzeichnis des Client-Servers; Zugriffsschutz = Dateisystem-Rechte des Datenverzeichnisses).

Login-Flow (verifiziert an `Jellyfin.Api/Controllers/UserController.cs:209-236`, `MediaBrowser.Controller/Authentication/AuthenticationResult.cs`):

1. `POST {ServerUrl}/Users/AuthenticateByName` mit Body `{"Username": "...", "Pw": "..."}` (Feldname `Pw`).
2. Response `AuthenticationResult`: `AccessToken`, `User.Id` (Remote-User-ID), `ServerId`.
3. Der `AccessToken` wird vom Client-Server für alle weiteren Requests verwendet — als `ApiKey`-Query-Parameter (nicht legacy-gated, Jellyfin.Server.Implementations/Security/AuthorizationContext.cs:103-133). Derselbe Mechanismus wie bisher, nur dass der Token per Login erzeugt statt von einem Admin angelegt wird.

Token-Haltung: der Token bleibt im Speicher des Plugins (ggf. Refetch bei 401). Der Login wird beim Server-Start (Channel-Factory) und nach Ablauf wiederholt — **nicht** persistiert, damit Passwort-Änderungen auf dem Remote-Server beim Neustart/Re-Login greifen. Sessions/Devices auf dem Remote-Server: pro Plugin-Start entsteht eine Device-Session („Jellyfin Federation" als Client-Name) — Remote-Admin sieht die Session in seiner Geräteliste; das ist dasselbe Bild wie ein normaler Client.

Pro Instanz ruft das Plugin beim Start (nach Login) `GET {ServerUrl}/Users/{remoteUserId}/Views` ab (der Login-User sieht seine Bibliotheken; `/Library/VirtualFolders` wäre Admin-only und entfällt) → pro Remote-Bibliothek (movies/tvshows/music) ein `FederationChannel`.

## Datenfluss

### 1. Browsen

Client → `GET /Channels/{channelId}/Items?ParentId=…` → `ChannelManager.GetChannelItemsInternal` → Plugin `GetChannelItems(InternalChannelItemQuery{FolderId=ExternalId des Parent})` → Remote-API `GET /Items?ParentId={remoteId}&Fields=Overview,Genres,People,MediaSources,Path&EnableImages=true&…` → Mapping zu `ChannelItemInfo[]` (Name, Overview, Genres, Studios, People, CommunityRating, RunTimeTicks, PremiereDate, ProductionYear, IndexNumber/ParentIndexNumber, `ImageUrl` = Remote-Primary-Image-URL, `FolderType` entsprechend) → `ChannelManager.GetChannelItemEntityAsync` erstellt/updated Entities und lädt Bilder.

Sortierung/Paging: der `InternalChannelItemQuery` gibt `StartIndex/Limit/SortBy` nicht vollständig durch — das Plugin lädt die Parent-Liste komplett (mit File-Cache des ChannelManagers, 3h) und überlässt Filter/Sort/Paging dem ChannelManager (bestehender Flow).

### 2. PlaybackInfo

Client → `POST /Items/{localId}/PlaybackInfo` → `MediaSourceManager.GetMediaSources` → Channel-Dynamic-Provider → Plugin `GetChannelItemMediaInfo(externalId)`:

1. Remote: `POST /Items/{remoteId}/PlaybackInfo` (Remote-AccessToken als `ApiKey`-Query-Parameter; DeviceProfile des Client-Request wird NICHT weitergegeben — Remote liefert ihre Standard-MediaSources).
2. Mapping: erste `MediaSourceInfo` der Remote-Antwort wird übernommen (MediaStreams, Container, Codec, Bitrate, RunTimeTicks) und gesetzt:
   - `Path = {ServerUrl}/Videos/{remoteId}/stream?MediaSourceId={remoteMediaSourceId}&Static=true&ApiKey={AccessToken}` (Audio analog `/Audio/{remoteId}/stream`)
   - `Protocol = MediaProtocol.Http`
   - `IsRemote = false`
   - `SupportsDirectPlay/SupportsDirectStream/SupportsTranscoding = true`
3. `MediaInfoHelper.SetDeviceSpecificData` (Jellyfin.Api/Helpers/MediaInfoHelper.cs:182-359) entscheidet per StreamBuilder anhand des **Client**-Device-Profils DirectPlay vs. Transcode — die Remote-MediaStreams liefern die Codec-/Container-Fakten dafür.

### 3. Stream — Direct Play (TV kann Codec abspielen)

Client → `GET /Videos/{localId}/stream?static=true&MediaSourceId=…` → `StreamingHelpers.GetStreamingState` → `state.InputProtocol == Http`, `state.InputPath` = Remote-URL → **existierender Branch** VideosController.cs:456-460 → `FileStreamResponseHelpers.GetStaticRemoteStreamResult`: Server öffnet Remote-Stream und piped Bytes; Range/Seek werden durchgereicht. Musik identisch über AudioHelper.cs:116-119 (`isStatic` wird server-seitig aus `SupportsDirectStream` gesetzt, UniversalAudioController.cs:182,236).

### 4. Stream — Transcode (TV kann Codec nicht)

`SetDeviceSpecificData` setzt `TranscodingUrl` auf den **lokalen** Server → `GET /Videos/{localId}/main.m3u8…` → lokales ffmpeg mit `-i {Remote-Stream-URL}` (ffmpeg kann HTTP-Inputs) → transcodiert lokal. Eingebettete Untertitel: Remote-URL mit `SubtitleMethod=Embed` (ffmpeg extrahiert aus dem Container); externe Untertitel V1 nur als Burn-in beim Transcode.

## Verifizierte Design-Entscheidungen

1. **`IsRemote = false` auf allen MediaSources.** Beide Redirect-Fallen sind auf `IsRemote=true` gegated — sonst bekäme der TV eine Tailscale-URL: `UniversalAudioController.cs:175` (`SupportsDirectPlay && Protocol==Http && IsRemote && enableRemoteMedia` → 302) und `MediaInfoHelper.cs:307` (`IsRemote && ForceRemoteSourceTranscoding` → Remote-Transcode-URL). Mit `IsRemote=false` kein Redirect, Client-Server bleibt Proxy.
2. **Audio-Pipe ohne Client-Mitwirkung**: `isStatic` wird aus `SupportsDirectStream` server-seitig bestimmt (UniversalAudioController.cs:182), nicht vom Client-Parameter `static` — TV-Clients brauchen nichts Besonderes zu senden.
3. **Keine DB-Migration**: `BaseItemEntity.ExternalId` (string, existierend) speichert die Remote-ID; `ChannelId` (Guid?, existierend) macht die Items zu Channel-Items.
4. **`SupportsDirectStream(path, Http) == true`** (MediaSourceManager.cs:280-301) für Stream-URLs → Audio/Video-DirectPlay läuft als Pipe.

## Trade-off (bekannt und akzeptiert)

**Transcodes laufen auf dem Client-Server** (ffmpeg liest per HTTP, encodiert lokal). Die Alternative (Remote-Transcode-URL durchreichen) würde Server-Änderungen erfordern und ist damit ausgeschlossen. Bei Tailscale + Direct-Play-fähigen TVs ist das der Normalfall (Pipe ohne CPU-Last); exotische Codecs kosten Client-CPU.

## Fehlerbehandlung

- **Remote unreachable beim Browsen:** Plugin-Exception → `Channel.GetItemsInternal`/`Folder.GetItemsInternal` fängt (Channel.cs:58-61, Folder.cs:1118-1122) → leeres Result, keine zerschoßene UI; Log via ILogger.
- **Remote unreachable beim PlaybackInfo/Stream:** HTTP-Fehler an den Client; Fehler im Log; RemoteApiClient mit 10s-Timeout, kein Retry-Storm (HttpClient-Default-Polly-less).
- **Login schlägt fehl (falsches Passwort/Offline):** erster Fetch beim Start schlägt fehl → Plugin-Log-Fehler, keine Channels für die Instanz; Server startet trotzdem. Auch bei 401 mid-run (Token invalidiert, Passwort geändert): Plugin refetcht einmal via Login und wiederholt den Request; bei erneutem Fehl → Fehler an Client + Log.
- **Config-Reload:** `PluginConfiguration` wird von BasePlugin persistiert; Channels werden aus der Config beim Start gebaut. Änderungen erfordern Neustart des Servers (V1; dokumentiert).
- **Status & UI (2026-11-22, feat/ui-and-status):** Dashboard page (IHasWebPages,
  host config API), admin-only Federation controller (status snapshot, one-shot
  TestCredentials), unauthenticated reachability loop (invariant 11), views cache
  fallback (invariant 12). Details: docs/pi/specs/2026-11-22-ui-and-status-design.md

## Testing

Setup: two local Jellyfin instances (different ports + data directories), a test
user (username + password) on B, the plugin on A.

1. **Unit tests** (this repo, xUnit): `RemoteApiClient` mapping
   (`BaseItemDto` → `ChannelItemInfo` including the folder-type hierarchy;
   `PlaybackInfo` → `MediaSourceInfo` with `IsRemote=false`, `Protocol=Http`),
   config parsing.
2. **Integration**: a second Jellyfin instance as the remote ("paul" role);
   credentials only in the local (untracked) config file — never committed.
3. **Extended integration, manual**: sidebar visibility; browse movies/series/
   seasons/episodes; check the `PlaybackInfo` response (media streams present,
   no remote host URLs except as the internal `Path`); direct-play stream via
   `curl` with `Range` (206 response); force a transcode (device profile
   without codec support) → local ffmpeg log; music via the universal audio
   endpoint.
4. **Samsung TV (or similar client)**: manual playback on a real device.


## Offene Punkte für Planungsphase

- Exakte `Fields`/`EnableImageTypes`-Parameter der Remote-Items-Query (minimale Payload, ausreichende Metadaten).
- `ISupportsLatestMedia` für „Neueste" auf der Startseite (nice-to-have, einzeln bewerten).
- Trickplay: Remote-Trickplay-URLs in `ChannelItemInfo` abbilden (falls unterstützt) oder weglassen (V1-Default).
