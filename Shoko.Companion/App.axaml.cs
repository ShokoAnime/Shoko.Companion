using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Launch;
using Shoko.Companion.Notifications;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Shoko.Companion.Windows;

namespace Shoko.Companion;

/// <summary>
/// The Avalonia application entry-point. Manages the tray icon, settings loading,
/// single-instance URL forwarding, and the playback coordinator lifecycle.
/// </summary>
public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private TrayIcon? _trayIcon;
    private IPlaybackCoordinator? _coordinator;
    private NativeMenuItem? _privacyMenuItem;
    private NativeMenuItem? _openWebUiMenuItem;
    /// <summary>
    /// Gets the current Media Session client, or null if not connected.
    /// </summary>
    public MediaSessionClient? MediaSessionClient { get; private set; }

    /// <summary>
    /// Gets the playback coordinator, or null if not yet initialized.
    /// </summary>
    public IPlaybackCoordinator? Coordinator => _coordinator;

    /// <summary>
    /// Loads the Avalonia XAML for the application.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called once the Avalonia framework is ready. Initialises the tray icon,
    /// settings, playback coordinator, handles pending URLs, and shows the
    /// first-run configuration window if needed.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Console.CancelKeyPress += OnConsoleOnCancelKeyPress;

        // Load persisted settings
        SettingsProvider.Instance.Load();

        InitialiseTrayIcon();

        // Start the playback coordinator (wires up all services)
        _coordinator = new PlaybackCoordinator();
        _coordinator.StateChanged += OnPlaybackStateChanged;
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.PositionTick += OnCoordinatorPositionTick;
        _coordinator.VolumeStateChanged += OnCoordinatorVolumeStateChanged;

        // Auto-connect Media Session if configured and enabled
        var sessionSettings = SettingsProvider.Instance.Settings;
        if (sessionSettings.MediaSessionEnabled
            && sessionSettings.MediaSessionAutoConnectId is { } autoConnectId
            && autoConnectId != Guid.Empty)
        {
            var autoConn = SettingsProvider.Instance.Settings.Connections
                .FirstOrDefault(c => c.Id == autoConnectId && c.ApiKey is { Length: > 0 });
            if (autoConn is not null)
            {
                var reachableUrl = autoConn.ProbeReachableBaseUrl();
                if (reachableUrl is not null)
                {
                    // Start the connection in the background
                    var capturedUrl = reachableUrl;
                    var capturedKey = autoConn.ApiKey!;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000); // Let the app settle
                        await ConnectMediaSessionAsync(capturedUrl, capturedKey);
                    });
                }
            }
        }

        // If we were launched with a URL (from CLI or single-instance forwarding),
        // dispatch it through the same action router as post-startup URLs.
        var initialUrl = SingleInstanceManager.ConsumePendingUrl();
        if (initialUrl is not null)
        {
            DispatchUrl(initialUrl);
            UpdateOpenWebUiState();
        }

        // Also handle URLs forwarded after startup (e.g. user clicks a shoko:// link
        // while the companion is already running).
        SingleInstanceManager.UrlReceived += url =>
        {
            // ListenForUrls runs on a background thread — dispatch to UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                DispatchUrl(url);
                UpdateOpenWebUiState();
            });
        };

        // Show startup modals sequentially: URL scheme prompt first,
        // then connection setup if needed.
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(500); // Let the tray settle

            // 1. URL scheme registration (shown once)
            if (!SettingsProvider.Instance.Settings.UrlSchemeRegistrationAsked)
            {
                if (await ShowUrlSchemeRegistrationPromptAsync())
                {
                    UrlSchemeRegistrar.Register();
                    PlatformNotificationService.Instance
                        .Show("Shoko Companion", "Registered the shoko:// URL scheme.");
                }
                SettingsProvider.Instance.Settings.UrlSchemeRegistrationAsked = true;
                SettingsProvider.Instance.Save();
            }

            // 2. Connection setup (shown when no connections exist)
            if (!SettingsProvider.Instance.Settings.HasConnections)
            {
                ShowSettingsWindow();
            }
        });

        base.OnFrameworkInitializationCompleted();
    }

    private void InitialiseTrayIcon()
    {
        var menu = new NativeMenu();

        var openWebUi = new NativeMenuItem("Open WebUI");
        openWebUi.Click += OnTrayOpenWebUiClick;
        _openWebUiMenuItem = openWebUi;
        UpdateOpenWebUiState();
        menu.Add(openWebUi);

        menu.Add(new NativeMenuItemSeparator());

        var settingsItem = new NativeMenuItem("Open Settings");
        settingsItem.Click += OnTraySettingsClick;
        menu.Add(settingsItem);

        var manageFolders = new NativeMenuItem("Manage Folders");
        manageFolders.Click += OnTrayManageFoldersClick;
        manageFolders.IsEnabled = SettingsProvider.Instance.Settings.HasConnections;
        menu.Add(manageFolders);

        menu.Add(new NativeMenuItemSeparator());

        _privacyMenuItem = new NativeMenuItem(
            SettingsProvider.Instance.Settings.PrivacyMode
                ? "Disable Privacy Mode"
                : "Enable Privacy Mode");
        _privacyMenuItem.Click += OnTrayTogglePrivacyClick;
        menu.Add(_privacyMenuItem);

        menu.Add(new NativeMenuItemSeparator());

        var about = new NativeMenuItem("About Shoko Companion");
        about.Click += OnTrayAboutClick;
        menu.Add(about);

        menu.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("Exit");
        exit.Click += OnTrayExit;
        menu.Add(exit);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Shoko Companion",
            Menu = menu,
            IsVisible = true
        };

        try
        {
            var uri = new Uri("avares://shoko-companion/icon.ico");
            using var assetStream = AssetLoader.Open(uri);
            _trayIcon.Icon = new WindowIcon(assetStream);
        }
        catch (Exception)
        {
            // Icon loading may fail on some platforms; continue without icon
        }
    }

    private void UpdateOpenWebUiState()
    {
        if (_openWebUiMenuItem is not null)
            _openWebUiMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(SettingsProvider.Instance.Settings.LastUsedServerUrl);
    }

    private void OnTrayOpenWebUiClick(object? sender, EventArgs args)
    {
        var baseUrl = SettingsProvider.Instance.Settings.LastUsedServerUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        try
        {
            OpenUrl(baseUrl);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to open web UI at {BaseUrl}", baseUrl);
        }
    }

    private void OnTrayTogglePrivacyClick(object? sender, EventArgs args)
    {
        var settings = SettingsProvider.Instance.Settings;
        settings.PrivacyMode = !settings.PrivacyMode;
        SettingsProvider.Instance.Save();

        if (_privacyMenuItem is not null)
            _privacyMenuItem.Header = settings.PrivacyMode
                ? "Disable Privacy Mode"
                : "Enable Privacy Mode";
    }

    private void OnTraySettingsClick(object? sender, EventArgs args)
    {
        ShowSettingsWindow();
    }

    private void OnTrayRegisterSchemeClick(object? sender, EventArgs args)
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

    private static void OnTrayUnregisterSchemeClick(object? sender, EventArgs args)
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

    private static void OnTrayAboutClick(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var dialog = new AboutDialog();
            dialog.Show();
        });
    }

    private static void OnTrayManageFoldersClick(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var dialog = new ManageFoldersDialog();
            dialog.Show();
        });
    }

    /// <summary>
    /// Determine how to handle the incoming URL.
    /// </summary>
    private async void DispatchUrl(string url)
    {
        switch (ShokoUrlParser.Parse(url))
        {
            case { IsPlayAction: true } when _coordinator is not null:
                await _coordinator.PlayAsync(url);
                break;

            case { IsOpenFolderAbsoluteAction: true } parsed:
            {
                var result = await FolderActionHandler.Instance.ResolveAbsolutePathAsync(parsed);
                if (result is { Opened: true } or { IsPermanentlyIgnored: true } or { FolderNotFound: true })
                    break;

                if (result.NeedsMapping)
                {
                    var dialog = new MissingMappingDialog(
                        result.ManagedFolderId!.Value,
                        serverPath: result.ServerPath,
                        baseUrl: parsed.ServerBaseUrl,
                        managedFolderName: result.ManagedFolderName);
                    dialog.Show();
                    dialog.Closed += async (_, _) =>
                    {
                        if (dialog.MappingSaved)
                            await FolderActionHandler.Instance.ResolveAbsolutePathAsync(parsed);
                    };
                }
                break;
            }

            case { IsOpenFolderAction: true } parsed:
            {
                if (FolderActionHandler.Instance.OpenFolder(parsed))
                    break;

                var folderInfo = await FolderActionHandler.Instance.ResolveManagedFolderInfoAsync(
                    parsed.ServerBaseUrl, parsed.ManagedFolderId.Value);
                var missingDialog = new MissingMappingDialog(
                    parsed.ManagedFolderId.Value,
                    serverPath: folderInfo?.Path,
                    baseUrl: parsed.ServerBaseUrl,
                    managedFolderName: folderInfo?.Name);
                missingDialog.Show();
                missingDialog.Closed += (_, _) =>
                {
                    if (missingDialog.MappingSaved)
                        FolderActionHandler.Instance.OpenFolder(parsed);
                };
                break;
            }
        }
    }

    private MainWindow? _settingsWindow;

    /// <summary>
    /// Shows the settings window. Only one instance can be open at a time —
    /// subsequent calls activate the existing window.
    /// </summary>
    public void ShowSettingsWindow()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new MainWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>
    /// Show a dialog asking the user if they want to register the URL scheme.
    /// </summary>
    private static Task<bool> ShowUrlSchemeRegistrationPromptAsync()
    {
        var tcs = new TaskCompletionSource<bool>();

        var window = new Window
        {
            Title = "Shoko Companion",
            Width = 420,
            Height = 170,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Register URL Scheme?",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = "Would you like to register the shoko:// URL scheme so " +
                               "clicking a Shoko link in your browser opens this app?",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button
                            {
                                Content = "Not Now",
                                Width = 100,
                                Tag = tcs,
                            },
                            new Button
                            {
                                Content = "Register",
                                Width = 100,
                                IsDefault = true,
                                Tag = tcs,
                            },
                        }
                    }
                }
            }
        };

        // Wire up buttons after window is created so we can capture the close action
        var stack = (StackPanel)((StackPanel)window.Content).Children[2];
        var noButton = (Button)stack.Children[0];
        var yesButton = (Button)stack.Children[1];

        noButton.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            window.Close();
        };
        yesButton.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            window.Close();
        };

        window.Show();
        return tcs.Task;
    }

    private void OnTrayExit(object? sender, EventArgs args)
        => DispatchShutdown();

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs args)
    {
        if (_trayIcon is null) return;

        var stateText = args.NewState switch
        {
            PlaybackState.Idle => "Shoko Companion",
            PlaybackState.Loading => "Loading...",
            PlaybackState.Playing => $"Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Stopped => "Shoko Companion",
            PlaybackState.Error => "Error",
            _ => "Shoko Companion"
        };

        Dispatcher.UIThread.Invoke(() =>
        {
            _trayIcon.ToolTipText = stateText;
        });
    }

    private void OnCoordinatorStateChanged(object? sender, PlaybackStateChangedEventArgs args)
    {
        if (MediaSessionClient is not { IsConnected: true } || _coordinator is null)
            return;

        // Update live capabilities based on playback state
        MediaSessionClient.HasActivePlayback = args.NewState
            is PlaybackState.Playing or PlaybackState.Paused;

        var state = args.NewState switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Idle => "Idle",
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Loading => "Loading",
            PlaybackState.Error => "Error",
            _ => "Idle",
        };

        var sSettings = SettingsProvider.Instance.Settings;
        var hideInfo = sSettings.EffectivePrivacyMode && sSettings.PrivacyModeHideMediaPlaybackInfo;

        _ = MediaSessionClient.ReportStateAsync(new PlaybackStateUpdateDto
        {
            State = state,
            VideoId = hideInfo ? null : _coordinator.CurrentFileId,
            Title = hideInfo ? null : _coordinator.CurrentTitle,
            Position = TimeSpan.FromSeconds(_coordinator.CurrentPositionSeconds),
            Duration = _coordinator.DurationSeconds.HasValue
                ? TimeSpan.FromSeconds(_coordinator.DurationSeconds.Value)
                : null,
            StreamUrl = hideInfo ? null : _coordinator.CurrentStreamUrl,
            IsPaused = args.NewState == PlaybackState.Paused,
            Volume = _coordinator.CurrentVolume,
            IsMuted = _coordinator.CurrentMuted,
        });
    }

    private void OnCoordinatorPositionTick(object? sender, TimeSpan position)
    {
        if (MediaSessionClient is not { IsConnected: true } || _coordinator is null)
            return;

        var state = _coordinator.CurrentState switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Idle => "Idle",
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Loading => "Loading",
            PlaybackState.Error => "Error",
            _ => "Idle",
        };

        var settings = SettingsProvider.Instance.Settings;
        var hideInfo = settings.EffectivePrivacyMode && settings.PrivacyModeHideMediaPlaybackInfo;

        _ = MediaSessionClient.ReportStateAsync(new PlaybackStateUpdateDto
        {
            State = state,
            VideoId = hideInfo ? null : _coordinator.CurrentFileId,
            Title = hideInfo ? null : _coordinator.CurrentTitle,
            Position = position,
            Duration = _coordinator.DurationSeconds.HasValue ? TimeSpan.FromSeconds(_coordinator.DurationSeconds.Value) : null,
            StreamUrl = hideInfo ? null : _coordinator.CurrentStreamUrl,
            IsPaused = state is "Paused",
            Volume = _coordinator.CurrentVolume,
            IsMuted = _coordinator.CurrentMuted,
        });
    }

    private void OnCoordinatorVolumeStateChanged(object? sender, EventArgs args)
    {
        if (MediaSessionClient is not { IsConnected: true } || _coordinator is null)
            return;

        var state = _coordinator.CurrentState switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Idle => "Idle",
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Loading => "Loading",
            PlaybackState.Error => "Error",
            _ => "Idle",
        };

        var settings = SettingsProvider.Instance.Settings;
        var hideInfo = settings.EffectivePrivacyMode && settings.PrivacyModeHideMediaPlaybackInfo;

        _ = MediaSessionClient.ReportStateAsync(new PlaybackStateUpdateDto
        {
            State = state,
            VideoId = hideInfo ? null : _coordinator.CurrentFileId,
            Title = hideInfo ? null : _coordinator.CurrentTitle,
            Position = TimeSpan.FromSeconds(_coordinator.CurrentPositionSeconds),
            Duration = _coordinator.DurationSeconds.HasValue ? TimeSpan.FromSeconds(_coordinator.DurationSeconds.Value) : null,
            StreamUrl = hideInfo ? null : _coordinator.CurrentStreamUrl,
            IsPaused = state is "Paused",
            Volume = _coordinator.CurrentVolume,
            IsMuted = _coordinator.CurrentMuted,
        });
    }

    /// <summary>
    /// Build a state DTO from the coordinator's current playback state,
    /// or null if the coordinator isn't active.
    /// </summary>
    private PlaybackStateUpdateDto? BuildStateFromCoordinator()
    {
        if (_coordinator is null)
            return null;

        var state = _coordinator.CurrentState switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Idle => "Idle",
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Loading => "Loading",
            PlaybackState.Error => "Error",
            _ => "Idle",
        };
        var settings = SettingsProvider.Instance.Settings;
        var hideInfo = settings.EffectivePrivacyMode && settings.PrivacyModeHideMediaPlaybackInfo;

        return new PlaybackStateUpdateDto
        {
            State = state,
            VideoId = hideInfo ? null : _coordinator.CurrentFileId,
            Title = hideInfo ? null : _coordinator.CurrentTitle,
            Position = TimeSpan.FromSeconds(_coordinator.CurrentPositionSeconds),
            Duration = _coordinator.DurationSeconds.HasValue
                ? TimeSpan.FromSeconds(_coordinator.DurationSeconds.Value)
                : null,
            StreamUrl = hideInfo ? null : _coordinator.CurrentStreamUrl,
            IsPaused = _coordinator.CurrentState is PlaybackState.Paused,
            Volume = _coordinator.CurrentVolume,
            IsMuted = _coordinator.CurrentMuted,
        };
    }

    /// <summary>
    /// Connect to the Media Session hub for the given server.
    /// </summary>
    public async Task ConnectMediaSessionAsync(string baseUrl, string apiKey)
    {
        // Capture playback state before disposing the old client,
        // so the new client can re-register with the correct state.
        var initialState = BuildStateFromCoordinator();

        if (MediaSessionClient is not null)
            await DisconnectMediaSessionAsync();

        MediaSessionClient = new MediaSessionClient(baseUrl, apiKey, DeviceInfo.DeviceName, _coordinator!, initialState);

        var available = await MediaSessionClient.IsPluginAvailableAsync(baseUrl, apiKey);
        if (available)
        {
            Logger.Info("Media Session plugin available, connecting to {Url}", baseUrl);
            await MediaSessionClient.ConnectAsync();
        }
        else
        {
            Logger.Warn("Media Session plugin not available at {Url}", baseUrl);
        }
    }

    /// <summary>
    /// Disconnect from the Media Session hub.
    /// </summary>
    public async Task DisconnectMediaSessionAsync()
    {
        if (MediaSessionClient is not null)
        {
            await MediaSessionClient.DisposeAsync();
            MediaSessionClient = null;
        }
    }

    private void OnConsoleOnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        DispatchShutdown();
    }

    private async void DispatchShutdown()
    {
        if (MediaSessionClient is not null)
            await MediaSessionClient.DisposeAsync();

        if (_coordinator is not null)
            await _coordinator.StopAsync();

        SingleInstanceManager.Release();

        Dispatcher.UIThread.Invoke(() =>
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;
            _trayIcon?.IsVisible = false;
            desktop.Shutdown(0);
        });
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
