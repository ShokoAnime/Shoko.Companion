using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Mpv;
#if DEBUG
using Shoko.Companion.Discord;
using Shoko.Companion.Notifications;
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
    private bool _loadingSettings;

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
#endif

    private void LoadSettings()
    {
        _loadingSettings = true;
        var s = SettingsProvider.Instance.Settings;
        MpvPathBox.Text = s.MpvPath ?? string.Empty;

        DiscordEnabledCheck.IsChecked = s.DiscordEnabled;
        DiscordClientIdBox.Text = s.DiscordClientIdOverride ?? string.Empty;
        DiscordIdlePresenceCheck.IsChecked = s.DiscordIdlePresence;
        DiscordPrivacyModeCheck.IsChecked = s.DiscordPrivacyMode;
        ResetDiscordClientIdButton.Click += (_, _) => DiscordClientIdBox.Text = string.Empty;

        MpvFullScreenCheck.IsChecked = s.MpvFullScreen;

        RestoreVolumeCheck.IsChecked = s.RestoreVolume;

        OnNewUrlCombo.SelectedIndex = s.OnNewUrlAction switch
        {
            OnNewUrlBehavior.Replace => 0,
            OnNewUrlBehavior.Ignore => 1,
            OnNewUrlBehavior.Append => 2,
            _ => 2,
        };
        PlaybackSyncingCheck.IsChecked = s.PlaybackSyncingEnabled;
        LivePlaybackSyncingCheck.IsChecked = s.LivePlaybackSyncingEnabled;
        SkipRestrictedCheck.IsChecked = s.SkipRestrictedContent;
        AlwaysUseRoutesCheck.IsChecked = s.AlwaysUseConfiguredRoutes;

        // Set the log level combo to the saved value
        var levelIndex = Array.IndexOf(LogLevelValues, s.LogLevel);
        LogLevelCombo.SelectedIndex = levelIndex >= 0 ? levelIndex : 2; // default to "Info"

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

    private void OnAutoSaveSetting(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var s = SettingsProvider.Instance.Settings;
        s.MpvPath = string.IsNullOrWhiteSpace(MpvPathBox.Text) ? null : MpvPathBox.Text.Trim();
        s.MpvFullScreen = MpvFullScreenCheck.IsChecked == true;
        s.DiscordEnabled = DiscordEnabledCheck.IsChecked == true;
        s.DiscordClientIdOverride = string.IsNullOrWhiteSpace(DiscordClientIdBox.Text) ? null : DiscordClientIdBox.Text.Trim();
        s.DiscordIdlePresence = DiscordIdlePresenceCheck.IsChecked == true;
        s.DiscordPrivacyMode = DiscordPrivacyModeCheck.IsChecked == true;

        s.OnNewUrlAction = OnNewUrlCombo.SelectedIndex switch
        {
            0 => OnNewUrlBehavior.Replace,
            1 => OnNewUrlBehavior.Ignore,
            2 => OnNewUrlBehavior.Append,
            _ => OnNewUrlBehavior.Append,
        };
        s.PlaybackSyncingEnabled = PlaybackSyncingCheck.IsChecked == true;
        s.LivePlaybackSyncingEnabled = LivePlaybackSyncingCheck.IsChecked == true;
        s.SkipRestrictedContent = SkipRestrictedCheck.IsChecked == true;
        s.AlwaysUseConfiguredRoutes = AlwaysUseRoutesCheck.IsChecked == true;

        s.RestoreVolume = RestoreVolumeCheck.IsChecked == true;

        if (LogLevelCombo.SelectedItem is ComboBoxItem item && item.Content is string level)
            s.LogLevel = level;

        SettingsProvider.Instance.Save();
    }

    private void OnManageFoldersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new ManageFoldersDialog();
        dialog.ShowDialog(this);
    }
}
