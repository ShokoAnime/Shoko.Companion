# AGENTS.md — Shoko Companion

## Project Overview

Avalonia-based Linux/Windows/macOS desktop tray companion for Shoko Server. Sits in the system tray, receives `shoko://` URL clicks, resolves metadata from the Shoko API, launches mpv with JSON IPC for playback, scrobbles watch progress, and integrates with the Media Session plugin for remote playback control.

## Stack

- **.NET 10** / C# 13 / `net10.0`
- **Avalonia 11.3.12** — desktop UI framework
- **NLog 6.1.3** — structured logging (JSONL files under `{ConfigRoot}/logs/`)
- **Newtonsoft.Json 13** — all JSON serialization (NO System.Text.Json)
- **Microsoft.AspNetCore.SignalR.Client 10.0** — Media Session plugin hub connection
- **Microsoft.AspNetCore.SignalR.Protocols.NewtonsoftJson 10.0** — the hub payload serialiser, so the connection obeys the Newtonsoft rule too
- **DiscordRichPresence 1.6.1.70** — Discord "Now Playing" integration
- **xunit** — tests

## Key Conventions

- **Assembly name:** `shoko-companion`
- **Namespaces:** `Shoko.Companion.*` throughout
- **Logging:** NLog via `private static readonly Logger Logger = LogManager.GetCurrentClassLogger();` — field named `Logger` (not `Log`)
- **One type per file.** No multiple public types at the root of a file. (Enums, classes, records, interfaces each get their own file.)
- **No `WindowStartupLocation`.** Leave window positioning to the OS.
- **All DTOs are in `Shoko.Companion.Server.Models`**, one class per file. DTO names match JSON property names.
- **Single-file publish** with native libs self-extract: `dotnet publish -c Release -r <rid>`
- **Test project** references internals via `[InternalsVisibleTo]`

## CI

Two hosts, and they do not build the same set of artefacts.

- `.gitea/workflows/` — `build.yml` on every push to `dev`, cutting a
  `v<x.y.z>-dev.<n>` prerelease; `release.yml` on a published `v*`
  release. Both run `scripts/package.sh`, which is the same command a
  human runs locally.
- `.github/workflows/` — unchanged, and still the only place the
  **Windows installer** and the **macOS .dmg** are built. `iscc`,
  `codesign` and `hdiutil` each need their own operating system, and the
  Gitea runner has only Linux.

So a Gitea build ships AppImages for both Linux RIDs and plain archives
for `win-x64` and `osx-arm64`. That is a deliberate degradation, not an
oversight: an unsigned `.app` assembled on Linux would be worse than no
`.dmg` at all on Apple Silicon.

`scripts/gitea-release-asset.sh` uploads assets over Gitea's API rather
than through an action, so it is dry-runnable (`--dry-run`) and needs
nothing from the runner.

## Architecture

### Entry Flow

```
Program.Main()
├── ExtractHomeArg / ExtractForceArg       (--force bypasses running-instance check)
├── CompanionPaths.Initialize (config root resolution)
├── LogService.InitLogger
├── SingleInstanceManager.Initialize (PID lock file + pipe server; --force overwrites stale lock)
├── ParseShokoUrl (checks for CLI verbs: register, unregister, or passes a shoko:// URL)
├── If AlreadyRunning → ForwardUrl via named pipe → exit(0)
└── AppBuilder → Avalonia app
    └── OnFrameworkInitializationCompleted
        ├── Load settings
        ├── Init tray icon
        ├── PlaybackCoordinator (wires mpv, server, discord, notifications)
        ├── Auto-connect Media Session (if configured — probes, connects, registers)
        ├── ConsumePendingUrl → PlayAsync if forwarded URL arrived
        ├── Subscribe to SingleInstanceManager.UrlReceived (post-startup URLs)
        ├── First-run → show settings window
        └── URL scheme registration prompt (once)
```

### Media Session API Integration

The companion optionally integrates with the Media Session plugin via SignalR:

