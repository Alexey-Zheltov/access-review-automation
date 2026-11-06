namespace AccessReviewPortal.Components.Dashboard;

/// <summary>A point on the x axis of a chart (snapshot or time bucket).</summary>
public sealed record ChartPoint(string Label, string? SubLabel, string Tooltip);

/// <summary>A series of values, one per <see cref="ChartPoint"/>.</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values, bool Area = false, bool Dashed = false);

/// <summary>One row of a bar gauge.</summary>
public sealed record BarGaugeItem(string Label, double Value, double Max, string Color, string ValueText, string? Note = null);

/// <summary>One slice of a donut chart.</summary>
public sealed record DonutSlice(string Label, double Value, string Color);
