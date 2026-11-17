using System.Diagnostics;
using System.IO;
using System.Text;

namespace BasaltUI.Core;

public enum RunState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>Everything needed to launch a basalt server the way the command line would.</summary>
public sealed class LaunchSpec
{
    public required string ExePath { get; init; }
    public required string ConfigPath { get; init; }
    public string ExtraArgs { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";

    /// <summary>The port the config file says the server will answer on; how the server's own processes are found.</summary>
    public int Port { get; init; }

    /// <summary>`serve --config <file>`, then the api key, then whatever the user typed. Quotes are handled here.</summary>
    public string BuildArguments()
    {
        var sb = new StringBuilder("serve --config ");
        sb.Append(Quote(ConfigPath));
        if (!string.IsNullOrWhiteSpace(ApiKey))
            sb.Append(" --api-key ").Append(Quote(ApiKey.Trim()));
        if (!string.IsNullOrWhiteSpace(ExtraArgs))
            sb.Append(' ').Append(ExtraArgs.Trim());
        return sb.ToString();
    }

    public static string Quote(string value) => value.Contains(' ') ? "\"" + value + "\"" : value;
}

/// <summary>
/// Owns one basalt server process: starts it in a hidden console, pipes its console log, and stops it the way
/// Ctrl+C does (basalt's clean path: QUIT to the engine, flush caches, close sockets) falling back to a kill.
/// A process started elsewhere can be adopted for watching and, on request, stopped through its own console.
/// </summary>
public sealed class BasaltProcess : IDisposable
{
    private readonly object gate = new();
    private Process? proc;
    private int adoptedPid;
    private bool stoppingByUs;
    private int exitCode = -1;
    private readonly List<string> lines = [];

    public event Action<string>? ConsoleLine;           // basalt's own console output
    public event Action<string>? Notice;                // what this controller did, for the log pane
    public event Action<RunState, string>? StateChanged;

    public RunState State { get; private set; } = RunState.Stopped;
    public string StateDetail { get; private set; } = "Stopped";
    public IReadOnlyList<string> Lines { get { lock (gate) return lines.ToArray(); } }

    public int Pid
    {
        get
        {
            lock (gate)
            {
                try { return proc?.Id ?? adoptedPid; }
                catch { return adoptedPid; }
            }
        }
    }

