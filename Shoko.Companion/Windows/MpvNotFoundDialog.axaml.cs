using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Shoko.Companion.Mpv;
using Shoko.Companion.Notifications;

namespace Shoko.Companion.Windows;

/// <summary>
/// Modal dialog shown when mpv cannot be found on the system.
/// Offers to browse for the executable or visit mpv.io for install help.
/// </summary>
public partial class MpvNotFoundDialog : Window
{
    /// <summary>
    /// The selected mpv path, or null if the user cancelled.
    /// </summary>
    public string? MpvPath
    {
        get
        {
            var path = MpvPathBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// </summary>
    public MpvNotFoundDialog()
    {
        InitializeComponent();
    }

    private void OnPathTextChanged(object? sender, TextChangedEventArgs e)
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(MpvPathBox.Text);
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        var path = await MpvProcess.FindMpvAsync();

        if (!string.IsNullOrWhiteSpace(path))
        {
            MpvPathBox.Text = path;
            PlatformNotificationService.Instance.Show("mpv Found",
                $"Located at {path}", NotificationSeverity.Info);
        }
        else
        {
            PlatformNotificationService.Instance.Show("mpv Not Found",
                "Could not find mpv. Try browsing manually or installing it.",
                NotificationSeverity.Warning);
        }

        RescanButton.IsEnabled = true;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select mpv executable",
            AllowMultiple = false,
        });

        if (files.Count == 1)
        {
            MpvPathBox.Text = files[0].Path.LocalPath;
            StatusText.Text = $"Selected: {MpvPathBox.Text}";
        }
    }

    private static void OnLearnMoreClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://mpv.io") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Ignore — the user can manually visit the site
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MpvPathBox.Text))
        {
            StatusText.Text = "Set the mpv path first.";
            return;
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        MpvPathBox.Text = string.Empty;
        Close();
    }
}
