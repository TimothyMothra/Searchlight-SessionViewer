using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using Searchlight.Abstractions;
using Searchlight.Diagnostics;

namespace Searchlight.ViewModels;

public sealed partial class StartupSettingsViewModel(IStartupRegistration registration) : ObservableObject
{
    private StartupRegistrationState _state = new(false, false);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    private bool _isBusy;

    [ObservableProperty]
    private string _message = string.Empty;

    public bool IsEnabled => _state.IsEnabled;
    public bool CanChange => _state.CanChange && !IsBusy;

    public Task LoadAsync() => UpdateAsync(registration.GetStateAsync);

    public Task SetEnabledAsync(bool enabled)
    {
        if (!CanChange || enabled == IsEnabled)
        {
            OnPropertyChanged(nameof(IsEnabled));
            return Task.CompletedTask;
        }
        return UpdateAsync(() => registration.SetEnabledAsync(enabled));
    }

    private async Task UpdateAsync(Func<Task<StartupRegistrationState>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _state = await operation();
            Message = _state.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or COMException or Win32Exception or SecurityException or ArgumentException or NotSupportedException)
        {
            Message = $"Could not update the startup setting: {ex.Message}";
            CoreLog.Write(Message);
        }
        finally
        {
            // ASSUMPTION: OS state is authoritative. Re-notify even on failure so
            // a toggle is restored instead of displaying a change that was not saved.
            IsBusy = false;
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(CanChange));
        }
    }
}
