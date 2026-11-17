using System.IO;

namespace BasaltUI.Core;

/// <summary>
/// App settings, config backups, downloaded releases and captured logs. The usual place is
/// %APPDATA%\BasaltUI, or wherever BASALTUI_HOME points.
/// A folder that does not exist and cannot be created — a locked-down profile, a mandatory folder that was
/// never given to this user, an anti-ransomware setting that dislikes new directories under AppData — used to
/// mean every save and every log line failed in silence: the app forgot everything between runs, and a dialog
/// could point at a log file that was never written. So a fallback is tried, and the reason the first place
/// was refused is kept in <see cref="Trouble"/> to be shown rather than swallowed.
/// </summary>
public static class AppPaths
{
    public static string Root { get; private set; } = "";

    /// <summary>
    /// Why the app is not keeping its files where it would like to, with every place it tried;
    /// null when the first place worked.
    /// </summary>
    public static string? Trouble { get; private set; }

    /// <summary>
    /// The scratch folder TMP/TEMP now point at, when the app managed to make one of its own; null when it left
    /// the system temp alone. WPF unpacks its font cache into <c>%TEMP%\WPF</c> while drawing the first window,
    /// so on a machine where a protective filter refuses that write the whole window comes down — the only answer
    /// is to hand it a temp folder the app has proved it can write in.
    /// </summary>
    public static string? ScratchDir { get; private set; }

    /// <summary>%TEMP% as it was before the app moved it, for diagnostics that need the real one.</summary>
    public static string OriginalTemp { get; private set; } = "";

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "logs", "basalt-ui.log");
    public static string BackupsDir => Path.Combine(Root, "config-backups");

    /// <summary>Where a newer basalt release is downloaded and unpacked before anything of yours is touched.</summary>
    public static string UpdateDir => Path.Combine(Root, "updates");

    /// <summary>How fast each model has run, per engine session: the model rows' history.</summary>
    public static string ThroughputFile => Path.Combine(Root, "model-throughput.json");

    static AppPaths() => Place();

    private static void Place()
    {
        var choices = new List<string>();
        Add(choices, Environment.GetEnvironmentVariable("BASALTUI_HOME") is { Length: > 0 } home
                         ? home
                         : Combine(Special(Environment.SpecialFolder.ApplicationData), "BasaltUI"));
        Add(choices, Combine(Special(Environment.SpecialFolder.LocalApplicationData), "BasaltUI"));
        Add(choices, Combine(Special(Environment.SpecialFolder.CommonApplicationData), "BasaltUI"));
        Add(choices, Combine(Path.GetTempPath(), "BasaltUI"));
        Add(choices, Path.Combine(AppContext.BaseDirectory, "BasaltUI"));   // a folder next to the exe: a portable install

        if (choices.Count == 0)
        {
            Root = Path.GetTempPath();
            Trouble = "no folder of ours could even be named";
            TryScratch();
            return;
        }

        var refused = new List<string>();
        for (var i = 0; i < choices.Count; i++)
        {
            try
            {
                MakeTree(choices[i]);
                Root = choices[i];
                Trouble = i == 0 ? null : string.Join("  ·  ", refused) + " — keeping its files in " + choices[i] + " instead";
                TryScratch();
                return;
            }
            catch (Exception e)
            {
                refused.Add(choices[i] + ": " + e.Message);
            }
        }

        // Nothing would take us. Say which place we wanted, so the message the user reads means something.
        Root = choices[0];
        Trouble = string.Join("  ·  ", refused) + " — it will run, and forget, until one of those is writable";
        TryScratch();
    }

    /// <summary>
    /// Move TMP/TEMP to a scratch folder of our own if we can make one, so that everything in the process —
    /// including WPF's own scratch writes — lands somewhere known to be writable.
    /// </summary>
    private static void TryScratch()
    {
        OriginalTemp = Path.GetTempPath();

        foreach (var where in new[] { Path.Combine(Root, "temp"), Path.Combine(AppContext.BaseDirectory, "temp") })
        {
            try
            {
                Directory.CreateDirectory(where);
                var probe = Path.Combine(where, "probe.tmp");
                File.WriteAllText(probe, "a word");
                if (File.ReadAllText(probe) != "a word")
                    throw new IOException("wrote it and could not read it back");
                File.Delete(probe);

                ScratchDir = where;
                foreach (var name in new[] { "TMP", "TEMP", "TMPDIR" })
                    Environment.SetEnvironmentVariable(name, where);
                return;
            }
            catch
            {
                // this candidate will not take a file either; try the next one, and if none of them will,
                // leave TMP/TEMP as Windows set them
            }
        }
    }

    private static void MakeTree(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var sub in new[] { "logs", "config-backups", "updates" })
            Directory.CreateDirectory(Path.Combine(root, sub));
    }

    private static void Add(List<string> to, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return;   // an unusable BASALTUI_HOME is simply not a place to try
        }

        foreach (var already in to)
            if (string.Equals(already, full, StringComparison.OrdinalIgnoreCase))
                return;

        to.Add(full);
    }

    private static string? Special(Environment.SpecialFolder which)
    {
        try
        {
            return Environment.GetFolderPath(which);
        }
        catch
        {
            return null;
        }
    }

    private static string? Combine(string? parent, string leaf)
        => string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, leaf);

    public static void Ensure()
    {
        try
        {
            MakeTree(Root);
        }
        catch
        {
            // the caller is about to report its own failure; Trouble already tells the wider story
        }
    }

    /// <summary>Fires a best-effort write; a logging failure must never take the app down.</summary>
    public static void Log(string line)
    {
        try
        {
            Ensure();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // ignored on purpose: what went wrong with the log is what Trouble is for
        }
    }
}
