using Avalonia.Controls;
using Searchlight.Abstractions;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Clipboard adapter over Avalonia's <c>TopLevel.Clipboard</c>. The clipboard is
/// only reachable through a live top-level window, so the host attaches its main
/// window after construction. Copies are dispatched fire-and-forget; "attached
/// and dispatched" is reported as success, matching the synchronous
/// <see cref="IClipboardService"/> contract.
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    private TopLevel? _topLevel;

    /// <summary>Attaches the window whose clipboard is used for copies.</summary>
    public void AttachTo(TopLevel topLevel) => _topLevel = topLevel;

    /// <inheritdoc />
    public bool SetText(string text)
    {
        var clipboard = _topLevel?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            clipboard.SetTextAsync(text).ContinueWith(
                t => Searchlight.Diagnostics.CoreLog.Write(
                    $"Clipboard: copy failed: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
