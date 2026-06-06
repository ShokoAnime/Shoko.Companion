using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using NLog;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog for managing folder mappings for a selected server connection.
/// Each connection has its own set of managed folder mappings.
/// </summary>
public partial class ManageFoldersDialog : Window
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private ObservableCollection<ManagedFolderMapping>? _mappings;

    /// <summary>
    /// Initializes the dialog with available connections.
    /// </summary>
    public ManageFoldersDialog()
    {
        InitializeComponent();
        LoadConnections();
    }

    private void LoadConnections()
    {
        var settings = SettingsProvider.Instance.Settings;
        ConnectionCombo.ItemsSource = settings.Connections;

        if (settings.Connections.Count > 0)
            ConnectionCombo.SelectedIndex = 0;
    }

    private async void OnConnectionChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var conn = ConnectionCombo.SelectedItem as ServerConnection;
        if (conn is null)
        {
            MappingListBox.ItemsSource = null;
            return;
        }

        _mappings = new ObservableCollection<ManagedFolderMapping>(conn.ManagedFolderMappings);
        MappingListBox.ItemsSource = _mappings;

        if (conn.CachedManagedFolders is not { Count: > 0 })
            await FetchAndCacheFoldersAsync(conn);

        UpdateAddButtonState(conn);
    }

    private async Task FetchAndCacheFoldersAsync(ServerConnection conn)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var api = new ShokoApiClient(http);

            var reachable = conn.ProbeReachableBaseUrl();
            if (reachable is null) return;

            api.SetBaseUrl(reachable);
            if (!string.IsNullOrWhiteSpace(conn.ApiKey))
                api.SetApiKey(conn.ApiKey);

            var folders = await api.FetchManagedFoldersAsync();
            if (folders is not null)
            {
                conn.CachedManagedFolders = folders;
                SettingsProvider.Instance.Save();
                UpdateAddButtonState(conn);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to fetch and cache managed folders for connection '{Name}'", conn.Name);
        }
    }

    private void UpdateAddButtonState(ServerConnection conn)
    {
        var cached = conn.CachedManagedFolders;
        if (cached is null || cached.Count == 0)
        {
            AddButton.IsEnabled = false;
            return;
        }
        var mappedIds = conn.ManagedFolderMappings.Select(m => m.Id).ToHashSet();
        AddButton.IsEnabled = cached.Any(f => !mappedIds.Contains(f.ID));
    }

    private async void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var conn = ConnectionCombo.SelectedItem as ServerConnection;
        if (conn is null) return;

        RefreshButton.IsEnabled = false;

        try
        {
            await FetchAndCacheFoldersAsync(conn);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var conn = ConnectionCombo.SelectedItem as ServerConnection;
        if (conn is null) return;

        if (conn.CachedManagedFolders is not { Count: > 0 })
            await FetchAndCacheFoldersAsync(conn);

        // Sync in-memory state before passing to the dialog filter
        if (_mappings is not null)
            conn.ManagedFolderMappings = _mappings.ToList();

        var dialog = new MappingEditDialog(conn);
        await dialog.ShowDialog(this);
        if (dialog.Result is not null)
        {
            _mappings?.Add(dialog.Result);
            SaveCurrentMappings();
            UpdateAddButtonState(conn);
        }
    }

    private async void OnEditClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mappings is null) return;
        var selected = MappingListBox.SelectedItem as ManagedFolderMapping;
        if (selected is null) return;

        var conn = ConnectionCombo.SelectedItem as ServerConnection;
        if (conn is null) return;

        if (conn.CachedManagedFolders is not { Count: > 0 })
            await FetchAndCacheFoldersAsync(conn);

        var dialog = new MappingEditDialog(conn, selected);
        await dialog.ShowDialog(this);
        if (dialog.Result is not null)
        {
            var idx = _mappings.IndexOf(selected);
            _mappings[idx] = dialog.Result;
            SaveCurrentMappings();
            UpdateAddButtonState(conn);
        }
    }

    private void OnRemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mappings is null) return;
        var selected = MappingListBox.SelectedItem as ManagedFolderMapping;
        if (selected is not null)
        {
            _mappings.Remove(selected);
            SaveCurrentMappings();
            var conn = ConnectionCombo.SelectedItem as ServerConnection;
            if (conn is not null)
                UpdateAddButtonState(conn);
        }
    }

    private void SaveCurrentMappings()
    {
        if (_mappings is null) return;

        var list = _mappings.ToList();
        var selectedConn = ConnectionCombo.SelectedItem as ServerConnection;

        // Reload may have replaced Settings.Connections with a new list.
        // Update every matching connection so the live Settings is correct
        // before serializing.
        foreach (var conn in SettingsProvider.Instance.Settings.Connections)
        {
            if (conn.Name == selectedConn?.Name)
                conn.ManagedFolderMappings = list;
        }

        if (selectedConn is not null)
            selectedConn.ManagedFolderMappings = list;

        SettingsProvider.Instance.Save();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
