using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Buddy.App.Project;

/// <summary>A line or a polygon of a chart: its name in the legend, its colour and one value per point (null: no value).</summary>
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<double?> Values, bool Dashed = false);

/// <summary>A row of a <see cref="BarChart"/>; <paramref name="Note"/> is written in place of a missing value ("n.d." by default).</summary>
public sealed record BarRow(string Label, double? Value, double? Change = null, string? Note = null);

/// <summary>A stacked part of a column: its value and colour.</summary>
public sealed record ColumnPart(double Value, Color Color);

/// <summary>Shared drawing helpers: the theme's ink for text and grid, the text itself.</summary>
public abstract class ChartBase : Control
{
    protected static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    protected IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    protected IBrush Ink => Brush("Ink", Brushes.Gray);

    protected IBrush Muted => Brush("Muted", Brushes.Gray);

    protected Pen GridPen => new(Brush("Divider", new SolidColorBrush(Colors.Gray, 0.2)), 1);

    protected FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, Italian, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    protected static Pen LinePen(Color color, double thickness, bool dashed = false) =>
        new(new SolidColorBrush(color), thickness, dashed ? new DashStyle([3, 3], 0) : null, PenLineCap.Round, PenLineJoin.Round);

    protected static string Format(double value) => value.ToString(value % 1 == 0 ? "0" : "0.0", Italian);
}

/// <summary>
/// A spider chart: one axis per aspect, from 0 in the middle to <see cref="Max"/> on the edge, a ring every two points.
/// The first series is filled; the others (a previous report) are only outlined. A missing value breaks the outline.
/// </summary>
public sealed class RadarChart : ChartBase
{
    public required IReadOnlyList<string> Axes { get; init; }

    public required IReadOnlyList<ChartSeries> Series { get; init; }

    /// <summary>What is written next to an axis whose value is missing in the first series ("n.d." when null).</summary>
    public IReadOnlyList<string?>? Notes { get; init; }

    public double Max { get; init; } = 10;

    public RadarChart() => Height = 300;

    public override void Render(DrawingContext context)
    {
        var n = Axes.Count;
        if (n < 3 || Bounds.Width <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(20, Math.Min(Bounds.Width / 2 - 78, Bounds.Height / 2 - 26));
        Point At(int axis, double value)
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * axis / n;
            var r = radius * Math.Clamp(value / Max, 0, 1);
            return new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        }

        // Rings and spokes.
        for (var ring = 2; ring <= Max; ring += 2)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(At(0, ring), false);
                for (var i = 1; i < n; i++)
                {
                    g.LineTo(At(i, ring));
                }

                g.EndFigure(true);
            }

            context.DrawGeometry(null, GridPen, geometry);
        }

        for (var i = 0; i < n; i++)
        {
            context.DrawLine(GridPen, center, At(i, Max));
            var missing = Series.Count > 0 && (i >= Series[0].Values.Count || Series[0].Values[i] is null);
            var label = Text(missing ? $"{Axes[i]} ({(Notes is { } notes && i < notes.Count ? notes[i] : null) ?? "n.d."})" : Axes[i], 11, Muted);
            var tip = At(i, Max + 0.9);
            var x = tip.X < center.X - 4 ? tip.X - label.Width : tip.X > center.X + 4 ? tip.X : tip.X - label.Width / 2;
            var y = tip.Y < center.Y - 4 ? tip.Y - label.Height : tip.Y > center.Y + 4 ? tip.Y : tip.Y - label.Height / 2;
            context.DrawText(label, new Point(x, y));
        }

        // The previous ones under, the current one on top.
        for (var s = Series.Count - 1; s >= 0; s--)
        {
            var series = Series[s];
            var points = Enumerable.Range(0, n).Select(i => i < series.Values.Count && series.Values[i] is { } v ? At(i, v) : (Point?)null).ToList();
            // A closed shape over the axes that have a value (a missing one is skipped, not drawn as zero).
            var pen = LinePen(series.Color, s == 0 ? 2 : 1.5, series.Dashed);
            var known = points.OfType<Point>().ToList();
            if (known.Count >= 3)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(known[0], s == 0);
                    foreach (var p in known.Skip(1))
                    {
                        g.LineTo(p);
                    }

                    g.EndFigure(true);
                }

                context.DrawGeometry(s == 0 ? new SolidColorBrush(series.Color, 0.22) : null, pen, geometry);
            }
            else if (known.Count == 2)
            {
                context.DrawLine(pen, known[0], known[1]);
            }

            if (s == 0)
            {
                var dot = new SolidColorBrush(series.Color);
                for (var i = 0; i < n; i++)
                {
                    if (points[i] is { } p)
                    {
                        context.DrawEllipse(dot, null, p, 3.5, 3.5);
                    }
                }
            }
        }
    }
}

