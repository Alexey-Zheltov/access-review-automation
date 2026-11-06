using System.Globalization;

namespace AccessReviewPortal.Components.Dashboard;

/// <summary>Colours of the dashboard (Grafana "classic" palette).</summary>
public static class GfColors
{
    public const string Green = "#73bf69";
    public const string Yellow = "#fade2a";
    public const string Orange = "#ff9830";
    public const string Red = "#f2495c";
    public const string Blue = "#5794f2";
    public const string Purple = "#b877d9";
    public const string Text = "#ccccdc";
}

/// <summary>A threshold: from this value on, the colour applies (like Grafana thresholds).</summary>
public sealed record Threshold(double From, string Color);

/// <summary>Scales, arcs and paths for the SVG panels. Numbers in SVG always use the invariant culture.</summary>
public static class ChartMath
{
    /// <summary>Lower is better, for example the share of users with unused roles.</summary>
    public static readonly Threshold[] LowIsGood = [new(0, GfColors.Green), new(10, GfColors.Yellow), new(30, GfColors.Red)];

    /// <summary>Higher is better, for example the review progress.</summary>
    public static readonly Threshold[] HighIsGood = [new(0, GfColors.Red), new(50, GfColors.Yellow), new(80, GfColors.Green)];

    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string ColorFor(double value, IReadOnlyList<Threshold> thresholds)
    {
        var color = thresholds[0].Color;
        foreach (var t in thresholds)
        {
            if (value >= t.From) color = t.Color;
        }
        return color;
    }

    /// <summary>Axis from 0 with about <paramref name="ticks"/> steps of 1, 2, 2.5 or 5 × 10^n (at least 1: the values are counts).</summary>
    public static (double Max, double Step) NiceScale(double max, int ticks = 4)
    {
        if (max <= 0) return (ticks, 1);
        var raw = max / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10) * magnitude;
        step = Math.Max(1, step);
        return (Math.Ceiling(max / step) * step, step);
    }

    public static IEnumerable<double> Ticks(double max, double step)
    {
        for (var i = 0; i * step <= max + step / 1000; i++)
        {
            yield return i * step;
        }
    }

    /// <summary>Point on a circle. Degrees: 0 = 3 o'clock, clockwise (the SVG y axis points down).</summary>
    public static (double X, double Y) Polar(double cx, double cy, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return (cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    /// <summary>Clockwise arc from <paramref name="startDegrees"/> to <paramref name="endDegrees"/>. Empty when the arc has no length.</summary>
    public static string Arc(double cx, double cy, double r, double startDegrees, double endDegrees)
    {
        var sweep = Math.Min(endDegrees - startDegrees, 359.99);
        if (sweep <= 0.01) return "";
        var (x0, y0) = Polar(cx, cy, r, startDegrees);
        var (x1, y1) = Polar(cx, cy, r, startDegrees + sweep);
        return $"M {F(x0)} {F(y0)} A {F(r)} {F(r)} 0 {(sweep > 180 ? 1 : 0)} 1 {F(x1)} {F(y1)}";
    }

    /// <summary>
    /// Sparkline through the values in a width × height box, scaled from the smallest to the largest value
    /// (a flat line when all values are equal). With <paramref name="area"/> the path is closed at the bottom.
    /// </summary>
    public static string Sparkline(IReadOnlyList<double> values, double width, double height, bool area)
    {
        if (values.Count == 0) return "";
        var min = values.Min();
        var span = values.Max() - min;
        double Y(double v) => span == 0 ? height * 0.55 : height - height * 0.1 - (v - min) / span * height * 0.75;
        List<(double X, double Y)> points = values.Count == 1
            ? [(0, Y(values[0])), (width, Y(values[0]))]
            : values.Select((v, i) => (i * width / (values.Count - 1), Y(v))).ToList();
        var line = "M " + string.Join(" L ", points.Select(p => $"{F(p.X)},{F(p.Y)}"));
        return area ? $"{line} L {F(width)},{F(height)} L 0,{F(height)} Z" : line;
    }

    /// <summary>True for the x labels to draw, so that about <paramref name="maxLabels"/> labels are shown.</summary>
    public static bool ShowLabel(int index, int count, int maxLabels) =>
        index % Math.Max(1, (int)Math.Ceiling(count / (double)maxLabels)) == 0;
}
