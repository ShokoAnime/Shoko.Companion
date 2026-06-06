using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Shoko.Companion.Configuration;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Windows;

/// <summary>
/// Dialog for adding or editing a managed folder mapping.
/// Lets the user pick a server folder from cached data and enter a local path.
/// When adding, only shows folders that don't already have a mapping.
/// When editing, shows all folders and pre-selects the existing one.
/// </summary>
public partial class MappingEditDialog : Window
{
    private readonly ManagedFolderMapping? _existing;

    /// <summary>
    /// The resulting mapping, or null if cancelled.
    /// </summary>
    public ManagedFolderMapping? Result { get; private set; }

    /// <summary>
    /// Parameterless constructor required by the Avalonia XAML loader.
    /// Use <see cref="MappingEditDialog(ServerConnection, ManagedFolderMapping?)"/> in code.
    /// </summary>
    public MappingEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the dialog with server folders from <paramref name="connection"/>.
    /// </summary>
    /// <param name="connection">The connection whose cached folders to show.</param>
    /// <param name="existing">Existing mapping to edit, or null for a new one.</param>
    public MappingEditDialog(ServerConnection connection, ManagedFolderMapping? existing = null)
    {
        InitializeComponent();
        _existing = existing;

        var allFolders = connection.CachedManagedFolders;
        List<ManagedFolderDto> folders;

        if (allFolders is null || allFolders.Count == 0)
        {
            folders = [];
        }
        else if (existing is not null)
        {
            // Editing: show all folders to allow changing the mapped folder
            folders = allFolders;
        }
        else
        {
            // Adding: show only unmapped folders
            var mappedIds = connection.ManagedFolderMappings.Select(m => m.Id).ToHashSet();
            folders = allFolders.Where(f => !mappedIds.Contains(f.ID)).ToList();
        }

        FolderCombo.ItemsSource = folders;
        FolderCombo.ItemTemplate = new FuncDataTemplate<ManagedFolderDto>((f, _) =>
            new TextBlock
            {
                Text = $"{f?.Name ?? "?"}  ({f?.Path ?? "?"})",
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        if (existing is not null)
        {
            var idx = folders.FindIndex(f => f.ID == existing.Id);
            if (idx >= 0)
                FolderCombo.SelectedIndex = idx;
            LocalPathBox.Text = existing.LocalPath;
        }
        else if (folders.Count > 0)
        {
            FolderCombo.SelectedIndex = 0;
        }

        SaveButton.IsEnabled = folders.Count > 0;
        Title = existing is null ? "Add Mapping" : "Edit Mapping";
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var selected = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _existing is not null
                ? "Select local folder for this mapping"
                : "Select local folder",
            AllowMultiple = false
        });

        if (selected.Count == 1)
            LocalPathBox.Text = selected[0].Path.LocalPath;
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = FolderCombo.SelectedItem as ManagedFolderDto;
        var localPath = LocalPathBox.Text?.Trim();

        if (selected is null || string.IsNullOrWhiteSpace(localPath))
            return;

        Result = new ManagedFolderMapping
        {
            Id = selected.ID,
            Name = selected.Name ?? string.Empty,
            ServerPath = selected.Path ?? string.Empty,
            LocalPath = localPath
        };

        Close();
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
