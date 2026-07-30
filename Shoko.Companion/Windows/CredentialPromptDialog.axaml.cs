using Avalonia.Controls;
using Avalonia.Interactivity;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Modal dialog that prompts for username/password to authenticate
/// with a Shoko server and obtain an API key.
/// </summary>
public partial class CredentialPromptDialog : Window
{
    private readonly string _baseUrl;

    /// <summary>
    /// The resolved API key, or null if the user cancelled.
    /// </summary>
    public string? ApiKey { get; private set; }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// </summary>
    public CredentialPromptDialog()
    {
        InitializeComponent();
        _baseUrl = null!;
    }

    /// <summary>
    /// Initializes the dialog for the given server.
    /// </summary>
    /// <param name="baseUrl">Full base URL including protocol (e.g. http://myserver:8111/subpath).</param>
    /// <param name="host">Host display name for the title (e.g. myserver:8111).</param>
    public CredentialPromptDialog(string baseUrl, string host)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        TitleText.Text = $"Authentication required for {host}";
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text?.Trim();
        var pass = PasswordBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(user))
        {
            StatusText.Text = "Enter a username first.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

        try
        {
            StatusText.Text = "Logging in…";
            StatusText.Foreground = Avalonia.Media.Brushes.Gray;
            LoginButton.IsEnabled = false;

            var result = await ShokoAuth.LoginAsync(_baseUrl, user, pass);

            if (result.HasApiKey)
            {
                ApiKey = result.ApiKey;
                Close();
            }
            else if (result.ResponseSucceeded)
            {
                StatusText.Text = "Login succeeded but no API key returned.";
                StatusText.Foreground = Avalonia.Media.Brushes.Orange;
            }
            else if (result.StatusCode is { } code)
            {
                StatusText.Text = $"Login failed ({code}). Check credentials.";
                StatusText.Foreground = Avalonia.Media.Brushes.Red;
            }
            else
            {
                StatusText.Text = $"Error: {result.Error}";
                StatusText.Foreground = Avalonia.Media.Brushes.Red;
            }
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ApiKey = null;
        Close();
    }
}
