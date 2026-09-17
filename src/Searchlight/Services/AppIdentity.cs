using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Searchlight.Services;

internal static partial class AppIdentity
{
    // ASSUMPTION: channels are stable across package upgrades; build/version must
    // not participate in the single-instance name.
    public static string Channel { get; } = typeof(AppIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SearchlightChannel").Value
        ?? throw new InvalidOperationException("The build has no Searchlight channel.");

    public static string DisplayName => Channel == "Dev" ? "Searchlight Dev" : "Searchlight";
    public static string InstancePrefix => $"Searchlight.{Channel}";
    public static string LogFileName => $"Searchlight.{Channel}.log";

    public static bool IsPackaged { get; } = HasPackageIdentity();

    // ASSUMPTION: MSIX builds set this metadata together with their manifest.
    // Unpackaged Production continues to manage its existing startup shortcut.
    public static bool SupportsStartup => Channel == "Production" && (!IsPackaged || StartupTaskEnabled);

    private static bool StartupTaskEnabled { get; } = bool.Parse(typeof(AppIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SearchlightStartupTaskEnabled").Value
        ?? throw new InvalidOperationException("The build has no startup-task capability metadata."));

    // ASSUMPTION: WinRT image loading requires application URIs in MSIX, while
    // the unpackaged host needs absolute file URIs.
    public static Uri AssetUri(string fileName) => IsPackaged
        ? new Uri($"ms-appx:///Assets/{fileName}")
        : new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", fileName));

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, 0);
        return result switch
        {
            122 => true, // ERROR_INSUFFICIENT_BUFFER: a package name is available.
            15700 => false, // APPMODEL_ERROR_NO_PACKAGE.
            _ => throw new Win32Exception(result),
        };
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, nint fullName);
}
