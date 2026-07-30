using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Launch;
using Shoko.Companion.Mpv;
using Shoko.Companion.Notifications;
using Shoko.Companion.Server;
#if DEBUG
using Shoko.Companion.Discord;
#endif

namespace Shoko.Companion.Windows;

/// <summary>
/// Settings / first-run configuration window where users enter server details,
/// mpv path, Discord preferences, etc.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly string[] LogLevelValues = ["Trace", "Debug", "Info", "Warn", "Error"];
    private readonly ObservableCollection<ServerConnection> _connections;
    private bool _loadingSettings = true;

    // Debounce timer for the Discord Client ID text box — saves 500ms after
    // the user stops typing, rather than relying on LostFocus.
    private Timer? _discordDebounceTimer;

    // Track media session connection state
    private bool _mediaSessionConnected;

    /// <summary>
    /// Creates the window, loads current settings, and (in DEBUG builds) adds
    /// test buttons for notifications, mpv, and Discord.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _connections = new ObservableCollection<ServerConnection>(
            SettingsProvider.Instance.Settings.Connections);
        ConnectionListBox.ItemsSource = _connections;
        ConnectionListBox.SelectionChanged += (_, _) =>
        {
            var hasSel = ConnectionListBox.SelectedItem is not null;
            EditConnectionButton.IsEnabled = hasSel;
            RemoveConnectionButton.IsEnabled = hasSel;
        };
        LoadSettings();
#if DEBUG
        AddDebugTestNotificationButton();
        AddDebugMpvTestButton();
        AddDebugDiscordTestButton();
        AddDebugMpvOsdTestButton();
        AddDebugMpvNotFoundDialogButton();
#endif

        // Disable Manage Folders if no connections
        ManageFoldersButton.IsEnabled = _connections.Count > 0;

        // If no connections exist, open the Add Connection dialog immediately
        if (_connections.Count == 0)
        {
            // Fire asynchronously so the window finishes loading first
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(300);
                OnAddConnectionClick(null, null!);
            });
        }
    }