1. **Probe**: `GET /api/plugin/MediaSession/v1/Available` to check plugin availability
2. **Connect**: `HubConnection` to `/signalr/plugin/MediaSession/v1` with `accessTokenFactory` sending the API key as Bearer token
3. **Register**: calls `RegisterSession({ Name, DeviceType: "companion", ClientName: "Shoko Companion", HostName, Platform, Version, Capabilities, Settings })` — two separate declarations, re-pushed independently afterwards through `UpdateCapabilities` and `UpdateSettings`. Capabilities say what this build can do; settings say how the viewer configured it. See **Privacy Mode** below for the settings half.
4. **Receive commands**: `Play` (new media with VideoId), `Resume` (unpause current), `Pause`, `Seek`, `Stop`, `SetTracks` → relayed to `PlaybackCoordinator`
5. **Report state**: via `UpdateState({ State, VideoId, Title, PositionSeconds, DurationSeconds, Tracks })` on coordinator state changes

### The hub connection serialises with Newtonsoft, and a state report is a patch

`ConnectAsync` registers `AddNewtonsoftJsonProtocol` with a plain
`DefaultContractResolver` — the same registration the Shoko host makes on
its side — through `MediaSessionClient.ConfigureHubPayload`. Two things
follow, and neither was true while the connection ran on the default
System.Text.Json protocol:

- **The `[JsonProperty]` names on the DTOs are the names on the wire.**
  They were inert before: STJ ignores them and camel-cased everything, and
  it only worked because Newtonsoft binds case-insensitively on the far
  side. Note that SignalR's *own* Newtonsoft default is a camel-casing
  resolver with `OverrideSpecifiedNames` on, so the plain resolver is what
  does the work here, not the protocol swap.
- **`PlaybackStateUpdateDto` can omit a field instead of nulling it.** Each
  property records that its setter ran and hands the flag to
  `ShouldSerialize…`, so an untouched property is absent from the frame
  while one set to `null` is written as null — `undefined` versus `null`,
  in the JS reading. The server tells the two apart through its own
  `{Field}IsSet` flags: absent means unchanged, present means this is the
  new value. Without it a deliberately minimal report wrote null over a
  volume nobody had touched. `StateReportIsPatchTests` captures real
  frames through the configured protocol and pins both halves.

### And a patch is what it sends: what moved, not everything

The five `ReportStateAsync` call sites still hand in the whole picture and
none of them knows about any of this. `PatchToSend` reduces the report to
the fields that moved since the last one the server accepted, so a position
tick is `{"Position":"00:00:35"}` where it was a 469-byte snapshot. Deciding
it centrally rather than per call site is the point: which fields moved is
not something a call site can know — a volume change and a tick arrive
through different events and can carry each other's news — and the caller's
whole picture is needed anyway, because `_lastState` is the payload a
registration and a reclaim carry, and a baseline cannot be described in
deltas.

Three things drop the baseline, and the first is the one that would break
quietly:

- **`SetSessionId`**, which every registration, every reclaim and the
  teardown goes through. SignalR orders messages within a connection, so a
  stream of patches converges; across a reconnect it does not, and the
  server may hold nothing or state from a previous session. The report
  after one is therefore whole.
- **A send that threw**, because whether it landed is not knowable here.
- **A report that says `idle`**, because the far side clears the item
  triple, the position, the duration and the tracks on one — so what it
  holds afterwards is not what the report said, and the same item playing
  again would otherwise be omitted as unchanged against a server holding
  none. Asked through `ShouldSerializeState()`, since an unnamed state
  reads as `idle`.

A field the baseline never named is always sent: silence about a field is
not a claim about its value. `MediaItemInfoDto` and
`PlaybackTrackSelectionDto` are records for the comparison — the
coordinator rebuilds both on every read, so reference equality would put
the whole item back on the wire every tick. The stopped-to-idle follow-up
keeps filling its four device properties for a new reason: omitting them no
longer wipes them, but that report becomes `_lastState`, and a registration
carrying no volume is the same wipe by another route.

