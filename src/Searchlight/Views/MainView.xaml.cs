using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace Searchlight.Views;

/// <summary>
/// Hosts the full two-pane list/details UI. Implemented as a <see cref="UserControl"/>
/// (a <c>FrameworkElement</c>) so that <c>x:Bind</c> converter lookups resolve correctly —
/// WinUI 3's <c>Window</c> is not a <c>FrameworkElement</c> and cannot serve as a converter
/// lookup root for top-level bindings.
/// </summary>
public sealed partial class MainView : UserControl
{
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The custom title bar drag strip. The host <see cref="MainWindow"/> passes this
    /// to <c>SetTitleBar</c> after extending content into the title bar so the region
    /// stays draggable.
    /// </summary>
    public FrameworkElement TitleBarElement => AppTitleBar;

    /// <summary>
    /// The app icon shown in the custom title strip. Loaded by absolute path because
    /// the app is unpackaged (<c>WindowsPackageType=None</c>), so <c>ms-appx:///</c> is
    /// unreliable. Mirrors the tray-icon load pattern in <c>App.xaml.cs</c>.
    /// </summary>
    public ImageSource AppIconSource { get; } =
        new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app_32.png")))
        {
            DecodePixelWidth = 36,
        };

    /// <summary>
    /// True when the app is running elevated (as administrator). Bound once (OneTime) by the
    /// title-strip shield icon so the user can tell at a glance the app is elevated — mirroring
    /// the UAC shield Windows Terminal shows. Elevation cannot change during the process
    /// lifetime, so a get-only OneTime value is sufficient.
    /// </summary>
    public bool IsElevated { get; } = ElevationHelper.IsElevated();

    public MainView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Wire the grouped list in code-behind: an x:Bind CollectionViewSource
        // Source inside UserControl.Resources is unreliable in WinUI 3, so build
        // the grouped view here. The CVS stays live against the observable
        // SessionGroups collection, so filter/refresh rebuilds flow through.
        CollectionViewSource groupedSource = new()
        {
            IsSourceGrouped = true,
            Source = ViewModel.SessionGroups,
        };
        SessionList.ItemsSource = groupedSource.View;
    }

    private void OnSessionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Double-click a row to resume that session.
        if (ViewModel.Details.ResumeCommand.CanExecute(null))
        {
            ViewModel.Details.ResumeCommand.Execute(null);
        }
    }

    /// <summary>
    /// Clicking a tick in the compact rail scrolls the list straight to that group's
    /// first session — the rail acts like a jump-to-group second scrollbar.
    /// </summary>
    private void OnRailTickClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionGroup group } && group.Count > 0)
        {
            SessionList.ScrollIntoView(group[0], ScrollIntoViewAlignment.Leading);
        }
    }

    /// <summary>
    /// Group headers don't forward mouse-wheel input to the list's ScrollViewer on their
    /// own, so hovering a header and scrolling does nothing. Translate the wheel delta into
    /// a manual vertical scroll so the wheel keeps working over the group labels.
    /// </summary>
    private void OnGroupHeaderPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        ScrollViewer? scrollViewer = FindDescendantScrollViewer(SessionList);
        if (scrollViewer is null)
        {
            return;
        }

        // Positive wheel delta = wheel up = scroll toward the top (smaller offset).
        int delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        scrollViewer.ChangeView(null, scrollViewer.VerticalOffset - delta, null, disableAnimation: false);
        e.Handled = true;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            ScrollViewer? nested = FindDescendantScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
