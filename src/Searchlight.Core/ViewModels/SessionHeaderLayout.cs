namespace Searchlight.ViewModels;

/// <summary>Platform-neutral placement of the session title and its action strip.</summary>
public readonly record struct SessionHeaderLayout(
    double Width, double Height, double TitleWidth, double TitleTop, double ActionsLeft)
{
    public const double ColumnGap = 12;
    public const double RowGap = 8;

    public static SessionHeaderLayout Calculate(
        double availableWidth, double titleWidth, double titleHeight, double actionsWidth, double actionsHeight)
    {
        // ASSUMPTION: the host supplies nonnegative measured sizes and a single-line
        // title. Use actual content widths, not a fixed window-size breakpoint.
        double combinedWidth = titleWidth + ColumnGap + actionsWidth;
        double width = double.IsPositiveInfinity(availableWidth) ? combinedWidth : Math.Max(0, availableWidth);
        bool below = combinedWidth > width;
        return new(width,
            below ? actionsHeight + RowGap + titleHeight : Math.Max(titleHeight, actionsHeight),
            below ? width : Math.Max(0, width - actionsWidth - ColumnGap),
            below ? actionsHeight + RowGap : 0,
            Math.Max(0, width - actionsWidth));
    }
}
