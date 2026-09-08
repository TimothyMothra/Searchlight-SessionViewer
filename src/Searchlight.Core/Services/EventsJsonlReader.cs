using System.Buffers;
using System.Text.Json;
using Searchlight.Diagnostics;
using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Head-parses a session's <c>events.jsonl</c> into a <see cref="SessionStartInfo"/>.
/// Reads at most <see cref="MaxLines"/> leading lines and <see cref="MaxInputBytes"/>
/// bytes without materializing the log. Extracts the <c>session.start</c> baseline, tracks the
/// latest <c>session.model_change</c> seen in-window, and captures the first
/// <c>user.message</c> content as a prompt preview. Read-only and null-safe.
/// </summary>
public sealed class EventsJsonlReader
{
    /// <summary>Upper bound on lines scanned per session (keeps the scan bounded).</summary>
    public const int MaxLines = 2000;

    // ASSUMPTION: UTF-8 JSONL supplies preview metadata, not a complete transcript.
    // Bound bytes as well as lines: tool output and pasted prompts can be arbitrarily large.
    /// <summary>Maximum input bytes read, including skipped events and line terminators.</summary>
    public const int MaxInputBytes = 8 * 1024 * 1024;

    /// <summary>Maximum event bytes retained, excluding line terminators; larger lines are skipped.</summary>
    public const int MaxEventLineBytes = 1024 * 1024;

    private const int PreviewLength = 2000;
    private const int ReadBufferSize = 16 * 1024;