**A patch is not a heartbeat, and nothing here treats it as one.** A client
that correctly goes quiet — and it does, for as long as playback stays
paused — looks like a dead one to anything inferring liveness from report
traffic. Nothing on this side does: `MediaSessionClient` owns no clock at
all, the stopped→idle timer is driven by calls to `ReportStateAsync` rather
than by sends (the cancel/restart bracket sits outside the send, so a report
that moves nothing still resets it), the scrobble timer feeds reports rather
than being fed by them, and reconnection hangs off the transport closing.
No `KeepAliveInterval` or `ServerTimeout` is configured anywhere here, so
SignalR's defaults stand: the client pings on its own when it has sent
nothing for 15s, and its 30s server timeout is reset by anything received.
What the far side makes of the silence is the far side's question.

### Only one of the two writes the watch state, and it takes an item to hand back

Registering a session is consent to the server writing watch state, so the
server always writes for a session it holds and the companion stands its own
scrobbling down instead. `PlaybackSessionManager.MediaSessionConnected` is
the switch, fed from `PlaybackCoordinator.MediaSessionId` — holding a session
id *is* being connected, so a socket that drops and reconnects (retried
forever, the id reclaimed via `ReconnectSession`) never reaches it, and there
is no second timeout to keep in step with the first.

The two directions are deliberately asymmetric:

- **Connecting stops us immediately**, part-way through an item and all. The
  server has seen this playback through the state reports since it
  registered, so it can complete a record it takes over.
- **Disconnecting resumes us only at the next item.** Ownership is latched
  per item in `PlaybackSession.SyncSuppressed`, set at `StartSession` /
  `OnNextFile` and raised but never lowered in between. Resuming mid-item
  would write a record whose first half is the server's — worst at the end,
  where `PersistUserData` fires on `PlaybackEnd` and a companion that took
  over at 80% would mark the item watched having seen a fifth of it. The
  cost is that the item playing when the session went away syncs no further
  than the server's last write, which is right: the server owned it.

`PlaybackSyncingEnabled` survives and now means "sync when no media session
is connected"; the settings window says so. The tick timer keeps running
while suppressed — `PositionTick` feeds the hub, and silencing that would
leave *nobody* writing. Both ends of the handover log a line, because one
that goes wrong is invisible until the watch state is already wrong.
`PlaybackSessionManagerSyncTests` holds all of it.

### Track selection goes both ways, and they are not the same way

`SetTracks` is a **command**: the plugin tells this session to switch, and
records nothing from it. `PlaybackStateUpdateDto.Tracks` is a **report**:
what mpv actually ended up playing, and the only thing the plugin stores
or hands to another session in a handoff. So a switch is applied to mpv,
mpv answers on `vid`/`aid`/`sid`, and *that* is what gets reported — a
track this file does not have leaves the plugin's picture of the session
true rather than hopeful.

Everything travels as **zero-based within-type ordinals**, never mpv's
1-based per-kind ids and never container stream IDs. `MpvTrackValue`
(`Playback/MpvTrackValue.cs`) is the only place the two numberings meet,
and it also owns the three sentinels: `-2` clears back to the file's
default (mpv `auto`), `-1` on the subtitle index means off (mpv `no`),
`null` says nothing and leaves that kind alone. `MpvTrackValueTests`
holds it to all of it — every failure there would be silent, because mpv
would take the wrong track and report it back as though a viewer had
chosen it.

The companion advertises `CanSelectTracks` whenever remote play is
allowed and something is loaded: mpv switches a track in place for a
decoder reset, which is the cheap end of what the capability is for.

**Auto-connect**: configured per-server-connection via `MediaSessionAutoConnectId` (Guid). Only one connection can auto-connect. Manual Connect/Disconnect buttons in the settings window.

### Stream URLs and the session id

Two stream URL shapes exist: Shoko's `/api/v3/File/{id}/Stream`, authenticated
by API key, and the plugin's `/api/plugin/MediaSession/v1/Stream/{videoId}[/...]`, which
is anonymous and guarded on a `sessionId` query parameter. `StreamUrls`
(`Playback/StreamUrls.cs`) recognises both and is the only place that knows
either shape.

