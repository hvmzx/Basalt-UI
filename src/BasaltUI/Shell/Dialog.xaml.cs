using System.Windows;
using System.Windows.Interop;

namespace BasaltUI.Shell;

/// <summary>
/// The app's own modal question, in the same voice as everything else on screen. Every caller is a
/// decision with consequences — updating basalt, closing over unsaved work — so the buttons are
/// always a quiet Cancel and one clear verb. Info variants exist for the answers that only have
/// one thing to say.
/// </summary>
public partial class Dialog : Window
{
    private bool confirmed;

    private Dialog(Window owner, string title, string message, string confirm, string cancel)
    {
        InitializeComponent();
        Owner = owner;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirm;
        CancelButton.Content = cancel;
        SourceInitialized += (_, _) => Win11.Round(this, App.IsDarkTheme);
    }

    /// <summary>Two buttons; true only when the confirm one was pressed (Enter or click).</summary>
    public static bool Confirm(Window? owner, string title, string message,
                               string confirm = "Continue", string cancel = "Cancel")
    {
        var box = new Dialog(OwnerOr(owner), title, message, confirm, cancel);
        box.ShowDialog();
        return box.confirmed;
    }

    /// <summary>One button, one fact.</summary>
    public static void Info(Window? owner, string title, string message)
    {
        var box = new Dialog(OwnerOr(owner), title, message, "OK", "OK");
        box.CancelButton.Visibility = Visibility.Collapsed;
        box.CancelButton.MinWidth = 0;
        box.ShowDialog();
    }

    /// <summary>A caller without a window on screen yet still has to ask something.</summary>
    private static Window? OwnerOr(Window? owner)
        => owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
           ?? Application.Current?.MainWindow;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
