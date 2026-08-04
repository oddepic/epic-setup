using System.Diagnostics;
using System.IO;
using EpicSetup.Models;

namespace EpicSetup.Services;

/// <summary>
/// Runs an installer silently based on the catalog-supplied installer type and
/// optional explicit silent args. Returns the installer's exit code and whether
/// it hit the per-app timeout (in which case the process tree was terminated).
/// Running app processes listed in the catalog's <c>closeApps</c> are killed
/// before the installer starts, so silent installs never block on a
/// "the app is running, please close it" prompt.
/// </summary>
public sealed class SilentInstaller
{
    public const int DefaultInstallTimeoutSeconds = 600;

    public sealed class RunResult
    {
        public int ExitCode { get; init; }
        public bool TimedOut { get; init; }
    }

    public async Task<RunResult> RunAsync(AppEntry app, string localPath, CancellationToken ct)
    {
        CloseRunning(app.CloseApps);

        var type = app.ParsedType;
        if (type == AppInstallerType.Auto)
        {
            type = localPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? AppInstallerType.Msi
                : AppInstallerType.Nsis; // reasonable default for `.exe`
        }

        int timeout = app.InstallTimeoutSeconds ?? DefaultInstallTimeoutSeconds;

        switch (type)
        {
            case AppInstallerType.Msi:
                return await RunProcessAsync("msiexec.exe",
                    $"/i \"{localPath}\" {app.SilentArgs?.Trim() ?? "/quiet /norestart INSTALLUSERCONTEXT=1"}",
                    Path.GetDirectoryName(localPath)!, timeout, ct);

            case AppInstallerType.Inno:
                return await RunProcessAsync(localPath,
                    app.SilentArgs?.Trim().Length > 0
                        ? app.SilentArgs
                        : "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-",
                    timeout, ct);

            case AppInstallerType.Nsis:
                return await RunProcessAsync(localPath,
                    app.SilentArgs?.Trim().Length > 0 ? app.SilentArgs : "/S",
                    timeout, ct);

            case AppInstallerType.Burn:
                return await RunProcessAsync(localPath, app.SilentArgs ?? "/quiet", timeout, ct);

            case AppInstallerType.Exe:
                return await RunProcessAsync(localPath, app.SilentArgs ?? "", timeout, ct);

            case AppInstallerType.Portable:
                return DeployPortable(app, localPath);

            case AppInstallerType.Script:
                return await RunScriptAsync(app.SilentArgs ?? "", timeout, ct);

            case AppInstallerType.WingetUpdate:
                return await RunScriptAsync(
                    "winget source update --disable-interactivity; " +
                    "winget upgrade --id Microsoft.AppInstaller -e --silent " +
                    "--accept-source-agreements --accept-package-agreements",
                    timeout, ct);

            default:
                return await RunProcessAsync(localPath, app.SilentArgs ?? "", timeout, ct);
        }
    }

    /// <summary>Kills any running process matching the catalog's closeApps names.</summary>
    private static void CloseRunning(IReadOnlyList<string> names)
    {
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                    try { p.WaitForExit(2000); } catch { }
                    p.Dispose();
                }
            }
            catch
            {
                // process enumeration can race; ignore
            }
        }
    }

    private static async Task<RunResult> RunProcessAsync(string fileName, string arguments,
        string? workingDir, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = new Process { StartInfo = psi };
        p.Start();
        // Drain output so the child never blocks on a full pipe.
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            try { await outTask; } catch { }
            try { await errTask; } catch { }
            if (ct.IsCancellationRequested) throw; // user cancelled: propagate
            return new RunResult { ExitCode = -1, TimedOut = true };
        }
        try { await outTask; } catch { }
        try { await errTask; } catch { }
        return new RunResult { ExitCode = p.ExitCode };
    }

    private static Task<RunResult> RunProcessAsync(string fileName, string arguments,
        int timeoutSeconds, CancellationToken ct) =>
        RunProcessAsync(fileName, arguments, null, timeoutSeconds, ct);

    private static async Task<RunResult> RunScriptAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            try { await outTask; } catch { }
            try { await errTask; } catch { }
            if (ct.IsCancellationRequested) throw;
            return new RunResult { ExitCode = -1, TimedOut = true };
        }
        try { await outTask; } catch { }
        try { await errTask; } catch { }
        return new RunResult { ExitCode = p.ExitCode };
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(3000); } catch { }
    }

    private static RunResult DeployPortable(AppEntry app, string localPath)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", app.PortableSubdir ?? app.Id);
        Directory.CreateDirectory(root);
        var dest = Path.Combine(root, Path.GetFileName(localPath));
        File.Copy(localPath, dest, true);
        return new RunResult { ExitCode = 0 };
    }
}
