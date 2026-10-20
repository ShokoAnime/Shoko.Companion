# AGENTS.md — Shoko Companion

## Project Overview

Avalonia-based Linux/Windows/macOS desktop tray companion for Shoko Server. Sits in the system tray, receives `shoko://` URL clicks, resolves metadata from the Shoko API, launches mpv with JSON IPC for playback, scrobbles watch progress, and integrates with the Media Session plugin for remote playback control.

## Stack

- **.NET 10** / C# 13 / `net10.0`
- **Avalonia 12.1.2** — desktop UI framework
- **NLog 6.2.1** — structured logging (JSONL files under `{ConfigRoot}/logs/`)
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

The companion optionally integrates with the server's media sessions via
SignalR. It knows the feature by name and nothing else: no plugin name, no
route.

1. **Detect**: `GET /api/v3/Plugin/Features` (Shoko's own endpoint) and find
   the feature named `media-sessions`. Its metadata carries `HubPath` and
   `ApiPath`; a missing feature, another major `Version`, or no hub path all
   mean "not available" (`MediaSessionClient.DetectAsync` /
   `FindMediaSessions`). Detection runs before any existing connection is
   torn down, so a failed probe leaves it alone
2. **Connect**: `HubConnection` to the feature's `HubPath`, sending the API
   key in the `apikey` header. **Never hardcode the hub path** — it is the
   server's to say
3. **Register**: calls `RegisterSession({ Name, DeviceType: "companion", ClientName: "Shoko Companion", HostName, Platform, Version, Capabilities, Settings })` — two separate declarations, re-pushed independently afterwards through `UpdateCapabilities` and `UpdateSettings`. Capabilities say what this build can do; settings say how the viewer configured it. See **Privacy Mode** below for the settings half.
4. **Receive commands**: `Play` (new media with VideoId), `Resume` (unpause current), `Pause`, `Seek`, `Stop`, `SetTracks` → relayed to `PlaybackCoordinator`
5. **Report state**: via `UpdateState` on coordinator state changes and position ticks — `State`, the `CurrentItem` / `NextItem` / `PreviousItem` triple, `Position`, `Duration`, `IsPaused`, `Volume`, `IsMuted`, `PlaybackSpeed`, `IsFullscreen`, `Tracks`, and `Seeked` on the one report after a seek lands. A patch, not a snapshot, and sent in the order it was made: see the next three sections

**A command only asks; the report is the result.** Since Kaname #335
(2026-10-20) the server applies no command itself: it sends the command,
changes nothing, and moves its state only when this session reports. So
**every command the companion carries out must end in a report of what
actually happened**, read from mpv, and never of what the command assumed.
A command that reports nothing leaves the server wrong until something else
happens to be reported; a report of a result that did not happen leaves it
wrong until something corrects it.

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
deltas. The one field a baseline never keeps is `Seeked`, because it is an
event: see below.

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

### And in the order it was made

Reports go out **in the order they were made**. `ReportStateAsync` records
`_lastState` and decides the stopped→idle timer at the call, in call order,
and queues the send behind every send queued before it (`EnqueueSend`); the
patch is taken inside the queue, so each one is against the report sent just
before it. The same queue carries `UpdatePlaylist`, `UpdateCapabilities` and
`UpdateSettings`. Each report used to be its own fire-and-forget task that
awaited a capability push before it sent, and one held up there was
overtaken by the next and landed after it: the server ended on the older
state, and `_reportedState` on the older baseline. `ReportOrderTests` holds
it, through `HubSendOverride`, the seam every queued send goes through.

### `Seeked` is an event, so it is never part of a baseline

The plugin stopped guessing seeks from position jumps (revam, 2026-10-20)
and relies on each client saying so: once a viewer is past its watch-sync
gate, a report with `Seeked: true` saves the watch position at once rather
than at the next 60 s interval. `true` means *this report's position is
where a seek landed*; absent means no seek, and the server never stores it.
So it is the one field that is not state, and three rules follow:

- **It rides on the tick, not beside it.** mpv's `seek` event sets
  `_pendingSeek`, and the first real `time-pos` after it calls
  `PlaybackSessionManager.OnSeek`, whose tick is raised with
  `PositionTickEventArgs.Seeked`. The coordinator forwards the flag on its
  own `PositionTick`, and `App.OnCoordinatorPositionTick` puts it on that
  report alone. A field anywhere else would be read by whichever tick got
  there first, and the timer's beat runs on the thread pool too. Every seek
  takes this path: one made in mpv, the hub's `Seek` (`SeekAsync` moves the
  session at once with `landed: false`, which is a position and not a seek
  report; mpv's own `seek` event reports the landing), and the resume seek
  at `file-loaded`, which is a `time-pos` set and so a seek to mpv. That
  one is issued before `StartSession`, so a pending seek is kept until a
  session exists rather than consumed into nothing, and `StopAsync` drops
  one that never landed. A null `time-pos` never consumes it, so a seek is
  never reported at 0.
- **`WithoutSeek` keeps it out of every baseline.** Both `_lastState`, the
  whole picture a registration or a reclaim carries, and `_reportedState`,
  the patch baseline, are stored through it. That is what makes it one-shot
  by construction: carried in `_lastState` it would be a seek replayed on
  every reconnect, and nothing at the call sites has to remember to clear
  it.
- **It is never compared, and it takes `Position` with it.** A report that
  says it is sent whatever else moved, with its position even when the
  baseline already holds that position, as it does after `SeekAsync` moved
  the session ahead of mpv. Null is not written at all: unlike the other
  fields there is no null the far side could hold.

`SeekReportTests` holds both halves.

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

### A position mpv cannot give is not 0

While a media session is connected, the position in the companion's reports
is what the server judges "watched" from, and the end of a file is where it matters most. When mpv
unloads a file at its end, `time-pos` goes unavailable and mpv sends
`"data": null`. That used to be read as 0, so the last tick of a finished
episode said *playing, at 0:00*, the plugin's end-of-playback save was made
from it, and an episode watched to the end was stored as watched for 5 ms
(2026-10-20, video 29529). Three rules now:

- **`PlaybackCoordinator.TimePosMs` maps null to null, and a null is
  ignored**: the last real position stands. A number, `0.0` included, is a
  position and is applied.
- **The last real position outlives the session.** `EndSession` keeps where
  it ended (`PlaybackSessionManager.LastKnownPositionMs` /
  `PositionVideoId`) until the next `StartSession`, because the `Stopped`
  report is built after the session object is gone and used to say 0. A
  timer beat that races `EndSession` sends no tick at all.
- **A position belongs to the file it was measured on.**
  `CurrentPositionSeconds`, which every report reads, and the forwarded
  `PositionTick` both go through `ReportablePositionMs`: a report naming a
  different item than the one measured gets 0, which covers mpv moving its
  playlist on before the next file's first `time-pos`. The null used to
  zero this by accident.
- **A file that played to its end is at its end.** The last real position
  is the last frame's timestamp, up to a frame short of the duration, and
  Shoko's completion threshold is a server setting that goes up to 100%:
  there, a file watched to the very end was never marked watched. revam,
  2026-10-20: *"round it up on the last frame if needed. that's a bug if
  anything."* So once mpv says the file ended, `PlaybackSession.ReportedPositionMs`
  is the larger of the last real position and the duration, and everything
  that reports or scrobbles a position reads it: the kept position, the
  state reports, the `Stopped` one, the tick, and the companion's own
  `PlaybackEnd` from `FinalizeCurrentFile` and `EndSession`. The duration is
  the session's `DurationMs`, Shoko's own figure for the file, set per file
  at `StartSession` / `OnNextFile`; mpv's `duration` property is not
  observed at all, and the coordinator's `_duration` is zeroed by
  `StopAsync`. An unknown duration (0) leaves the last real position, and
  the position is never lowered. `PositionMs` stays the measurement.

  "mpv says the file ended" is `OnEofReached`, and three things call it,
  because `--keep-open` (which `MpvProcess.Launch` passes) means the
  **last** file of a playlist gets no `end-file` at all — mpv pauses on its
  last frame instead:
  - `end-file` with reason `eof`, which is how every file but the last ends;
  - `eof-reached` going true, which is how the last one ends;
  - the end-of-file pause the coordinator stops on, because that pause can
    arrive before `eof-reached` does. It is also what a viewer pausing in
    the last 300 ms gets, which the coordinator already treats as the end.

  A `stop`, `quit`, `error` or `redirect` end, which includes a playlist
  move the viewer made, is not one of them. `OnNextFile` clears the flag,
  and `ReportablePositionMs` still gives a report naming the next item 0,
  so the duration never travels onto it.

Omitting an unknown position would be the patch-shaped answer, but
`_lastState` is the payload a registration or a reclaim carries and the
plugin refuses a baseline that does not name `Position`. The stopped→idle
follow-up's `TimeSpan.Zero` stays for the same reason: on `idle` the server
zeroes the position itself and ignores the one reported. The end-of-file
pause detection reads `CurrentPositionMs`, which is unchanged, and fires in
more cases now that the null no longer zeroes it. `EndOfFilePositionTests`
holds all of it.

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
by API key, and the media session plugin's `{ApiPath}/Stream/{videoId}[/...]`,
which is anonymous and guarded on a `sessionId` query parameter. `StreamUrls`
(`Playback/StreamUrls.cs`) recognises both and is the only place that knows
either shape.

**`ApiPath` is the server's, never a constant here.** It comes from the
`media-sessions` feature's metadata, which `ShokoApiClient.DetectMediaSessionsAsync`
finds and caches per base URL. `PrepareM3u8Async` awaits it before reading a
playlist, since every playback path goes through there, and the coordinator's
`Urls` rebuilds the recogniser when the path changes. A `StreamUrls` built with
no path recognises APIv3 only. Do not write the plugin's route or name into
this repository: the server says where things are.

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
├── On eof-reached/idle → stop + final scrobble
└── On end-file reason error → Error with mpv's file_error (never on idle)
```

### Playback Flow (via Media Session API)

**Every command ends in a report of what actually happened.** Since
Kaname #335 the server applies no command itself and shows only what this
session reports, so a command is not done when mpv has been told; it is done
when mpv's answer has been reported. The rules that follow from it:

- **Report from mpv, not from the command.** A state follows from mpv's own
  events (`file-loaded`, the `pause` and `paused-for-cache` properties,
  `end-file`, `eof-reached`), never from what a command expected mpv to do.
  `JumpToPlaylistItem` used to report `Playing` as it sent the jump, which
  was false whenever `MpvStartPaused` held mpv paused, and nothing ever
  corrected it.
- **Playback that starts from nothing starts a session, whatever started
  it.** `PlayAsync`, and `Resume` or `JumpToPlaylistItem` on a queue sitting
  in an idle mpv, all go through `ExpectSessionFor` (state `Loading`, the
  video expected), and `file-loaded` with no session running starts one in
  `StartSessionForLoadedFile`, the one place a session starts, for the file
  mpv's `path` names. Only `PlayAsync` used to, and playback started from
  the queue ran with no session: no ticks, a position stuck at 0, no
  `Seeked`, `Tracks` always null and `SetTracks` ignored.
- **A command that does not change what is playing leaves the state alone.**
  A `Play` that appends reports the queue (`UpdatePlaylist`), not `Loading`;
  it used to report `Loading` and then restore the state saved before it,
  which overwrote a pause or a resume the viewer made in between.
- **A Stop always reports, and wins.** `StopAsync` reports `Stopped` even
  when mpv fails to stop, and bumps `_loadGeneration`, which a `PlayAsync`
  still resolving checks after every await: one that lost to a Stop (or to a
  newer Play) gives up without reporting, and `file-loaded` does the same.
  Without it a Stop during the load was overwritten by the load's `Loading`
  and mpv was launched anyway.
- **The end-of-file pause is a stop.** A pause within 300 ms of the end with
  nothing queued reports `Stopped` at the duration, not `Paused`, per
  9f0dc90 (revam's ruling).
- **A file mpv cannot play is reported as `Error`, from mpv's `end-file`
  reason and never from idleness.** mpv ends a file it could not load or
  play with `end-file` reason `error` and, on recent mpv, a `file_error`
  such as "loading failed" or "unrecognized file format";
  `HandleFileError` reports `SetState(Error, file_error)`. The hub contract
  has no field for the message, so the hub gets `error` and the message goes
  to the notification, the log and `PlaybackStateChangedEventArgs`. mpv going idle is
  never read as a failure, because a load still preparing is idle too, and a
  Play that is merely slow must not be cut off. Until this a jump or resume
  whose file never loaded sat in `preparing` for good. How a failure is
  matched to its file:
  - **The attempt.** `start-file` and `end-file` carry the same
    `playlist_entry_id`, so only an error for the entry mpv last started
    counts, and only if it started under the current `_loadGeneration`. An
    error from a load a Stop or a newer Play abandoned changes nothing.
  - **The file.** The entry id is looked up in mpv's playlist as last
    observed (each entry's `id`); when it names a video, that must be the
    one expected: `ExpectSessionFor`'s while waiting, the session's or
    `path`'s while playing. An entry the cache does not hold (the `.m3u8`
    itself failing) is matched by the attempt alone.
  - **What it ends.** The load is bumped like a Stop, so neither a
    `file-loaded` inside its delay nor a `PlayAsync` still awaiting its
    `loadfile` can report over the Error. The session ends through
    `EndFailedSession`: a file mpv never gave a position for gets no
    `PlaybackEnd` of ours and keeps no position. The timer stops with it.
  - **mpv moves on by itself.** An error ends the file with `PT_ERROR`,
    and `mp_play_files` advances on it exactly as on an end of file;
    `--keep-open` only holds a file that reached `AT_END_OF_FILE`
    (`handle_keep_open`). So the next entry starts normally at its
    `file-loaded`, with no `Loading` in between, and with nothing after the
    failed file mpv goes idle (`--idle=yes`) and `Error` is the last report.
    Read from mpv's source (`player/loadfile.c`, `player/playloop.c`), not
    measured: no mpv was at hand.

  Until this landed none of `end-file` reached the coordinator: mpv writes
  an event's fields beside `event` rather than under `data`, and
  `MpvIpcClient` handed on `data ?? args`, which is null for `end-file`. So
  the `eof` branch above never ran outside the tests either.
  `MpvIpcClient.EventPayload` now hands on the whole message for every event
  but a property change and a client message. `MpvEventPayloadTests` holds
  it.

`CommandReportTests` drives each command through the coordinator against a
mocked mpv that answers a property set by reporting it back, with
`FakeShokoServer` on loopback for the `shoko://` URLs to resolve against.

When a `Play` command arrives via the SignalR hub:
1. `MediaSessionClient.HandlePlayAsync(request)` is invoked
2. Constructs a `shoko://` URL from `request.VideoId` and delegates to `PlaybackCoordinator.PlayAsync()`
3. The standard playlist resolution, mpv launch, and scrobble pipeline runs as normal; the session starts at `file-loaded`, and the state after `Loading` is mpv's to say

When a `Resume` command arrives via the SignalR hub:
1. `ResumeAsync()` is called on `PlaybackCoordinator`
2. mpv's `pause` property is set to `false` — the current file continues playing, and mpv's answer on `pause` is what reports `Playing`
3. No URL resolution, no playlist load — no media change. Unless mpv is idle
   with a queue (`AddToPlaylist` into an idle mpv): then it plays the queue's
   current entry, as a `JumpToPlaylistItem` would, and that starts a session

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
