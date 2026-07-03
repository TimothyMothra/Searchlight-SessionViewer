using System.Text.Json;

namespace Searchlight.Services;

/// <summary>
/// Facts head-parsed from a Claude Code session transcript: the first real user
/// prompt, the model from the first assistant message, the CLI version, working
/// directory, branch, and start time. Any field may be null — transcripts open
/// with a variable number of housekeeping records.
/// </summary>
public sealed record ClaudeHeadInfo
{
    /// <summary>Model id from the first assistant message (e.g. <c>claude-sonnet-5</c>).</summary>
    public string? Model { get; init; }

    /// <summary>Claude Code CLI version recorded on the first user message.</summary>
    public string? Version { get; init; }

    /// <summary>Working directory recorded on the first user message.</summary>
    public string? Cwd { get; init; }

    /// <summary>Git branch recorded on the first user message.</summary>
    public string? GitBranch { get; init; }

    /// <summary>Timestamp of the first user message.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>First typed user prompt, trimmed for a one-line preview.</summary>
    public string? FirstUserPrompt { get; init; }

    /// <summary>AI-generated session title from the transcript's <c>ai-title</c> record.</summary>
    public string? AiTitle { get; init; }
}

/// <summary>
/// Head-parses a Claude Code session <c>&lt;id&gt;.jsonl</c> transcript. Reads at
/// most <see cref="MaxLines"/> leading lines so multi-megabyte transcripts are
/// never materialized. Transcripts interleave housekeeping records
/// (<c>ai-title</c>, <c>file-history-snapshot</c>, hook attachments) with
/// <c>user</c> / <c>assistant</c> message records; only the latter carry the
/// facts we need. Read-only and null-safe.
/// </summary>
public sealed class ClaudeJsonlHeadReader
{
    /// <summary>Upper bound on lines scanned per transcript (keeps the scan bounded).</summary>
    public const int MaxLines = 500;

    /// <summary>
    /// How many lines the scan keeps going past the core facts hoping for an
    /// <c>ai-title</c>. Titles reliably land within the first few dozen records;
    /// transcripts that never get one shouldn't pay for all <see cref="MaxLines"/>.
    /// </summary>
    public const int TitleSearchLines = 150;

    private const int PreviewLength = 2000;

    /// <summary>
    /// Parses the head of the transcript at <paramref name="jsonlPath"/>, or
    /// returns <c>null</c> when the file is absent or unreadable.
    /// </summary>
    public ClaudeHeadInfo? Read(string jsonlPath)
    {
        if (!File.Exists(jsonlPath))
        {
            return null;
        }

        string? model = null;
        string? version = null;
        string? cwd = null;
        string? branch = null;
        DateTimeOffset? startTime = null;
        string? firstPrompt = null;
        string? aiTitle = null;

        try
        {
            using var stream = new FileStream(
                jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            int count = 0;
            while ((line = reader.ReadLine()) is not null && count < MaxLines)
            {
                count++;
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // Torn/partial trailing line while a session writes.
                }

                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    string? type = GetString(root, "type");

                    if (type == "user")
                    {
                        ParseUserRecord(root, ref version, ref cwd, ref branch, ref startTime, ref firstPrompt);
                    }
                    else if (type == "ai-title")
                    {
                        // Later ai-title records supersede earlier ones (retitles).
                        aiTitle = GetString(root, "aiTitle") ?? aiTitle;
                    }
                    else if (type == "assistant" && model is null)
                    {
                        if (root.TryGetProperty("message", out JsonElement msg))
                        {
                            model = GetString(msg, "model");
                        }
                    }

                    // ai-title lands tens of lines in (after the first exchange),
                    // so it must be part of the exit condition — an early break on
                    // model+prompt alone walks straight past the friendly name.
                    // Title-less transcripts stop at TitleSearchLines instead of
                    // paying for the full MaxLines window.
                    if (model is not null
                        && firstPrompt is not null
                        && version is not null
                        && (aiTitle is not null || count >= TitleSearchLines))
                    {
                        break; // Everything we head-parse for is in hand.
                    }
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return new ClaudeHeadInfo
        {
            Model = model,
            Version = version,
            Cwd = cwd,
            GitBranch = branch,
            StartTime = startTime,
            FirstUserPrompt = firstPrompt,
            AiTitle = aiTitle,
        };
    }

    private static void ParseUserRecord(
        JsonElement root,
        ref string? version,
        ref string? cwd,
        ref string? branch,
        ref DateTimeOffset? startTime,
        ref string? firstPrompt)
    {
        version ??= GetString(root, "version");
        cwd ??= GetString(root, "cwd");
        branch ??= GetString(root, "gitBranch");

        if (startTime is null
            && GetString(root, "timestamp") is { } ts
            && DateTimeOffset.TryParse(ts, out DateTimeOffset parsed))
        {
            startTime = parsed;
        }

        if (firstPrompt is not null || IsMeta(root))
        {
            return;
        }

        string? text = ExtractUserText(root);

        // Skip harness-injected wrappers (<command-name>, <system-reminder>, …)
        // — they aren't something the user typed.
        if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('<'))
        {
            text = text.Trim();
            firstPrompt = text.Length <= PreviewLength ? text : text[..PreviewLength];
        }
    }

    private static bool IsMeta(JsonElement root) =>
        root.TryGetProperty("isMeta", out JsonElement m) && m.ValueKind == JsonValueKind.True;

    /// <summary>
    /// User message content is either a plain string or a content-block array;
    /// take the first <c>text</c> block in the array form.
    /// </summary>
    private static string? ExtractUserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement msg)
            || !msg.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (GetString(block, "type") == "text")
                {
                    return GetString(block, "text");
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
