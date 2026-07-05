using Avalonia;

namespace Searchlight.Avalonia;

/// <summary>Entry point for the cross-platform Avalonia host.</summary>
public static class Program
{
    /// <summary>
    /// Starts the app. <c>--source=claude|copilot|both</c> picks the session
    /// store(s); without it the host auto-detects which of <c>~/.claude</c> /
    /// <c>~/.copilot</c> exist. <c>--demo</c> runs against synthetic sessions.
    /// </summary>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Configures the Avalonia app builder (also used by the previewer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
