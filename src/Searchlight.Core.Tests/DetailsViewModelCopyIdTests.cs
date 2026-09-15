using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Verifies the copy-id command records the full session GUID to the injected
/// clipboard and reports success, and no-ops safely when no session is loaded.
/// Exercises the same <see cref="IClipboardService"/> seam the WinUI host uses.
/// </summary>
public sealed class DetailsViewModelCopyIdTests
{
    private static DetailsViewModel BuildViewModel(out MockClipboardService clipboard)
    {
        clipboard = new MockClipboardService();
        return new DetailsViewModel(new MockSessionDataSource(), new MockResumeLauncher(), clipboard);
    }

    [Fact]
    public void CopyId_CannotExecute_WhenNoSession()
    {
        DetailsViewModel vm = BuildViewModel(out MockClipboardService clipboard);

        Assert.False(vm.CopyIdCommand.CanExecute(null));

        vm.CopyIdCommand.Execute(null);

        Assert.Null(clipboard.LastCopiedText);
    }

    [Fact]
    public void CopyId_CopiesFullGuid_AndReportsSuccess()
    {
        DetailsViewModel vm = BuildViewModel(out MockClipboardService clipboard);

        // Load the first detailed synthetic session (deterministic mock fixture).
        var source = new MockSessionDataSource();
        var first = source.LoadAll()[0];
        vm.Load(first);

        Assert.True(vm.CopyIdCommand.CanExecute(null));

        vm.CopyIdCommand.Execute(null);

        Assert.Equal(vm.Session!.Id, clipboard.LastCopiedText);
        Assert.Equal("Session id copied to clipboard.", vm.StatusMessage);
    }

    [Fact]
    public async Task OverviewCopyButton_UsesExistingCommandAndTracksSessionSelection()
    {
        DetailsViewModel vm = BuildViewModel(out MockClipboardService clipboard);
        var sessions = new MockSessionDataSource().LoadAll();
        foreach (var session in sessions.Take(2))
        {
            vm.Load(session);
            // Copy is available immediately, without waiting for the metadata read.
            var copyField = Assert.Single(vm.MetadataGroups.SelectMany(g => g.Fields), field => field.CanCopy);
            Assert.Equal("Session ID", copyField.Label);
            Assert.Contains(copyField, vm.MetadataGroups.Single(g => g.Title == "Overview").Fields);
            Assert.Equal(session.Id, copyField.Value);
            Assert.Same(vm.CopyIdCommand, copyField.CopyCommand);
            Assert.True(copyField.CopyCommand!.CanExecute(null));
            copyField.CopyCommand.Execute(null);
            Assert.Equal(session.Id, clipboard.LastCopiedText);
            await vm.CurrentLoad;
        }
        vm.Load(null);
        Assert.Empty(vm.MetadataGroups);
        Assert.False(vm.CopyIdCommand.CanExecute(null));
    }
}
