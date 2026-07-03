using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Searchlight.Models;
using Searchlight.ViewModels;

namespace Searchlight.Avalonia;

/// <summary>A recency group header row in the flattened session list.</summary>
public sealed record HeaderItem(string Title);

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
        _flat.Clear();

        if (_vm is not null)
        {
            foreach (SessionGroup group in _vm.SessionGroups)
            {
                _flat.Add(new HeaderItem(group.Key));
                foreach (SessionInfo session in group)
                {
                    _flat.Add(session);
                }
            }
        }

        SyncSelectionFromViewModel();
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