**Every media session URL the companion plays carries the companion's own session
id** — attached when the URL has none, replacing whatever is there when it has
one — **and when the companion holds no session id, the URL is swapped back
for the APIv3 endpoint.** The companion must never stream under another
session's id: the guard exists so a stream belongs to a session, and user-data
writes are keyed by it, so borrowing one scrobbles under it too. The reasoning
is written out on `StreamUrls`; `StreamUrlsTests` holds it to it.

`MediaSessionClient` pushes its session id to `IPlaybackCoordinator.MediaSessionId`
as it registers, reconnects and disconnects. It is null until `RegisterSession`
returns, which is why the APIv3 fallback exists at all — a playlist can be
fetched before the hub connects.

mpv fetches the Shoko playlist itself and follows the entry URLs in it, so
`PrepareM3u8Async` rewrites the playlist and hands mpv a local copy whenever
any entry is a media session URL. A playlist of plain APIv3 entries is untouched.
Rewriting only the entry URL is enough: the plugin propagates the `sessionId`
it was called with into every child URI it emits.

### Playback Flow

```
PlayAsync(shokoUrl)
├── ShokoUrlParser.Parse — strip shoko: prefix, detect action (play / open-folder)
├── ResolveCredentialsAsync — find API key in connection, probe server, or prompt user
├── FetchPlaylistJsonAsync — download playlist JSON from Shoko (strip .m3u8 suffix)
├── PlaylistItemDto = { Episode, AdditionalEpisodes[], Parts[] }
├── ParseM3u8Async — optional m3u8 download for stream metadata (anime/episode names, poster)
├── OnNewUrlAction: Replace → stop & replace | Ignore → discard | Append → add to playlist
├── Find/launch mpv with --input-ipc-server
├── MpvIpcClient (JSON IPC via Unix socket / named pipe)
├── LoadFileAsync (or AppendFileAsync) → mpv plays the file
├── Pre-fetch resume position → seek on file-loaded
├── Observe time-pos, pause, duration, eof-reached, idle-active
├── On position change → scrobble via ShokoApiClient (periodic + pause/resume/stop)
├── Auto-watch at ≥97.5% position
└── On eof-reached/idle → stop + final scrobble
```

### Playback Flow (via Media Session API)

When a `Play` command arrives via the SignalR hub:
1. `MediaSessionClient.OnPlay(request)` is invoked
2. Constructs a `shoko://` URL from `request.VideoId` and delegates to `PlaybackCoordinator.PlayAsync()`
3. The standard playlist resolution, mpv launch, and scrobble pipeline runs as normal

When a `Resume` command arrives via the SignalR hub:
1. `ResumeAsync()` is called on `PlaybackCoordinator`
2. mpv's `pause` property is set to `false` — the current file continues playing
3. No URL resolution, no playlist load — no media change

## Settings

Stored as JSON at `{ConfigRoot}/settings.json`. Key fields:

### Global
- `MpvPath` — optional override for mpv binary
- `IsFullscreen` — saved current fullscreen state, source of truth; restored
  on mpv connect and persisted on every change (default `true`, replaces
  the old `MpvFullScreen` launch toggle)
- `OnNewUrlAction` — `Replace` / `Ignore` / `Append` (default `Append`)
- `PlaybackSyncingEnabled` — master toggle for the scrobbling this companion
  does itself, which is only what plays while no media session is connected
  (default `true`)
- `PlaybackSyncingBehavior` — `AfterPlayback` / `OnEveryEvent` / `LiveSync` (default `AfterPlayback`)
- `SkipRestrictedContent` — skip scrobbling for adult content (default `true`)
- `DiscordEnabled`, `DiscordClientIdOverride`, `DiscordIdlePresence`
- `PrivacyModeHideDiscord` (replaces `DiscordPrivacyMode`)

## Privacy Mode

Global privacy mode with per-feature sub-toggles. Settings in `CompanionSettings`:

