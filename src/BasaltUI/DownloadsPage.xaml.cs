using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BasaltUI.Core;

namespace BasaltUI;

/// <summary>
/// The downloads centre. Everything on this page comes from <see cref="DownloadCenter"/> — the app
/// owns the downloads, this page only shows them and carries the user's buttons. Refreshing the
/// summary line happens on the centre's list event; the rows themselves update live through the
/// bindings, because every transfer reports on the UI thread.
/// </summary>
public partial class DownloadsPage : UserControl
{
    private App App => App.Current;

    public DownloadsPage() => InitializeComponent();

    /// <summary>Called each time the page comes to the front, so the summary line is current.</summary>
    public void OnShown()
    {
        DownList.ItemsSource = App.Downloads.Items;
        RefreshSummary();
    }

    /// <summary>The line above the list: how many are on their way, where they land.</summary>
    public void RefreshSummary()
    {
        var center = App.Downloads;
        var active = center.ActiveCount;
        CountText.Text = center.Items.Count == 0
            ? $"nothing downloaded yet — new downloads land in {App.Settings.ModelsFolder}"
            : active switch
            {
                0 => $"{center.Items.Count} finished or stopped — new downloads land in {App.Settings.ModelsFolder}",
                1 => "1 download on its way",
                var n => $"{n} downloads on their way, one at a time",
            };
    }

    // ---- buttons ---------------------------------------------------------------------------------------------
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item)
            App.Downloads.Pause(item);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item)
            App.Downloads.Resume(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item)
            App.Downloads.Remove(item);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => App.Downloads.ClearHistory();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Settings.ModelsFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // a folder that will not open is not worth a dialog
        }
    }
}
