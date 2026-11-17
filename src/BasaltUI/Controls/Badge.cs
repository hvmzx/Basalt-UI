using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BasaltUI.Core;

namespace BasaltUI.Controls;

/// <summary>
/// A model's family mark: a small tinted tile with the family's monogram, the kind of thing that sits to
/// the left of a name in a list. The tile is the family's colour at low strength over whatever surface it
/// stands on, so dark and light themes both read it without a second copy of it; a row with no family the
/// names know draws nothing at all rather than an empty box.
/// </summary>
public class Badge : FrameworkElement
{
    public static readonly DependencyProperty IdentityProperty = DependencyProperty.Register(
        nameof(Identity), typeof(ModelIdentity.Identity), typeof(Badge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ModelIdentity.Identity? Identity
    {
        get => (ModelIdentity.Identity?)GetValue(IdentityProperty);
        set => SetValue(IdentityProperty, value);
    }

    private static readonly Typeface Face =
        new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        var id = Identity;
        if (id is null)
            return;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10)
            return;

        var tint = id.Tint;
        var fill = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B));
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x59, tint.R, tint.G, tint.B)), 1);
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(fill, edge, rect, Math.Min(w, h) / 4, Math.Min(w, h) / 4);

        // The full-strength tint for the letters: a whitish monogram would disappear on the same tile once
        // it stood on a light surface.
        var text = Text(id.Monogram, Math.Max(9, Math.Min(w, h) / 2.3), id.Tint);
        dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2 - 0.5));
    }

    private static FormattedText Text(string s, double size, Color color)
        => new(s,
               CultureInfo.CurrentCulture,
               FlowDirection.LeftToRight,
               Face, size, new SolidColorBrush(color), 1.0);
}
