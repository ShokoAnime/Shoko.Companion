using System;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Shoko.Companion.Windows;

/// <summary>
/// About dialog showing the app name, version, repository link, and license.
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>
    /// Initializes the About dialog, loads the embedded app icon, and
    /// reads the assembly version.
    /// </summary>
    public AboutDialog()
    {
        InitializeComponent();
        LoadIcon();
        LoadVersion();
    }

    private void LoadIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("shoko-companion.icon.png");
            if (stream is not null)
                AppIcon.Source = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Icon loading is best-effort
        }
    }

    private void LoadVersion()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";

        VersionText.Text = $"v{version}";
    }

    private void OnRepoLinkClick(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/ShokoAnime/Shoko.Companion")
                { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
