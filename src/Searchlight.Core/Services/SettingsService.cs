using System.Text.Json;
using System.Text.Json.Nodes;
using Searchlight.Diagnostics;
using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Shared settings with locked, atomic, field-level three-way merges. Each instance
/// is UI-thread-owned; independent instances/processes synchronize through disk.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly string? _path;
    private readonly SharedStatePaths? _paths;
    private AppSettings _observed = new();
    private long _revision;
    private Task<bool>? _reloadTask;

    public AppSettings Current { get; } = new();
    /// <summary>True while external values are applied; hosts must not restart on elevation changes then.</summary>
    public bool IsReloading { get; private set; }
    public string PersistenceNotice { get; private set; } = string.Empty;
    public event EventHandler? PersistenceFailed;

    public SettingsService() : this(SharedStatePaths.Default) { }

    internal SettingsService(SharedStatePaths paths)
    {
        _paths = paths;
        _path = paths.SettingsFile;
        Initialize();
    }

    // Null retains the isolated in-memory seam used by tests and demos.
    internal SettingsService(string? path)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        Initialize();
    }

    private void Initialize()
    {
        Reload();
        Current.PropertyChanged += (_, _) => { if (!IsReloading) Save(); };
    }

    /// <summary>Loads other instances' changes without discarding locally unsaved edits.</summary>
    public bool Reload()
    {
        _revision++;
        if (_path is null) return true;
        try
        {
            var disk = ReadSnapshot();
            var merged = Merge(_observed, Current, disk);
            _observed = Clone(disk);
            // Bound property callbacks can read notes or save another preference;
            // release the cross-process lock before notifying any UI observers.
            Apply(merged);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Report($"Could not reload shared settings at {_path}. Existing data was not replaced.", ex);
            return false;
        }
    }

    /// <summary>Reads off-thread, then merges and notifies on the calling UI context.</summary>
    public Task<bool> ReloadAsync()
    {
        if (_reloadTask is { IsCompleted: false }) return _reloadTask;
        return _reloadTask = ReloadFromDiskAsync();
    }

    private async Task<bool> ReloadFromDiskAsync()
    {
        if (_path is null) return true;
        while (true)
        {
            long revision = _revision;
            try
            {
                var disk = await Task.Run(ReadSnapshot);
                // ASSUMPTION: the UI owns Current. A save/reload during this read
                // invalidates the snapshot; retry rather than undoing newer edits.
                if (revision != _revision) continue;
                var merged = Merge(_observed, Current, disk);
                _observed = Clone(disk);
                _revision++;
                Apply(merged);
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                if (revision != _revision) continue;
                Report($"Could not reload shared settings at {_path}. Existing data was not replaced.", ex);
                return false;
            }
        }
    }

    private AppSettings ReadSnapshot()
    {
        _paths?.MigrateLegacy();
        using var gate = SharedStateIO.Lock(Path.GetDirectoryName(_path!)!);
        return Read().Settings;
    }

    /// <summary>Saves changed scalars and individual pin/name edits; returns false with a persistent notice on failure.</summary>
    public bool Save()
    {
        _revision++;
        if (_path is null) return true;
        try
        {
            _paths?.MigrateLegacy();
            AppSettings merged;
            using (SharedStateIO.Lock(Path.GetDirectoryName(_path)!))
            {
                var (document, disk) = Read();
                merged = Merge(_observed, Current, disk);
                JsonObject known = JsonSerializer.SerializeToNode(merged, s_json)!.AsObject();
                foreach (var field in known) document[field.Key] = field.Value?.DeepClone();
                document["SchemaVersion"] = 1;
                SharedStateIO.WriteAtomic(_path, document.ToJsonString(s_json));
                _observed = Clone(merged);
            }
            Apply(merged);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Report($"Could not save shared settings at {_path}. Your changes remain in this window; repair storage and refresh/retry.", ex);
            return false;
        }
    }

    private (JsonObject Document, AppSettings Settings) Read()
    {
        string? text = SharedStateIO.ReadOptional(_path!);
        if (text is null) return (new(), new());
        var document = JsonNode.Parse(text, documentOptions: new() { AllowDuplicateProperties = false }) as JsonObject
            ?? throw new JsonException("Settings must be a JSON object.");
        if (document.TryGetPropertyValue("SchemaVersion", out var version)
            && (version is not JsonValue value || !value.TryGetValue<int>(out int number) || number != 1))
            throw new JsonException("Unsupported settings schema. Use a compatible Searchlight version.");
        // Unknown properties survive every write. Known fields must retain their
        // types; corrupt or newer incompatible state is never replaced by defaults.
        AppSettings settings = document.Deserialize<AppSettings>(s_json)
            ?? throw new JsonException("Settings cannot be null.");
        if (settings.CustomResumeCommand is null)
            throw new JsonException("CustomResumeCommand cannot be null.");
        if (settings.PinnedSessionIds is null || settings.PinnedSessionIds.Any(string.IsNullOrWhiteSpace)
            || settings.CustomSessionNames is null || settings.CustomSessionNames.Any(p => p.Value is null))
            throw new JsonException("Invalid pin or custom-name data.");
        return (document, settings);
    }

    private static AppSettings Clone(AppSettings value) => new()
    {
        UseSharedTerminalWindow = value.UseSharedTerminalWindow,
        RunElevated = value.RunElevated,
        EnableMonitoring = value.EnableMonitoring,
        AppendYolo = value.AppendYolo,
        UseCustomResumeCommand = value.UseCustomResumeCommand,
        CustomResumeCommand = value.CustomResumeCommand,
        NotesPaneVisible = value.NotesPaneVisible,
        HideEmptySessions = value.HideEmptySessions,
        HideUnnamedSessions = value.HideUnnamedSessions,
        PinnedSessionIds = [.. value.PinnedSessionIds],
        CustomSessionNames = new(value.CustomSessionNames),
    };

    private static AppSettings Merge(AppSettings baseline, AppSettings local, AppSettings disk)
    {
        // ASSUMPTION: same-field settings edits use last-writer-wins; different
        // fields/IDs merge. Notes deliberately use stricter conflict detection.
        AppSettings result = Clone(disk);
        if (local.UseSharedTerminalWindow != baseline.UseSharedTerminalWindow) result.UseSharedTerminalWindow = local.UseSharedTerminalWindow;
        if (local.RunElevated != baseline.RunElevated) result.RunElevated = local.RunElevated;
        if (local.EnableMonitoring != baseline.EnableMonitoring) result.EnableMonitoring = local.EnableMonitoring;
        if (local.AppendYolo != baseline.AppendYolo) result.AppendYolo = local.AppendYolo;
        if (local.UseCustomResumeCommand != baseline.UseCustomResumeCommand) result.UseCustomResumeCommand = local.UseCustomResumeCommand;
        if (local.CustomResumeCommand != baseline.CustomResumeCommand) result.CustomResumeCommand = local.CustomResumeCommand;
        if (local.NotesPaneVisible != baseline.NotesPaneVisible) result.NotesPaneVisible = local.NotesPaneVisible;
        if (local.HideEmptySessions != baseline.HideEmptySessions) result.HideEmptySessions = local.HideEmptySessions;
        if (local.HideUnnamedSessions != baseline.HideUnnamedSessions) result.HideUnnamedSessions = local.HideUnnamedSessions;
        var removed = baseline.PinnedSessionIds.Except(local.PinnedSessionIds).ToHashSet();
        var added = local.PinnedSessionIds.Except(baseline.PinnedSessionIds).ToArray();
        result.PinnedSessionIds = [.. added, .. disk.PinnedSessionIds.Where(id => !removed.Contains(id) && !added.Contains(id))];
        foreach (string id in baseline.CustomSessionNames.Keys.Except(local.CustomSessionNames.Keys))
            result.CustomSessionNames.Remove(id);
        foreach (var (id, name) in local.CustomSessionNames)
            if (!baseline.CustomSessionNames.TryGetValue(id, out string? old) || old != name)
                result.CustomSessionNames[id] = name;
        return result;
    }

    private void Apply(AppSettings value)
    {
        IsReloading = true;
        try
        {
            // ASSUMPTION: monitoring observers must see an external opt-out before
            // any other preference callback can emit diagnostics during this reload.
            Current.EnableMonitoring = value.EnableMonitoring;
            Current.UseSharedTerminalWindow = value.UseSharedTerminalWindow;
            Current.RunElevated = value.RunElevated;
            Current.AppendYolo = value.AppendYolo;
            Current.UseCustomResumeCommand = value.UseCustomResumeCommand;
            Current.CustomResumeCommand = value.CustomResumeCommand;
            Current.NotesPaneVisible = value.NotesPaneVisible;
            Current.HideEmptySessions = value.HideEmptySessions;
            Current.HideUnnamedSessions = value.HideUnnamedSessions;
            if (!Current.PinnedSessionIds.SequenceEqual(value.PinnedSessionIds)) Current.PinnedSessionIds = value.PinnedSessionIds;
            if (Current.CustomSessionNames.Count != value.CustomSessionNames.Count
                || Current.CustomSessionNames.Any(p => !value.CustomSessionNames.TryGetValue(p.Key, out var name) || name != p.Value))
                Current.CustomSessionNames = value.CustomSessionNames;
        }
        finally { IsReloading = false; }
    }

    internal static bool IsStorageError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException
            or System.Text.DecoderFallbackException or System.Text.EncoderFallbackException;

    private void Report(string message, Exception ex)
    {
        PersistenceNotice = $"{message} {ex.Message}";
        if (CoreLog.IsEnabled) CoreLog.Write($"{PersistenceNotice} {ex}");
        PersistenceFailed?.Invoke(this, EventArgs.Empty);
    }
}
