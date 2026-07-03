using Avalonia;

namespace Searchlight.Avalonia;

/// <summary>Entry point for the cross-platform Avalonia host.</summary>
public static class Program
{
    /// <summary>
    /// Starts the app. <c>--demo</c> runs against synthetic sessions instead of
    /// the live <c>~/.claude</c> store.
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
