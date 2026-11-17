using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BasaltUI.Core;
using BasaltUI.ViewModels;

namespace BasaltUI;

/// <summary>
/// One model's history: every session of basalt the app watched it run. The list is the recorder's own
/// words — a per-second pass through the view model is all it takes for the open session's line to move
/// while the window stands — and the window only shows what was actually seen. Time the app was away is
/// not pretended away with: it comes back as one line marked ≈, counted from the engine's own totals.
/// </summary>
public partial class ModelHistoryWindow : Window
{
    private sealed class Line
    {
        public string When { get; init; } = "";
        public string State { get; init; } = "";
        public string Requests { get; init; } = "";
        public string Decode { get; init; } = "";
        public string Prefill { get; init; } = "";

        public override string ToString() => When + "|" + State + "|" + Requests + "|" + Decode + "|" + Prefill;
    }

    private readonly HomeModelRow row;
    private readonly MainViewModel vm;
    private readonly DispatcherTimer timer;
    private string builtBefore = "";

    public ModelHistoryWindow(HomeModelRow row, MainViewModel vm)
    {
        this.row = row;
        this.vm = vm;
        InitializeComponent();

        SourceInitialized += (_, _) => Shell.Win11.Round(this, App.IsDarkTheme);
        TitleText.Text = row.Id;
        TitleDetail.Text = row.Detail;

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();

        Refresh();
    }

    /// <summary>The rows, rebuilt only when one of them actually changed — the list must not flicker.</summary>
    private void Refresh()
    {
        var list = vm.HistoryFor(row);
        var lines = new List<Line>(list.Count);
        var now = DateTime.Now;

        foreach (var s in list)
        {
            var local = s.Since.ToLocalTime();
            var when = local.Date == now.Date
                ? local.ToString("HH:mm")
                : local.ToString("d MMM, HH:mm");
            lines.Add(new Line
            {
                When = when,
                State = s.Current ? "now" : s.Gap ? "≈" : "",
                Requests = s.Requests.ToString(),
                Decode = s.DecodeRange,
                Prefill = s.PrefillRange,
            });
        }

        var built = string.Join("§", lines);
        if (built == builtBefore)
            return;
        builtBefore = built;

        RowsList.ItemsSource = null;
        RowsList.ItemsSource = lines;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        timer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        base.OnClosed(e);
    }
}
