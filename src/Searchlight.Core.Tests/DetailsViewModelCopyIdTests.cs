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
}
