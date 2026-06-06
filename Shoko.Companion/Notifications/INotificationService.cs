namespace Shoko.Companion.Notifications;

/// <summary>
/// Service interface for showing desktop notifications to the user.
/// Implementations use platform-native notification mechanisms.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Shows a desktop notification with the specified title, message, and severity.
    /// </summary>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification body text.</param>
    /// <param name="severity">The severity level of the notification (default <see cref="NotificationSeverity.Info" />).</param>
    void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info);
}
