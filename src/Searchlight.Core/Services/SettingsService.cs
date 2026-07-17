using System;
using System.IO;
using System.Text.Json;
using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> as JSON under
/// <c>%LOCALAPPDATA%\Searchlight\settings.json</c>. The in-memory
/// <see cref="Current"/> instance is observable; any property change is saved
/// automatically. All I/O is best-effort — a missing or corrupt file simply
/// yields defaults, and save failures are swallowed (settings are non-critical).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    // Null path => in-memory only (no disk load, no disk save). Used by tests for
    // isolation so they never read or clobber the real %LOCALAPPDATA% settings.json.
    private readonly string? _path;

    /// <summary>The live settings instance. Bind UI directly to its properties.</summary>
    public AppSettings Current { get; }

    /// <summary>Loads settings from disk (or defaults) and wires auto-save.</summary>
    public SettingsService()
        : this(DefaultPath())
    {
    }

    /// <summary>
    /// Test/advanced constructor. A non-null <paramref name="path"/> loads from and
    /// saves to that file; a null path yields an isolated, in-memory-only instance
    /// (defaults, never touches disk).
    /// </summary>
    internal SettingsService(string? path)
    {
        _path = path;
        Current = _path is not null ? Load() : new AppSettings();
        // Auto-persist whenever any setting changes (e.g. the ToggleSwitch flips).
        Current.PropertyChanged += (_, _) => Save();
    }

    private static string DefaultPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Searchlight");
        return Path.Combine(dir, "settings.json");
    }

    private AppSettings Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, s_json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt/unreadable file → fall through to defaults.
        }

        return new AppSettings();
    }

    /// <summary>Writes the current settings to disk (best-effort).</summary>
    public void Save()
    {
        if (_path is null)
        {
            return; // In-memory (test) instance: never touch disk.
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, s_json));
        }
        catch (Exception)
        {
            // Non-critical: settings just won't persist this session.
        }
    }
}
