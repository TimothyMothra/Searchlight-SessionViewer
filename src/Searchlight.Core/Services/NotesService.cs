using System;
using System.Collections.Generic;
using System.IO;

namespace Searchlight.Services;

/// <summary>
/// Reads and persists free-form per-session notes as plain-text sidecar files
/// under <c>%LOCALAPPDATA%\Searchlight\notes\{sessionId}.md</c> — one file per
/// session, keyed by the session UUID. This keeps notes out of
/// <c>settings.json</c> (which would bloat on every keystroke-save) and leaves
/// them readable/editable outside the app. Like <see cref="SettingsService"/>,
/// all I/O is best-effort: read failures yield an empty string and write
/// failures are swallowed (notes are non-critical). Searchlight never writes to
/// the user's <c>~/.copilot</c> source — notes live only in the app's own dir.
/// </summary>
public sealed class NotesService
{
    // Null dir => in-memory only (no disk I/O). Used by tests for isolation so
    // they never read or clobber the real %LOCALAPPDATA% notes folder.
    private readonly string? _dir;

    // Backing store for the in-memory (test) mode; unused when _dir is set.
    private readonly Dictionary<string, string> _memory = [];

    /// <summary>Creates a notes service backed by the default on-disk folder.</summary>
    public NotesService()
        : this(DefaultDir())
    {
    }

    /// <summary>
    /// Test/advanced constructor. A non-null <paramref name="dir"/> reads from and
    /// writes to sidecar files under that folder; a null dir yields an isolated,
    /// in-memory-only instance (never touches disk).
    /// </summary>
    internal NotesService(string? dir) => _dir = dir;

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Searchlight",
        "notes");

    /// <summary>
    /// Returns the note text for a session, or an empty string when no note exists
    /// (or on any read error). Never throws.
    /// </summary>
    public string Read(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return string.Empty;
        }

        if (_dir is null)
        {
            return _memory.GetValueOrDefault(sessionId, string.Empty);
        }

        try
        {
            string path = PathFor(sessionId);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Persists the note text for a session (best-effort). Whitespace-only or empty
    /// text removes the note file entirely rather than storing a blank note.
    /// </summary>
    public void Write(string sessionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        text ??= string.Empty;
        bool isEmpty = text.Trim().Length == 0;

        if (_dir is null)
        {
            if (isEmpty)
            {
                _memory.Remove(sessionId);
            }
            else
            {
                _memory[sessionId] = text;
            }

            return;
        }

        try
        {
            string path = PathFor(sessionId);
            if (isEmpty)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            Directory.CreateDirectory(_dir);
            File.WriteAllText(path, text);
        }
        catch (Exception)
        {
            // Non-critical: the note just won't persist this session.
        }
    }

    /// <summary>True when a non-empty note is stored for the session.</summary>
    public bool HasNote(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (_dir is null)
        {
            return _memory.ContainsKey(sessionId);
        }

        try
        {
            return File.Exists(PathFor(sessionId));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string PathFor(string sessionId) =>
        Path.Combine(_dir!, Sanitize(sessionId) + ".md");

    // Session ids are UUIDs and already file-safe, but guard defensively so a
    // stray character can never escape the notes folder.
    private static string Sanitize(string id)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
