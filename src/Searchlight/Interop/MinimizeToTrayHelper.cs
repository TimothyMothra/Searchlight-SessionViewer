using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Searchlight.Interop;

/// <summary>
/// Makes a WinUI 3 window "minimize to the tray" instead of to the taskbar. The
/// <see cref="Microsoft.UI.Windowing.AppWindow.Changed"/> event fires only AFTER the
/// window has already animated to a taskbar button, so hiding from there still leaves
/// a visible minimize. Instead we subclass the window and intercept the Win32
/// <c>WM_SYSCOMMAND</c> / <c>SC_MINIMIZE</c> message BEFORE the default minimize
/// happens, suppress it, and invoke a callback that hides the window to the tray —
/// mirroring the close-to-tray behavior exactly.
/// </summary>
/// <remarks>
/// This app owns a single window, so the original window-proc pointer and the hide
/// callback are stored in static fields. The replacement proc is an
/// <see cref="UnmanagedCallersOnlyAttribute"/> static (required to hand a raw function
/// pointer to Win32); it runs on the UI thread's message pump, so invoking the managed
/// hide callback there is safe.
/// </remarks>
internal static unsafe partial class MinimizeToTrayHelper
{
    // Index into the window's extra data for its window procedure.
    private const int GWLP_WNDPROC = -4;

    private const uint WM_SYSCOMMAND = 0x0112;

    // The low 4 bits of wParam are reserved by the OS, so command codes are masked
    // with 0xFFF0 before comparison.
    private const int SC_MINIMIZE = 0xF020;
    private const nint SC_MASK = 0xFFF0;

    private static nint s_originalWndProc;
    private static Action? s_onMinimize;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Subclasses <paramref name="window"/> so a minimize request hides it to the tray
    /// via <paramref name="onMinimize"/> instead of minimizing to the taskbar. Call
    /// once, after the window is created. Safe no-op if the handle can't be resolved.
    /// </summary>
    public static void Enable(Window window, Action onMinimize)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == 0)
        {
            return;
        }

        s_onMinimize = onMinimize;

        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> newProc = &WndProc;
        s_originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, (nint)newProc);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_SYSCOMMAND && (wParam & SC_MASK) == SC_MINIMIZE)
        {
            // Suppress the default minimize (no taskbar button, no animation) and hide
            // to the tray instead, matching the close-to-tray behavior.
            s_onMinimize?.Invoke();
            return 0;
        }

        return CallWindowProc(s_originalWndProc, hWnd, msg, wParam, lParam);
    }
}