/// <summary>Lines over time: one point per label on the x axis, the y axis from <see cref="Min"/> to <see cref="Max"/>.</summary>
public sealed class LineChart : ChartBase
{
    public required IReadOnlyList<string> Labels { get; init; }

    public required IReadOnlyList<ChartSeries> Series { get; init; }

    public double Min { get; init; }

    public double Max { get; init; } = 10;

    /// <summary>Distance between the grid's lines (and their labels).</summary>
    public double Step { get; init; } = 2;

    public LineChart() => Height = 190;

    public override void Render(DrawingContext context)
    {
        if (Labels.Count == 0 || Bounds.Width <= 0 || Max <= Min)
        {
            return;
        }

        const double left = 28, right = 14, top = 10, bottom = 22;
        var width = Bounds.Width - left - right;
        var height = Bounds.Height - top - bottom;
        double X(int i) => left + (Labels.Count == 1 ? width / 2 : width * i / (Labels.Count - 1));
        double Y(double v) => top + height * (1 - (Math.Clamp(v, Min, Max) - Min) / (Max - Min));

        for (var v = Min; v <= Max + 0.001; v += Step)
        {
            context.DrawLine(GridPen, new Point(left, Y(v)), new Point(left + width, Y(v)));
            var label = Text(Format(v), 10, Muted);
            context.DrawText(label, new Point(left - 6 - label.Width, Y(v) - label.Height / 2));
        }

        // At most about one label every 44 pixels, the last one always.
        var every = Math.Max(1, (int)Math.Ceiling(Labels.Count * 44 / Math.Max(1, width)));
        for (var i = 0; i < Labels.Count; i++)
        {
            if (i % every == 0 || i == Labels.Count - 1)
            {
                var label = Text(Labels[i], 10, Muted);
                context.DrawText(label, new Point(Math.Clamp(X(i) - label.Width / 2, 0, Bounds.Width - label.Width), top + height + 6));
            }
        }

        for (var s = Series.Count - 1; s >= 0; s--)
        {
            var series = Series[s];
            var pen = LinePen(series.Color, s == 0 ? 2.4 : 1.6, series.Dashed);
            var dot = new SolidColorBrush(series.Color);
            Point? previous = null;
            for (var i = 0; i < Labels.Count && i < series.Values.Count; i++)
            {
                if (series.Values[i] is not { } v)
                {
                    previous = null; // a gap, not a line through nothing
                    continue;
                }

                var point = new Point(X(i), Y(v));
                if (previous is { } p)
                {
                    context.DrawLine(pen, p, point);
                }

                context.DrawEllipse(dot, null, point, s == 0 ? 3.2 : 2.4, s == 0 ? 3.2 : 2.4);
                previous = point;
            }

            // The first series' last value, written next to its point.
            if (s == 0 && series.Values.Select((v, i) => (v, i)).LastOrDefault(x => x.v is not null) is { v: { } last } at)
            {
                var label = Text(Format(last), 10, Ink, bold: true);
                context.DrawText(label, new Point(Math.Min(X(at.i) + 5, Bounds.Width - label.Width), Y(last) - label.Height - 2));
            }
        }
    }
}

/// <summary>
/// One horizontal bar per row, from 0 to <see cref="Max"/>: the label on the left, the value and how it moved on the
/// right. A row without a value shows only its empty track and "n.d.".
/// </summary>
public sealed class BarChart : ChartBase
{
    public required IReadOnlyList<BarRow> Rows { get; init; }

    public double Max { get; init; } = 10;

    public Color Color { get; init; } = Color.Parse("#B99BF5");

    private const double RowHeight = 24;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, Rows.Count * RowHeight + 4);

    public override void Render(DrawingContext context)
    {
        if (Rows.Count == 0 || Bounds.Width <= 0)
        {
            return;
        }

        var labels = Rows.Select(r => Text(r.Label, 11, Muted)).ToList();
        var labelWidth = Math.Min(Bounds.Width * 0.42, labels.Max(l => l.Width) + 10);
        var notes = Rows.Select(r => r.Value is null ? Text(r.Note ?? "n.d.", 10, Muted) : null).ToList();
        var valueWidth = Math.Max(54, notes.Max(n => n?.Width ?? 0) + 12);
        var track = Math.Max(20, Bounds.Width - labelWidth - valueWidth);
        var trackBrush = Brush("Divider", new SolidColorBrush(Colors.Gray, 0.2));
        var fill = new SolidColorBrush(Color);
        var up = new SolidColorBrush(Color.Parse("#5FBF77"));
        var down = new SolidColorBrush(Color.Parse("#E8857A"));
        for (var i = 0; i < Rows.Count; i++)
        {
            var (_, value, change, _) = Rows[i];
            var middle = i * RowHeight + RowHeight / 2;
            context.DrawText(labels[i], new Point(0, middle - labels[i].Height / 2));
            var bar = new Rect(labelWidth, middle - 5, track, 10);
            context.DrawRectangle(trackBrush, null, bar, 5, 5);
            if (value is { } v && v > 0)
            {
                context.DrawRectangle(fill, null, bar.WithWidth(track * Math.Clamp(v / Max, 0, 1)), 5, 5);
            }

            var number = notes[i] ?? Text(Format(value!.Value), 11, Ink, bold: true);
            var x = labelWidth + track + 8;
            context.DrawText(number, new Point(x, middle - number.Height / 2));
            if (change is { } c && c != 0)
            {
                var arrow = Text(c > 0 ? $"↑{Format(c)}" : $"↓{Format(-c)}", 10, c > 0 ? up : down, bold: true);
                context.DrawText(arrow, new Point(x + number.Width + 4, middle - arrow.Height / 2));
            }
        }
    }
}

