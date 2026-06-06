# Shoko Companion — Usage Guide

This guide walks through installing, configuring, and using Shoko Companion day-to-day. For an overview and architecture, see [README.md](./README.md).

---

## 1. Install mpv

The companion drives [mpv](https://mpv.io/) for playback. Install it and make sure it's on your `PATH`:

- **Windows:** `winget install mpv` or [download a build](https://mpv.io/installation/)
- **macOS:** `brew install mpv`
- **Linux:** `sudo apt install mpv` / `sudo pacman -S mpv` / etc.

> The companion auto-discovers mpv on first use and saves the path to its settings. If it can't find mpv, set `MpvPath` in `settings.json`.

## 2. Get the companion

Either grab a published single-file binary for your platform from CI artifacts (`shoko-companion-<rid>`), or build it yourself:

```bash
dotnet run --project Shoko.Companion/Shoko.Companion.csproj
```

On launch it places an icon in your system tray.

## 3. First run

The first time you start the companion:

1. **Main Window** — Opens automatically when no server connections are configured. Click **Add…** to create one:
   - **Name** — display label for the connection (e.g. `My Server`)
   - **Routes** — one or more addresses (host:port) for the server. Probed in order. Each route can set HTTP or HTTPS.
   - **API Key** — paste your Shoko API key (optional — can set later, or authenticate interactively)
   - Click **Save** to add the connection.

2. **URL scheme prompt** — After closing the settings window, the companion asks:
   > *"Would you like to register the shoko:// URL scheme so clicking a Shoko link in your browser opens this app?"*
   - Click **Register** to set it up, or **Not Now** to skip (you can always do it later from the tray menu).

> **Auto-configuration shortcut:** If you skip the connection setup, the first `shoko:` URL you click will auto-detect the server address and create a connection for you. If the server requires an API key, you'll be prompted to enter it (or provide credentials to log in).

## 4. Register the `shoko://` URL scheme

So your browser/Web UI can hand off playback to the companion, the `shoko://` protocol handler needs to be registered with the OS.

**On first run** the companion will prompt you automatically (see Section 3). If you skipped it, you can register at any time:

- **From the tray menu** — right-click the tray icon and choose **Register URL Scheme**.
- **From the command line:**
```bash
shoko-companion register
shoko-companion --home /path/to/home register
shoko-companion --force   # bypass running-instance check (stale lock file)
```

To remove the handler later:

```bash
shoko-companion unregister
```

| Platform | How it's registered |
|---|---|
| Windows | Per-user registry under `HKCU\Software\Classes\shoko` |
| Linux | `~/.local/share/applications/shoko-url-handler.desktop` + `xdg-mime` + hicolor theme icon |
| macOS | Requires an `.app` bundle with `CFBundleURLTypes` (best-effort; see notes) |

## 5. Play something

In the **Shoko Web UI**, use the "Send to External Player" action (wording to be revisited). The flow:

```
shoko: URL ──▶ Companion ──▶ launches mpv, plays the stream, syncs playback events
```

- If the companion is already running, the new link is forwarded to the existing instance.
- While playing, it sends playback events at a configurable interval (default ~10s) plus on play / pause / resume / stop, and marks the file **watched** at ≥ 97.5%.
- Closing the mpv window sends a final **stop** event.
- If a new `shoko:` URL arrives while something is playing, the default behaviour is to **append** to the mpv playlist. You can change this to `"Replace"` (stop + start new) or `"Ignore"` via `OnNewUrlAction` in settings.

### The `shoko:` URL format

The `shoko:` URL is an **intent scheme** — the path segment after the host
selects the action and its parameters.

Full form:
```
shoko:[//][http[s]://]<server host>[/<path>]/<intent>?<params>
```

Available intents:

**Play a playlist**
```
shoko:[//][http[s]://]<server host>[/<path>]/play?playlist=<playlist id>
```

`playlist` items: `s<seriesId>`, `e<episodeId>`, `f<fileId>`, or a bare series
id (comma-separated for multiple).

**Open folder (by managed folder ID)**
```
shoko:[//][http[s]://]<server host>[/<path>]/open-folder?managedFolder=<managed folder id>[&relativePath=<relative path>]
```

**Open folder (by absolute server path)**
```
shoko:[//][http[s]://]<server host>[/<path>]/open-folder?path=<absolute server path>
```

Examples:
```
shoko:server:8111/play?playlist=s1234
shoko:server:8111/open-folder?managedFolder=42&relativePath=Series/Show
shoko:server:8111/open-folder?path=/mnt/anime/Series/Show/ep01.mkv
```

## 6. Tray menu

Right-click (Windows/Linux) or click (macOS) the tray icon:

| Item | Action |
|---|---|
| **Open WebUI** | Opens the first connection's host in your browser |
| — | |
| **Open Settings** | Opens the main configuration window |
| **Manage Folders** | Opens the managed folder mappings dialog |
| — | |
| **Enable / Disable Discord Presence** | Toggles Rich Presence on/off |
| **Register URL Scheme** | Registers the `shoko://` protocol handler with the OS |
| **Unregister URL Scheme** | Removes the `shoko://` protocol handler |
| — | |
| **Exit** | Sends a final stop event and quits |

The tray tooltip reflects the current playback state: "Shoko Companion" when idle or stopped, "Loading...", "Playing", "Paused", or "Error".

## 7. Discord Rich Presence

Discord Rich Presence uses a built-in application ID. Toggle it on from the tray menu (**Enable Discord Presence**). It will light up automatically when you play something, as long as the Discord desktop client is running.

### Customise (optional)

1. Create a Discord application at <https://discord.com/developers/applications> and copy its **Application (Client) ID**.
2. In `settings.json` set:
   ```json
    "DiscordClientIdOverride": "your-application-id",
   "DiscordEnabled": true
   ```
3. (Optional) Upload a square logo art asset named `shoko_default` to your Discord app for the fallback image.

Presence shows the series + episode and elapsed time. Poster art is used when the playlist provides a public CDN image URL (AniDB/TMDB).

### Idle presence

Set `"DiscordIdlePresence": true` to show "Shoko Companion / Browsing" in Discord when nothing is playing, switching to "Idle" after 10 minutes of inactivity (default `false`).

## 8. Where things live

| Platform | Config + logs |
|---|---|
| Windows | `%APPDATA%\shoko-companion\` |
| macOS | `~/Library/Application Support/shoko-companion/` |
| Linux | `$XDG_CONFIG_HOME/shoko-companion/` (or `~/.config/shoko-companion/`) |

Override the whole root with the `SHOKO_COMPANION_HOME` environment variable or the `--home <path>` argument.

Logs are under `logs/` — handy when troubleshooting.

### `settings.json` fields

| Key | Type | Default | Description |
|---|---|---|---|---|
| `Connections` | array | `[]` | Server connections (see README.md for schema). |
| `MpvPath` | string | `null` | Path to mpv (auto-discovered on first use). |
| `OnNewUrlAction` | string | `"Append"` | `"Replace"` / `"Ignore"` / `"Append"` when a new URL arrives while playing. |
| `PlaybackSyncingEnabled` | bool | `true` | Master toggle for all playback event syncing. |
| `LivePlaybackSyncingEnabled` | bool | `false` | Periodic position updates (requires `PlaybackSyncingEnabled`). |
| `SkipRestrictedContent` | bool | `true` | Skip playback event syncing for restricted content. |
| `ScrobbleIntervalMs` | int | `10000` | Progress update interval in ms (min 5000). |
| `DiscordEnabled` | bool | `false` | Enable Discord Rich Presence. |
| `DiscordClientIdOverride` | string | `null` | Override for the built-in Discord app ID. |
| `DiscordIdlePresence` | bool | `false` | Show "Browsing" → "Idle" presence when not playing. |
| `DiscordPrivacyMode` | bool | `false` | Hide anime title and poster from Discord presence. |
| `AlwaysUseConfiguredRoutes` | bool | `false` | Skip direct URL check; always use connection route table. |
| `LogLevel` | string | `"Info"` | One of: Trace, Debug, Info, Warn, Error. |

The app hot-reloads `settings.json` when edited externally.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| **"mpv not found"** notification | Install mpv or set `MpvPath` in `settings.json`. |
| **"mpv launch failed"** | Check the path in `settings.json`; ensure mpv runs from a terminal. |
| **Nothing happens on `shoko:` link** | Use **Register URL Scheme** from the tray menu, or run `shoko-companion register`. Ensure the companion is running in the tray. |
| **"No API key configured"** | Open **Settings** and add an API key to your connection. |
| **"No local mapping found"** | Open **Manage Folders** from the tray and add a mapping for the folder ID. |
| **Watched status not updating** | Check `logs/` for playback event errors; verify the API key is valid. |
| **Discord presence missing** | Make sure the Discord desktop client is running. If you've set `DiscordClientIdOverride`, verify it's valid. |
| **macOS doesn't open links** | macOS URL schemes require a proper `.app` bundle with `CFBundleURLTypes`. |
