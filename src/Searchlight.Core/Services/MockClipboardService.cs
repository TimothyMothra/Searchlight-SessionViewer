using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// No-op <see cref="IClipboardService"/> for mock/demo mode and tests. Records the last
/// copied text (useful for assertions) and reports success without touching the real
/// system clipboard, so demos and screenshots never mutate the user's clipboard.
/// </summary>
public sealed class MockClipboardService : IClipboardService
{
    /// <summary>The text passed to the most recent <see cref="SetText"/> call, if any.</summary>
    public string? LastCopiedText { get; private set; }

    /// <inheritdoc />
    public bool SetText(string text)
    {
        LastCopiedText = text;
        return true;
    }
}
