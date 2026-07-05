using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Searchlight.Models;
using Searchlight.ViewModels;

namespace Searchlight.Avalonia;

/// <summary>
/// A recency group header row in the flattened session list. A class (not a
/// record) so headers compare by reference — the rail's jump-to-group lookup
/// must land on the clicked group even if two groups ever shared a title.
/// </summary>
public sealed class HeaderItem(string title)
{
    /// <summary>The group's display label.</summary>
    public string Title { get; } = title;
}

/// <summary>
/// Main window. The Core <see cref="MainViewModel"/> exposes grouped sessions as
/// <c>ObservableCollection&lt;SessionGroup&gt;</c> (built for WinUI's grouped
/// CollectionViewSource); Avalonia's ListBox has no native grouping, so this
/// code-behind flattens groups into a single header+row list and keeps selection
/// in sync both ways.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<object> _flat = [];
    private readonly Dictionary<SessionGroup, HeaderItem> _headerByGroup = [];
    private readonly List<SessionGroup> _observedGroups = [];
    private MainViewModel? _vm;
    private bool _rebuildQueued;
    private bool _syncingSelection;

    /// <summary>Creates the window and wires the flattened list.</summary>
    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _flat;
        SessionList.ContainerPrepared += OnContainerPrepared;
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    /// <summary>
    /// Header rows must not participate in selection or keyboard navigation —
    /// otherwise arrow keys get bounced back at every group boundary. Containers
    /// are recycled, so both states are set explicitly.
    /// </summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            bool isHeader = e.Index >= 0 && e.Index < _flat.Count && _flat[e.Index] is HeaderItem;
            item.IsEnabled = !isHeader;
            item.Focusable = !isHeader;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_vm?.LoadCommand.CanExecute(null) == true)
        {
            _vm.LoadCommand.Execute(null);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SessionGroups.CollectionChanged -= OnGroupsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.SessionGroups.CollectionChanged += OnGroupsChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        QueueRebuild();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueRebuild();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSession))
        {
            SyncSelectionFromViewModel();
        }
    }

    /// <summary>
    /// Coalesces the burst of CollectionChanged events from a group rebuild
    /// (Clear + N Adds) into one flatten pass after the view-model finishes.
    /// </summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        foreach (SessionGroup group in _observedGroups)
        {
            group.CollectionChanged -= OnGroupContentChanged;
        }

        _observedGroups.Clear();
        _flat.Clear();
        _headerByGroup.Clear();

        if (_vm is not null)
        {
            foreach (SessionGroup group in _vm.SessionGroups)
            {
                // Background enrichment replaces rows INSIDE a group
                // (group[i] = enriched) without touching the outer collection —
                // observe each group so those in-place upgrades reach _flat.
                group.CollectionChanged += OnGroupContentChanged;
                _observedGroups.Add(group);

                var header = new HeaderItem(group.Key);
                _headerByGroup[group] = header;
                _flat.Add(header);
                foreach (SessionInfo session in group)
                {
                    _flat.Add(session);
                }
            }
        }

        SyncSelectionFromViewModel();
    }

    /// <summary>
    /// Mirrors an in-place row replacement (a background-enriched session) into
    /// the flattened list, keeping the selection attached to the new instance.
    /// Anything more structural than a single-item Replace falls back to a full
    /// rebuild.
    /// </summary>
    private void OnGroupContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Replace
            && e.OldItems is [SessionInfo oldItem]
            && e.NewItems is [SessionInfo newItem])
        {
            int index = _flat.IndexOf(oldItem);
            if (index >= 0)
            {
                _flat[index] = newItem;
                SyncSelectionFromViewModel();
            }

            return;
        }

        QueueRebuild();
    }

    /// <summary>
    /// Clicking a tick in the compact rail scrolls the list straight to that
    /// group's header — the rail acts like a jump-to-group second scrollbar.
    /// Scrolling to the end first makes the follow-up ScrollIntoView land the
    /// header at the top of the viewport (leading alignment) instead of the
    /// bottom edge.
    /// </summary>
    private void OnRailTickClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: SessionGroup group }
            || !_headerByGroup.TryGetValue(group, out HeaderItem? header))
        {
            return;
        }

        int index = _flat.IndexOf(header);
        if (index < 0)
        {
            return;
        }

        SessionList.ScrollIntoView(_flat.Count - 1);
        SessionList.ScrollIntoView(index);
    }

    private void SyncSelectionFromViewModel()
    {
        _syncingSelection = true;
        try
        {
            SessionList.SelectedItem = _vm?.SelectedSession;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null)
        {
            return;
        }

        if (SessionList.SelectedItem is SessionInfo session)
        {
            _vm.SelectedSession = session;
        }
        else if (SessionList.SelectedItem is HeaderItem)
        {
            // Headers aren't selectable — bounce back to the real selection.
            SyncSelectionFromViewModel();
        }
    }

    private void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null
            && SessionList.SelectedItem is SessionInfo
            && _vm.Details.ResumeCommand.CanExecute(null))
        {
            _vm.Details.ResumeCommand.Execute(null);
        }
    }
}
