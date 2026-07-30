# AGENTS.md — Shoko Companion

## Project Overview

Avalonia-based Linux/Windows/macOS desktop tray companion for Shoko Server. Sits in the system tray, receives `shoko://` URL clicks, resolves metadata from the Shoko API, launches mpv with JSON IPC for playback, scrobbles watch progress, and integrates with the Media Session plugin for remote playback control.

## Stack

- **.NET 10** / C# 13 / `net10.0`
- **Avalonia 11.3.12** — desktop UI framework
- **NLog 6.1.3** — structured logging (JSONL files under `{ConfigRoot}/logs/`)
- **Newtonsoft.Json 13** — all JSON serialization (NO System.Text.Json)
- **Microsoft.AspNetCore.SignalR.Client 10.0** — Media Session plugin hub connection
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
3. **Register**: calls `RegisterSession({ Name, DeviceType: "companion", ClientName: "Shoko Companion", HostName })`
4. **Receive commands**: `Play` (new media with VideoId), `Resume` (unpause current), `Pause`, `Seek`, `Stop` → relayed to `PlaybackCoordinator`
5. **Report state**: via `UpdateState({ State, VideoId, Title, PositionSeconds, DurationSeconds })` on coordinator state changes

**Auto-connect**: configured per-server-connection via `MediaSessionAutoConnectId` (Guid). Only one connection can auto-connect. Manual Connect/Disconnect buttons in the settings window.

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
- `MpvFullScreen` — launch mpv in full screen (default `true`)
- `OnNewUrlAction` — `Replace` / `Ignore` / `Append` (default `Append`)
- `PlaybackSyncingEnabled` — master toggle for scrobbling (default `true`)
- `PlaybackSyncingBehavior` — `AfterPlayback` / `OnEveryEvent` / `LiveSync` (default `AfterPlayback`)
- `SkipRestrictedContent` — skip scrobbling for adult content (default `true`)
- `DiscordEnabled`, `DiscordClientIdOverride`, `DiscordIdlePresence`
- `PrivacyModeHideDiscord` (replaces `DiscordPrivacyMode`)

## Privacy Mode

Global privacy mode with per-feature sub-toggles. Settings in `CompanionSettings`:

- `PrivacyMode` — master switch. When OFF, sub-toggles have no effect and individual feature toggles control behavior independently.
- `PrivacyModeHideDiscord` — hide title/poster from Discord (replaces `DiscordPrivacyMode`)
- `PrivacyModeHideMediaPlaybackInfo` — strip title/file info from Media Session hub state
- `PrivacyModeDisableRemoteControl` — deny remote Play/Pause/Seek/Stop via Media Session API
- `PrivacyModeDisableRemoteScreenshots` — deny remote screenshot capture via Media Session API
- `PrivacyModeDisablePlaybackEvents` — block all scrobbling to Shoko server
- `PrivacyModeForRestrictedContent` — auto-activate privacy mode for restricted (adult) content
- `PrivacyModeMpvKeybinding` — mpv key combo to toggle privacy during playback (default `Ctrl+p`, no Lua scripts needed)
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
