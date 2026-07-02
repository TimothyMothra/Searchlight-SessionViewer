using System.Runtime.InteropServices;
using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Windows <see cref="IClipboardService"/> implementation backed by the Win32 clipboard
/// API (user32 <c>OpenClipboard</c>/<c>SetClipboardData</c>).
/// </summary>
/// <remarks>
/// The obvious choice — <c>Windows.ApplicationModel.DataTransfer.Clipboard.SetContent</c> —
/// is unreliable in an <b>unpackaged</b> WinUI 3 app (<c>WindowsPackageType=None</c>): the
/// call frequently returns without throwing yet the data never actually lands on the
/// clipboard, because there is no package identity backing the delayed-render broker. That
/// produced the "success message but nothing pasted" bug. The classic Win32 clipboard path
/// writes a <c>CF_UNICODETEXT</c> global block directly and is bulletproof regardless of
/// packaging identity, so we use it here.
/// </remarks>
public sealed partial class ClipboardService : IClipboardService
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    // The clipboard is a single global resource; another process can briefly hold it open.
    // Retry a few times before giving up so a transient lock doesn't surface as a failure.
    private const int MaxAttempts = 5;
    private const int RetryDelayMs = 15;

    /// <inheritdoc />
    public bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TrySetText(text))
            {
                return true;
            }

            Thread.Sleep(RetryDelayMs);
        }

        return false;
    }

    private static bool TrySetText(string text)
    {
        // A null owner is fine; the clipboard is then associated with the current task.
        if (!OpenClipboard(nint.Zero))
        {
            return false;
        }

        nint hGlobal = nint.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            // UTF-16, +1 char for the terminating null.
            int bytes = (text.Length + 1) * 2;
            hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (hGlobal == nint.Zero)
            {
                return false;
            }

            nint target = GlobalLock(hGlobal);
            if (target == nint.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                // Null terminator immediately after the copied characters.
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == nint.Zero)
            {
                // Ownership was NOT transferred to the system; the finally block frees it.
                return false;
            }

            // The system now owns the block — do not free it.
            hGlobal = nint.Zero;
            return true;
        }
        finally
        {
            if (hGlobal != nint.Zero)
            {
                GlobalFree(hGlobal);
            }

            CloseClipboard();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint hMem);
}
