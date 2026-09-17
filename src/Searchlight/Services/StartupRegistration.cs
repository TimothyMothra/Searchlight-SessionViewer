using System.Runtime.InteropServices;
using Microsoft.Win32;
using Searchlight.Abstractions;
using Windows.ApplicationModel;

namespace Searchlight.Services;

internal sealed class StartupRegistration : IStartupRegistration
{
    private const string TaskId = "SearchlightStartup";
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Searchlight.lnk");
    private static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Searchlight", "app", "Searchlight.exe");

    public async Task<StartupRegistrationState> GetStateAsync()
    {
        if (AppIdentity.Channel != "Production")
            return new(false, false, "Auto-start is available only in Production.");
        if (!AppIdentity.SupportsStartup)
            return new(false, false, "Auto-start is not supported by this package.");
        if (AppIdentity.IsPackaged)
            return FromTaskState((await StartupTask.GetAsync(TaskId)).State);
        if (!string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase))
            return new(false, false, "Install Searchlight before configuring auto-start.");
        return await ReadShortcutStateAsync();
    }

    private static Task<StartupRegistrationState> ReadShortcutStateAsync()
    {
        var completion = new TaskCompletionSource<StartupRegistrationState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // ASSUMPTION: WScript.Shell is apartment-threaded. Create, use, and release
        // its COM objects on a worker STA, never through the UI apartment.
        var worker = new Thread(() =>
        {
            try { completion.SetResult(ReadShortcutState()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Searchlight startup-state reader",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    public async Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
    {
        StartupRegistrationState state = await GetStateAsync();
        if (!state.CanChange || state.IsEnabled == enabled) return state;
        if (AppIdentity.IsPackaged)
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled) return FromTaskState(await task.RequestEnableAsync());
            task.Disable();
            return FromTaskState(task.State);
        }
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            WithShortcut(shortcut =>
            {
                shortcut.TargetPath = InstalledExe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(InstalledExe);
                shortcut.Arguments = "";
                shortcut.Description = "Searchlight (run at login)";
                shortcut.IconLocation = $"{InstalledExe},0";
                shortcut.Save();
                return true;
            });
        }
        else
        {
            File.Delete(ShortcutPath);
        }
        return ReadShortcutState();
    }

    private static StartupRegistrationState ReadShortcutState()
    {
        if (!File.Exists(ShortcutPath)) return new(false, true);
        string target = WithShortcut(shortcut => (string)shortcut.TargetPath);
        if (!string.Equals(target, InstalledExe, StringComparison.OrdinalIgnoreCase))
            return new(false, false, "The Searchlight startup shortcut points to another location. Manage it in Windows startup settings.");

        // ASSUMPTION: Windows' StartupApproved status byte is 2/6 when enabled and
        // 3/7 when disabled. Read it only; unknown states fail closed, and we never
        // overwrite this Windows-owned record to bypass a user/policy decision.
        using RegistryKey? approval = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
        object? value = approval?.GetValue("Searchlight.lnk");
        if (value is null) return new(true, true);
        if (value is byte[] { Length: >= 1 } bytes)
        {
            if (bytes[0] is 2 or 6) return new(true, true);
            if (bytes[0] is 3 or 7)
                return new(false, false, "Windows has disabled auto-start. Re-enable Searchlight in Windows startup settings.");
        }
        return new(false, false, "Windows controls this startup entry. Manage it in Windows startup settings.");
    }

    private static StartupRegistrationState FromTaskState(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(true, true),
        StartupTaskState.Disabled => new(false, true),
        StartupTaskState.DisabledByUser => new(false, false, "Windows has disabled auto-start. Re-enable Searchlight in Windows startup settings."),
        StartupTaskState.DisabledByPolicy => new(false, false, "Your organization's policy disables auto-start."),
        StartupTaskState.EnabledByPolicy => new(true, false, "Your organization's policy requires auto-start."),
        _ => throw new InvalidOperationException($"Unsupported startup state: {state}."),
    };

    private static T WithShortcut<T>(Func<dynamic, T> action)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new NotSupportedException("Windows shortcut management is unavailable.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows shortcut manager.");
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(ShortcutPath);
            return action(shortcut);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
