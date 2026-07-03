using System.Text.Json;

namespace Searchlight.Services;

/// <summary>
/// One entry from a Claude Code project folder's <c>sessions-index.json</c>.
/// All enrichment fields are nullable — older Claude Code versions wrote
/// sparser indexes, and some sessions never get indexed at all.
/// </summary>
public sealed record ClaudeSessionEntry
{
    /// <summary>Session UUID.</summary>
    public required string SessionId { get; init; }

    /// <summary>Absolute path to the session's <c>.jsonl</c> transcript.</summary>
    public string? FullPath { get; init; }

    /// <summary>First user prompt, as recorded by the index.</summary>
    public string? FirstPrompt { get; init; }

    /// <summary>AI-generated one-line session summary, when present.</summary>
    public string? Summary { get; init; }

    /// <summary>Message count recorded by the index.</summary>
    public int MessageCount { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTimeOffset? Modified { get; init; }

    /// <summary>Git branch the session was started on, when recorded.</summary>
    public string? GitBranch { get; init; }

    /// <summary>Decoded workspace path (e.g. <c>/Users/jane/Source/app</c>).</summary>
    public string? ProjectPath { get; init; }

    /// <summary>True for sidechain (subagent) transcripts.</summary>
    public bool IsSidechain { get; init; }
}

/// <summary>
/// Reads a Claude Code project folder's <c>sessions-index.json</c> — the cheap
/// bulk source for the session list (id, first prompt, summary, timestamps,
/// branch, workspace path). Read-only and null-safe: a missing, locked, or
/// malformed index degrades to an empty list, never throws.
/// </summary>
public sealed class ClaudeSessionIndexReader
{
    /// <summary>
    /// Parses the index in <paramref name="projectDir"/>, or returns an empty
    /// list when absent or unreadable.
    /// </summary>
    public IReadOnlyList<ClaudeSessionEntry> Read(string projectDir)
    {
        string path = ClaudePaths.SessionsIndex(projectDir);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("entries", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ClaudeSessionEntry>(entries.GetArrayLength());
            foreach (JsonElement e in entries.EnumerateArray())
            {
                string? id = GetString(e, "sessionId");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                result.Add(new ClaudeSessionEntry
                {
                    SessionId = id,
                    FullPath = GetString(e, "fullPath"),
                    FirstPrompt = GetString(e, "firstPrompt"),
                    Summary = GetString(e, "summary"),
                    MessageCount = GetInt(e, "messageCount"),
                    Created = GetTime(e, "created"),
                    Modified = GetTime(e, "modified"),
                    GitBranch = GetString(e, "gitBranch"),
                    ProjectPath = GetString(e, "projectPath"),
                    IsSidechain = GetBool(e, "isSidechain"),
                });
            }

            return result;
        }
        catch (Exception)
        {
            // Locked / corrupt / mid-write index → treat as absent.
            return [];
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out int n) // fractional/overflowing → 0, not a dropped index
            ? n
            : 0;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTime(JsonElement e, string name) =>
        GetString(e, name) is { } s && DateTimeOffset.TryParse(s, out DateTimeOffset t)
            ? t
            : null;
}
