using System.Windows;

namespace BasaltUI;

/// <summary>The changelog the update popup opens on demand: a window of its own, so it can be read
/// while the question "update from X to Y?" is still open behind it.</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow() => InitializeComponent();

    public static async Task ShowAsync(Window? owner, App app, string title)
    {
        var window = new ChangelogWindow { Owner = owner ?? Application.Current.MainWindow };
        window.Notes.ShowText(title, "reading the release notes…");
        window.Show();
        try
        {
            var text = await app.Vm.ChangelogAsync();
            window.Notes.ShowText(title, text);
        }
        catch (System.Exception e)
        {
            window.Notes.ShowText(title, "The release notes could not be read:\n\n" + e.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
