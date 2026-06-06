using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog shown when an open-folder URL references a managed folder ID
/// that has no local mapping configured. Lets the user pick a local path,
/// optionally ignore the ID, or permanently ignore future requests.
/// </summary>
public partial class MissingMappingDialog : Window
{
    private readonly int _managedId;
    private readonly string? _serverPath;
    private readonly string? _baseUrl;
    private readonly string? _managedFolderName;

    /// <summary>
    /// True if the user saved a new mapping (caller should retry opening).
    /// </summary>
    public bool MappingSaved { get; private set; }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// Use <see cref="MissingMappingDialog(int, string?, string?, string?)"/> in code.
    /// </summary>
    public MissingMappingDialog()
    {
        InitializeComponent();
        _managedId = 0;
        _serverPath = null;
        _baseUrl = null;
        _managedFolderName = null;
    }

    /// <summary>
    /// Initializes the dialog for the given managed folder ID.
    /// Looks up cached folder info from the connection's <see cref="ServerConnection.CachedManagedFolders"/>,
    /// or uses <paramref name="managedFolderName"/> if provided (for the absolute-path flow
    /// where folders have already been fetched).
    /// </summary>
    /// <param name="managedId">The managed folder ID from the URL.</param>
    /// <param name="serverPath">Optional server-side path for reference.</param>
    /// <param name="baseUrl">Full base URL from the parsed shoko URL (used to look up the connection by route).</param>
    /// <param name="managedFolderName">Explicit managed folder display name (overrides cache lookup).</param>
    public MissingMappingDialog(int managedId, string? serverPath = null,
        string? baseUrl = null, string? managedFolderName = null)
    {
        InitializeComponent();
        _managedId = managedId;
        _serverPath = serverPath;
        _baseUrl = baseUrl;
        _managedFolderName = managedFolderName;

        // Try to look up the folder name from cached server data
        string? folderName = null;
        string? folderPath = null;

        if (!string.IsNullOrWhiteSpace(managedFolderName))
        {
            folderName = managedFolderName;
            folderPath = serverPath;
        }

        var routeKey = RouteResolver.ExtractRouteKey(baseUrl);
        if (routeKey is not null && folderName is null)
        {
            var conn = SettingsProvider.Instance.Settings.GetConnectionByRouteKey(routeKey);
            var cached = conn?.CachedManagedFolders?.FirstOrDefault(f => f.ID == managedId);
            if (cached is not null)
            {
                folderName = cached.Name;
                folderPath = cached.Path;
            }
        }

        var displayName = folderName ?? $"Folder #{managedId}";
        InfoText.Text = $"\"{displayName}\" was requested but has no local mapping." +
                        (folderPath is not null
                            ? $"\nServer path: {folderPath}"
                            : "\nChoose a local folder to map it.");
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Select local folder for managed folder #{_managedId}",
            AllowMultiple = false
        });

        if (folders.Count == 1)
        {
            LocalPathBox.Text = folders[0].Path.LocalPath;
        }
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var localPath = LocalPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        // Save the new mapping on the connection
        var settings = SettingsProvider.Instance.Settings;
        var routeKey = RouteResolver.ExtractRouteKey(_baseUrl);
        var conn = routeKey is not null ? settings.GetConnectionByRouteKey(routeKey) : null;
        if (conn is not null)
        {
            // Use explicitly passed values, fall back to cache lookup
            var cached = conn.CachedManagedFolders?.FirstOrDefault(f => f.ID == _managedId);
            var resolvedPath = _serverPath ?? cached?.Path;
            var resolvedName = _managedFolderName ?? cached?.Name;

            var existing = conn.ManagedFolderMappings.Find(m => m.Id == _managedId);
            if (existing is not null)
            {
                existing.LocalPath = localPath;
                if (resolvedPath is not null)
                    existing.ServerPath = resolvedPath;
                if (resolvedName is not null)
                    existing.Name = resolvedName;
            }
            else
            {
                conn.ManagedFolderMappings.Add(new ManagedFolderMapping
                {
                    Id = _managedId,
                    Name = resolvedName ?? string.Empty,
                    ServerPath = resolvedPath ?? string.Empty,
                    LocalPath = localPath
                });
            }

            // Clear ignore if it was previously ignored
            conn.IgnoredManagedFolderIds.Remove(_managedId);
        }

        SettingsProvider.Instance.Save();
        MappingSaved = true;
        Close();
    }

    private void OnIgnoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = SettingsProvider.Instance.Settings;
        var routeKey = RouteResolver.ExtractRouteKey(_baseUrl);
        if (routeKey is not null)
        {
            var useHttps = _baseUrl?.StartsWith("https", StringComparison.OrdinalIgnoreCase) ?? false;
            var conn = settings.GetOrCreateConnectionByRouteKey(routeKey, useHttps);
            conn.IgnoredManagedFolderIds.Add(_managedId);
        }
        SettingsProvider.Instance.Save();
        MappingSaved = false;
        Close();
    }

}