    /// <summary>
    /// Parses the head of <c>events.jsonl</c> in <paramref name="folderPath"/>, or
    /// returns <c>null</c> when the file is absent or has no <c>session.start</c>.
    /// </summary>
    public SessionStartInfo? Read(string folderPath)
    {
        string path = CopilotPaths.EventsJsonl(folderPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Disable FileStream read-ahead so the byte budget also bounds physical input.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1, FileOptions.SequentialScan);
            return Read(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The caller owns the stream. This seam also lets tests verify actual input consumption.
    internal SessionStartInfo? Read(Stream stream)
    {
        string? copilotVersion = null;
        string? contextTier = null;
        string? producer = null;
        DateTimeOffset? startTime = null;
        string? cwd = null;
        bool alreadyInUse = false;
        string? model = null;
        string? reasoningEffort = null;
        string? firstPrompt = null;
        bool haveStart = false;

        try
        {
            bool firstLine = true;
            foreach (ReadOnlyMemory<byte> rawLine in ReadLines(stream))
            {
                ReadOnlyMemory<byte> line = rawLine;
                if (firstLine && line.Span.StartsWith("\uFEFF"u8))
                    line = line[3..];
                firstLine = false;
                if (line.IsEmpty) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("type", out JsonElement typeEl) ||
                        typeEl.ValueKind != JsonValueKind.String ||
                        (root.TryGetProperty("data", out JsonElement data) &&
                         data.ValueKind != JsonValueKind.Object))
                    {
                        continue;
                    }

                    switch (typeEl.GetString())
                    {
                        case "session.start":
                            ParseStart(
                                root, ref copilotVersion, ref contextTier, ref producer,
                                ref startTime, ref cwd, ref alreadyInUse, ref model,
                                ref reasoningEffort);
                            haveStart = true;
                            break;

                        case "session.model_change":
                            ParseModelChange(root, ref model, ref reasoningEffort);
                            break;

                        case "user.message" when firstPrompt is null:
                            firstPrompt = ExtractFirstPrompt(root);
                            break;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        catch (IOException)
        {
            return haveStart ? Build() : null;
        }

        return haveStart ? Build() : null;

        SessionStartInfo Build() => new()
        {
            CopilotVersion = copilotVersion,
            ContextTier = contextTier,
            Producer = producer,
            StartTime = startTime,
            Cwd = cwd,
            AlreadyInUse = alreadyInUse,
            Model = model,
            ReasoningEffort = reasoningEffort,
            FirstUserPrompt = firstPrompt,
        };
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadLines(Stream stream)
    {
        byte[] input = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        byte[] line = ArrayPool<byte>.Shared.Rent(MaxEventLineBytes);
        int total = 0, count = 0, length = 0;
        bool oversized = false, skipLf = false;
        try
        {
            while (total < MaxInputBytes && count < MaxLines)
            {
                int read = stream.Read(input, 0, Math.Min(ReadBufferSize, MaxInputBytes - total));
                if (read == 0)
                {
                    if (length > 0 && !oversized)
                        yield return line.AsMemory(0, length);
                    yield break;
                }
                total += read;
                int offset = 0;
                while (offset < read && count < MaxLines)
                {
                    if (skipLf)
                    {
                        skipLf = false;
                        if (input[offset] == (byte)'\n')
                        {
                            offset++;
                            continue;
                        }
                    }

                    int delimiter = input.AsSpan(offset, read - offset).IndexOfAny((byte)'\r', (byte)'\n');
                    int take = delimiter < 0 ? read - offset : delimiter;
                    if (!oversized)
                    {
                        if (take > MaxEventLineBytes - length)
                        {
                            oversized = true;
                            CoreLog.Write($"EventsJsonlReader: skipped event exceeding {MaxEventLineBytes} bytes.");
                        }
                        else
                        {
                            Buffer.BlockCopy(input, offset, line, length, take);
                            length += take;
                        }
                    }
                    offset += take;
                    if (delimiter < 0) continue;

                    skipLf = input[offset++] == (byte)'\r';
                    count++;
                    // The consumer finishes with this pooled memory before MoveNext reuses it.
                    if (!oversized) yield return line.AsMemory(0, length);
                    length = 0;
                    oversized = false;
                }
            }

            if (total >= MaxInputBytes)
                CoreLog.Write($"EventsJsonlReader: preview input limit reached ({MaxInputBytes} bytes).");
            if (count >= MaxLines)
                CoreLog.Write($"EventsJsonlReader: preview line limit reached ({MaxLines} lines).");
            // ASSUMPTION: a budget boundary is not EOF. Drop an unterminated tail rather than
            // treating a possibly incomplete live event as complete or reading past the budget.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(line);
        }
    }

    private static void ParseStart(
        JsonElement root, ref string? copilotVersion, ref string? contextTier,
        ref string? producer, ref DateTimeOffset? startTime, ref string? cwd,
        ref bool alreadyInUse, ref string? model, ref string? reasoningEffort)
    {
        // session.start places its fields directly under "data".
        JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d : root;

        copilotVersion = GetString(data, "copilotVersion") ?? copilotVersion;
        contextTier = GetString(data, "contextTier") ?? contextTier;
        producer = GetString(data, "producer") ?? producer;
        cwd = GetContextCwd(data) ?? cwd;
        model = GetString(data, "selectedModel") ?? model;
        reasoningEffort = GetString(data, "reasoningEffort") ?? reasoningEffort;

        if (data.TryGetProperty("startTime", out JsonElement st) &&
            st.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(st.GetString(), out DateTimeOffset parsed))
        {
            startTime = parsed;
        }

        if (data.TryGetProperty("alreadyInUse", out JsonElement au) &&
            (au.ValueKind == JsonValueKind.True || au.ValueKind == JsonValueKind.False))
        {
            alreadyInUse = au.GetBoolean();
        }
    }

    private static void ParseModelChange(
        JsonElement root, ref string? model, ref string? reasoningEffort)
    {
        JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d : root;
        model = GetString(data, "newModel") ?? model;
        reasoningEffort = GetString(data, "reasoningEffort") ?? reasoningEffort;
    }

    private static string? ExtractFirstPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = content.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > PreviewLength ? text[..PreviewLength] + "\u2026" : text;
    }

    private static string? GetContextCwd(JsonElement data)
    {
        if (data.TryGetProperty("context", out JsonElement ctx) &&
            ctx.ValueKind == JsonValueKind.Object)
        {
            return GetString(ctx, "cwd");
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
