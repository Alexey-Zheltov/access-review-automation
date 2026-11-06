using System.Globalization;
using AccessReviewPortal.Components.Dashboard;

namespace AccessReviewPortal.Tests;

public class ChartMathTests
{
    [Fact]
    public void Axis_steps_are_round_numbers()
    {
        Assert.Equal((15.0, 5.0), ChartMath.NiceScale(12));
        Assert.Equal((30.0, 10.0), ChartMath.NiceScale(30));
        Assert.Equal((80.0, 20.0), ChartMath.NiceScale(63));
        Assert.Equal((1.0, 1.0), ChartMath.NiceScale(1));
        Assert.Equal((4.0, 1.0), ChartMath.NiceScale(0));
        Assert.Equal(new[] { 0.0, 5, 10, 15 }, ChartMath.Ticks(15, 5));
    }

    [Fact]
    public void Gauge_arc_starts_bottom_left_and_ends_bottom_right()
    {
        Assert.Equal("M 46.31 121 A 62 62 0 1 1 153.69 121", ChartMath.Arc(100, 90, 62, 150, 390));
        Assert.Equal("", ChartMath.Arc(100, 90, 62, 150, 150));
    }

    [Fact]
    public void Sparkline_uses_the_range_of_the_values()
    {
        Assert.Equal("M 0,36 L 50,21 L 100,6", ChartMath.Sparkline([1, 2, 3], 100, 40, area: false));
        Assert.Equal("M 0,36 L 50,21 L 100,6 L 100,40 L 0,40 Z", ChartMath.Sparkline([1, 2, 3], 100, 40, area: true));
        Assert.Equal("M 0,22 L 100,22", ChartMath.Sparkline([5, 5], 100, 40, area: false));
        Assert.Equal("M 0,22 L 100,22", ChartMath.Sparkline([7], 100, 40, area: false));
    }

    [Fact]
    public void Thresholds_pick_the_last_matching_colour()
    {
        Assert.Equal(GfColors.Red, ChartMath.ColorFor(0, ChartMath.HighIsGood));
        Assert.Equal(GfColors.Yellow, ChartMath.ColorFor(50, ChartMath.HighIsGood));
        Assert.Equal(GfColors.Green, ChartMath.ColorFor(92, ChartMath.HighIsGood));
        Assert.Equal(GfColors.Green, ChartMath.ColorFor(5, ChartMath.LowIsGood));
        Assert.Equal(GfColors.Red, ChartMath.ColorFor(92, ChartMath.LowIsGood));
    }

    [Fact]
    public void Svg_numbers_do_not_depend_on_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", ChartMath.F(1.5));
            Assert.StartsWith("M 0,36 L 50,21", ChartMath.Sparkline([1, 2, 3], 100, 40, area: false));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
