using System.Collections.Specialized;
using Searchlight.Diagnostics;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

[Collection("Events reader")]
public sealed class ViewportMetadataTests
{
    [Theory]
    [InlineData(-100, 100, false)]
    [InlineData(-100, 101, true)]
    [InlineData(0, 400, true)]
    [InlineData(399, 100, true)]
    [InlineData(400, 100, false)]
    [InlineData(600, 100, false)]
    [InlineData(0, 0, false)]
    public void ViewportEntry_RequiresPositiveOverlap(double y, double height, bool expected) =>
        Assert.Equal(expected, MetadataViewport.Intersects(600, 400, 0, y, 600, height));

    [Fact]
    public void HiddenUnmeasuredOrHorizontallyClippedSections_DoNotMaterialize()
    {
        Assert.False(MetadataViewport.Intersects(0, 400, 0, 0, 600, 400));
        Assert.False(MetadataViewport.Intersects(600, 0, 0, 0, 600, 400));
        Assert.False(MetadataViewport.Intersects(600, 400, 0, 0, 0, 400));
        Assert.False(MetadataViewport.Intersects(600, 400, 600, 0, 100, 400));
        Assert.False(MetadataViewport.Intersects(600, 400, -100, 0, 100, 400));
        Assert.False(MetadataViewport.Intersects(600, 400, double.NaN, 0, 100, 400));
    }

    [Fact]
    public void OnlyOverviewStartsMaterialized_AndScrollMaterializationIsOneWay()
    {
        var previous = CoreLog.Sink;
        try
        {
            CoreLog.Sink = null;
            var groups = SessionMetadata.Create(new MockSessionDataSource().LoadAll()[0]);
            Assert.True(groups[0].IsOverview);
            Assert.True(groups[0].IsMaterialized);
            Assert.All(groups.Skip(1), group =>
            {
                Assert.False(group.IsMaterialized);
                Assert.True(group.PlaceholderHeight > 0);
                Assert.Equal(0, group.MaterializationRequestedAt);
            });
            int changes = 0;
            var workspace = groups[1];
            workspace.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(workspace.IsMaterialized)) changes++; };
            Assert.True(workspace.Materialize());
            Assert.False(workspace.Materialize());
            Assert.Equal(1, changes);
            Assert.Equal(0, workspace.MaterializationRequestedAt);
            Assert.False(groups[2].IsMaterialized);
            Assert.False(groups[3].IsMaterialized);
        }
        finally { CoreLog.Sink = previous; }
    }

    [Fact]
    public async Task Refresh_PreservesRealizedSectionsAndReusesUnchangedGroups()
    {
        var source = new MockSessionDataSource();
        var vm = new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService());
        var session = source.LoadAll()[0];
        vm.Load(session);
        await vm.CurrentLoad;
        SessionMetadataGroup[] original = [.. vm.MetadataGroups];
        original[1].Materialize();
        int replacements = 0;
        vm.MetadataGroups.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Replace) replacements++; };
        vm.Load(session with { CustomName = "UI-only rename" }, refresh: true);
        await vm.CurrentLoad;
        Assert.Equal(0, replacements);
        for (int i = 0; i < original.Length; i++) Assert.Same(original[i], vm.MetadataGroups[i]);

        vm.Load(session with { Workspace = session.Workspace! with { Branch = "changed-native-branch" } }, refresh: true);
        await vm.CurrentLoad;
        Assert.Equal(2, replacements); // Overview and workspace; no event/storage re-creation.
        Assert.True(vm.MetadataGroups[1].IsMaterialized);
        Assert.Same(original[2], vm.MetadataGroups[2]);
        Assert.Same(original[3], vm.MetadataGroups[3]);
        Assert.False(vm.MetadataGroups[2].IsMaterialized);
        Assert.False(vm.MetadataGroups[3].IsMaterialized);

        vm.Load(source.LoadAll()[1]);
        await vm.CurrentLoad;
        Assert.True(vm.MetadataGroups[0].IsMaterialized);
        Assert.All(vm.MetadataGroups.Skip(1), group => Assert.False(group.IsMaterialized));
        vm.Load(null);
        Assert.Empty(vm.MetadataGroups);
    }

    [Fact]
    public async Task Monitoring_SeparatesReadProjectionAndPublicationWithoutLoggingFieldValues()
    {
        var previous = CoreLog.Sink;
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            CoreLog.Sink = messages.Enqueue;
            var source = new MockSessionDataSource();
            var vm = new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService());
            var session = source.LoadAll()[0] with
            {
                Workspace = source.LoadAll()[0].Workspace! with { Name = "PRIVATE_SESSION_MARKER", Cwd = "PRIVATE_PATH_MARKER" },
            };
            vm.Load(session);
            await vm.CurrentLoad;
            Assert.Contains(messages, message => message.StartsWith("DetailsRead request=") && message.Contains("worker_ms="));
            Assert.Contains(messages, message => message.StartsWith("DetailsProjection request=") && message.Contains("presentation="));
            Assert.Contains(messages, message => message.StartsWith("DetailsPublication request=") && message.Contains("presentation="));
            Assert.DoesNotContain(messages, message => message.Contains("PRIVATE_SESSION_MARKER") || message.Contains("PRIVATE_PATH_MARKER"));
            Assert.True(vm.MetadataGroups[0].MaterializationRequestedAt > 0);
            Assert.All(vm.MetadataGroups.Skip(1), group => Assert.Equal(0, group.MaterializationRequestedAt));
            vm.MetadataGroups[1].Materialize();
            Assert.True(vm.MetadataGroups[1].MaterializationRequestedAt > 0);
            Assert.True(vm.MetadataGroups[1].TakeMaterializationTimestamp() > 0);
            Assert.Equal(0, vm.MetadataGroups[1].TakeMaterializationTimestamp());
        }
        finally { CoreLog.Sink = previous; }
    }
}
