using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>The family marks the app carries, loaded once and frozen: a real logo replaces the
    /// monogram where one exists, and everything here reads whether or not the window is still open.</summary>
    private static readonly Dictionary<string, BitmapSource> Logos = new(StringComparer.OrdinalIgnoreCase);

    private static BitmapSource? Logo(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (Logos.TryGetValue(key, out var known))
            return known;
        try
        {
            var image = new BitmapImage(new Uri(
                $"pack://application:,,,/BasaltUI;component/Assets/{key}.png", UriKind.Absolute));
            image.Freeze();
            Logos[key] = image;
            return image;
        }
        catch
        {
            Logos[key] = null!;                       // an asset that is not there: the monogram takes over
            return null;
        }
    }

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

        var mark = Logo(id.Logo);
        if (mark is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            // The family's own mark, fitted inside the tile: never stretched, letterboxed instead, and
            // small enough that the tile's rounded edge keeps its shape around it.
            var scale = Math.Min(w / mark.PixelWidth, h / mark.PixelHeight) * 0.72;
            var draw = mark.Width > 0 && mark.Height > 0
                ? new Rect((w - mark.Width * scale) / 2, (h - mark.Height * scale) / 2, mark.Width * scale, mark.Height * scale)
                : new Rect((w - mark.PixelWidth * scale) / 2, (h - mark.PixelHeight * scale) / 2, mark.PixelWidth * scale, mark.PixelHeight * scale);
            dc.DrawImage(mark, draw);
            return;
        }

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
