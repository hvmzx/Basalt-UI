using System.IO;
using System.Windows;
using BasaltUI.Core;

namespace BasaltUI;

/// <summary>
/// A config's load preview, as three bars per model and one set of totals for the whole config. The
/// window stays open while other configs are selected and re-measures itself for each one.
/// </summary>
public partial class LoadPreviewWindow : Window
{
    private sealed record TierRow(string Name, double VramFraction, string VramText,
                                  double RamFraction, string RamText,
                                  double SsdFraction, string SsdText);

    public static LoadPreviewWindow? Current { get; private set; }

    public double TotalVramFraction { get; private set; }
    public string TotalVramText { get; private set; } = "";
    public double TotalRamFraction { get; private set; }
    public string TotalRamText { get; private set; } = "";
    public double TotalSsdFraction { get; private set; }
    public string TotalSsdText { get; private set; } = "";

    public LoadPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Shell.Win11.Round(this, App.IsDarkTheme);
        Closed += (_, _) => { if (Current == this) Current = null; };
    }

    public static async Task ShowAsync(Window owner, string name, IReadOnlyList<LoadPreview.Options> models)
    {
        var window = Current ?? new LoadPreviewWindow { Owner = owner };
        Current = window;
        window.TitleText.Text = "Preview — " + name;
        window.Verdict.Text = "measuring " + models.Count + " model" + (models.Count == 1 ? "" : "s") + "…";
        if (!window.IsVisible)
            window.Show();
        await window.UpdateFor(name, models);
    }

    /// <summary>Re-measure for a different selection while the window is already open.</summary>
    public async Task UpdateFor(string name, IReadOnlyList<LoadPreview.Options> models)
    {
        TitleText.Text = "Preview — " + name;
        Verdict.Text = "measuring…";
        try
        {
            var plans = await Task.Run(() => models.Select(m => LoadPreview.ForFile(m.Name, m)).ToList());
            Render(models, plans);
        }
        catch (System.Exception e)
        {
            Verdict.Text = "The preview could not be measured:\n\n" + e.Message;
        }
    }

    private void Render(IReadOnlyList<LoadPreview.Options> models, IReadOnlyList<LoadPreview.Plan> plans)
    {
        var bytes = BasaltLocator.HumanBytes;
        var hardware = plans.FirstOrDefault(p => p.Hardware is not null)?.Hardware;
        // What is actually free now, not the card's total: a tier cannot use more than exists, and
        // whatever does not fit goes to the SSD rather than pretending RAM or VRAM grew.
        var vramRoom = hardware?.VramFree ?? 0;
        var ramRoom = hardware?.RamFree ?? 0;

        var rows = new List<TierRow>();
        long vram = 0, ram = 0, ssd = 0, files = 0;
        for (var i = 0; i < plans.Count; i++)
        {
            var tier = plans[i].Tiers ?? new LoadPreview.Tiers(0, 0, 0, 0);
            var v = System.Math.Min(tier.Vram, vramRoom);
            var r = System.Math.Min(tier.Ram, ramRoom);
            var s = tier.Ssd + (tier.Vram - v) + (tier.Ram - r);
            vram += v;
            ram += r;
            ssd += s;
            files += tier.Total;
            rows.Add(new TierRow(Path.GetFileName(models[i].Name),
                vramRoom > 0 ? (double)v / vramRoom : 0,
                bytes(v) + " of " + bytes(vramRoom) + " free",
                ramRoom > 0 ? (double)r / ramRoom : 0,
                bytes(r) + " of " + bytes(ramRoom) + " free",
                tier.Total > 0 ? (double)s / tier.Total : 0,
                bytes(s) + (s > 0 ? " of the file streams from SSD" : " — all in memory")));
        }
        ModelRows.ItemsSource = rows;

        TotalVramFraction = vramRoom > 0 ? (double)vram / vramRoom : 0;
        TotalVramText = bytes(vram) + " of " + bytes(vramRoom) + " free";
        TotalRamFraction = ramRoom > 0 ? (double)ram / ramRoom : 0;
        TotalRamText = bytes(ram) + " of " + bytes(ramRoom) + " free";
        TotalSsdFraction = files > 0 ? (double)ssd / files : 0;
        TotalSsdText = bytes(ssd) + (ssd > 0 ? " streams on demand" : " — everything fits in memory");
        DataContext = this;

        Verdict.Text = hardware is null || !hardware.VramKnown
            ? "Nothing reported the GPU — the bars show the files' own sizes, not a fit."
            : ssd > 0
                ? $"About {bytes(ssd)} would stream from the SSD — replies slow down when experts are read from there."
                : "Everything fits in the VRAM and RAM free right now, experts included.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
