using Searchlight.Services;
using Searchlight.ViewModels;
using Searchlight.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Searchlight;

/// <summary>
/// Main window shell — Mica backdrop hosting the <see cref="MainView"/> UserControl
/// (the actual two-pane list/details UI bound to <see cref="MainViewModel"/>).
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private readonly MainView _root;
    private readonly DispatcherQueue _dispatcher;

    // Kept alive for the lifetime of the window: UISettings raises
    // ColorValuesChanged on a background thread when the user flips the OS theme.
    private readonly UISettings _uiSettings = new();

    private bool _loaded;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Searchlight: Historical Session Viewer";

        // Mica backdrop for the modern Fluent look.
        SystemBackdrop = new MicaBackdrop();

        // Remove the default (bright white) system title bar and let the dark Mica
        // content fill that region. This also drops the duplicate window-title text —
        // the app label now lives once, in the custom title-bar strip below.
        ExtendsContentIntoTitleBar = true;

        // Host the UI in a FrameworkElement so x:Bind converter lookups resolve.
        _root = new MainView(viewModel);
        Content = _root;

        // Give the resize-grip its window handle so it can hand off to the native
        // WM_NCLBUTTONDOWN/HTBOTTOMRIGHT resize loop.
        _root.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // The custom top strip becomes the draggable title-bar region.
        SetTitleBar(_root.TitleBarElement);

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Follow the system dark/light setting instead of defaulting to a white
        // titlebar, and re-apply live when the user toggles the OS theme.
        ApplyTheme();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        Activated += OnActivated;
        Closed += OnClosed;
        SizeChanged += OnSizeChangedLog;
    }

    /// <summary>
    /// Logs each window size change so a grip-drag session can be reconstructed from
    /// the temp-file log: the first SizeChanged after a "grip PointerPressed" line is
    /// the moment the resize actually starts (used to measure the perceived lag).
    /// </summary>
    private void OnSizeChangedLog(object sender, WindowSizeChangedEventArgs args)
    {
        App.LogVerbose($"resize: SizeChanged -> {args.Size.Width:0}x{args.Size.Height:0}");
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Kick off the initial load exactly once, on first activation.
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (ViewModel.LoadCommand.CanExecute(null))
        {
            ViewModel.LoadCommand.Execute(null);
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        // ColorValuesChanged fires off the UI thread; marshal back before touching XAML.
        _dispatcher.TryEnqueue(ApplyTheme);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    /// <summary>
    /// Aligns the window content theme (and therefore the Mica tint) plus the
    /// standard titlebar caption buttons with the current OS apps theme.
    /// </summary>
    private void ApplyTheme()
    {
        ElementTheme theme = SystemThemeHelper.GetAppsTheme();
        _root.RequestedTheme = theme;
        ApplyTitleBarTheme(theme == ElementTheme.Dark);
    }

    /// <summary>
    /// Themes the standard (non-extended) titlebar caption buttons for the given
    /// mode. The background is left transparent so the Mica backdrop shows through
    /// the caption; only the glyph/hover colors need to flip with the theme.
    /// </summary>
    private void ApplyTitleBarTheme(bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;

        Color foreground = dark ? Colors.White : Colors.Black;
        Color inactiveForeground = dark
            ? Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0)
            : Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E);
        Color hoverBackground = dark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        Color pressedBackground = dark
            ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x25, 0x00, 0x00, 0x00);

        // Keep the caption background transparent so Mica renders behind it.
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }
}
