using System.IO;
using System.Windows;
using BasaltUI.Core;

namespace BasaltUI;

/// <summary>
/// A config's load preview, as three bars per model and one set of totals for the whole config —
/// the split the files describe and the memory that is free right now, without a paragraph per model.
/// </summary>
public partial class LoadPreviewWindow : Window
{
    private sealed record TierRow(string Name, double VramFraction, string VramText,
                                  double RamFraction, string RamText,
                                  double SsdFraction, string SsdText);

    public double TotalVramFraction { get; private set; }
    public string TotalVramText { get; private set; } = "";
    public double TotalRamFraction { get; private set; }
    public string TotalRamText { get; private set; } = "";
    public double TotalSsdFraction { get; private set; }
    public string TotalSsdText { get; private set; } = "";

    public LoadPreviewWindow() => InitializeComponent();

    public static async Task ShowAsync(Window owner, string name, IReadOnlyList<LoadPreview.Options> models)
    {
        var window = new LoadPreviewWindow { Owner = owner };
        window.TitleText.Text = "Preview — " + name;
        window.Verdict.Text = "measuring " + models.Count + " model" + (models.Count == 1 ? "" : "s") + "…";
        window.Show();

        try
        {
            var plans = await Task.Run(() => models.Select(m => LoadPreview.ForFile(m.Name, m)).ToList());
            window.Render(models, plans);
        }
        catch (System.Exception e)
        {
            window.Verdict.Text = "The preview could not be measured:\n\n" + e.Message;
        }
    }

    private void Render(IReadOnlyList<LoadPreview.Options> models, IReadOnlyList<LoadPreview.Plan> plans)
    {
        var bytes = BasaltLocator.HumanBytes;
        var hardware = plans.FirstOrDefault(p => p.Hardware is not null)?.Hardware;
        var vramTotal = hardware?.VramTotal ?? 0;
        var ramTotal = hardware?.RamTotal ?? 0;

        var rows = new List<TierRow>();
        long vram = 0, ram = 0, ssd = 0;
        for (var i = 0; i < plans.Count; i++)
        {
            var tier = plans[i].Tiers ?? new LoadPreview.Tiers(0, 0, 0, 0);
            vram += tier.Vram;
            ram += tier.Ram;
            ssd += tier.Ssd;
            rows.Add(new TierRow(Path.GetFileName(models[i].Name),
                vramTotal > 0 ? (double)tier.Vram / vramTotal : 0,
                bytes(tier.Vram) + (vramTotal > 0 ? " of " + bytes(vramTotal) : ""),
                ramTotal > 0 ? (double)tier.Ram / ramTotal : 0,
                bytes(tier.Ram) + (ramTotal > 0 ? " of " + bytes(ramTotal) : ""),
                tier.Total > 0 ? (double)tier.Ssd / tier.Total : 0,
                bytes(tier.Ssd) + " of the " + bytes(tier.Total) + " file"));
        }
        ModelRows.ItemsSource = rows;

        TotalVramFraction = vramTotal > 0 ? (double)vram / vramTotal : 0;
        TotalVramText = bytes(vram) + (vramTotal > 0 ? " of " + bytes(vramTotal) : " (GPU not measured)");
        TotalRamFraction = ramTotal > 0 ? (double)ram / ramTotal : 0;
        TotalRamText = bytes(ram) + (ramTotal > 0 ? " of " + bytes(ramTotal) : "");
        TotalSsdFraction = vram + ram > 0 ? (double)ssd / (vram + ram + ssd) : 0;
        TotalSsdText = bytes(ssd) + (ssd > 0 ? " streams on demand" : " — everything fits in memory");
        DataContext = this;

        Verdict.Text = hardware is null || !hardware.VramKnown
            ? "Nothing reported the GPU — the memory bars show the files' own sizes, not a fit."
            : vram > hardware.VramFree
                ? $"About {bytes(vram - hardware.VramFree)} more VRAM than is free right now — the engine will tier more experts into RAM or stream them."
                : "Fits in the VRAM free right now, experts included.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
