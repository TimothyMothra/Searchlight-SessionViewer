using Searchlight.Diagnostics;

namespace Searchlight.Services;

/// <summary>Plain-text shared notes with optimistic concurrency and recoverable conflicting drafts.</summary>
public sealed class NotesService
{
    private readonly string? _dir;
    private readonly SharedStatePaths? _paths;
    private readonly Dictionary<string, string> _memory = [];
    private readonly Dictionary<string, string?> _observed = [];
    private readonly object _sync = new();
    private readonly string _writerId = Guid.NewGuid().ToString("N");

    public string PersistenceNotice { get; private set; } = string.Empty;
    public event EventHandler? PersistenceFailed;
    /// <summary>Recovery file for the last failed write; null if no durable backup was possible.</summary>
    public string? LastRecoveryPath { get; private set; }

    public NotesService() : this(SharedStatePaths.Default) { }
    internal NotesService(SharedStatePaths paths)
    {
        _paths = paths;
        _dir = paths.NotesDirectory;
    }
    // Null retains isolated test/demo storage; no user-profile access.
    internal NotesService(string? dir) => _dir = dir is null ? null : Path.GetFullPath(dir);

    /// <summary>Reads a snapshot and records the version against which the next write is checked.</summary>
    public string Read(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return string.Empty;
        lock (_sync)
        {
            if (_dir is null) return _memory.GetValueOrDefault(sessionId, string.Empty);
            try
            {
                _paths?.MigrateLegacy();
                using var gate = SharedStateIO.Lock(_dir);
                string? text = SharedStateIO.ReadOptional(PathFor(sessionId));
                _observed[sessionId] = text;
                return text ?? string.Empty;
            }
            catch (Exception ex) when (SettingsService.IsStorageError(ex))
            {
                _observed.Remove(sessionId);
                Report($"Could not read note {sessionId}; editing cannot overwrite an unread snapshot.", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Saves only if the note still matches its observed snapshot. Read before
    /// editing existing notes. Conflicts throw with a persistent recovery path.
    /// </summary>
    public void Write(string sessionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        text ??= string.Empty;
        string? desired = string.IsNullOrWhiteSpace(text) ? null : text;
        lock (_sync)
        {
            LastRecoveryPath = null;
            if (_dir is null)
            {
                if (desired is null) _memory.Remove(sessionId);
                else _memory[sessionId] = desired;
                return;
            }
            try
            {
                _paths?.MigrateLegacy();
                using var gate = SharedStateIO.Lock(_dir);
                string path = PathFor(sessionId);
                string? disk = SharedStateIO.ReadOptional(path);
                _observed.TryGetValue(sessionId, out string? baseline);
                // ASSUMPTION: text equality defines a note version (including
                // absence). Unobserved existing files must not be overwritten.
                if (disk != baseline && disk != desired)
                    throw new NotesConflictException(sessionId);
                if (desired is null) File.Delete(path);
                else SharedStateIO.WriteAtomic(path, desired);
                _observed[sessionId] = desired;
            }
            catch (Exception ex) when (SettingsService.IsStorageError(ex))
            {
                string recovery;
                try { recovery = PreserveDraft(sessionId, text); LastRecoveryPath = recovery; }
                catch (Exception recoveryError) when (SettingsService.IsStorageError(recoveryError))
                {
                    Report($"Could not save note {sessionId} OR its recovery draft. Keep this window open and copy the draft before exiting.", recoveryError);
                    throw new IOException(PersistenceNotice, new AggregateException(ex, recoveryError));
                }
                if (ex is NotesConflictException conflict)
                {
                    conflict.RecoveryPath = recovery;
                    Report($"Note {sessionId} changed in another window. Shared text was not replaced. Your draft is retained at {recovery}. Compare and reconcile the files before editing again.", ex);
                    throw;
                }
                Report($"Could not save note {sessionId}. Your draft is retained at {recovery}.", ex);
                throw;
            }
        }
    }

    private string PreserveDraft(string sessionId, string text)
    {
        // Each window owns a separate draft, so retries never overwrite another
        // window's recovery. Keep recovery files even after later successful saves.
        string path = Path.Combine(_dir!, "conflicts", $"{SafeId(sessionId)}.{_writerId}.md");
        SharedStateIO.WriteAtomic(path, text);
        return path;
    }

    public bool HasNote(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && LoadNoteIds().Contains(sessionId);

    /// <summary>Presence indexing never changes the observed version of an edited note.</summary>
    public IReadOnlySet<string> LoadNoteIds()
    {
        lock (_sync)
        {
            if (_dir is null) return new HashSet<string>(_memory.Keys, StringComparer.Ordinal);
            try
            {
                _paths?.MigrateLegacy();
                using var gate = SharedStateIO.Lock(_dir);
                string conflicts = Path.Combine(_dir, "conflicts");
                if (SharedStateIO.DirectoryExists(conflicts)
                    && Directory.EnumerateFiles(conflicts, "*.md").Any()
                    && string.IsNullOrEmpty(PersistenceNotice))
                {
                    PersistenceNotice = $"Recovered note drafts exist at {conflicts}. Compare them with shared notes; remove recovery files only after reconciliation.";
                    CoreLog.Write(PersistenceNotice);
                    PersistenceFailed?.Invoke(this, EventArgs.Empty);
                }
                return Directory.EnumerateFiles(_dir, "*.md", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (SettingsService.IsStorageError(ex))
            {
                Report("Could not reload shared notes.", ex);
                throw;
            }
        }
    }

    private string PathFor(string id) => Path.Combine(_dir!, SafeId(id) + ".md");

    private static string SafeId(string id)
    {
        // Reject rather than normalize: distinct session IDs must never alias the
        // same file. UUIDs and legacy optimistic-chat IDs satisfy this contract.
        if (id is "." or ".." || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new IOException("The session ID is not safe for note storage.");
        return id;
    }

    private void Report(string message, Exception ex)
    {
        PersistenceNotice = $"{message} {ex.Message}";
        CoreLog.Write($"{PersistenceNotice} {ex}");
        PersistenceFailed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class NotesConflictException(string sessionId)
    : IOException($"Concurrent edits detected for note {sessionId}.")
{
    public string? RecoveryPath { get; internal set; }
}