#if DEBUG
    /// <summary>
    /// DEBUG-only helper: adds a button that fires a test OS notification.
    /// </summary>
    private void AddDebugTestNotificationButton()
    {
        var button = new Button
        {
            Content = "Fire Test Notification (DEBUG)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        button.Click += (_, _) =>
        {
            PlatformNotificationService.Instance
                .Show("Shoko Companion", "This is a test notification.", NotificationSeverity.Info);
        };
        FormPanel.Children.Add(button);
    }

    /// <summary>
    /// DEBUG-only helper: adds a button that finds and launches mpv with notifications.
    /// </summary>
    private void AddDebugMpvTestButton()
    {
        var button = new Button
        {
            Content = "Test mpv Open (DEBUG)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        button.Click += async (_, _) =>
        {
            var mpvPath = await MpvProcess.FindMpvAsync();
            if (mpvPath is null)
            {
                PlatformNotificationService.Instance.Show("mpv Test", "mpv not found on this system.", NotificationSeverity.Warning);
                return;
            }

            var ipcPath = MpvProcess.GetDefaultIpcPath();
            var process = MpvProcess.Launch(mpvPath, ipcPath);
            if (process is null)
            {
                PlatformNotificationService.Instance.Show("mpv Test", $"Failed to launch at {mpvPath}", NotificationSeverity.Error);
                return;
            }

            try
            {
                PlatformNotificationService.Instance.Show("mpv Test", $"PID {process.Id} started. Waiting 2s…", NotificationSeverity.Info);
                await Task.Delay(2000);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                PlatformNotificationService.Instance.Show("mpv Test", "mpv stopped successfully.", NotificationSeverity.Info);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "mpv test error");
                PlatformNotificationService.Instance.Show("mpv Test", ex.Message, NotificationSeverity.Error);
            }
        };
        FormPanel.Children.Add(button);
    }

    /// <summary>
    /// DEBUG-only helper: adds a button that fires a test Discord presence.
    /// </summary>
    private void AddDebugDiscordTestButton()
    {
        var button = new Button
        {
            Content = "Test Discord Presence (DEBUG)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        button.Click += async (_, _) =>
        {
            try
            {
                var s = SettingsProvider.Instance.Settings;
                if (!s.CanUseDiscord)
                {
                    PlatformNotificationService.Instance.Show("Discord Test", "Discord not configured.", NotificationSeverity.Warning);
                    return;
                }

                var discord = new DiscordPresenceService();
                await discord.InitializeAsync(s.DiscordClientId);
                discord.SetPresence(new DiscordPresenceData(
                    Details: "Test Presence",
                    State: "DEBUG mode — checking Rich Presence",
                    LargeImageKey: null,
                    LargeImageText: null,
                    StartTimeStamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ));
                PlatformNotificationService.Instance.Show("Discord Test", "Presence set — check your Discord profile!", NotificationSeverity.Info);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Discord presence test failed");
                PlatformNotificationService.Instance.Show("Discord Test", ex.Message, NotificationSeverity.Error);
            }
        };
        FormPanel.Children.Add(button);
    }

    private void AddDebugMpvNotFoundDialogButton()
    {
        var button = new Button
        {
            Content = "Test mpv Not Found Dialog (DEBUG)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        button.Click += async (_, _) =>
        {
            var dialog = new MpvNotFoundDialog();
            if (this.IsVisible)
            {
                await dialog.ShowDialog(this);
            }
            else
            {
                dialog.Show();
            }

            if (!string.IsNullOrWhiteSpace(dialog.MpvPath))
                Logger.Info("mpv path set via debug dialog: {Path}", dialog.MpvPath);
        };
        FormPanel.Children.Add(button);
    }
    /// <summary>
    /// DEBUG-only helper: adds a button that fires a test OSD message via the
    /// playback coordinator's ShowOsdTextAsync.
    /// Only shows if mpv is actually running.
    /// </summary>
    private void AddDebugMpvOsdTestButton()
    {
        var button = new Button
        {
            Content = "Test mpv OSD (DEBUG)",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        button.Click += async (_, _) =>
        {
            var app = Avalonia.Application.Current as App;
            if (app?.Coordinator is not null)
                await app.Coordinator.ShowOsdTextAsync("DEBUG: OSD test message");
        };
        FormPanel.Children.Add(button);
    }
#endif

    private void LoadSettings()
    {
        _loadingSettings = true;
        var s = SettingsProvider.Instance.Settings;
        MpvPathBox.Text = s.MpvPath ?? string.Empty;

        PrivacyModeCheck.IsChecked = s.PrivacyMode;
        PrivacyModeHideDiscordCheck.IsChecked = s.PrivacyModeHideDiscord;
        PrivacyModeHideMediaPlaybackInfoCheck.IsChecked = s.PrivacyModeHideMediaPlaybackInfo;
        PrivacyModeDisableRemoteControlCheck.IsChecked = s.PrivacyModeDisableRemoteControl;
        PrivacyModeDisableRemoteScreenshotsCheck.IsChecked = s.PrivacyModeDisableRemoteScreenshots;
        PrivacyModeDisablePlaybackEventsCheck.IsChecked = s.PrivacyModeDisablePlaybackEvents;
        PrivacyModeMpvKeybindingBox.Text = s.PrivacyModeMpvKeybinding ?? string.Empty;

        DiscordEnabledCheck.IsChecked = s.DiscordEnabled;
        DiscordClientIdBox.Text = s.DiscordClientIdOverride ?? string.Empty;
        DiscordIdlePresenceCheck.IsChecked = s.DiscordIdlePresence;
        ResetDiscordClientIdButton.Click += (_, _) => DiscordClientIdBox.Text = string.Empty;

        MpvFullScreenCheck.IsChecked = s.MpvFullScreen;
        MpvStartPausedCheck.IsChecked = s.MpvStartPaused;

        RestoreVolumeCheck.IsChecked = s.RestoreVolume;
        if (s.Volume.HasValue)
        {
            VolumeSlider.Value = s.Volume.Value;
            VolumeLabel.Text = $"{s.Volume.Value}%";
        }
        else
        {
            VolumeSlider.Value = 100;
            VolumeLabel.Text = "100%";
        }

        OnNewUrlCombo.SelectedIndex = s.OnNewUrlAction switch
        {
            OnNewUrlBehavior.Replace => 0,
            OnNewUrlBehavior.Ignore => 1,
            OnNewUrlBehavior.Append => 2,
            _ => 2,
        };
        PlaybackSyncingCheck.IsChecked = s.PlaybackSyncingEnabled;
        LivePlaybackSyncingCheck.IsChecked = s.LivePlaybackSyncingEnabled;
        PrivacyModeForRestrictedCheck.IsChecked = s.PrivacyModeForRestrictedContent;
        AlwaysUseRoutesCheck.IsChecked = s.AlwaysUseConfiguredRoutes;

        // Set the log level combo to the saved value
        var levelIndex = Array.IndexOf(LogLevelValues, s.LogLevel);
        LogLevelCombo.SelectedIndex = levelIndex >= 0 ? levelIndex : 2; // default to "Info"

        LoadMediaSessionSection();

        _loadingSettings = false;
    }

    private async void OnAddConnectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var dialog = new AddConnectionDialog();
            await dialog.ShowDialog(this);

            if (dialog.Connection is not null)
            {
                SettingsProvider.Instance.Settings.Connections.Add(dialog.Connection);
                _connections.Add(dialog.Connection);
                ConnectionListBox.SelectedItem = dialog.Connection;
                ManageFoldersButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open add-connection dialog");
        }
    }

    private void OnEditConnectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = ConnectionListBox.SelectedItem as ServerConnection;
        if (selected is null) return;

        var dialog = new EditConnectionDialog(selected);
        dialog.ShowDialog(this);
    }

    private void OnRemoveConnectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = ConnectionListBox.SelectedItem as ServerConnection;
        if (selected is null) return;

        SettingsProvider.Instance.Settings.Connections.Remove(selected);
        _connections.Remove(selected);
    }

    private async void OnBrowseMpvClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select mpv executable",
                AllowMultiple = false
            });

            if (files.Count == 1)
            {
                MpvPathBox.Text = files[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to browse for mpv executable");
        }
    }

    private void OnDiscordClientIdTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_loadingSettings) return;

        // Debounce: reset timer on each keystroke, save 500ms after typing stops.
        _discordDebounceTimer?.Dispose();
        _discordDebounceTimer = new Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var s = SettingsProvider.Instance.Settings;
                s.DiscordClientIdOverride = string.IsNullOrWhiteSpace(DiscordClientIdBox.Text)
                    ? null
                    : DiscordClientIdBox.Text.Trim();
                SettingsProvider.Instance.Save();
            });
        }, null, 500, Timeout.Infinite);
    }

    private void OnAutoSaveSetting(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var s = SettingsProvider.Instance.Settings;
        s.MpvPath = string.IsNullOrWhiteSpace(MpvPathBox.Text) ? null : MpvPathBox.Text.Trim();
        s.MpvFullScreen = MpvFullScreenCheck.IsChecked == true;
        s.MpvStartPaused = MpvStartPausedCheck.IsChecked == true;
        s.PrivacyMode = PrivacyModeCheck.IsChecked == true;
        s.PrivacyModeHideDiscord = PrivacyModeHideDiscordCheck.IsChecked == true;
        s.PrivacyModeHideMediaPlaybackInfo = PrivacyModeHideMediaPlaybackInfoCheck.IsChecked == true;
        s.PrivacyModeDisableRemoteControl = PrivacyModeDisableRemoteControlCheck.IsChecked == true;
        s.PrivacyModeDisableRemoteScreenshots = PrivacyModeDisableRemoteScreenshotsCheck.IsChecked == true;
        s.PrivacyModeDisablePlaybackEvents = PrivacyModeDisablePlaybackEventsCheck.IsChecked == true;
        s.PrivacyModeMpvKeybinding = string.IsNullOrWhiteSpace(PrivacyModeMpvKeybindingBox.Text)
            ? "Ctrl+p"
            : PrivacyModeMpvKeybindingBox.Text.Trim();

        s.DiscordEnabled = DiscordEnabledCheck.IsChecked == true;
        s.DiscordClientIdOverride = string.IsNullOrWhiteSpace(DiscordClientIdBox.Text) ? null : DiscordClientIdBox.Text.Trim();
        s.DiscordIdlePresence = DiscordIdlePresenceCheck.IsChecked == true;

        s.OnNewUrlAction = OnNewUrlCombo.SelectedIndex switch
        {
            0 => OnNewUrlBehavior.Replace,
            1 => OnNewUrlBehavior.Ignore,
            2 => OnNewUrlBehavior.Append,
            _ => OnNewUrlBehavior.Append,
        };
        s.PlaybackSyncingEnabled = PlaybackSyncingCheck.IsChecked == true;
        s.LivePlaybackSyncingEnabled = LivePlaybackSyncingCheck.IsChecked == true;
        s.PrivacyModeForRestrictedContent = PrivacyModeForRestrictedCheck.IsChecked == true;
        s.AlwaysUseConfiguredRoutes = AlwaysUseRoutesCheck.IsChecked == true;

        s.RestoreVolume = RestoreVolumeCheck.IsChecked == true;
        s.Volume = (int)VolumeSlider.Value;

        if (LogLevelCombo.SelectedItem is ComboBoxItem item && item.Content is string level)
            s.LogLevel = level;

        // Media Session auto-connect
        s.AllowRemotePlay = AllowRemotePlayCheck.IsChecked == true;
        s.AllowRemoteScreenshot = AllowRemoteScreenshotCheck.IsChecked == true;
        s.ScreenshotSubtitleBehavior = ScreenshotSubtitleCombo.SelectedIndex switch
        {
            0 => Configuration.ScreenshotSubtitleBehavior.Disabled,
            1 => Configuration.ScreenshotSubtitleBehavior.OnlyWhenPaused,
            2 => Configuration.ScreenshotSubtitleBehavior.Always,
            _ => Configuration.ScreenshotSubtitleBehavior.OnlyWhenPaused,
        };
        if (MediaSessionConnectionCombo.SelectedItem is MediaSessionConnectionItem msItem && msItem.Id != Guid.Empty)
            s.MediaSessionAutoConnectId = msItem.Id;
        else
            s.MediaSessionAutoConnectId = null;

        SettingsProvider.Instance.Save();

        // Push updated capabilities to the hub after saving
        if (Avalonia.Application.Current is App app && app.MediaSessionClient is not null)
        {
            _ = app.MediaSessionClient.UpdateCapabilitiesOnHubAsync();
        }
    }

    private void OnVolumeSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var val = (int)e.NewValue;
        VolumeLabel.Text = $"{val}%";
        OnAutoSaveSetting(sender, null!);
    }

    private void OnManageFoldersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new ManageFoldersDialog();
        dialog.ShowDialog(this);
    }

    private void LoadMediaSessionSection()
    {
        var s = SettingsProvider.Instance.Settings;
        var items = new List<MediaSessionConnectionItem>
        {
            new() { Display = "(None)", Id = Guid.Empty }
        };
        items.AddRange(s.Connections.Select(c => new MediaSessionConnectionItem
        {
            Display = c.Name,
            Id = c.Id,
        }));

        AllowRemotePlayCheck.IsChecked = s.AllowRemotePlay;
        AllowRemoteScreenshotCheck.IsChecked = s.AllowRemoteScreenshot;
        ScreenshotSubtitleCombo.SelectedIndex = s.ScreenshotSubtitleBehavior switch
        {
            Configuration.ScreenshotSubtitleBehavior.Disabled => 0,
            Configuration.ScreenshotSubtitleBehavior.OnlyWhenPaused => 1,
            Configuration.ScreenshotSubtitleBehavior.Always => 2,
            _ => 1,
        };

        MediaSessionConnectionCombo.ItemsSource = items;

        if (s.MediaSessionAutoConnectId.HasValue && s.MediaSessionAutoConnectId.Value != Guid.Empty)
        {
            var match = items.FirstOrDefault(i => i.Id == s.MediaSessionAutoConnectId.Value);
            if (match is not null)
                MediaSessionConnectionCombo.SelectedItem = match;
            else
                MediaSessionConnectionCombo.SelectedItem = items[0];
        }
        else
        {
            MediaSessionConnectionCombo.SelectedItem = items[0];
        }

        UpdateMediaSessionState();
    }

    private void UpdateMediaSessionState()
    {
        var app = Avalonia.Application.Current as App;
        var connected = app?.MediaSessionClient?.IsConnected == true;
        _mediaSessionConnected = connected;

        MediaSessionStatusText.Text = connected
            ? $"Connected - {app?.MediaSessionClient?.SessionName ?? "Unknown"}"
            : "Not connected";
        MediaSessionStatusText.Foreground = connected
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Green)
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray);

        ConnectMediaSessionButton.IsEnabled = !connected;
        DisconnectMediaSessionButton.IsEnabled = connected;
    }

    private async void OnConnectMediaSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = MediaSessionConnectionCombo.SelectedItem as MediaSessionConnectionItem;
        if (selected is null || selected.Id == Guid.Empty)
            return;

        var conn = SettingsProvider.Instance.Settings.Connections
            .FirstOrDefault(c => c.Id == selected.Id && c.ApiKey is { Length: > 0 });
        if (conn is null)
        {
            MediaSessionStatusText.Text = "Connection has no API key. Edit the connection and log in first.";
            MediaSessionStatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);
            return;
        }

        var reachableUrl = conn.ProbeReachableBaseUrl();
        if (reachableUrl is null)
        {
            MediaSessionStatusText.Text = $"Could not reach {conn.Name}. Check the route.";
            MediaSessionStatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);
            return;
        }

        var app = Avalonia.Application.Current as App;
        if (app is null) return;

        MediaSessionStatusText.Text = "Checking plugin availability...";
        MediaSessionStatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Gray);

        var available = await MediaSessionClient.IsPluginAvailableAsync(reachableUrl, conn.ApiKey!);
        if (!available)
        {
            MediaSessionStatusText.Text = "Media Session plugin not available on this server.";
            MediaSessionStatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);
            return;
        }

        await app.ConnectMediaSessionAsync(reachableUrl, conn.ApiKey!);
        UpdateMediaSessionState();
    }

    private async void OnDisconnectMediaSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var app = Avalonia.Application.Current as App;
        if (app is null) return;

        await app.DisconnectMediaSessionAsync();
        UpdateMediaSessionState();
    }

    private void OnRefreshMediaSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadMediaSessionSection();
    }

    private void OnRegisterUrlSchemeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            UrlSchemeRegistrar.Register();
            PlatformNotificationService.Instance
                .Show("Shoko Companion", "Registered the shoko:// URL scheme.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to register the shoko:// URL scheme");
        }
    }

    private void OnUnregisterUrlSchemeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            UrlSchemeRegistrar.Unregister();
            PlatformNotificationService.Instance
                .Show("Shoko Companion", "Unregistered the shoko:// URL scheme.");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to unregister the shoko:// URL scheme");
        }
    }
}

/// <summary>
/// Combo box item representing a server connection for Media Session selection.
/// </summary>
internal sealed class MediaSessionConnectionItem
{
    public string Display { get; init; } = string.Empty;
    public Guid Id { get; init; }

    public override string ToString() => Display;
}
