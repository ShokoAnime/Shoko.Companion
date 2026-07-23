using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Singleton that loads, saves, and monitors the settings.json file for changes.
/// </summary>
public class SettingsProvider
{
    /// <summary>
    /// Singleton instance of the settings provider.
    /// </summary>
    public static SettingsProvider Instance { get; } = new();

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        DefaultValueHandling = DefaultValueHandling.Include
    };

    /// <summary>
    /// The current application settings.
    /// </summary>
    public CompanionSettings Settings { get; private set; } = new();

    /// <summary>
    /// True if no settings file existed at load time (first launch).
    /// </summary>
    public bool IsFirstRun { get; private set; }
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Raised when settings are loaded or changed (including from the file watcher).
    /// </summary>
    public event Action<CompanionSettings>? SettingsChanged;

    /// <summary>
    /// Loads settings from disk. If the file does not exist, creates default settings and saves them.
    /// Starts watching the file for external changes.
    /// </summary>
    public void Load()
    {
        var path = CompanionPaths.SettingsPath;

        if (!File.Exists(path))
        {
            IsFirstRun = true;
            Settings = new CompanionSettings();
            Save();
            return;
        }

        IsFirstRun = false;

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonConvert.DeserializeObject<CompanionSettings>(json, JsonSettings);
            if (loaded is not null)
            {
                Settings = loaded;

                // Validate connections: each must have at least one route
                Settings.Connections.RemoveAll(c => c.Routes is null || c.Routes.Count == 0);

                // Infer name from host:port portion of first route if not set
                foreach (var conn in Settings.Connections)
                {
                    if (string.IsNullOrWhiteSpace(conn.Name) && conn.Routes.Count > 0)
                        conn.Name = conn.Routes[0].BaseUrl.Split('/')[0];
                }

                // Assign IDs to connections that don't have one (upgrade from older settings)
                var needsSave = false;
                foreach (var conn in Settings.Connections)
                {
                    if (conn.Id == Guid.Empty)
                    {
                        conn.Id = Guid.NewGuid();
                        needsSave = true;
                    }
                }
                if (needsSave)
                    Save();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to load settings from {Path}", path);
            Settings = new CompanionSettings();
            Save();
        }

        StartWatching();
    }

    /// <summary>
    /// Persists the current settings to disk and fires <see cref="SettingsChanged"/>.
    /// </summary>
    public void Save()
    {
        var path = CompanionPaths.SettingsPath;
        if (string.IsNullOrWhiteSpace(path) || !CompanionPaths.IsInitialized)
            return;
        var json = JsonConvert.SerializeObject(Settings, JsonSettings);
        File.WriteAllText(path, json);
        // Fire change event so services can react
        SettingsChanged?.Invoke(Settings);
    }

    private void StartWatching()
    {
        _watcher?.Dispose();
        var dir = Path.GetDirectoryName(CompanionPaths.SettingsPath);
        if (dir is null) return;

        _watcher = new FileSystemWatcher(dir)
        {
            Filter = "settings.json",
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => Reload();
    }

    private async void Reload()
    {
        // Debounce: FileSystemWatcher fires twice on save; wait 200ms
        await Task.Delay(200);
        try
        {
            var json = File.ReadAllText(CompanionPaths.SettingsPath);
            var loaded = JsonConvert.DeserializeObject<CompanionSettings>(json, JsonSettings);
            if (loaded is not null)
            {
                Settings = loaded;
                SettingsChanged?.Invoke(Settings);
            }
        }
        catch (Exception ex)
        {
            // Ignore transient file locks during save
            Logger.Debug(ex, "Failed to reload settings after file change (will retry on next change)");
        }
    }
}
