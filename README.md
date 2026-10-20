# Shoko Companion

Shoko Companion is a small tray app for [Shoko Server](https://shokoanime.com/). When you click a `shoko://` link in the Shoko Web UI, it plays the video in [mpv](https://mpv.io/) on your computer and reports your progress back to the server, so resume positions and watched states stay in sync.

It can also:

- open a file's folder in your file manager, using mappings from the server's managed folders to paths on your machine;
- show what you're watching in Discord (Rich Presence);
- join the server's media sessions, if the server has a plugin that provides them, so you can see and control playback from another device and hand a video over from one device to another.

It runs on Windows (x64), Linux (x64 and arm64) and macOS (Apple Silicon).

For step-by-step setup and day-to-day use, read the **[usage guide](./USAGE.md)**.

## Getting it

Download the latest build from the [Releases page](https://github.com/ShokoAnime/Shoko.Companion/releases):

| Platform | File |
|---|---|
| Windows | `shoko-companion.Installer.exe` |
| Linux x64 | `shoko-companion-linux-x64.AppImage` |
| Linux arm64 | `shoko-companion-linux-arm64.AppImage` |
| macOS (Apple Silicon) | `shoko-companion-osx-arm64.dmg` |

The release builds bundle the .NET runtime, so there's nothing else to install for the app itself. You do need:

- a Shoko Server you can reach from this machine;
- mpv, installed somewhere the companion can find it (see the [usage guide](./USAGE.md#1-install-mpv));
- a Discord desktop client or RPC server, if you want Rich Presence.

## How playback is synced

When you play something, the companion asks the server for the playlist, picks up your resume position, and starts mpv. While it plays, the companion tells the server what happened. How much it tells depends on `PlaybackSyncingBehavior`:

- **After playback** (the default): one event when you stop.
- **On every event**: start, pause, resume and stop.
- **Live sync**: the same, plus a progress update about once a minute while playing.

A file counts as watched when mpv reaches the end of it, or when you stop at 97.5% or later.

### Who writes the watch state

When the companion is connected to a media session on the server, the server writes your watch state itself, based on what the companion reports. Two writers on the same record would fight, so they take turns:

- **While a media session is connected, the companion's own syncing is off.** It stops as soon as the session registers, even halfway through a video. That's safe, because the server has seen the same playback since the session started and can finish the record.
- **When the session goes away, the companion takes over again at the next video, never halfway through one.** A watch record describes one viewing. If the companion took over at 80%, it would mark the video watched having seen only the last fifth. So the video that was playing when the session ended keeps whatever the server last wrote. The server owned that viewing.
- **A dropped connection isn't the session going away.** The companion keeps reconnecting and reclaims the same session, so a network hiccup changes nothing.

Both handovers are written to the log (`PlaybackSessionManager`), because a handover that goes wrong is otherwise invisible until your watch state already is.

`PlaybackSyncingEnabled` only covers the companion's own syncing. Turning it off doesn't stop the server writing for a connected session. To stop both, use privacy mode with **Disable playback event syncing**.

The same goes for `PlaybackSyncingBehavior`, `SyncUserDataInitialSkipEventCount` and `SyncUserDataLiveScrobbleTickThreshold`: they only shape the companion's own syncing. While a media session is connected, when and how often your watch state is written is decided by the server's own settings, not by these.

### Privacy mode

Privacy mode can be switched from the tray menu, the settings window, or inside mpv (`Ctrl+p` by default). While it's on, the companion tells the server, which then hides what this device plays from everyone else and stops recording it. The server applies this per item and never turns it back off for an item once it's on. Nothing already recorded is removed.

The sub-options decide what else privacy mode covers: hiding the title and poster from Discord, refusing remote screenshots, and stopping playback syncing altogether. You can also have privacy mode switch on by itself for restricted content.

## Configuration

Most people never need to touch the settings file: everything below can be set from the settings window. The file lives in a per-platform folder:

| Platform | Settings file |
|---|---|
| Windows | `%APPDATA%\shoko-companion\settings.json` |
| macOS | `~/Library/Application Support/shoko-companion/settings.json` |
| Linux | `$XDG_CONFIG_HOME/shoko-companion/settings.json`, or `~/.config/shoko-companion/settings.json` if `XDG_CONFIG_HOME` isn't set |

Logs go in a `logs/` folder next to it. The companion notices when you edit the file by hand and reloads it.

### Command line

```bash
shoko-companion [--home <path>] [--force] [register | unregister | <url>]
```

- `--home <path>` (or `--home=<path>`) keeps settings and logs in another folder. The `SHOKO_COMPANION_HOME` environment variable does the same. The argument wins over the variable, and either wins over the platform default.
- `--force` starts even if a lock file says another instance is running. Use it after a crash leaves a stale lock behind.
- `register` and `unregister` add or remove the `shoko://` link handler. If the companion isn't already running, it then starts as usual.
- A `shoko:` (or plain `http://`/`https://`) URL is played or opened. If the companion is already running, the URL is handed to the running instance.

### `settings.json` reference

Options that are enums are saved by name, as listed below.

| Key | Type | Default | Description |
|---|---|---|---|
| `Connections` | array | `[]` | Server connections (see below). |
| `OnNewUrlAction` | enum | `Append` | What a new link does while something plays: `Replace`, `Ignore` or `Append` (add to the playlist). |
| `AlwaysUseConfiguredRoutes` | bool | `false` | Always go through the connection's routes instead of trying the address in the link first. Useful behind reverse proxies. |
| `LastUsedServerUrl` | string | `null` | Last server address that worked. Set automatically; used by **Open WebUI**. |
| `MpvPath` | string | `null` | Path to mpv. Found and saved on first use if empty. |
| `MpvStartPaused` | bool | `false` | Start playback paused. |
| `IsFullscreen` | bool | `true` | Fullscreen state, remembered between plays. |
| `Volume` | int | `100` | Volume (0 to 130), remembered between plays. |
| `Muted` | bool | `false` | Mute state, remembered between plays. |
| `PlaybackSyncingEnabled` | bool | `true` | Sync playback to the server while no media session is connected. |
| `PlaybackSyncingBehavior` | enum | `AfterPlayback` | `AfterPlayback`, `OnEveryEvent` or `LiveSync` (see above). Own syncing only. |
| `SyncUserDataInitialSkipEventCount` | int | `3` | Progress events to skip at the start, while the player settles. Own syncing only. |
| `SyncUserDataLiveScrobbleTickThreshold` | int | `6` | Live sync interval in 10-second ticks (1 to 60). The default is one update a minute. Own syncing only. |
| `MediaSessionEnabled` | bool | `false` | Use the server's media sessions. |
| `MediaSessionAutoConnectId` | Guid | `null` | ID of the connection to join media sessions on at start-up. |
| `AllowRemotePlay` | bool | `true` | Let other devices start and control playback here. |
| `AllowRemoteVolumeControl` | bool | `true` | Let other devices change the volume and mute. |
| `AllowRemoteScreenshot` | bool | `true` | Let other devices ask for a screenshot of the current frame. |
| `AllowSessionHandoff` | bool | `true` | Let another device move what it's playing to this one. |
| `ScreenshotSubtitleBehavior` | enum | `OnlyWhenPaused` | Hide subtitles in remote screenshots: `Disabled` (never), `OnlyWhenPaused` or `Always`. |
| `DiscordEnabled` | bool | `false` | Show Discord Rich Presence. |
| `DiscordClientIdOverride` | string | `null` | Your own Discord application ID instead of the built-in one. |
| `DiscordIdlePresence` | bool | `false` | Show a "Browsing" presence when nothing plays. |
| `DiscordButton1` | enum | `AniDB` | First presence button: `Disabled`, `AniDB`, `TMDB` or `TvdbOrImdb`. |
| `DiscordButton2` | enum | `Disabled` | Second presence button, same choices. |
| `PrivacyMode` | bool | `false` | Privacy mode on or off. |
| `PrivacyModeHideDiscord` | bool | `false` | In privacy mode, show "Watching Anime" in Discord instead of the title and poster. |
| `PrivacyModeDisableRemoteScreenshots` | bool | `false` | In privacy mode, refuse remote screenshots. |
| `PrivacyModeDisablePlaybackEvents` | bool | `false` | In privacy mode, sync nothing, neither from the companion nor from the server on its behalf. |
| `PrivacyModeForRestrictedContent` | bool | `false` | Switch privacy mode on by itself for restricted content. |
| `PrivacyModeMpvKeybinding` | string | `Ctrl+p` | mpv key that toggles privacy mode. |
| `SettingsMpvKeybinding` | string | `Ctrl+Shift+o` | mpv key that opens the companion's settings. |
| `LogLevel` | string | `Info` | `Trace`, `Debug`, `Info`, `Warn` or `Error`. |
| `UrlSchemeRegistrationAsked` | bool | `false` | Whether the first-run link handler prompt has been shown. |

Each entry in `Connections`:

| Key | Type | Default | Description |
|---|---|---|---|
| `id` | Guid | new | Stable ID, referenced by `MediaSessionAutoConnectId`. |
| `Name` | string | `""` | Display name. |
| `Routes` | array | `[]` | Addresses for the server, tried in order (see below). |
| `ApiKey` | string | `null` | API key for the server. |
| `IgnoredManagedFolderIds` | array | `[]` | Managed folders you chose never to be asked about again. |
| `ManagedFolderMappings` | array | `[]` | Local paths for the server's managed folders: `Id`, `Name`, `ServerPath`, `LocalPath`. |

Each entry in `Routes`:

| Key | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | string | `""` | Host, port and optional sub-path, without the protocol, like `server:8111` or `192.168.1.5:8111/shoko`. |
| `UseHttps` | bool | `false` | Use HTTPS instead of HTTP. |

## For developers

### Building

You need the .NET 10 SDK.

```bash
dotnet build Shoko.Companion/Shoko.Companion.csproj
dotnet run --project Shoko.Companion/Shoko.Companion.csproj
dotnet test Shoko.Companion.Tests/Shoko.Companion.Tests.csproj
```

The project publishes as a single file. To get the same self-contained binary the releases use:

```bash
dotnet publish Shoko.Companion/Shoko.Companion.csproj -c Release -r linux-x64 \
  -p:PublishSingleFile=true -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Swap the runtime identifier for `linux-arm64`, `win-x64` or `osx-arm64`. Avalonia unpacks its native libraries (Skia, HarfBuzz) to a temporary folder on first start; that's expected.

The installers are built by `.github/workflows/release.yml` when a release is published: Inno Setup for Windows (`packaging/win/installer.iss`, which also registers `shoko://`), an AppImage for Linux (`packaging/linux/build.sh`) and a `.dmg` with an app bundle for macOS (`packaging/osx/build.sh`, whose `Info.plist` declares the `shoko` URL scheme). `scripts/package.sh` builds what a Linux machine can: the AppImages, plus plain archives for Windows and macOS.

### Architecture

```
shoko: URL ──▶ App.DispatchUrl ──┬─▶ PlaybackCoordinator ──┬─▶ ShokoApiClient
                                 │                         ├─▶ MpvIpcClient
                                 │                         ├─▶ PlaybackSessionManager (syncing, Discord)
                                 │                         └─▶ MediaSessionClient (SignalR)
                                 │
                                 └─▶ FolderActionHandler ──▶ file manager
```

- **`ShokoUrlParser`** turns a `shoko:` URL into an action (`play` or `open-folder`) and finds or creates the matching connection.
- **`App.DispatchUrl`** sends `play` to `PlaybackCoordinator` and `open-folder` to `FolderActionHandler`.
- **`PlaybackCoordinator`** fetches the playlist (`/api/v3/Playlist/Generate`), starts mpv with JSON IPC, restores the resume position, volume and fullscreen state, and follows mpv's state.
- **`PlaybackSessionManager`** decides what to sync and when, and builds the Discord presence.
- **`ShokoApiClient`** talks to the server: `/api/auth`, `/api/v3/File/{id}/UserData`, `/api/v3/File/{id}/Scrobble`, `/api/v3/File/{id}/Stream`, `/api/v3/ManagedFolder` and `/api/v3/Plugin/Features`. Servers are probed through `/api/v3/Init/Version`.
- **`MediaSessionClient`** finds the `media-sessions` feature through `/api/v3/Plugin/Features`, connects to the hub path the server gives, registers the session and relays remote commands. It never hardcodes a plugin name or route.
- **`MpvIpcClient`** speaks mpv's JSON IPC over a Unix socket or named pipe.
- **`FolderActionHandler`** maps managed folder IDs or server paths to local folders and opens them.
- **`SingleInstanceManager`** keeps one instance running and forwards URLs to it over a named pipe.
- **`PlatformNotificationService`** shows notifications through `notify-send` (Linux), PowerShell (Windows) or `osascript` (macOS).

[AGENTS.md](./AGENTS.md) has the detailed notes on the media session protocol, stream URLs and the reasoning behind them.

### Debugging

`.vscode/launch.json` has two launch targets:

- **Launch shoko-companion** starts it plainly. With no connections, the settings window opens.
- **Launch shoko-companion (with URL)** passes a sample `shoko:` URL so you can step through the whole flow.

Both set `SHOKO_COMPANION_HOME=${workspaceFolder}/companion-dev`, so debugging never touches your real settings.

To send real `shoko://` clicks to the debug build, open the settings window and press **Register** under **URL Scheme**. That registers the debug executable, with its custom home baked in, as the link handler. A click then starts a second instance, which hands the URL to the running debug session and exits. When you're done, unregister it the same way, or from the command line:

```bash
dotnet run --project Shoko.Companion/Shoko.Companion.csproj -- unregister
```

## Acknowledgements

Shoko Companion builds on these projects:

- [Shoko Server](https://shokoanime.com/), which does the real work. The companion is just a sidekick.
- [mpv](https://mpv.io/), whose JSON IPC makes it easy to drive from another program.
- [Avalonia](https://avaloniaui.net/), for one UI codebase across Windows, Linux and macOS, on top of Skia and HarfBuzz.
- [DiscordRichPresence](https://github.com/Lachee/discord-rpc-csharp) by Lachee, the C# bindings for Discord Rich Presence.
- [NLog](https://nlog-project.org/), for logs in the same JSONL format as the server.
- [Newtonsoft.Json](https://www.newtonsoft.com/json), for JSON handling.
- [ASP.NET Core SignalR](https://learn.microsoft.com/aspnet/core/signalr/), for the media session connection.
- [xUnit](https://xunit.net/) and [Moq](https://github.com/devlooped/moq), for the tests.
- [.NET](https://dotnet.microsoft.com/), which runs it all.

## License

[MIT](./LICENSE) © Shoko
