using Avalonia.Controls;
using Avalonia.Interactivity;
using Shoko.Companion.Configuration;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog for adding or editing a single <see cref="ConnectionRoute"/>.
/// Shows a BaseUrl text field + UseHttps checkbox + live preview of the full URL.
/// </summary>
public partial class RouteEditDialog : Window
{
    private readonly ConnectionRoute? _original;

    /// <summary>
    /// The resulting route, or null if the user cancelled.
    /// </summary>
    public ConnectionRoute? Result { get; private set; }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// </summary>
    public RouteEditDialog()
    {
        InitializeComponent();
        _original = null;
    }

    /// <summary>
    /// Opens the dialog for the given route (null = add mode, non-null = edit mode).
    /// </summary>
    public RouteEditDialog(ConnectionRoute? existing)
    {
        InitializeComponent();
        _original = existing;

        if (existing is not null)
        {
            BaseUrlBox.Text = existing.BaseUrl;
            UseHttpsCheck.IsChecked = existing.UseHttps;
            Title = "Edit Route";
        }
        else
        {
            Title = "Add Route";
        }

        BaseUrlBox.TextChanged += (_, _) => UpdatePreview();
        UseHttpsCheck.IsCheckedChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var baseUrl = BaseUrlBox.Text?.Trim();
        var https = UseHttpsCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            PreviewText.Text = $"Full URL: {(https ? "https" : "http")}://{baseUrl.TrimEnd('/')}";
        else
            PreviewText.Text = string.Empty;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            PreviewText.Text = "Server address is required.";
            return;
        }

        Result = new ConnectionRoute
        {
            BaseUrl = baseUrl,
            UseHttps = UseHttpsCheck.IsChecked == true
        };

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
