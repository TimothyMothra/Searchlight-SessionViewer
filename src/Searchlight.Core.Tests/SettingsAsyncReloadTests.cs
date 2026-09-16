using System.Threading.Channels;
using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

[Collection("Events reader")]
public sealed class SettingsAsyncReloadTests : IDisposable
{
    // ASSUMPTION: tests own synthetic storage and explicitly pump a UI context;
    // real user settings and timing-sensitive sleeps are not needed.
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), $".async-settings-test-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task LockedReadReturnsImmediatelyAndConcurrentRefreshesShareTheTask()
    {
        var settings = new SettingsService(SettingsPath);
        Task<bool> first;
        using (SharedStateIO.Lock(_directory))
        {
            first = settings.ReloadAsync();
            Assert.False(first.IsCompleted);
            Assert.Same(first, settings.ReloadAsync());
            File.WriteAllText(SettingsPath, """{"AppendYolo":true}""");
            Assert.False(settings.Current.AppendYolo);
        }

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(settings.Current.AppendYolo);
        File.WriteAllText(SettingsPath, """{"AppendYolo":false}""");
        Assert.True(await settings.ReloadAsync());
        Assert.False(settings.Current.AppendYolo);
    }

    [Fact]
    public async Task AppliesExternalStateOnlyOnCapturedContextWithReloadGuard()
    {
        var settings = new SettingsService(SettingsPath);
        var context = new QueuedContext();
        List<string?> changes = [];
        settings.Current.PropertyChanged += (_, e) =>
        {
            Assert.Same(context, SynchronizationContext.Current);
            Assert.True(settings.IsReloading);
            changes.Add(e.PropertyName);
        };
        Task<bool> reload;
        using (SharedStateIO.Lock(_directory))
        {
            File.WriteAllText(SettingsPath, """{"RunElevated":true,"AppendYolo":true}""");
            reload = context.Invoke(settings.ReloadAsync);
        }

        Action apply = await context.NextAsync();
        Assert.False(settings.Current.RunElevated);
        Assert.Empty(changes);
        context.Invoke(apply);
        Assert.True(await reload);
        Assert.True(settings.Current.RunElevated);
        Assert.Contains(nameof(AppSettings.AppendYolo), changes);
        Assert.False(settings.IsReloading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleSnapshotCannotUndoInterveningSaveOrSynchronousReload(bool synchronousReload)
    {
        var settings = new SettingsService(SettingsPath);
        var context = new QueuedContext();
        Task<bool> reload;
        using (SharedStateIO.Lock(_directory))
            reload = context.Invoke(settings.ReloadAsync);

        Action applyStaleSnapshot = await context.NextAsync();
        context.Invoke(() =>
        {
            if (synchronousReload)
            {
                File.WriteAllText(SettingsPath, """{"AppendYolo":true}""");
                Assert.True(settings.Reload());
            }
            else
            {
                settings.Current.AppendYolo = true;
                settings.Current.PinnedSessionIds = ["local-pin"];
            }
        });
        context.Invoke(applyStaleSnapshot);
        while (!reload.IsCompleted) context.Invoke(await context.NextAsync());

        Assert.True(await reload);
        Assert.True(settings.Current.AppendYolo);
        Assert.True(new SettingsService(SettingsPath).Current.AppendYolo);
        if (!synchronousReload) Assert.Equal(["local-pin"], settings.Current.PinnedSessionIds);
    }

    [Fact]
    public async Task FailedRefreshReportsAndPreservesMemoryAndDisk()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.AppendYolo = true;
        File.WriteAllText(SettingsPath, "{invalid");
        int failures = 0;
        settings.PersistenceFailed += (_, _) => failures++;

        Assert.False(await settings.ReloadAsync());
        Assert.True(settings.Current.AppendYolo);
        Assert.Equal("{invalid", File.ReadAllText(SettingsPath));
        Assert.Equal(1, failures);
        Assert.Contains("Could not reload", settings.PersistenceNotice);
    }

    [Fact]
    public async Task RefreshPreservesUnsavedEditsWhileMergingOtherExternalFields()
    {
        var settings = new SettingsService(SettingsPath);
        File.WriteAllText(SettingsPath, "{invalid");
        settings.Current.AppendYolo = true;
        Assert.NotEmpty(settings.PersistenceNotice);
        File.WriteAllText(SettingsPath, """{"RunElevated":true,"Future":"keep"}""");

        Assert.True(await settings.ReloadAsync());
        Assert.True(settings.Current.AppendYolo);
        Assert.True(settings.Current.RunElevated);
        Assert.True(settings.Save());
        Assert.Contains("\"Future\": \"keep\"", File.ReadAllText(SettingsPath));
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!_callbacks.Writer.TryWrite(() => callback(state)))
                throw new InvalidOperationException("The test UI queue is closed.");
        }

        public Task<Action> NextAsync() =>
            _callbacks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public T Invoke<T>(Func<T> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public void Invoke(Action action) => Invoke(() => { action(); return true; });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
