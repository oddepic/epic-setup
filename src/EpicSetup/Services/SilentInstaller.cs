using System.Diagnostics;
using System.IO;
using EpicSetup.Models;

namespace EpicSetup.Services;

/// <summary>
/// Runs an installer silently based on the catalog-supplied installer type and
/// optional explicit silent args. Returns the installer's exit code.
/// </summary>
public sealed class SilentInstaller
{
    public int Run(AppEntry app, string localPath, CancellationToken ct)
    {
        var type = app.ParsedType;
        if (type == AppInstallerType.Auto)
        {
            type = localPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? AppInstallerType.Msi
                : AppInstallerType.Nsis; // reasonable default for `.exe`
        }

        switch (type)
        {
            case AppInstallerType.Msi:
                return RunProcess("msiexec.exe",
                    $"/i \"{localPath}\" {app.SilentArgs?.Trim() ?? "/quiet /norestart INSTALLUSERCONTEXT=1"}",
                    Path.GetDirectoryName(localPath)!, ct);

            case AppInstallerType.Inno:
                return RunProcess(localPath,
                    app.SilentArgs?.Trim().Length > 0
                        ? app.SilentArgs
                        : "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-",
                    ct);

            case AppInstallerType.Nsis:
                return RunProcess(localPath,
                    app.SilentArgs?.Trim().Length > 0 ? app.SilentArgs : "/S",
                    ct);

            case AppInstallerType.Burn:
                return RunProcess(localPath, app.SilentArgs ?? "/quiet", ct);

            case AppInstallerType.Exe:
                return RunProcess(localPath, app.SilentArgs ?? "", ct);

            case AppInstallerType.Portable:
                return DeployPortable(app, localPath);

            case AppInstallerType.Script:
                return RunScript(app.SilentArgs ?? "", ct);

            case AppInstallerType.WingetUpdate:
                return RunScript(
                    "winget source update --disable-interactivity; " +
                    "winget upgrade --id Microsoft.AppInstaller -e --silent " +
                    "--accept-source-agreements --accept-package-agreements",
                    ct);

            default:
                return RunProcess(localPath, app.SilentArgs ?? "", ct);
        }
    }

    private static int RunProcess(string fileName, string arguments, string? workingDir, CancellationToken ct)
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
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } });
        p.WaitForExit();
        try { outTask.Wait(ct); } catch { }
        try { errTask.Wait(ct); } catch { }
        return p.ExitCode;
    }

    private static int RunProcess(string fileName, string arguments, CancellationToken ct) =>
        RunProcess(fileName, arguments, null, ct);

    private static int RunScript(string command, CancellationToken ct)
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
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } });
        p.WaitForExit();
        try { outTask.Wait(ct); } catch { }
        try { errTask.Wait(ct); } catch { }
        return p.ExitCode;
    }

    private static int DeployPortable(AppEntry app, string localPath)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", app.PortableSubdir ?? app.Id);
        Directory.CreateDirectory(root);
        var dest = Path.Combine(root, Path.GetFileName(localPath));
        File.Copy(localPath, dest, true);
        return 0;
    }
}