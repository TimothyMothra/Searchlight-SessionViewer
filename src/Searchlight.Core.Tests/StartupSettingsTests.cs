using Searchlight.Abstractions;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class StartupSettingsTests
{
    [Fact]
    public async Task RepeatedLoadsDoNotOverlapOrReenableControlsBeforeReadCompletes()
    {
        var pending = new TaskCompletionSource<StartupRegistrationState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new FakeRegistration { PendingRead = pending.Task };
        var vm = new StartupSettingsViewModel(registration);

        Task first = vm.LoadAsync();
        await vm.LoadAsync();
        Assert.Equal(1, registration.Reads);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanChange);

        pending.SetResult(new(true, true));
        await first;
        Assert.True(vm.CanChange);
        Assert.True(vm.IsEnabled);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadingAndRefreshingReflectOsStateWithoutWriting()
    {
        var registration = new FakeRegistration { State = new(true, true) };
        var vm = new StartupSettingsViewModel(registration);
        await vm.LoadAsync();
        Assert.True(vm.IsEnabled);
        Assert.True(vm.CanChange);
        registration.State = new(false, true);
        await vm.LoadAsync();
        Assert.False(vm.IsEnabled);
        Assert.Equal(0, registration.Writes);
    }

    [Fact]
    public async Task ToggleChangesOnlyRegistrationAndAvoidsRedundantWrites()
    {
        var registration = new FakeRegistration { State = new(true, true) };
        var vm = new StartupSettingsViewModel(registration);
        await vm.LoadAsync();
        await vm.SetEnabledAsync(false);
        Assert.False(vm.IsEnabled);
        Assert.Equal(1, registration.Writes);
        await vm.SetEnabledAsync(false);
        Assert.Equal(1, registration.Writes);
        await vm.SetEnabledAsync(true);
        Assert.True(vm.IsEnabled);
        Assert.Equal(2, registration.Writes);
    }

    [Fact]
    public async Task OsManagedStartupCannotBeOverridden()
    {
        var registration = new FakeRegistration { State = new(false, false, "Disabled by Windows") };
        var vm = new StartupSettingsViewModel(registration);
        await vm.LoadAsync();
        await vm.SetEnabledAsync(true);
        Assert.False(vm.CanChange);
        Assert.False(vm.IsEnabled);
        Assert.Equal("Disabled by Windows", vm.Message);
        Assert.Equal(0, registration.Writes);
    }

    [Fact]
    public async Task FailedChangeRestoresPriorStateAndReportsFailure()
    {
        var registration = new FakeRegistration { State = new(true, true), FailWrite = true };
        var vm = new StartupSettingsViewModel(registration);
        await vm.LoadAsync();
        int notifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsEnabled)) notifications++; };
        await vm.SetEnabledAsync(false);
        Assert.True(vm.IsEnabled);
        Assert.True(vm.CanChange);
        Assert.Contains("access denied", vm.Message);
        Assert.True(notifications > 0);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadingDisablesChangesUntilTheActualStateIsKnown()
    {
        var pending = new TaskCompletionSource<StartupRegistrationState>();
        var registration = new FakeRegistration { PendingRead = pending.Task };
        var vm = new StartupSettingsViewModel(registration);
        Task loading = vm.LoadAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanChange);
        await vm.SetEnabledAsync(true);
        Assert.Equal(0, registration.Writes);
        pending.SetResult(new(true, true));
        await loading;
        Assert.True(vm.IsEnabled);
        Assert.True(vm.CanChange);
    }

    [Fact]
    public async Task OsCanDeclineAnEnableRequest()
    {
        var registration = new FakeRegistration
        {
            State = new(false, true),
            WriteResult = new(false, false, "Blocked by policy"),
        };
        var vm = new StartupSettingsViewModel(registration);
        await vm.LoadAsync();
        await vm.SetEnabledAsync(true);
        Assert.False(vm.IsEnabled);
        Assert.False(vm.CanChange);
        Assert.Equal("Blocked by policy", vm.Message);
    }

    // ASSUMPTION: startup tests never modify this machine's real startup registrations.
    private sealed class FakeRegistration : IStartupRegistration
    {
        public StartupRegistrationState State = new(false, true);
        public StartupRegistrationState? WriteResult;
        public Task<StartupRegistrationState>? PendingRead;
        public int Writes;
        public int Reads;
        public bool FailWrite;
        public Task<StartupRegistrationState> GetStateAsync()
        {
            Reads++;
            return PendingRead ?? Task.FromResult(State);
        }
        public Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
        {
            if (FailWrite) throw new UnauthorizedAccessException("access denied");
            Writes++;
            State = WriteResult ?? new(enabled, true);
            return Task.FromResult(State);
        }
    }
}