    public bool StartedByUs { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LaunchError { get; private set; }

    /// <summary>The port this server answers on: the reliable way back to its processes, since basalt's launcher
    /// hands the work to a helper that is gone again and its children are no longer findable from our own pid.</summary>
    public int ServePort { get; set; }

    public bool IsAlive
    {
        get
        {
            lock (gate)
            {
                try
                {
                    if (proc is { } p && !p.HasExited)
                        return true;
                    if (proc is null && adoptedPid > 0)
                        return Process.GetProcessById(adoptedPid) is not null;
                }
                catch (ArgumentException)
                {
                    return false;                                    // GetProcessById: the pid is gone
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch { /* the process object is being torn down */ }
                return false;
            }
        }
    }

    // ---- start ---------------------------------------------------------------------------------------------
    public bool Start(LaunchSpec spec)
    {
        lock (gate)
        {
            if (IsAlive)
                return false;
            LaunchError = null;

            if (!File.Exists(spec.ExePath))
            {
                Fail("basalt.exe was not found: " + spec.ExePath);
                return false;
            }
            if (!File.Exists(spec.ConfigPath))
            {
                Fail("the config file was not found: " + spec.ConfigPath);
                return false;
            }

            try
            {
                var workDir = spec.WorkingDirectory.Length > 0 ? spec.WorkingDirectory : DirOf(spec.ExePath);

                // PYTHONUTF8/PYTHONIOENCODING: basalt's log carries non-ASCII, and a redirected pipe would
                // otherwise use the ANSI code page and turn those lines into decoding errors.
                var psi = new ProcessStartInfo
                {
                    FileName = spec.ExePath,
                    Arguments = spec.BuildArguments(),
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,                             // CREATE_NO_WINDOW: an invisible console, which is what Ctrl+C needs
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.Environment["PYTHONUTF8"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment.Remove("PYTHONWARNINGS");

                var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (_, e) => Emit(e.Data, false);
                p.ErrorDataReceived += (_, e) => Emit(e.Data, true);
                p.Exited += (_, _) => OnExited(p);
                proc = p;
                StartedByUs = true;
                adoptedPid = 0;
                ServePort = spec.Port;
                StartedAt = DateTime.Now;
                stoppingByUs = false;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                Set(RunState.Starting, $"Starting — pid {p.Id}");
                Notice?.Invoke($"started {Path.GetFileName(spec.ExePath)} {spec.BuildArguments()}  (pid {p.Id})");
                AppPaths.Log($"start: {spec.ExePath} {spec.BuildArguments()}");
                return true;
            }
            catch (Exception e)
            {
                Fail("could not start basalt: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>Takes over the display of a server the user started themselves; we do not own that process.</summary>
    public void Adopt(int pid, string detail)
    {
        lock (gate)
        {
            if (proc is not null)
                return;
            adoptedPid = pid;
            StartedByUs = false;
            StartedAt = null;
            Set(RunState.Running, detail);
        }
    }

    public void ClearAdoption()
    {
        lock (gate)
        {
            if (proc is null && adoptedPid != 0)
            {
                adoptedPid = 0;
                Set(RunState.Stopped, "Stopped");
            }
        }
    }

    // ---- stop --------------------------------------------------------------------------------------------
    /// <summary>
    /// True graceful stop: Ctrl+C into the server's console, so basalt sends QUIT to its engine and closes
    /// cleanly. Only when that does not land do we terminate the tree — always for a server we started,
    /// and for an adopted one only when the user directed this very stop (a restart pick names the server
    /// to replace; leaving it running would leave the port held and the restart nothing).
    /// </summary>
    public async Task<bool> StopAsync(int graceSeconds, bool force = false, bool userDirected = false)
    {
        int pid;
        int[] kids;
        bool owned;
        lock (gate)
        {
            if (!IsAlive)
                return true;
            pid = Pid;
            owned = StartedByUs;
            kids = NativeMethods.ServerTree(pid, ServePort).Where(p => p != pid).ToArray();
            stoppingByUs = true;
            Set(RunState.Stopping, "Stopping — Ctrl+C to the server");
        }

        AppPaths.Log($"stop: our pid {pid}, port {ServePort}, the rest of the server [{string.Join(", ", kids)}]");
        var sent = NativeMethods.SendCtrlC(pid, kids, AppPaths.Log);        if (!sent)
            Notice?.Invoke(owned
                ? "no console to send Ctrl+C to — terminating the process instead"
                : "that server runs in another session; its console is not reachable");

        var seconds = force ? 2 : Math.Max(2, graceSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var retryAt = sent ? DateTime.UtcNow.AddSeconds(seconds * 0.35) : DateTime.MaxValue;
        var finalAt = sent ? DateTime.UtcNow.AddSeconds(seconds * 0.75) : DateTime.MaxValue;
        var resent = false;
        var finished = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive)
                return FinishStopped(sent ? "stopped" : "terminated");

            // basalt's own protocol: the first signal stops the server politely, the next one ends a
            // stubborn engine at once. The server is a process-group root, though — a plain Ctrl+C can
            // be masked for it entirely — so the later signals go as Ctrl+Break, which Windows will not
            // let a group root refuse, and which Python turns into the same KeyboardInterrupt.
            if (!resent && DateTime.UtcNow >= retryAt)
            {
                resent = NativeMethods.SendCtrlC(pid, NativeMethods.ServerTree(pid, ServePort).Where(p => p != pid),
                                                 AppPaths.Log, breakInstead: true);
                if (resent)
                    Notice?.Invoke("still running — Ctrl+Break, which the server hears over its mask");
            }
            else if (resent && !finished && DateTime.UtcNow >= finalAt)
            {
                finished = true;
                NativeMethods.SendCtrlC(pid, NativeMethods.ServerTree(pid, ServePort).Where(p => p != pid),
                                        AppPaths.Log, breakInstead: true);
            }
            await Task.Delay(150);
        }

        if (!owned && !userDirected)
        {
            // Not our child, and nobody here directed this stop: sending Ctrl+C was a favour the user
            // asked for. Killing somebody else's server, or everything under it, is not ours to decide.
            Notice?.Invoke("that server did not stop and is not ours to terminate — leaving it alone");
            lock (gate)
            {
                stoppingByUs = false;
                if (proc is null && adoptedPid != 0)
                    Set(RunState.Running, "running — started elsewhere");
            }
            return false;
        }

        Notice?.Invoke(!owned
            ? "the server this restart replaces did not answer Ctrl+C — ending it forcibly, as the restart asked"
            : $"still running after {seconds} s — terminating the process tree");
        ForceKillTree(pid);
        for (var i = 0; i < 40 && IsAlive; i++)
            await Task.Delay(100);
        return FinishStopped("terminated");
    }

    private bool FinishStopped(string how)
    {
        lock (gate)
        {
            adoptedPid = 0;
            Set(RunState.Stopped, how);
            Notice?.Invoke("server " + how);
        }
        return true;
    }

    public void ForceKillTree(int pid)
    {
        try
        {
            var kids = pid > 0 ? NativeMethods.ChildProcesses(pid) : [];
            var target = proc;
            if (target is not null)
                target.Kill(entireProcessTree: true);
            else if (pid > 0)
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
            foreach (var kid in kids)
            {
                try { Process.GetProcessById(kid).Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
        }
        catch (Exception e)
        {
            Notice?.Invoke("could not terminate: " + e.Message);
        }
    }

    // ---- internals ---------------------------------------------------------------------------------------
    private void OnExited(Process p)
    {
        int? code = null;
        try
        {
            p.WaitForExit(2000);
            code = p.ExitCode;
        }
        catch { /* a fast exit can lose the code */ }

        lock (gate)
        {
            exitCode = code ?? -1;
            proc = null;
            if (stoppingByUs)
                return;                                                // StopAsync reports the stop itself
            if (code == 0)
                Set(RunState.Stopped, "Stopped (exit 0)");
            else
                Fail($"basalt exited with code {code}" + (code == -1073741510 ? " (killed)" : ""));
        }
        Notice?.Invoke($"process exited ({code})");
    }

    private void Emit(string? line, bool isError)
    {
        if (line is null)
            return;
        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > 4000)
                lines.RemoveRange(0, 1000);
        }
        ConsoleLine?.Invoke(isError ? "! " + line : line);

        // the server prints its own milestones; use them to refine the state early
        if (line.Contains("] ready", StringComparison.Ordinal) || line.Contains("[server] weights loaded", StringComparison.Ordinal))
            MaybeRunning();
        else if (line.Contains("Loading", StringComparison.OrdinalIgnoreCase) || line.Contains("[server] load", StringComparison.Ordinal))
            lock (gate) { if (State == RunState.Starting) Set(RunState.Starting, "Loading the model…"); }
        else if (line.Contains("[server] swap", StringComparison.Ordinal))
            Notice?.Invoke(line.Trim());
    }

    private void MaybeRunning()
    {
        lock (gate)
        {
            if (State is RunState.Starting or RunState.Running && IsAlive)
                Set(RunState.Running, $"Running — pid {Pid}");
        }
    }

    private void Fail(string message)
    {
        LaunchError = message;
        Notice?.Invoke(message);
        AppPaths.Log("error: " + message);
        Set(RunState.Failed, message);
    }

    private void Set(RunState state, string detail)
    {
        State = state;
        StateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    private static string DirOf(string file)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(file)) ?? Environment.CurrentDirectory; }
        catch { return Environment.CurrentDirectory; }
    }

    public int? LastExitCode => exitCode;

    /// <summary>True when the process ended inside the span: a start that dies at once never served,
    /// and its exit code is the honest answer rather than a launch that looked fine.</summary>
    public async Task<bool> WaitForEarlyExitAsync(TimeSpan span)
    {
        var end = DateTime.UtcNow + span;
        while (DateTime.UtcNow < end)
        {
            if (!IsAlive)
                return true;
            await Task.Delay(250);
        }
        return !IsAlive;
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { proc?.Close(); } catch { /* already gone */ }
            proc = null;
        }
    }
}
