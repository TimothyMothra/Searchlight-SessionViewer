using System.Runtime.InteropServices;

namespace Searchlight.Interop;

/// <summary>
/// Win32 helpers for the custom bottom-right resize grip. WinUI 3 exposes no managed API for
/// interactive edge/corner sizing. The original implementation replayed the classic
/// <c>ReleaseCapture()</c> + <c>WM_NCLBUTTONDOWN</c> trick to enter the OS''s own modal sizing
/// loop, but in a WinUI <c>PointerPressed</c> context that loop was unreliable: it took ~1.1s to
/// pick up the first mouse movement and frequently required a second click to release the corner,
/// because the synthesized WinUI pointer event is out of sync with the underlying Win32 button
/// state. We instead track the cursor ourselves and drive <c>AppWindow.Resize</c> directly — this
/// helper just reads the absolute screen-space cursor position so the caller can compute deltas.
/// </summary>
internal static partial class ResizeGripInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Reads the current cursor position in absolute (virtual-screen) physical pixels.
    /// Returns <see langword="false"/> if the OS call fails, in which case the out params are 0.
    /// Physical-pixel screen coordinates map 1:1 to <c>AppWindow.Size</c>, so a drag delta needs
    /// no DPI conversion.
    /// </summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out POINT p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }
}
