using System;
using System.Diagnostics;
using System.IO;

namespace BasaltUI.Core;

/// <summary>
/// Downloading the app's own installer and letting it finish after this copy has closed. A Windows exe
/// cannot overwrite itself while it is running, so the actual replacement is done by a small detached
/// helper that waits for our pid, then runs the installer (or drops a bare exe over this one) and relaunches.
/// </summary>
public static class AppUpdateInstaller
{
    public static async Task<string?> DownloadAsync(AppRelease release, AppSettings settings,
                                                    IProgress<DownloadProgress>? progress,
                                                    Action<string>? trace, CancellationToken ct)
    {
        var asset = release.Asset;
        if (asset is null || asset.Url.Length == 0)
            return "this release does not carry a Windows exe to download";
        Directory.CreateDirectory(AppPaths.UpdateDir);
        var path = Path.Combine(AppPaths.UpdateDir, asset.Name);
        var job = new DownloadJob(new Uri(asset.Url), path, asset.Sha256, asset.Size, asset.Name);
        return await SegmentedDownload.RunAsync(job, settings.DownloadConnections, progress, trace, ct)
                                      .ConfigureAwait(false);
    }

    public static string LocalPathFor(AppRelease release)
        => release.Asset is { } a ? Path.Combine(AppPaths.UpdateDir, a.Name) : "";

    /// <summary>Starts the after-exit helper and returns its path, for the log.</summary>
    public static string ScheduleInstall(string newExe, int appPid, string currentExe)
    {
        var helper = Path.Combine(AppPaths.UpdateDir, "apply-app-update.ps1");
        var body = string.Join("\n",
            "param([int]$AppPid, [string]$NewExe, [string]$CurrentExe)",
            "$deadline = (Get-Date).AddMinutes(10)",
            "while ((Get-Process -Id $AppPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {",
            "    Start-Sleep -Milliseconds 400",
            "}",
            "if (-not (Test-Path $NewExe)) { exit 1 }",
            "if ($NewExe -match '-setup\\.exe$') {",
            "    Start-Process -FilePath $NewExe -ArgumentList '/VERYSILENT /NORESTART /RELAUNCH' -WorkingDirectory (Split-Path $NewExe)",
            "} else {",
            "    try { Copy-Item $CurrentExe \"$CurrentExe.previous\" -Force -ErrorAction SilentlyContinue } catch {}",
            "    Copy-Item $NewExe $CurrentExe -Force",
            "    Start-Process -FilePath $CurrentExe",
            "}");
        File.WriteAllText(helper, body + "\n");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                    "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + helper + "\"" +
                        " -AppPid " + appPid +
                        " -NewExe \"" + newExe + "\"" +
                        " -CurrentExe \"" + currentExe + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        return helper;
    }
}
