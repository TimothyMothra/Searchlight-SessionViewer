using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Searchlight.Diagnostics;
using Searchlight.Models;
using Searchlight.Services;
using Xunit;
using Xunit.Abstractions;

namespace Searchlight.Core.Tests;

// ASSUMPTION: synthetic UTF-8 fixtures model metadata, not complete transcripts.
// Serialize this collection because CoreLog.Sink is process-wide.
[CollectionDefinition("Events reader", DisableParallelization = true)]
public sealed class EventsReaderCollection;

[Collection("Events reader")]
public sealed class EventsJsonlReaderTests(ITestOutputHelper output)
{
    private const string Start = """{"type":"session.start","data":{"selectedModel":"baseline","reasoningEffort":"low"}}""";

    [Fact]
    public void Read_PreservesStartLatestModelAndFirstNonemptyPreview()
    {
        var result = Read(
            """{"type":"session.start","data":{"copilotVersion":"1.2","contextTier":"long","producer":"cli","startTime":"2026-09-08T12:00:00Z","context":{"cwd":"synthetic"},"alreadyInUse":true,"selectedModel":"baseline","reasoningEffort":"low"}}""" + "\n" +
            Prompt(" \t\r\n ") + "\n" + Prompt("  first\r\nprompt  ") + "\n" +
            """{"type":"session.model_change","data":{"newModel":"second","reasoningEffort":"high"}}""" + "\n" +
            Prompt("later") + "\n" +
            """{"type":"session.model_change","data":{"newModel":"latest"}}""");

        Assert.NotNull(result);
        Assert.Equal("1.2", result.CopilotVersion);
        Assert.Equal("long", result.ContextTier);
        Assert.Equal("cli", result.Producer);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero), result.StartTime);
        Assert.Equal("synthetic", result.Cwd);
        Assert.True(result.AlreadyInUse);
        Assert.Equal("latest", result.Model);
        Assert.Equal("high", result.ReasoningEffort);
        Assert.Equal("first  prompt", result.FirstUserPrompt);
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2000)]
    [InlineData(2001)]
    public void Read_TruncatesPreviewOnlyAbove2000Characters(int length)
    {
        var result = Read(Start + "\n" + Prompt(new string('x', length)));
        Assert.Equal(new string('x', Math.Min(length, 2000)) + (length > 2000 ? "…" : ""), result!.FirstUserPrompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"session.start\"")]
    [InlineData("{\"type\":\"user.message\",\"data\":{\"content\":\"orphan\"}}")]
    [InlineData("{\"type\":\"session.start\",\"data\":null}")]
    public void Read_ReturnsNullWithoutValidStart(string text) => Assert.Null(Read(text));

    [Fact]
    public void Read_SkipsMalformedAndWrongShapeEvents()
    {
        string[] invalid =
        [
            "", "{", "null", "[]", "1", "\"text\"", "{}", "{\"type\":42}",
            """{"type":"session.start","data":null}""",
            """{"type":"session.model_change","data":[]}""",
            """{"type":"user.message","data":3}""",
            """{"type":"user.message","data":{"content":false}}""",
            """{"type":"session.model_change","data":{"newModel":true}}"""
        ];
        var result = Read(Start + "\n" + string.Join('\n', invalid) + "\n" + Prompt("valid") + "\n{");
        Assert.Equal("baseline", result!.Model);
        Assert.Equal("valid", result.FirstUserPrompt);
    }

    [Fact]
    public void Read_PreservesFlatStartAndSequentialBaselineSelection()
    {
        var result = Read(
            """{"type":"session.model_change","newModel":"before"}""" + "\n" +
            """{"type":"session.start","copilotVersion":"one","selectedModel":"start"}""" + "\n" +
            """{"type":"session.start","contextTier":"long"}""" + "\n" +
            """{"type":"session.model_change","newModel":"after"}""");
        Assert.Equal("one", result!.CopilotVersion);
        Assert.Equal("long", result.ContextTier);
        Assert.Equal("after", result.Model);
    }

    [Theory]
    [InlineData("\n", 1)]
    [InlineData("\r\n", 1)]
    [InlineData("\r", 7)]
    [InlineData("\r\n", 16384)]
    public void Read_HandlesBomUnicodeAndSplitTerminators(string newline, int chunkSize)
    {
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes("\uFEFF" + Start + newline + Prompt("héllo 🌍") + newline), chunkSize);
        var result = new EventsJsonlReader().Read(stream);
        Assert.Equal("héllo 🌍", result!.FirstUserPrompt);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Read_EnforcesEventByteLimitAndContinuesAtNextLine(int excess)
    {
        string paddedChange = PadEvent(
            """{"type":"session.model_change","data":{"newModel":"within"}}""",
            EventsJsonlReader.MaxEventLineBytes + excess);
        var result = Read(Start + "\n" + paddedChange + "\r\n" + Prompt("after"));
        Assert.Equal(excess > 0 ? "baseline" : "within", result!.Model);
        Assert.Equal("after", result.FirstUserPrompt);
    }

    [Fact]
    public void Read_CountsUtf8BytesRatherThanCharacters()
    {
        string oversized = Prompt(new string('é', EventsJsonlReader.MaxEventLineBytes / 2));
        Assert.True(oversized.Length < EventsJsonlReader.MaxEventLineBytes);
        var result = Read(Start + "\n" + oversized + "\n" + Prompt("within"));
        Assert.Equal("within", result!.FirstUserPrompt);
    }

    [Fact]
    public void Read_StopsAtLineBudgetIncludingBlankAndInvalidLines()
    {
        string prefix = Start + "\n" + string.Concat(Enumerable.Repeat("\ninvalid\n", 999));
        var result = Read(prefix + Prompt("last-in-window") + "\n" +
            """{"type":"session.model_change","data":{"newModel":"outside"}}""");
        Assert.Equal("baseline", result!.Model);
        Assert.Equal("last-in-window", result.FirstUserPrompt);
        Assert.Null(Read(string.Concat(Enumerable.Repeat("\n", EventsJsonlReader.MaxLines)) + Start));
    }

    [Fact]
    public void Read_OversizedLinesAlsoCountAgainstLineBudget()
    {
        string text = new string('x', EventsJsonlReader.MaxEventLineBytes + 1) + "\n" +
            new string('\n', EventsJsonlReader.MaxLines - 1) + Start;
        Assert.Null(Read(text));
    }

    [Fact]
    public void Read_EnforcesTotalByteBudgetWithoutReadingOrParsingBeyondIt()
    {
        string prefix = Start + "\n";
        string last = Prompt("at-boundary") + "\n";
        string filler = new string('x', EventsJsonlReader.MaxInputBytes - prefix.Length - last.Length - 1) + "\n";
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(prefix + filler + last + Prompt("outside")));
        var result = new EventsJsonlReader().Read(stream);
        Assert.Equal(EventsJsonlReader.MaxInputBytes, stream.BytesRead);
        Assert.Equal("at-boundary", result!.FirstUserPrompt);
    }

    [Fact]
    public void Read_DropsUnterminatedTailAtBudgetBoundaryEvenIfPrefixLooksValid()
    {
        string prefix = Start + "\n";
        string tail = Prompt("not-confirmed-complete");
        string filler = new string('x', EventsJsonlReader.MaxInputBytes - prefix.Length - tail.Length - 1) + "\n";
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(prefix + filler + tail + "invalid-suffix\n"));
        var result = new EventsJsonlReader().Read(stream);
        Assert.Equal(EventsJsonlReader.MaxInputBytes, stream.BytesRead);
        Assert.Null(result!.FirstUserPrompt);
    }

    [Fact]
    public void Read_BoundsAnUnterminatedHugeLineAndLogsOnlyLimits()
    {
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(Start + "\n" + new string('x', EventsJsonlReader.MaxInputBytes)));
        var messages = new List<string>();
        var previous = CoreLog.Sink;
        try
        {
            CoreLog.Sink = messages.Add;
            Assert.NotNull(new EventsJsonlReader().Read(stream));
        }
        finally
        {
            CoreLog.Sink = previous;
        }
        Assert.Equal(EventsJsonlReader.MaxInputBytes, stream.BytesRead);
        Assert.Contains(messages, m => m.Contains("skipped event"));
        Assert.Contains(messages, m => m.Contains("input limit"));
        Assert.DoesNotContain(messages, m => m.Contains("baseline") || m.Contains("session.start"));
    }

    [Fact]
    public void Read_LogsLineLimit()
    {
        var messages = new List<string>();
        var previous = CoreLog.Sink;
        try
        {
            CoreLog.Sink = messages.Add;
            Read(new string('\n', EventsJsonlReader.MaxLines));
        }
        finally
        {
            CoreLog.Sink = previous;
        }
        Assert.Contains(messages, m => m.Contains("line limit"));
    }

    [Fact]
    public void Read_PreservesCompletedMetadataOnIoFailure()
    {
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(Start + "\n"), failAtEnd: true);
        Assert.Equal("baseline", new EventsJsonlReader().Read(stream)!.Model);
    }

    [Fact]
    public void Read_MissingFileAndLiveWriterAreSupported()
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "EventsReaderFixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var reader = new EventsJsonlReader();
            Assert.Null(reader.Read(folder));
            using var writer = new FileStream(Path.Combine(folder, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            writer.Write(Encoding.UTF8.GetBytes(Start + "\n" + Prompt("live") + "\n{"));
            writer.Flush();
            Assert.Equal("live", reader.Read(folder)!.FirstUserPrompt);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Read_DoesNotAllocatePayloadSizedClonesOrLineStrings()
    {
        string ignored = JsonSerializer.Serialize(new { type = "tool.output", data = new { content = new string('x', 4000) } }) + "\n";
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(Start + "\n" + string.Concat(Enumerable.Repeat(ignored, 1800))));
        var reader = new EventsJsonlReader();
        reader.Read(stream);
        stream.Position = 0;
        // ASSUMPTION: after warming shared pools, metadata-only parsing should allocate
        // far less than this synthetic 7 MiB input, independent of tool payload strings.
        long started = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = reader.Read(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Synthetic scan: {stream.Length:N0} input bytes, {allocated:N0} allocated bytes, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
        Assert.NotNull(result);
        Assert.True(allocated < 2 * 1024 * 1024, $"Allocated {allocated:N0} bytes for the synthetic metadata scan.");
    }

    private static SessionStartInfo? Read(string text)
    {
        using var stream = new RecordingStream(Encoding.UTF8.GetBytes(text));
        return new EventsJsonlReader().Read(stream);
    }

    private static string Prompt(string text) =>
        "{\"type\":\"user.message\",\"data\":{\"content\":" +
        JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "}}";

    private static string PadEvent(string json, int bytes) => json + new string(' ', bytes - json.Length);

    private sealed class RecordingStream(byte[] bytes, int chunkSize = int.MaxValue, bool failAtEnd = false) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAtEnd && Position == Length) throw new IOException("Synthetic read failure.");
            int read = base.Read(buffer, offset, Math.Min(count, chunkSize));
            BytesRead += read;
            return read;
        }
    }
}
