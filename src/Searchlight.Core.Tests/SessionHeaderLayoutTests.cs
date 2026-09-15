using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionHeaderLayoutTests
{
    [Theory]
    [InlineData(511, true)]
    [InlineData(512, false)]
    [InlineData(513, false)]
    public void TitleMovesBelowActions_OnlyWhenCombinedWidthsExceedAvailableSpace(double width, bool below)
    {
        var layout = SessionHeaderLayout.Calculate(width, 200, 38, 300, 32);
        Assert.Equal(width, layout.Width);
        Assert.Equal(below ? 40 : 0, layout.TitleTop);
        Assert.Equal(below ? 78 : 38, layout.Height);
        Assert.Equal(below ? width : width - 312, layout.TitleWidth);
        Assert.Equal(width - 300, layout.ActionsLeft);
    }

    [Fact]
    public void LongTitle_GetsFullSecondRow_WithoutIncreasingItsLineHeight()
    {
        var layout = SessionHeaderLayout.Calculate(600, 1400, 38, 300, 32);
        Assert.Equal(600, layout.TitleWidth);
        Assert.Equal(40, layout.TitleTop);
        Assert.Equal(78, layout.Height);
        Assert.Equal(300, layout.ActionsLeft);
    }

    [Fact]
    public void ChangingTitleOrButtonWidths_ReevaluatesPlacement()
    {
        Assert.Equal(0, SessionHeaderLayout.Calculate(600, 250, 38, 300, 32).TitleTop);
        Assert.Equal(40, SessionHeaderLayout.Calculate(600, 350, 38, 300, 32).TitleTop);
        Assert.Equal(40, SessionHeaderLayout.Calculate(600, 250, 38, 350, 32).TitleTop);
        Assert.Equal(0, SessionHeaderLayout.Calculate(700, 350, 38, 300, 32).TitleTop);
    }

    [Fact]
    public void UnboundedMeasurement_ProducesFiniteNaturalSize()
    {
        var layout = SessionHeaderLayout.Calculate(double.PositiveInfinity, 200, 38, 300, 32);
        Assert.Equal(512, layout.Width);
        Assert.Equal(38, layout.Height);
        Assert.Equal(0, layout.TitleTop);
    }

    [Fact]
    public void NarrowPane_ClampsTitleSpaceAndKeepsActionsAboveIt()
    {
        var layout = SessionHeaderLayout.Calculate(100, 200, 38, 300, 32);
        Assert.Equal(100, layout.TitleWidth);
        Assert.Equal(0, layout.ActionsLeft);
        Assert.Equal(40, layout.TitleTop);
    }
}