- `PrivacyMode` — master switch. When OFF, sub-toggles have no effect and individual feature toggles control behavior independently.
- `PrivacyModeHideDiscord` — hide title/poster from Discord (replaces `DiscordPrivacyMode`)
- `PrivacyModeDisableRemoteScreenshots` — deny remote screenshot capture via Media Session API
- `PrivacyModeDisablePlaybackEvents` — block all scrobbling to Shoko server
- `PrivacyModeForRestrictedContent` — auto-activate privacy mode for restricted (adult) content
- `PrivacyModeMpvKeybinding` — mpv key combo to toggle privacy during playback (default `Ctrl+p`, no Lua scripts needed)

### Three of them are declared to the plugin, and that is where privacy happens

`MediaSessionClient.BuildCurrentSettings` maps `PrivacyMode`,
`PrivacyModeForRestrictedContent` and `PrivacyModeDisablePlaybackEvents`
onto the plugin's `SessionSettings` — `PrivacyModeEnabled`,
`AlwaysUsePrivacyModeForRestrictedContent`, `DisablePlaybackEventSyncing`.
Sent on the registration payload beside `Capabilities`, and re-sent through
`UpdateSettings` whenever `SettingsProvider.SettingsChanged` fires and the
declaration actually moved. The server resolves privacy **per item**, and
it **ratchets** — once on for an item it never comes off — so this is a
one-way switch on the far side however often the local one is flipped.

**The master switch sends `PrivacyMode`, never `EffectivePrivacyMode`.**
The effective value folds in the restricted-content trigger, and the two
do not have the same reach server-side: the master switch re-resolves the
*whole queue* when it transitions on, while the restricted rule
deliberately never reaches back. Sending the effective value would make one
restricted episode retroactively privatise everything queued, permanently.
`SessionSettingsDeclarationTests` pins this.

**Two settings were removed when this landed** —
`PrivacyModeHideMediaPlaybackInfo` and `PrivacyModeDisableRemoteControl`.
Both re-implemented locally what the server does once told, and the server
does each of them better: it withholds a private item from every observer
as two opaque fields rather than blanking a title while still sending the
stream URL, and it refuses remote control of a private item at the session
manager across eleven commands. `privacyOverrideControl` went with the
second, so the capability builder no longer gates anything on privacy —
a capability says what the build can do, and "can, but privacy is on right
now" is a state.

**The client never lies to the server.** State and playlist are reported
in full whatever privacy says; filtering happens where the server speaks
to somebody other than this session. That is what keeps the playlist,
handoff and track selection working for the session's own viewer.

### `CanReportState` is the one inbound capability, and it is always true

Every other flag is a dispatch gate — what may be done *to* this session.
`CanReportState` says this client *reports*, so it is a property of the
build and not of what is loaded. It was gated on the loaded-file flag,
which denied exactly the two reports that matter most: `Stopped` and
`Idle` are by definition the states where nothing is playing. The plugin
refuses a report from a session that declared it does not report, so the
session was not corrected but **frozen** at the last state the server had
accepted — a position and a duration for an item that had stopped, which
under privacy leaves the *shape* of what was watched on the wire after the
viewer stopped it. There is no switch that turns reporting off; this is an
unconditional yes.

### State names are the server's, lowercase, in exactly one place

`PlaybackStateWire.ToWireName` is the only thing that spells a state for
the hub. The `State` field is a string here and an enum there, so nothing
in either build type-checks it: the server's `Loading` was split into
`preparing` and `buffering`, this client kept sending `"Loading"`, and
for thirteen days **every load report failed argument binding at the hub**
— the method never ran, the server kept the state it already held, and
the only trace was a `Logger.Debug` line here. Five copies of the same
switch statement is what let one wrong name sit in four of them.

