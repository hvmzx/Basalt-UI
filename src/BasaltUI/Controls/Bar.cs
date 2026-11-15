using System.Windows;
using System.Windows.Media;

namespace BasaltUI.Controls;

/// <summary>
/// A compact meter: a caption, a filled track, and a value at the right. Used for GPU memory, RAM,
/// utilisation and the expert cache, where a bar reads faster than a number. Colours are resolved from
/// the theme tokens at render time, so the light and dark palettes both reach in here.
/// </summary>
public class Bar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(Bar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Bar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(Bar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Bar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningProperty = DependencyProperty.Register(
        nameof(Warning), typeof(bool), typeof(Bar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Memory does not get to be red. An inference server that has filled its VRAM is doing its job — the KV
    /// cache takes what it is offered — so a full memory bar is a fact, not a warning. Set this on the memory
    /// meters and they stay the calm colour whatever they hold.
    /// </summary>
    public static readonly DependencyProperty CalmProperty = DependencyProperty.Register(
        nameof(Calm), typeof(bool), typeof(Bar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly FontFamily FaceFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas");
    private static readonly Typeface Face = new(FaceFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceValue = new(MonoFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public bool Warning { get => (bool)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }
    public bool Calm { get => (bool)GetValue(CalmProperty); set => SetValue(CalmProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2)
            return;

        var faint = Frozen(Named("TextFaint"));
        var secondary = Frozen(Named("TextSecondary"));
        var primary = Frozen(Named("TextPrimary"));
        var accent = Frozen(Named("Accent"));
        var good = Frozen(Named("Good"));
        var warn = Frozen(Named("Warn"));
        var bad = Frozen(Named("Bad"));

        var trackH = 5.0;
        var trackY = h - trackH - 1;
        var rect = new Rect(0, trackY, w, trackH);

        dc.DrawRoundedRectangle(Frozen(WithAlpha(Named("LineHi"), 0.35)), null, rect, 2.5, 2.5);

        var fill = Math.Clamp(Fraction, 0, 1);
        if (fill > 0.001)
        {
            var brush = BarBrush ?? (Calm ? good : Warning ? bad : fill > 0.9 ? bad : fill > 0.78 ? warn : accent);
            dc.PushClip(new RectangleGeometry(rect, 2.5, 2.5));
            dc.DrawRectangle(brush, null, new Rect(0, trackY, w * fill, trackH));
            dc.Pop();
        }

        var textY = h - trackH - 19;
        if (!string.IsNullOrWhiteSpace(Caption))
            dc.DrawText(Text(Caption, 11.5, Face, secondary), new Point(0, textY));

        if (!string.IsNullOrWhiteSpace(ValueText))
        {
            var value = Text(ValueText, 12, FaceValue, primary);
            dc.DrawText(value, new Point(Math.Max(0, w - value.Width), textY - 1));
        }
        else
        {
            Dash(dc, Math.Max(0, w - 13), textY, 13, faint);   // no reading yet, said quietly
        }
    }

    private static void Dash(DrawingContext dc, double x, double y, double width, Brush brush)
    {
        var rect = new Rect(x, y + width / 2, width, 1.5);
        dc.DrawRoundedRectangle(brush, null, rect, 0.75, 0.75);
    }

    private Color Named(string key)
        => TryFindResource(key) is SolidColorBrush s ? s.Color : Color.FromRgb(128, 128, 128);

    private static Color WithAlpha(Color c, double opacity) => Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B);

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static FormattedText Text(string s, double size, Typeface face, Brush brush)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
}
