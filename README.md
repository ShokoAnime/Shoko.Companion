# Shoko Companion

A lightweight, cross-platform **system-tray companion** for [Shoko Server](https://shokoanime.com/) that plays your anime in **mpv**, reports watch progress back to the server, (optionally) integrates with the **Media Session** plugin for remote playback control, and (optionally) shows what you're watching as **Discord Rich Presence**.

- **Binary:** `shoko-companion`
- **Display name:** Shoko Companion
- **Framework:** .NET 10 · Avalonia 11
- **Platforms:** Windows (x64), Linux (x64 / arm64), macOS (arm64)
- **Repository:** [github.com/ShokoAnime/Shoko.Companion](https://github.com/ShokoAnime/Shoko.Companion)

---

## Features

- **Plays in mpv** — catches `shoko://` URLs, launches mpv, plays the stream.
- **Playback events** — periodic position updates, start/pause/resume/stop events, auto-marks watched at ≥ 97.5%. `Play` (new media) and `Resume` (unpause current) are separate commands.
- **Resume support** — pre-fetches resume position and seeks mpv on file load.
- **Server connections** — multiple connections with route fallback; auto-discovered from  the `shoko://` URLs or manually configured.
- **Managed folder mappings** — resolves "Open Folder" actions from the web UI to local paths.
- **Discord Rich Presence** — optionally shows series/episode info in Discord.
- **Cross-platform tray app** — Windows, Linux, macOS. Native notifications.

### Media Session API Support

When the **Media Session** plugin is installed on the Shoko server, the companion can connect to its SignalR hub for remote playback control. This enables:
- Other devices/clients for the user to see what's playing and send play/pause/seek/stop commands
- Live state updates pushed via SignalR alongside REST-based scrobbling
- Per-connection auto-connect with manual Connect/Disconnect in settings

---

## Requirements

| Requirement | Notes |
|---|---|
| **A running Shoko Server** | v3 API reachable from this machine. |
| **mpv** | Must be installed and discoverable (in `PATH` or a common install location). [mpv.io](https://mpv.io/installation/) |
| **.NET 10 runtime** | Only if you run the framework-dependent build. |
| **Discord desktop client** | Only if you enable Rich Presence. |

---

## Build & run

```bash
# from the repo root
dotnet build Shoko.Companion/Shoko.Companion.csproj

# run (framework-dependent)
dotnet run --project Shoko.Companion/Shoko.Companion.csproj
```

### Single-file, self-contained publish (one binary per platform)

```bash
# Self-contained single-file (bundles .NET runtime)
dotnet publish Shoko.Companion/Shoko.Companion.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

# Linux x64 / arm64 / macOS arm64 (change -r accordingly)
dotnet publish ... -r linux-x64   -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish ... -r linux-arm64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish ... -r osx-arm64   -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

> Avalonia extracts its native libraries (Skia/HarfBuzz) to a temp directory on first run — this is expected.

---

## Configuration

Settings live in a JSON file under a per-platform config root:

| Platform | Location |
|---|---|---|
| Windows | `%APPDATA%\shoko-companion\settings.json` |
| macOS | `~/Library/Application Support/shoko-companion/settings.json` |
| Linux | `$XDG_CONFIG_HOME/shoko-companion/settings.json` (falls back to `~/.config/shoko-companion/settings.json` if `XDG_CONFIG_HOME` is not set) |

Logs are written next to it under `logs/`.

### Custom home directory

Relocate the entire config/log root two ways (precedence: `--home` → env var → platform default):

```bash
# --home argument
shoko-companion --home /path/to/home

# --force: bypass the running-instance check (e.g. after a crash left a stale lock file)
shoko-companion --force

# SHOKO_COMPANION_HOME environment variable
SHOKO_COMPANION_HOME=/path/to/dev-home dotnet run --project Shoko.Companion/Shoko.Companion.csproj
```

### `settings.json` reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Connections` | array | `[]` | List of server connections (see below). |
| `MpvPath` | string | `null` | Path to the mpv binary. Auto-discovered and saved on first use. |
| `MpvFullScreen` | bool | `true` | Launch mpv in full-screen mode. |
| `OnNewUrlAction` | string | `"Append"` | When a new `shoko:` URL arrives while playing: `"Replace"` (stop + start new), `"Ignore"` (silently discard), or `"Append"` (add to mpv playlist). |
| `PlaybackSyncingEnabled` | bool | `true` | Master toggle for all playback event syncing (start/end/pause/resume). |
| `LivePlaybackSyncingEnabled` | bool | `false` | Periodic position updates during playback (requires `PlaybackSyncingEnabled`). |
| `MediaSessionAutoConnectId` | Guid | `null` | Server connection ID to auto-connect for the Media Session SignalR hub. Null disables auto-connect. |
| `SyncUserDataInitialSkipEventCount` | int | `3` | Number of initial non-pause events to skip after starting, letting the player settle. |
| `SyncUserDataLiveScrobbleTickThreshold` | int | `3` | Number of position events accumulated before sending a live progress update. |
| `SyncUserDataLivePositionThresholdMs` | int | `5000` | Minimum position change (ms) required to trigger a live progress update. |
| `SkipRestrictedContent` | bool | `true` | Skip playback event syncing for restricted/adult content. |
| `ScrobbleIntervalMs` | int | `10000` | How often to send progress updates (min 5000). |
| `DiscordEnabled` | bool | `false` | Enable Discord Rich Presence. |
| `DiscordClientIdOverride` | string | `null` | Override for the built-in Discord app ID. |
| `DiscordIdlePresence` | bool | `false` | Show "Browsing" → "Idle" presence when nothing is playing. |
| `DiscordPrivacyMode` | bool | `false` | Hide anime title and poster from Discord presence; show generic "Watching Anime" instead. |
| `AlwaysUseConfiguredRoutes` | bool | `false` | Skip direct URL reachability check; always use the connection's route table. |
| `LogLevel` | string | `"Info"` | One of: `Trace`, `Debug`, `Info`, `Warn`, `Error`. |

Each connection object in `Connections`:

| Key | Type | Default | Description |
|---|---|---|---|
| `Name` | string | `""` | Display name for the connection (user-editable). |
| `Routes` | array | `[]` | Ordered list of route objects (see below). Probed in order. |
| `ApiKey` | string | `null` | API key for this server. |
| `IgnoredManagedFolderIds` | array | `[]` | Managed folder IDs the user has silenced. |
| `ManagedFolderMappings` | array | `[]` | Local path mappings for the server's managed folders. |
| `CachedManagedFolders` | array | `null` | Cached folder metadata from the server (not persisted). |

Each route object in `Routes`:

| Key | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | string | `""` | Host, port, and optional sub-path (e.g. `server:8111` or `192.168.1.5:8111/subpath`). No protocol prefix. |
| `UseHttps` | bool | `false` | Whether to connect via HTTPS. |

You can edit `settings.json` by hand (the app hot-reloads it) or via the tray menu.

---

## Architecture

```
shoko: URL ──▶ DispatchUrl ──┬─▶ PlaybackCoordinator
                             │          │
                             │    ┌─────┼────────┬──────────────┐
                             │    ▼     ▼        ▼              ▼
                             │   Api   Mpv     Discord      Notifications
                             │  Client IPC     Presence
                             │
                             └─▶ FolderActionHandler
                                     │
                                     ▼
                                File Manager
```

- **`DispatchUrl`** (in `App`) parses the incoming URL and routes `play` to `PlaybackCoordinator` and `open-folder` to `FolderActionHandler`.
- **`PlaybackCoordinator`** orchestrates everything: resolves the playlist JSON, pre-fetches resume position, forwards the stream URL to mpv, observes mpv state, and syncs playback events.
- **`MpvIpcClient`** speaks mpv's JSON IPC and raises typed property/event callbacks.
- **`ShokoApiClient`** wraps the v3 endpoints (`/api/auth`, `/api/v3/Playlist/Generate`, `/api/v3/File/{id}/UserData`, `/api/v3/File/{id}/Scrobble`).
- **`ShokoUrlParser`** handles both legacy and new URL formats.
- **`FolderActionHandler`** resolves managed folder IDs to local paths and opens the OS file manager.
- **`SingleInstanceManager`** ensures only one instance runs and forwards URLs to it.

See **[USAGE.md](./USAGE.md)** for the step-by-step user guide.

---

## Development & debugging

The repo ships VS Code targets (`.vscode/launch.json`):

- **Launch Shoko.Companion** — plain run (shows the connection setup window on first run).
- **Launch Shoko.Companion (with URL)** — passes a sample `shoko:` URL so you can step through the full flow.

Both use `SHOKO_COMPANION_HOME=${workspaceFolder}/companion-dev` so debugging stays isolated from your real config.

### URL scheme forwarding during development

With the debug companion running, right-click the tray icon and choose
**Register URL Scheme**. This registers the debug executable (with its
`--home <dev-home>` baked in) as the `shoko://` handler.

Whenever a `shoko://` link is clicked in your browser, the registered
handler launches a secondary instance that immediately forwards the URL
to the running debug session via the named pipe and exits — a lightweight
trampoline. If nothing is running, one starts up.

When you're done, unregister to restore the production handler — either
from the tray menu or the command line:

```bash
dotnet run --project Shoko.Companion/Shoko.Companion.csproj -- unregister
```

Run the tests:

```bash
dotnet test Shoko.Companion.Tests/Shoko.Companion.Tests.csproj
```

---

## Acknowledgements

Shoko Companion would not exist without these amazing projects:

- **[Shoko Server](https://shokoanime.com/)** — the anime metadata server that powers the entire ecosystem. This companion is just a sidekick.
- **[mpv](https://mpv.io/)** — the best media player, with a beautiful JSON IPC that makes programmatic control a joy.
- **[Avalonia](https://avaloniaui.net/)** — cross-platform UI framework that lets us target Windows, Linux, and macOS from one codebase. Underpinned by **Skia** and **HarfBuzz**.
- **[DiscordRichPresence](https://github.com/Lachee/discord-rpc-csharp)** by Lachee — clean C# bindings for Discord's Rich Presence SDK.
- **[NLog](https://nlog-project.org/)** — structured logging that matches the server's JSONL format.
- **[Newtonsoft.Json](https://www.newtonsoft.com/json)** — the de facto JSON library for .NET.
- **[xunit](https://xunit.net/)** and **[Moq](https://github.com/devlooped/moq)** — testing and mocking, keeping things solid.
- **[.NET](https://dotnet.microsoft.com/)** — the runtime and ecosystem that makes cross-platform desktop apps viable.

---

## License

[MIT](./LICENSE) © Shoko
