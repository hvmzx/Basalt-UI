using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BasaltUI.Core;

namespace BasaltUI;

/// <summary>
/// A release's notes, in the app: the changelog markdown from the releases API read as text — headings,
/// bullets, inline code — the way it was written, without sending the user to a browser that may never
/// visibly come up. The GitHub page stays one button away for anything the renderer does not draw.
/// </summary>
public partial class ReleaseNotesPage : UserControl
{
    private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    private App? app;

    public ReleaseNotesPage()
    {
        InitializeComponent();
    }

    /// <summary>Fills the page; call once before the page is made visible.</summary>
    public void Show(App app, BasaltRelease release)
    {
        this.app = app;
        ReleaseTitle.Text = release.Tag + (release.Name.Length > 0 ? " — " + release.Name : "");
        GitHubButton.Visibility = string.IsNullOrWhiteSpace(release.HtmlUrl) ? Visibility.Collapsed : Visibility.Visible;
        Render(release.Notes);
    }

    /// <summary>The same renderer over assembled changelog text — used by the update popup's details window.</summary>
    public void ShowText(string title, string markdown)
    {
        ReleaseTitle.Text = title;
        GitHubButton.Visibility = Visibility.Collapsed;
        Render(markdown);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => app?.Vm.OpenReleasePage();

    // ---- the little markdown renderer ---------------------------------------------------------------------

    private void Render(string markdown)
    {
        NotesPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            NotesPanel.Children.Add(Plain("(no notes were written for this release)"));
            return;
        }

        var inFence = false;
        var fence = new StringBuilder();

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Trim().StartsWith("```"))
            {
                if (inFence)
                {
                    NotesPanel.Children.Add(new TextBlock
                    {
                        Text = fence.ToString().TrimEnd(),
                        FontFamily = (FontFamily)FindResource("FontMono"),
                        FontSize = 12,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 4),
                    });
                    fence.Clear();
                }
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                fence.AppendLine(raw);
                continue;
            }

            var t = line.Trim();
            if (t.Length == 0)
                continue;

            if (t.StartsWith("#"))
            {
                var level = t.Length - t.TrimStart('#').Length;
                t = t.TrimStart('#').Trim();
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = t,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level <= 1 ? 18 : 15,
                    Margin = new Thickness(0, level <= 1 ? 12 : 10, 0, level <= 1 ? 6 : 4),
                });
            }
            else if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ "))
                NotesPanel.Children.Add(Bullet(t[2..].Trim(), "·"));
            else if (t.Length > 2 && char.IsDigit(t[0]) && t[1] == '.' && t[2] == ' ')
                NotesPanel.Children.Add(Bullet(t, " "));
            else
                NotesPanel.Children.Add(Plain(t));
        }
    }

    /// <summary>A bullet: the marker lives outside the wrapped text so the wrap hangs under the words.</summary>
    private static DockPanel Bullet(string text, string marker)
    {
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 2, 0, 2) };
        var mark = new TextBlock
        {
            Text = marker,
            Foreground = (Brush)Application.Current.FindResource("Accent"),
            FontFamily = (FontFamily)Application.Current.FindResource("FontMono"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
        };
        DockPanel.SetDock(mark, Dock.Left);
        panel.Children.Add(mark);
        panel.Children.Add(Rich(text));
        return panel;
    }

    private static TextBlock Plain(string text)
    {
        var block = Rich(text);
        block.Margin = new Thickness(0, 2, 0, 2);
        return block;
    }

    /// <summary>Inline markdown to runs: **bold** and `code`; link URLs are dropped, the words stay.</summary>
    private static TextBlock Rich(string text)
    {
        text = Link.Replace(text, "$1");
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var read = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > read)
                block.Inlines.Add(new Run(text[read..m.Index]));
            var token = m.Value;
            if (token.StartsWith("**"))
                block.Inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.SemiBold });
            else
                block.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = (FontFamily)Application.Current.FindResource("FontMono"),
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.FindResource("Accent"),
                });
            read = m.Index + m.Length;
        }
        if (read < text.Length)
            block.Inlines.Add(new Run(text[read..]));
        return block;
    }
}
