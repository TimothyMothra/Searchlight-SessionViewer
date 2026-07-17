using Searchlight.Interop;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace Searchlight.Views;

/// <summary>
/// Hosts the full two-pane list/details UI. Implemented as a <see cref="UserControl"/>
/// (a <c>FrameworkElement</c>) so that <c>x:Bind</c> converter lookups resolve correctly —
/// WinUI 3's <c>Window</c> is not a <c>FrameworkElement</c> and cannot serve as a converter
/// lookup root for top-level bindings.
/// </summary>
public sealed partial class MainView : UserControl
{
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The custom title bar drag strip. The host <see cref="MainWindow"/> passes this
    /// to <c>SetTitleBar</c> after extending content into the title bar so the region
    /// stays draggable.
    /// </summary>
    public FrameworkElement TitleBarElement => AppTitleBar;

    /// <summary>
    /// The app icon shown in the custom title strip. Loaded by absolute path because
    /// the app is unpackaged (<c>WindowsPackageType=None</c>), so <c>ms-appx:///</c> is
    /// unreliable. Mirrors the tray-icon load pattern in <c>App.xaml.cs</c>.
    /// </summary>
    public ImageSource AppIconSource { get; } =
        new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app_32.png")))
        {
            DecodePixelWidth = 36,
        };

    /// <summary>
    /// True when the app is running elevated (as administrator). Bound once (OneTime) by the
    /// title-strip shield icon so the user can tell at a glance the app is elevated — mirroring
    /// the UAC shield Windows Terminal shows. Elevation cannot change during the process
    /// lifetime, so a get-only OneTime value is sufficient.
    /// </summary>
    public bool IsElevated { get; } = ElevationHelper.IsElevated();

    /// <summary>
    /// The host window's native HWND, injected by <see cref="MainWindow"/> after the content
    /// is set. Used by the bottom-right resize grip to hand off a native window-resize loop
    /// (WinUI 3 exposes no managed API to programmatically start an edge/corner resize).
    /// </summary>
    public nint WindowHandle { get; set; }

