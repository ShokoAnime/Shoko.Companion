using System;
using NLog;
#if WINDOWS
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif

#pragma warning disable CA1416
namespace Shoko.Companion.Notifications;

/// <summary>
/// Renders Windows toast notifications directly in-process via WinRT, bypassing
/// PowerShell. Caller is expected to have called <see cref="WindowsAppUserModelId.EnsureRegistered"/>
/// at least once during startup so <see cref="WindowsAppUserModelId.AppId"/> resolves
/// to an installed Start Menu shortcut.
///
/// The <c>Windows.UI.Notifications</c> projection is only referenced on Windows
/// builds (see the conditional <c>Microsoft.Windows.SDK.NET.Ref</c> package in the
/// project file); on other platforms <see cref="Show"/> throws
/// <see cref="PlatformNotSupportedException"/> and the caller falls back to msg.exe.
/// </summary>
internal static class WindowsToast
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Show a toast. Throws on failure — caller falls back to msg.exe.
    /// </summary>
    public static void Show(string title, string message)
    {
#if WINDOWS
        var xml = BuildXml(title, message);
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // ToastNotificationManager is a singleton — safe to call repeatedly.
        var notifier = ToastNotificationManager.CreateToastNotifier(WindowsAppUserModelId.AppId);
        var toast = new ToastNotification(doc);
        notifier.Show(toast);

        Logger.Info("Toast shown: {Title} | {Message}", title, message);
#else
        throw new PlatformNotSupportedException("Windows toasts are only supported on Windows");
#endif
    }

    private static string BuildXml(string title, string message)
        => $"<toast><visual><binding template=\"ToastGeneric\"><text>{EscapeXml(title)}</text><text>{EscapeXml(message)}</text></binding></visual></toast>";

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}