namespace Shoko.Companion.Notifications;

/// <summary>
/// Defines the severity levels for desktop notifications.
/// </summary>
public enum NotificationSeverity
{
    /// <summary>
    /// Informational notification (default). Displayed with an information icon.
    /// </summary>
    Info,

    /// <summary>
    /// Warning notification. Displayed with a warning icon and normal urgency.
    /// </summary>
    Warning,

    /// <summary>
    /// Error notification. Displayed with an error icon and critical urgency.
    /// </summary>
    Error
}