    public MainView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Wire the grouped list in code-behind: an x:Bind CollectionViewSource
        // Source inside UserControl.Resources is unreliable in WinUI 3, so build
        // the grouped view here. The CVS stays live against the observable
        // SessionGroups collection, so filter/refresh rebuilds flow through.
        CollectionViewSource groupedSource = new()
        {
            IsSourceGrouped = true,
            Source = ViewModel.SessionGroups,
        };
        SessionList.ItemsSource = groupedSource.View;
    }

    private void OnSessionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Double-click a row to resume that session.
        if (ViewModel.Details.ResumeCommand.CanExecute(null))
        {
            ViewModel.Details.ResumeCommand.Execute(null);
        }
    }

    /// <summary>
    /// Clicking a tick in the compact rail scrolls the list straight to that group's
    /// first session — the rail acts like a jump-to-group second scrollbar.
    /// </summary>
    private void OnRailTickClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionGroup group } && group.Count > 0)
        {
            SessionList.ScrollIntoView(group[0], ScrollIntoViewAlignment.Leading);
        }
    }

    // Command-bound buttons inside a Flyout don't auto-close it, so the rename actions
    // are wired as Click handlers that run the command then dismiss the flyout.
    private void OnRenameSaveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RenameCommand.Execute(null);
        RenameFlyout.Hide();
    }

    private void OnRenameResetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetNameCommand.Execute(null);
        RenameFlyout.Hide();
    }

    /// <summary>
    /// Group headers don't forward mouse-wheel input to the list's ScrollViewer on their
    /// own, so hovering a header and scrolling does nothing. Translate the wheel delta into
    /// a manual vertical scroll so the wheel keeps working over the group labels.
    /// </summary>
    private void OnGroupHeaderPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        ScrollViewer? scrollViewer = FindDescendantScrollViewer(SessionList);
        if (scrollViewer is null)
        {
            return;
        }

        // Positive wheel delta = wheel up = scroll toward the top (smaller offset).
        int delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        scrollViewer.ChangeView(null, scrollViewer.VerticalOffset - delta, null, disableAnimation: false);
        e.Handled = true;
    }

    // Manual bottom-right resize state. We drive AppWindow.Resize ourselves from pointer moves
    // rather than the OS's WM_NCLBUTTONDOWN modal loop, which in this WinUI context lagged ~1.1s
    // before the first resize and often needed a second click to release the corner.
    private bool _resizing;
    private int _resizeStartCursorX;
    private int _resizeStartCursorY;
    private int _resizeStartWidth;
    private int _resizeStartHeight;
    private Microsoft.UI.Windowing.AppWindow? _resizeAppWindow;

    // Smallest window the grip will resize to (physical pixels). Prevents the user from
    // collapsing the window into an unusable sliver.
    private const int MinResizeWidth = 480;
    private const int MinResizeHeight = 360;

    private Microsoft.UI.Windowing.AppWindow? ResolveAppWindow()
    {
        if (_resizeAppWindow is not null)
        {
            return _resizeAppWindow;
        }

        nint hwnd = WindowHandle;
        if (hwnd == 0)
        {
            return null;
        }

        Microsoft.UI.WindowId id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _resizeAppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        return _resizeAppWindow;
    }

    /// <summary>
    /// Bottom-right resize grip: on left-button press, start a manual resize. We capture the
    /// pointer and record the absolute cursor position and current window size; subsequent
    /// PointerMoved events translate the cursor delta directly into AppWindow.Resize calls.
    /// Screen-space physical pixels map 1:1 to AppWindow.Size, so no DPI math is needed.
    /// </summary>
    private void OnResizeGripPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only the left mouse button initiates a resize; ignore right/middle/pen/touch contacts
        // that aren't a primary press so the grip doesn't hijack other gestures.
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Microsoft.UI.Windowing.AppWindow? appWindow = ResolveAppWindow();
        if (appWindow is null || !ResizeGripInterop.TryGetCursorPos(out int cx, out int cy))
        {
            return;
        }

        _resizing = true;
        _resizeStartCursorX = cx;
        _resizeStartCursorY = cy;
        _resizeStartWidth = appWindow.Size.Width;
        _resizeStartHeight = appWindow.Size.Height;

        // Capture the pointer so we keep receiving PointerMoved even when the cursor leaves the
        // 28x28 grip (which it immediately does as the window grows/shrinks under the corner).
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
        Searchlight.App.LogVerbose($"resize: grip PointerPressed (first click) start={_resizeStartWidth}x{_resizeStartHeight} cursor={cx},{cy}");
    }

    /// <summary>
    /// While the grip owns the pointer, translate the absolute cursor delta into a new window
    /// size and apply it immediately. Runs on every pointer move for smooth, lag-free resizing.
    /// </summary>
    private void OnResizeGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || _resizeAppWindow is null)
        {
            return;
        }

        if (!ResizeGripInterop.TryGetCursorPos(out int cx, out int cy))
        {
            return;
        }

        int newWidth = System.Math.Max(MinResizeWidth, _resizeStartWidth + (cx - _resizeStartCursorX));
        int newHeight = System.Math.Max(MinResizeHeight, _resizeStartHeight + (cy - _resizeStartCursorY));
        _resizeAppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, newHeight));
        e.Handled = true;
    }

    private void OnResizeGripReleased(object sender, PointerRoutedEventArgs e)
    {
        EndResize(sender, "PointerReleased (button up)");
        e.Handled = true;
    }

    private void OnResizeGripCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndResize(sender, "PointerCaptureLost");
    }

    private void EndResize(object sender, string reason)
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        ((UIElement)sender).ReleasePointerCaptures();
        Searchlight.App.LogVerbose($"resize: end ({reason})");
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            ScrollViewer? nested = FindDescendantScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
