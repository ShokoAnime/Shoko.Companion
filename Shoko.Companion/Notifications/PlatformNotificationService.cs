using System;
using System.Diagnostics;
using NLog;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Notifications;

/// <summary>
/// Cross-platform notification service using OS-native shell commands.
/// No third-party notification libraries — hand-rolled per-OS shell-out.
///
/// Platform detection is done at runtime via OperatingSystem.Is[Windows|Linux|MacOS]().
/// All launches are fire-and-forget; exceptions are caught and logged silently.
/// </summary>
public class PlatformNotificationService : INotificationService
{
    /// <summary>
    /// Shared singleton instance. The service is stateless (it shells out per call).
    /// </summary>
    public static INotificationService Instance { get; } = new PlatformNotificationService();

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Show a desktop notification using the current platform's native mechanism.
    /// </summary>
    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info)
    {
        Logger.Info("Showing notification: {Title} | {Message} | {Severity}", title, message, severity);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                ShowLinux(title, message, severity);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ShowMacOs(title, message, severity);
            }
            else if (OperatingSystem.IsWindows())
            {
                ShowWindows(title, message, severity);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to show notification: {Title}", title);
        }
    }

    // ── Linux ──────────────────────────────────────────────────────────

    private static void ShowLinux(string title, string message, NotificationSeverity severity)
    {
        var psi = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("Shoko Companion");

        // App icon: prefer the themed name when installed (via `register`), else the
        // extracted file path, else a stock theme icon matching the severity.
        var icon = AppIcon.LinuxThemeIconInstalled
            ? AppIcon.ThemedName
            : AppIcon.EnsureConfigIcon() ?? StockIconName(severity);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(icon);

        if (severity == NotificationSeverity.Error)
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("critical");
        }
        else if (severity == NotificationSeverity.Warning)
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("normal");
        }

        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(message);

        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Stock freedesktop theme icon names used when no app icon is available.
    /// </summary>
    private static string StockIconName(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => "dialog-error",
        NotificationSeverity.Warning => "dialog-warning",
        _ => "dialog-information"
    };

    // ── macOS ──────────────────────────────────────────────────────────

    private static void ShowMacOs(string title, string message, NotificationSeverity severity)
    {
        var escapedTitle = EscapeForAppleScript(title);
        var escapedMessage = EscapeForAppleScript(message);

        var script = $"display notification \"{escapedMessage}\" with title \"{escapedTitle}\" subtitle \"Shoko Companion\"";

        if (severity == NotificationSeverity.Error)
        {
            script += " sound name \"Basso\"";
        }

        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        Process.Start(psi)?.Dispose();
    }

    // ── Windows ────────────────────────────────────────────────────────

    private static void ShowWindows(string title, string message, NotificationSeverity severity)
    {
        // In-process WinRT toast first; fall back to msg.exe if it fails.
        try
        {
            WindowsAppUserModelId.EnsureRegistered();
            WindowsToast.Show(title, message);
        }
        catch (Exception ex)
        {
            // Last-resort fallback: a centered Win32 popup. Less ideal but never silent.
            Logger.Debug(ex, "Inline toast failed; falling back to msg.exe");
            ShowWindowsMsg(title, message);
        }
    }

    /// <summary>
    /// Fallback: send a popup dialog via the msg command.
    /// Works on local interactive Windows sessions.
    /// </summary>
    private static void ShowWindowsMsg(string title, string message)
    {
        var psi = new ProcessStartInfo("msg")
        {
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add($"{title}: {message}");

        Process.Start(psi)?.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Escape a string for embedding in an AppleScript string literal.
    /// </summary>
    private static string EscapeForAppleScript(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
