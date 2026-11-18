using System.Windows;

namespace BasaltUI;

/// <summary>The changelog the update popup opens on demand: a window of its own, so it can be read
/// while the question "update from X to Y?" is still open behind it.</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }    /// <summary>The basalt changelog.</summary>
    public static Task ShowAsync(Window? owner, App app, string title)
        => ShowAsync(owner, title, () => app.Vm.ChangelogAsync());

    /// <summary>The same window over any changelog — the app's own update uses it for its releases.</summary>
    public static async Task ShowAsync(Window? owner, string title, Func<Task<string>> changelog)
    {
        var window = new ChangelogWindow { Owner = owner ?? Application.Current.MainWindow };
        window.Notes.ShowText(title, "reading the release notes…");
        window.Show();
        try
        {
            var text = await changelog();
            window.Notes.ShowText(title, text);
        }
        catch (System.Exception e)
        {
            window.Notes.ShowText(title, "The release notes could not be read:\n\n" + e.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
