using System.Windows;
using System.Windows.Media;

namespace BasaltUI.Controls;

/// <summary>
/// A small line chart that floats on the card instead of living in a box of its own: one value per
/// second, newest at the right, drawn as a smooth line over a soft wash of its own colour. No frame,
/// no gridlines, no ground — the hairline rectangles that used to surround these made every card look
/// like a form. The current value is printed over the plot in the mono face.
/// </summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DodgerBlue));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Sparkline), new FrameworkPropertyMetadata("",
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(Sparkline), new FrameworkPropertyMetadata("",
            FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Face =
        new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Mono =
        new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public Sparkline()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool Percent { get => (bool)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4)
            return;

        var plot = new Rect(0, 17, w, Math.Max(0, h - 17));
        if (plot.Width <= 2 || plot.Height <= 2)
            return;

        var stroke = Stroke as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(90, 200, 250));
        var faint = Named<SolidColorBrush>("TextFaint");
        var captionBrush = faint ?? Brushes.Gray;

        // the chart's own headline: the caption quietly on the left, the live value in mono on the right
        if (!string.IsNullOrWhiteSpace(Caption))
            dc.DrawText(Text(Caption, 11, Face, captionBrush), new Point(0, 1));
        if (!string.IsNullOrWhiteSpace(ValueText))
        {
            var value = Text(ValueText, 12, Mono, stroke);
            dc.DrawText(value, new Point(Math.Max(0, w - value.Width), 0.5));
        }

        var values = Values ?? [];
        var top = Percent ? 100 : Maximum;
        if (top <= 0)
        {
            foreach (var v in values)
                if (!double.IsNaN(v) && v > top)
                    top = v;
        }
        if (top <= 0)
            top = 1;
        if (Percent)
            top = Math.Max(top, 100);

        dc.PushClip(new RectangleGeometry(plot));

        // A baseline hairline, tinted from the faint token so it shows on both themes.
        if (faint is SolidColorBrush faintSolid)
        {
            var baseline = new Pen(new SolidColorBrush(Color.FromArgb(64, faintSolid.Color.R,
                faintSolid.Color.G, faintSolid.Color.B)), 1);
            baseline.Freeze();
            dc.DrawLine(baseline, new Point(plot.Left, plot.Bottom - 0.5), new Point(plot.Right, plot.Bottom - 0.5));
        }

        var samples = 0;
        foreach (var v in values)
            if (!double.IsNaN(v))
                samples++;

        // A chart with history that is all zeroes and a chart with no history look the same from the outside,
        // and both read as "something is wrong" without saying which. Say it in the plot, in the same voice as
        // the rest of the app's empty states.
        if (samples < 2)
        {
            var note = Text("no samples yet", 11, Face, captionBrush);
            dc.DrawText(note, new Point(plot.X + (plot.Width - note.Width) / 2,
                                        plot.Y + (plot.Height - note.Height) / 2));
        }
        else if (values.Length >= 2)
        {
            var count = values.Length;
            var step = plot.Width / Math.Max(count - 1, 1);

            var pts = new List<Point>(count);
            for (var i = 0; i < count; i++)
            {
                var v = values[i];
                if (double.IsNaN(v))
                    continue;
                pts.Add(new Point(plot.X + i * step, plot.Bottom - Math.Clamp(v / top, 0, 1) * (plot.Height - 3) - 1.5));
            }

            if (pts.Count >= 2)
            {
                var smooth = Smooth(pts);

                var area = Fill ?? new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(52, stroke.Color.R, stroke.Color.G, stroke.Color.B), 0),
                        new GradientStop(Color.FromArgb(0, stroke.Color.R, stroke.Color.G, stroke.Color.B), 1),
                    },
                };
                area.Freeze();

                var fillGeo = new StreamGeometry();
                using (var ctx = fillGeo.Open())
                {
                    ctx.BeginFigure(pts[0], true, true);
                    foreach (var seg in smooth)
                        ctx.LineTo(seg, true, false);
                    ctx.LineTo(new Point(pts[^1].X, plot.Bottom), true, false);
                    ctx.LineTo(new Point(pts[0].X, plot.Bottom), true, false);
                }
                fillGeo.Freeze();

                var lineGeo = new StreamGeometry();
                using (var ctx = lineGeo.Open())
                {
                    ctx.BeginFigure(pts[0], false, false);
                    foreach (var seg in smooth)
                        ctx.LineTo(seg, true, false);
                }
                lineGeo.Freeze();

                dc.DrawGeometry(area, null, fillGeo);
                dc.DrawGeometry(null, new Pen(stroke, 1.7)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                }, lineGeo);
            }
        }

        dc.Pop();
    }

    /// <summary>
    /// Catmull-Rom through every sample, flattened to line segments: the same data, drawn the way a
    /// modern chart draws it — no saw-teeth from twelve points a second.
    /// </summary>
    private static List<Point> Smooth(List<Point> pts)
    {
        if (pts.Count < 3)
            return pts;

        var flat = new List<Point>(pts.Count * 2);
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];

            for (var t = 1; t <= 4; t++)
            {
                var s = t / 4.0;
                var s2 = s * s;
                var s3 = s2 * s;
                flat.Add(new Point(
                    0.5 * ((2 * p1.X) + (-p0.X + p2.X) * s + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * s2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * s3),
                    0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * s + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * s2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * s3)));
            }
        }
        return flat;
    }

    /// <summary>A frozen brush by role, resolved at render time so the light and dark tokens both reach here.</summary>
    private T? Named<T>(string key) where T : class
        => TryFindResource(key) as T;

    private static FormattedText Text(string s, double size, Typeface face, Brush brush)
        => new(s,
               System.Globalization.CultureInfo.CurrentCulture,
               FlowDirection.LeftToRight,
               face, size, brush, 1.0);
}