The names are lowercase because that is what the contract declares on the
enum, and it is the only spelling both JSON stacks accept. The Shoko host
registers `AddNewtonsoftJsonProtocol`, which wins the `json` protocol
whichever order the registrations happen in, and Newtonsoft matches
case-insensitively — which is the only reason `"Playing"` ever worked.
System.Text.Json, the protocol the plugin's own `AddSignalR()` would
supply, matches exactly and refuses every capitalised name. `DeviceType`
was the same shape of bug waiting to happen and is now `"companion"` too.
`PlaybackStateWireTests` pins the whole vocabulary and round-trips it
through both serialisers.

### `CanStop` is gated differently from every other transport flag

`Playing`, `Paused` and `Buffering` are the loaded-and-playable states,
and everything transport-shaped reads them. A stall is not idleness: the
media is fine, the position is real and the last frame is still up, only
the cache ran dry — so a session that went deaf whenever `paused-for-cache`
went true was deaf at the moment somebody reached for the remote.
`Preparing` is the opposite and stays out: pre-processing a file to build
the frame index has produced nothing to seek in or capture, and declaring
otherwise is a promise this client cannot keep.

`CanStop` also holds while preparing, because stopping is control of the
session's *attention* rather than of playback. A viewer who started the
wrong file should not have to wait out a frame-index build to say so.

Both conditions are derived inside `BuildCurrentCapabilities`, from the
state itself rather than from booleans the caller computed — two flags
handed in would be two things every future state has to remember to move.

### Both declarations are pushed from one event, and neither is pushed twice

`UpdateCapabilitiesOnHubAsync` and `UpdateSettingsOnHubAsync` each compare
against the last declaration the server accepted and send nothing when it
has not moved, so callers push freely rather than each working out first
whether the answer changed. Both DTOs are records for that comparison.
Capabilities are refreshed from `SettingsChanged` — half the flags are
computed from settings, and privacy is raised by that event and by no
other one the push used to hang off — and from `ReportStateAsync`, which
catches the inputs no save touches: what is loaded, and
`EffectivePrivacyMode` turning itself on for restricted content. Both are
forced after a reconnect, where what the reclaimed session holds is not
known. The settings window no longer pushes capabilities itself; being one
of four places that remembered to is what let a tray or mpv privacy toggle
change the client's mind without telling the server.
- `ScreenshotSubtitleBehavior` — subtitle visibility on screenshot: `Disabled` / `OnlyWhenPaused` (default) / `Always`
- `MediaSessionEnabled` — global enabled switch for Media Session API integration
- `MediaSessionAutoConnectId` — Guid of the connection to auto-connect for Media Session API (null = none)
- `LogLevel` — Trace/Debug/Info/Warn/Error
- `UrlSchemeRegistrationAsked` — one-time prompt flag
- `AlwaysUseConfiguredRoutes` — bypass direct URL check (default `false`)

### Per-Connection (`Connections[]`)
- `Id` (Guid) — stable unique identifier (auto-generated)
- `Name` — display name
- `Routes[]` — `{ BaseUrl, UseHttps }` probed in order
- `ApiKey` — stored API key
- `IgnoredManagedFolderIds`, `ManagedFolderMappings` — folder management

## CLI Verbs

Parsed in `Program`:
- `register` — register `shoko://` URL scheme with OS
- `unregister` — remove URL scheme registration
- `--force` — bypass running-instance check (overwrites stale lock file)
- Any `shoko:` / `http://` / `https://` URL — play or forward

## Notifications

`PlatformNotificationService` shells out to OS-native tools:
- **Linux:** `notify-send`
- **Windows:** `PowerShell` `[Windows.UI.Notifications.ToastNotificationManager]`
- **macOS:** `osascript` with `display notification`

## Build & Publish

```bash
# Normal build
dotnet build

# Tests
dotnet test Shoko.Companion.Tests/Shoko.Companion.Tests.csproj

# Single-file Windows build (framework-dependent)
dotnet publish -c Release -r win-x64 --self-contained false

# Single-file Linux x64 / arm64 / macOS arm64
dotnet publish -c Release -r linux-x64 --self-contained false
dotnet publish -c Release -r linux-arm64 --self-contained false
dotnet publish -c Release -r osx-arm64 --self-contained false
```
