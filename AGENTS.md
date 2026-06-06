# AGENTS.md — Shoko Companion

## Project Overview

Avalonia-based Linux/Windows/macOS desktop tray companion for Shoko Server. Sits in the system tray, receives `shoko://` URL clicks, resolves playlist metadata from the Shoko API, launches mpv with JSON IPC for playback, and scrobbles watch progress back to the server.

## Stack

- **.NET 10** / C# 13 / `net10.0`
- **Avalonia 11.3.12** — desktop UI framework
- **NLog 6.1.3** — structured logging (JSONL files under `{ConfigRoot}/logs/`)
- **Newtonsoft.Json 13** — all JSON serialization (NO System.Text.Json)
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
        ├── ConsumePendingUrl → PlayAsync if forwarded URL arrived
        ├── Subscribe to SingleInstanceManager.UrlReceived (post-startup URLs)
        ├── First-run → show settings window
        └── URL scheme registration prompt (once)
```

### Single Instance

- **PID lock file** at `{ConfigRoot}/.lockfile` — replaces old Named Mutex (unreliable on Linux)
- **URL forwarding:** secondary instance writes URL via `NamedPipeClientStream`, primary listens on `NamedPipeServerStream` (backed by Unix domain sockets on Linux)
- `UrlReceived` event fires when URL arrives at running instance

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

### IPC Socket Lifecycle

- Socket path: `/tmp/shoko-companion-mpv-{companionPid}.sock` on Linux
- Stale socket deleted before launch (`LaunchAndConnectAsync`)
- Socket deleted on mpv exit (`OnProcessExited`)
- Socket deleted on shutdown (`StopAsync`, cleanup before flags are reset)

## Models

All DTOs match the **Shoko Server API v3** responses exactly.

```csharp
PlaylistItemDto
├── Episode (EpisodeDto?)
├── AdditionalEpisodes (List<EpisodeDto>)
└── Parts (List<FileDto>)

EpisodeDto
├── IDs (EpisodeIdsDto)      // ID, ParentSeries, AniDB, TvDB[], IMDB[], TMDB?
├── HasCustomName, Description, IsFavorite, IsHidden
├── Images (ImagesDto?)       // Posters, Backdrops, Banners, Logos, Discs
├── Duration (TimeSpan), ResumePosition (TimeSpan?)
├── WatchCount, UserRating (object?), Watched (DateTime?), Size (int)
├── Created, Updated
└── Name (string)

EpisodeIdsDto
├── ID (int), ParentSeries (int), AniDB (int)
├── TvDB (List<int>), IMDB (List<string>)
└── TMDB (TMDBIdsDto?)

TMDBIdsDto
├── Episode (List<int>), Movie (List<int>), Show (List<int>)

EpisodeTypeDto (enum)
├── Unknown=0, Episode=1, Special=2, Credits=3
└── Trailer=4, Parody=5, Other=6

FileDto
├── ID (int), Size (long), IsVariation, IsIgnored
├── Hashes (List<HashDto>), Locations (List<LocationDto>)
├── AVDump (AVDumpDto?), Resolution (string?)
├── Duration, ResumePosition, Viewed, Watched, Imported
└── Created, Updated

HashDto
├── Type (string?), Value (string?)

LocationDto
├── ID (int), FileID (int), ManagedFolderID (int)
├── RelativePath (string?), IsAccessible (bool)

AVDumpDto
├── Status (string?), Progress (double?), SucceededCreqCount (int?), FailedCreqCount (int?)
├── PendingCreqCount (int?)
├── StartedAt (DateTime?), LastDumpedAt (DateTime?), LastVersion (string?)

SeriesDto
├── IDs (SeriesIdsDto?)
└── Name (string?)

SeriesIdsDto
├── ID (int), AniDB (int?)

ImagesDto
├── Posters (List<object>), Backdrops (List<object>), Banners (List<object>)
├── Logos (List<object>), Discs (List<object>)

VideoUserDataDto
├── [JsonProperty("progressPosition")] ProgressPosition (TimeSpan?)
├── [JsonProperty("watchedCount")]     WatchedCount (int)
├── [JsonProperty("lastWatchedAt")]    LastWatchedAt (DateTime?)
└── [JsonProperty("lastUpdatedAt")]    LastUpdatedAt (DateTime)

AuthRequest
├── [JsonProperty("user")]   User (string)
├── [JsonProperty("pass")]   Pass (string)
└── [JsonProperty("device")] Device (string)

AuthResponse
└── [JsonProperty("apikey")] ApiKey (string?)

ManagedFolderDto
├── ID (int), Path (string?), Name (string?), IsEnabled (bool)

ScrobbleEventType (enum)
├── None=0, UserInteraction=1, PlaybackStart=2, PlaybackPause=3
├── PlaybackResume=4, PlaybackProgress=5, PlaybackEnd=6, Import=7
└── ToQueryValue() → "play" | "pause" | "resume" | "scrobble" | "stop" | "user-interaction"
```

## Settings

Stored as JSON at `{ConfigRoot}/settings.json`. Fields:

- `ServerBaseUrl`, `ApiKey` — Shoko server connection
- `MpvPath` — optional override for mpv binary
- `OnNewUrlAction` — `Replace` / `Ignore` / `Append` (default `Append`)
- `PlaybackSyncingEnabled` — master toggle for scrobbling (default `true`)
- `LivePlaybackSyncingEnabled` — periodic position updates (default `true`)
- `SkipRestrictedContent` — skip scrobbling for adult content (default `true`)
- `AlwaysUseConfiguredRoutes` — bypass direct URL check (default `false`)
- `ScrobbleIntervalMs` — default 15000
- `DiscordEnabled`, `DiscordClientIdOverride` — Discord presence
- `DiscordIdlePresence` — idle presence toggle (default `false`)
- `LogLevel` — Trace/Debug/Info/Warn/Error
- `UrlSchemeRegistrationAsked` — one-time prompt flag

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
dotnet test

# Single-file Windows build (framework-dependent)
dotnet publish -c Release -r win-x64 --self-contained false

# Single-file Linux build
dotnet publish -c Release -r linux-x64 --self-contained false

# Single-file Linux arm64 / macOS arm64
dotnet publish -c Release -r linux-arm64 --self-contained false
dotnet publish -c Release -r osx-arm64 --self-contained false
```
