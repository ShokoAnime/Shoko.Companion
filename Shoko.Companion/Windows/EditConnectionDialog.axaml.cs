using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog for editing an existing connection's name, routes, and API key.
/// </summary>
public partial class EditConnectionDialog : Window
{
    private readonly ServerConnection _connection;
    private readonly ObservableCollection<ConnectionRoute> _routes = new();

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// Use <see cref="EditConnectionDialog(ServerConnection)"/> in code.
    /// </summary>
    public EditConnectionDialog()
    {
        InitializeComponent();
        _connection = null!;
    }

    /// <summary>
    /// Initializes the dialog with the given connection's values.
    /// </summary>
    public EditConnectionDialog(ServerConnection connection)
    {
        InitializeComponent();
        _connection = connection;

        NameBox.Text = connection.Name;

        foreach (var route in connection.Routes)
            _routes.Add(new ConnectionRoute { BaseUrl = route.BaseUrl, UseHttps = route.UseHttps });

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
        var firstRoute = _routes.FirstOrDefault();
        var baseUrl = firstRoute?.FullUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            StatusText.Text = "Add at least one route first.";
            StatusText.Foreground = Avalonia.Media.Brushes.Red;
            return;
        }

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

            var result = await ShokoAuth.LoginAsync(baseUrl, user, pass);

            if (result.HasApiKey)
            {
                _connection.ApiKey = result.ApiKey;
                _connection.Name = NameBox.Text?.Trim() ?? _connection.Name;
                _connection.Routes = _routes.ToList();
                SettingsProvider.Instance.Save();
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

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _connection.Name = NameBox.Text?.Trim() ?? _connection.Name;
        _connection.Routes = _routes.ToList();

        SettingsProvider.Instance.Save();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