/// <summary>Columns, each a stack of parts, with its total on top and its label under.</summary>
public sealed class ColumnChart : ChartBase
{
    public required IReadOnlyList<string> Labels { get; init; }

    public required IReadOnlyList<IReadOnlyList<ColumnPart>> Columns { get; init; }

    public ColumnChart() => Height = 170;

    public override void Render(DrawingContext context)
    {
        if (Labels.Count == 0 || Bounds.Width <= 0)
        {
            return;
        }

        const double top = 16, bottom = 22;
        var height = Bounds.Height - top - bottom;
        var max = Math.Max(1, Columns.Max(c => c.Sum(p => p.Value)));
        var slot = Bounds.Width / Labels.Count;
        var barWidth = Math.Min(34, slot * 0.6);
        context.DrawLine(GridPen, new Point(0, top + height), new Point(Bounds.Width, top + height));
        for (var i = 0; i < Labels.Count; i++)
        {
            var x = slot * i + (slot - barWidth) / 2;
            var y = top + height;
            var total = 0.0;
            foreach (var part in i < Columns.Count ? Columns[i] : [])
            {
                if (part.Value <= 0)
                {
                    continue;
                }

                var h = height * part.Value / max;
                context.DrawRectangle(new SolidColorBrush(part.Color), null, new Rect(x, y - h, barWidth, h), 2, 2);
                y -= h;
                total += part.Value;
            }

            if (total > 0)
            {
                var value = Text(Format(total), 10, Ink, bold: true);
                context.DrawText(value, new Point(x + (barWidth - value.Width) / 2, y - value.Height - 1));
            }

            var label = Text(Labels[i], 10, Muted);
            context.DrawText(label, new Point(slot * i + (slot - label.Width) / 2, top + height + 5));
        }
    }
}

/// <summary>The panels that hold the charts on a page: a title, an optional line under it, the chart and its legend.</summary>
public static class ChartPanels
{
    public static Control Panel(string title, string? subtitle, Control chart, IEnumerable<(string Name, Color Color, bool Dashed)>? legend = null)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = TextSize.Base, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var line = new TextBlock { Text = subtitle, FontSize = TextSize.Small, TextWrapping = TextWrapping.Wrap };
            line.Classes.Add("muted");
            stack.Children.Add(line);
        }

        stack.Children.Add(chart);
        if (legend is not null)
        {
            var keys = new WrapPanel();
            foreach (var (name, color, dashed) in legend)
            {
                var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 12, 2) };
                key.Children.Add(new Border
                {
                    Width = 10, Height = dashed ? 2 : 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(color, dashed ? 0.7 : 1),
                });
                var text = new TextBlock { Text = name, FontSize = TextSize.Small };
                text.Classes.Add("muted");
                key.Children.Add(text);
                keys.Children.Add(key);
            }

            stack.Children.Add(keys);
        }

        var panel = new Border { Child = stack, Margin = new Thickness(0, 0, 12, 12) };
        panel.Classes.Add("panel");
        panel.Bind(Layoutable.WidthProperty, panel.GetResourceObservable("PageCardWidth"));
        return panel;
    }

    /// <summary>A bar split in two by the ratio of <paramref name="good"/> to <paramref name="bad"/>, with the numbers.</summary>
    public static Control Ratio(int good, int bad, Color goodColor, Color badColor)
    {
        var grid = new Grid { Height = 14, ColumnDefinitions = new ColumnDefinitions($"{Math.Max(good, 0.001)}*,{Math.Max(bad, 0.001)}*"), Margin = new Thickness(0, 6, 0, 2) };
        var left = new Border { Background = new SolidColorBrush(goodColor), CornerRadius = new CornerRadius(4, bad == 0 ? 4 : 0, bad == 0 ? 4 : 0, 4) };
        var right = new Border { Background = new SolidColorBrush(badColor), CornerRadius = new CornerRadius(good == 0 ? 4 : 0, 4, 4, good == 0 ? 4 : 0) };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
