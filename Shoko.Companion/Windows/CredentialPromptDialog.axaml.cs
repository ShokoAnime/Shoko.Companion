using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Modal dialog that prompts for credentials (API key or username/password login)
/// for a given server. Used when a new URL arrives without an API key and no
/// stored connection is found, or when the server returns 401.
/// Does not include route editing — just credential entry.
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
        SaveButton.IsEnabled = false;
    }

    private void OnApiKeyTextChanged(object? sender, TextChangedEventArgs e)
    {
        var key = ApiKeyBox.Text?.Trim();
        SaveButton.IsEnabled = Guid.TryParse(key, out _);
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text?.Trim();
        var pass = PasswordBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            StatusText.Text = "Enter username and password first.";
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
                ApiKeyBox.Text = result.ApiKey;
                SaveButton.IsEnabled = Guid.TryParse(result.ApiKey, out _);
                StatusText.Text = "Logged in! API key populated.";
                StatusText.Foreground = Avalonia.Media.Brushes.Green;
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

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key) || !Guid.TryParse(key, out _))
        {
            StatusText.Text = "Enter a valid API key (GUID format).";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

        ApiKey = key;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ApiKey = null;
        Close();
    }
}
