using System;
using Microsoft.UI.Xaml.Controls;
using Searchlight.ViewModels;
using Windows.Foundation;

namespace Searchlight.Controls;

/// <summary>Measures the title naturally and moves it below the actions only when needed.</summary>
public sealed partial class SessionHeaderPanel : Panel
{
    private Size _naturalTitle;

    protected override Size MeasureOverride(Size availableSize)
    {
        // ASSUMPTION: MainView supplies exactly two children, title/badge then actions.
        if (Children.Count != 2)
            throw new InvalidOperationException("SessionHeaderPanel requires a title and an action strip.");

        Children[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _naturalTitle = Children[0].DesiredSize;
        Children[1].Measure(new Size(availableSize.Width, double.PositiveInfinity));
        Size actions = Children[1].DesiredSize;
        var layout = SessionHeaderLayout.Calculate(
            availableSize.Width, _naturalTitle.Width, _naturalTitle.Height, actions.Width, actions.Height);
        Children[0].Measure(new Size(layout.TitleWidth, double.PositiveInfinity));
        layout = SessionHeaderLayout.Calculate(
            availableSize.Width, _naturalTitle.Width, Children[0].DesiredSize.Height, actions.Width, actions.Height);
        return new Size(layout.Width, layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size actions = Children[1].DesiredSize;
        Size title = Children[0].DesiredSize;
        var layout = SessionHeaderLayout.Calculate(
            finalSize.Width, _naturalTitle.Width, title.Height, actions.Width, actions.Height);
        Children[0].Arrange(new Rect(0, layout.TitleTop, layout.TitleWidth, title.Height));
        Children[1].Arrange(new Rect(layout.ActionsLeft, 0, actions.Width, actions.Height));
        return finalSize;
    }
}
