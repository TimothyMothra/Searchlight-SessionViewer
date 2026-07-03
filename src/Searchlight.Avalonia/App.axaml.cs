using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Searchlight.Abstractions;
using Searchlight.Avalonia.Services;
using Searchlight.Composition;
using Searchlight.ViewModels;

namespace Searchlight.Avalonia;

/// <summary>
/// Avalonia application and composition root. Builds the DI container over the
/// platform-neutral Core (Claude Code data source, or the synthetic mock when
/// launched with <c>--demo</c>) plus this host's platform services.
/// </summary>
public sealed class App : Application
{
    private ServiceProvider? _services;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool demo = desktop.Args?.Contains("--demo") == true;

            var clipboard = new AvaloniaClipboardService();

            var services = new ServiceCollection();
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
            if (!demo)
            {
                services.AddSingleton<IClipboardService>(clipboard);
                services.AddSingleton<IResumeLauncher, TerminalResumeLauncher>();
            }

            services.AddClaudeCore(useMock: demo);
            _services = services.BuildServiceProvider();

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };
            clipboard.AttachTo(window);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
