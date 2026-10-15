# Using Shoko Companion

This guide walks you through installing Shoko Companion, connecting it to your server, and using it day to day. For an overview, the full settings reference and developer notes, see the [README](./README.md).

## 1. Install mpv

The companion plays everything in [mpv](https://mpv.io/), so install that first:

- **Windows:** `winget install mpv`, or [download a build](https://mpv.io/installation/).
- **macOS:** `brew install mpv`.
- **Linux:** your package manager, for example `sudo apt install mpv` or `sudo pacman -S mpv`.

The companion looks for mpv on your `PATH` and in the usual install folders (Program Files on Windows, Homebrew on macOS, `/usr/bin` and `/usr/local/bin` on Linux). The first time it finds mpv, it saves the path. If it can't find mpv, it asks you where it is. You can also set the path yourself in the settings window.

## 2. Install the companion

Download the build for your platform from the [Releases page](https://github.com/ShokoAnime/Shoko.Companion/releases):

- **Windows:** run `shoko-companion.Installer.exe`. The installer also sets up `shoko://` links for you.
- **Linux:** download `shoko-companion-linux-x64.AppImage` (or the `arm64` one), make it executable and run it.
- **macOS:** open `shoko-companion-osx-arm64.dmg` and drag Shoko Companion into Applications. The app bundle handles `shoko://` links once it's installed.

Once started, the companion sits in your system tray.

## 3. First start

1. **Link handler.** The companion asks once whether it should handle `shoko://` links, so clicking a link in your browser opens it. Choose **Register**, or skip it for now and do it later (see step 4).
2. **Server connection.** If you have no connections yet, the settings window opens. Click **Add…** under **Server Connections** and fill in:
   - **Connection name**, like `My Home Server`.
   - **Routes**: one or more addresses for the server, such as `myserver:8111` or `192.168.1.5:8111/shoko`. Tick **Use HTTPS** where needed. Routes are tried in order, so put the one you use most at the top.
   - **Username** and **password**, then **Login & Fetch Key**. The companion logs in once and keeps the API key, not your password.
   - Click **Save**.

You can also skip adding a connection. The first `shoko:` link you click will set one up from the address in the link, and if the server wants you to log in, the companion asks for your username and password then.

## 4. Set up `shoko://` links

If you skipped the prompt on first start, open the settings window from the tray and press **Register** under **URL Scheme**. **Unregister** removes it again. From a terminal, the same thing is:

```bash
shoko-companion register
shoko-companion unregister
```

How the link handler is set up depends on the platform:

| Platform | Where |
|---|---|
| Windows | The registry, under `HKEY_CURRENT_USER\Software\Classes\shoko`. The installer registers it for all users instead. |
| Linux | `~/.local/share/applications/shoko-url-handler.desktop`, made the default with `xdg-mime`. |
| macOS | The app bundle's `Info.plist`. Registering from the app does nothing here; install the app from the `.dmg`, and remove it to unregister. |

## 5. Play something

In the Shoko Web UI, choose to play in an external player. The link goes to the companion, which starts mpv and plays the video from your resume position.

- If the companion is already running, the link is handed to it, so you never get two copies.
- If something is already playing, the new video is added to mpv's playlist. You can change that under **When a new URL arrives** in the settings: replace what's playing, or ignore the new link.
- Your progress goes back to the server as you watch (see [How playback is synced](./README.md#how-playback-is-synced)). A video counts as watched when it plays to the end, or when you stop at 97.5% or later.
- Closing mpv stops playback and sends the final update.

The companion remembers your volume, mute and fullscreen state between plays. In mpv, `Ctrl+p` toggles privacy mode and `Ctrl+Shift+o` opens the companion's settings. Both keys can be changed in the settings window.

### Opening folders

The Web UI can also ask the companion to open a file's folder. The server knows the folder by its own path, which usually isn't the path on your computer, so the companion needs a mapping. The first time it sees a folder it doesn't know, it asks you where that folder is on this machine. Choose **Save & Open** to remember it, or **Ignore & Don't Ask Again** to never be asked about that folder. You can change mappings later under **Manage Folders** in the tray menu.

### The `shoko:` link format

You won't usually write these yourself, but it helps to know what they look like:

```
shoko:[//][http[s]://]<server>[/<path>]/<action>?<parameters>
```

Play a playlist:

```
shoko:server:8111/play?playlist=s1234
```

The playlist is what the server's playlist API accepts: `s<series ID>`, `e<episode ID>`, `f<file ID>` or a bare series ID, several separated by commas.

Open a folder, by managed folder ID and an optional relative path, or by the absolute path on the server:

```
shoko:server:8111/open-folder?managedFolder=42&relativePath=Series/Show
shoko:server:8111/open-folder?path=/mnt/anime/Series/Show/ep01.mkv
```

## 6. The tray menu

Right-click the tray icon (or click it, depending on your platform) for:

| Item | What it does |
|---|---|
| **Open WebUI** | Opens the last server the companion reached in your browser. Greyed out until it has reached one. |
| **Open Settings** | Opens the settings window. |
| **Manage Folders** | Opens the folder mappings. Greyed out until you have a connection. |
| **Enable/Disable Privacy Mode** | Switches privacy mode. |
| **About Shoko Companion** | Shows the version. |
| **Exit** | Stops playback, sends the final update and quits. |

Hovering over the icon shows what it's doing: Loading, Playing, Buffering, Paused or Error.

## 7. Media sessions

If your Shoko Server has a plugin that provides media sessions, the companion can join them. Other devices can then see what's playing here, pause, seek, switch tracks, take screenshots, and send videos to play. You can also move a video from another device to this one and carry on where it left off.

To set it up, open the settings and go to **Media Session API**:

1. Tick **Enable Media Session API**.
2. Choose the connection under **Auto-connect connection**, so it connects whenever the companion starts. Only one connection can auto-connect. **Connect** and **Disconnect** work by hand.
3. Decide what other devices may do: **Allow remote play**, **Allow remote volume control**, **Allow remote screenshot** and **Allow session handoff to this device**. All are on by default.

While connected, the server keeps your watch state, not the companion. The README explains [who writes the watch state](./README.md#who-writes-the-watch-state) and why the switch happens only between videos.

## 8. Privacy mode

Turn privacy mode on from the tray menu, the settings window, or `Ctrl+p` in mpv. The server then hides what this device plays from everyone else and stops recording it. Nothing already recorded is removed.

Under **Privacy Mode** in the settings you can also choose to:

- **Hide Discord presence info**: show "Watching Anime" instead of the title and poster.
- **Disable remote screenshots**.
- **Disable playback event syncing (scrobbling)**: sync nothing at all, from the companion or from the server.
- **Always use privacy mode for restricted content**: switch it on by itself for restricted series.

## 9. Discord Rich Presence

Tick **Enable Discord Rich Presence** in the settings. When the Discord desktop client is running, it shows the series, the episode and how long you've been watching, with the series poster when the server provides one (never for restricted content). A button links to the series on AniDB by default; set `DiscordButton1` and `DiscordButton2` in `settings.json` to use TMDB, TVDB or IMDb instead, or no button.

**Show idle presence when not playing** shows "Browsing" while nothing plays, switches to "Idle" after 10 minutes, and clears it after an hour.

To use your own Discord application:

1. Create one at <https://discord.com/developers/applications> and copy its application ID.
2. Paste it into **Discord client ID** in the settings, or set `DiscordClientIdOverride` in `settings.json`.
3. Optionally upload an art asset named `shoko_default`. It's shown when there's no poster.

## 10. Where things are kept

| Platform | Settings and logs |
|---|---|
| Windows | `%APPDATA%\shoko-companion\` |
| macOS | `~/Library/Application Support/shoko-companion/` |
| Linux | `$XDG_CONFIG_HOME/shoko-companion/`, or `~/.config/shoko-companion/` |

Start the companion with `--home <path>`, or set `SHOKO_COMPANION_HOME`, to keep everything somewhere else. Logs are in the `logs/` folder; the log level is under **Logging** in the settings. Every setting is listed in the [README](./README.md#settingsjson-reference).

## Troubleshooting

| Problem | What to try |
|---|---|
| The companion can't find mpv | Install mpv, or set the path under **mpv Player** in the settings. |
| mpv doesn't start | Check the mpv path in the settings, and that mpv runs from a terminal. |
| Clicking a `shoko:` link does nothing | Register the link handler (step 4), and check the companion is in the tray. |
| It keeps asking you to log in | Edit the connection and use **Login & Save** to fetch a new API key. |
| It asks where a folder is | Pick the local folder, or set it up under **Manage Folders**. |
| Watched status doesn't update | Check the logs for sync errors and that the API key still works. If a media session is connected, the server does the syncing. |
| No Discord presence | Make sure the Discord desktop app is running, and if you set your own client ID, that it's right. |
| It says another instance is running, but none is | Start it once with `--force` to clear the stale lock. |
| macOS doesn't open links | Install the app from the `.dmg`; links need the app bundle. |
