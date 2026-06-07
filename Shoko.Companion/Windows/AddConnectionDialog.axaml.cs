using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog for adding a new server connection.
/// Supports route configuration and username/password login to fetch an API key.
/// </summary>
public partial class AddConnectionDialog : Window
{
    private readonly ObservableCollection<ConnectionRoute> _routes = new()
    {
        new ConnectionRoute { BaseUrl = "localhost:8111" }
    };

    private string? _fetchedApiKey;

    /// <summary>
    /// The created connection, or null if the user cancelled.
    /// </summary>
    public ServerConnection? Connection { get; private set; }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// </summary>
    public AddConnectionDialog()
    {
        InitializeComponent();
        RouteListBox.ItemsSource = _routes;
        RouteListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var hasSel = RouteListBox.SelectedItem is not null;
        var index = RouteListBox.SelectedIndex;
        EditRouteButton.IsEnabled = hasSel;
        RemoveRouteButton.IsEnabled = hasSel && _routes.Count > 1;
        MoveUpButton.IsEnabled = hasSel && index > 0;
        MoveDownButton.IsEnabled = hasSel && index < _routes.Count - 1;
    }

    private async void OnAddRouteClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new RouteEditDialog(null);
        await dialog.ShowDialog(this);
        if (dialog.Result is not null)
            _routes.Add(dialog.Result);
    }

    private async void OnEditRouteClick(object? sender, RoutedEventArgs e)
    {
        var selected = RouteListBox.SelectedItem as ConnectionRoute;
        if (selected is null) return;

        var dialog = new RouteEditDialog(selected);
        await dialog.ShowDialog(this);
        if (dialog.Result is not null)
        {
            var idx = _routes.IndexOf(selected);
            _routes[idx] = dialog.Result;
        }
    }

    private void OnRemoveRouteClick(object? sender, RoutedEventArgs e)
    {
        var selected = RouteListBox.SelectedItem as ConnectionRoute;
        if (selected is not null && _routes.Count > 1)
            _routes.Remove(selected);
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        var index = RouteListBox.SelectedIndex;
        if (index <= 0) return;
        _routes.Move(index, index - 1);
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        var index = RouteListBox.SelectedIndex;
        if (index < 0 || index >= _routes.Count - 1) return;
        _routes.Move(index, index + 1);
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var user = UsernameBox.Text?.Trim();
        var pass = PasswordBox.Text?.Trim();
        var firstRoute = _routes.FirstOrDefault();

        var baseUrl = firstRoute?.FullUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            StatusText.Text = "Fill in connection name, username, and password first.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

        try
        {
            StatusText.Text = "Logging in…";
            StatusText.Foreground = Avalonia.Media.Brushes.Gray;
            LoginButton.IsEnabled = false;

            var result = await ShokoAuth.LoginAsync(baseUrl, user, pass);

            if (result.HasApiKey)
            {
                _fetchedApiKey = result.ApiKey;
                StatusText.Text = "Logged in! API key ready.";
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
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "Enter a connection name.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

        if (_routes.Count == 0)
        {
            StatusText.Text = "At least one route is required.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

        var conn = new ServerConnection
        {
            Name = name,
            Routes = _routes.ToList(),
            ApiKey = _fetchedApiKey
        };

        Connection = conn;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Connection = null;
        Close();
    }
}
