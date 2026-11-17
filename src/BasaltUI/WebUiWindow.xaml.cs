using System;
using System.IO;
using System.Windows;
using BasaltUI.Core;

namespace BasaltUI;

/// <summary>
/// The engine's dashboard, in the app. The page is a live modern web app, so it runs on WebView2 — the
/// Chromium runtime Windows 11 already ships — because the only browser WPF can host without it cannot
/// read a single line of the page's JavaScript. The server answers with its own offline handling, so the
/// window is opened whether the server is up yet or not.
/// </summary>
public partial class WebUiWindow : Window
{
    private readonly string url;
    private string pendingUrl;
    private bool environmentReady;

    public WebUiWindow(App app, string url)
    {
        this.url = url;
        pendingUrl = url;
        InitializeComponent();
        UrlText.Text = url;
        Browser.DefaultBackgroundColor = ThemeBacking();
        SourceInitialized += (_, _) => Shell.Win11.Round(this, App.IsDarkTheme);

        // The browser is brought up here, in Loaded, with our own user-data folder. Setting a Source or
        // navigating earlier would make the control initialise itself first with the default environment —
        // and then EnsureCoreWebView2Async refuses, which is the error dialog the user had to click away.
        Loaded += async (_, _) =>
        {
            try
            {
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(userDataFolder: Path.Combine(AppPaths.Root, "webview2"));
                await Browser.EnsureCoreWebView2Async(environment);
                environmentReady = true;
                if (pendingUrl.Length > 0)
                {
                    Browser.CoreWebView2.Navigate(pendingUrl);
                    pendingUrl = "";
                }
            }
            catch (Exception e)
            {
                Shell.Dialog.Info(this, "Basalt UI",
                    "The embedded browser could not start:\n\n" + e.Message +
                    "\n\nThe engine's web page is at " + url + " — it can still be opened in your browser.");
            }
        };
    }

    /// <summary>Point the window at a different address of the same server, once it is on screen.</summary>
    public void Navigate(string url)
    {
        UrlText.Text = url;
        if (environmentReady && Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.Navigate(url);
        else
            pendingUrl = url;   // still initialising: it is navigated to the moment that is done
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is not null)
            Browser.CoreWebView2.Reload();
    }

    /// <summary>WebView2 wants a System.Drawing colour; the app's token is a WPF one.</summary>
    private System.Drawing.Color ThemeBacking()
    {
        var c = ((System.Windows.Media.SolidColorBrush)FindResource("Window")).Color;
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    /// <summary>The browser paints its own backing, so it is told about a theme switch while it is open.</summary>
    public void RefreshTheme()
    {
        if (Browser is not null)
            Browser.DefaultBackgroundColor = ThemeBacking();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
