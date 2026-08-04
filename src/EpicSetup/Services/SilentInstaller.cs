using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
                if (app.RunAsUser)
                    return await RunUnelevatedAsync(localPath, app.SilentArgs ?? "", timeout, ct);
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

    /// <summary>
    /// Runs an installer with a medium-integrity, privilege-restricted token so
    /// per-user Squirrel installers (Spotify) don't refuse to run as admin.
    /// </summary>
    private static async Task<RunResult> RunUnelevatedAsync(string fileName, string arguments,
        int timeoutSeconds, CancellationToken ct)
    {
        var p = StartUnelevated(fileName, arguments ?? "");
        if (p is null)
            return new RunResult { ExitCode = -1 };

        using (p)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                KillTree(p);
                if (ct.IsCancellationRequested) throw;
                return new RunResult { ExitCode = -1, TimedOut = true };
            }
            return new RunResult { ExitCode = p.ExitCode };
        }
    }

    private static Process? StartUnelevated(string fileName, string arguments)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenAssignPrimary | TokenQuery, out var hToken))
            return null;
        try
        {
            if (!DuplicateTokenEx(hToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var hDup))
                return null;
            try
            {
                if (!CreateRestrictedToken(hDup, DisableMaxPrivilege, 0, null, 0, null, 0, null, out var hRestricted))
                    return null;
                try
                {
                    if (!ConvertStringSidToSid("S-1-16-8192", out var pMediumSid)) // Medium integrity
                        return null;
                    try
                    {
                        var label = new TOKEN_MANDATORY_LABEL
                        {
                            Label = new SID_AND_ATTRIBUTES { Sid = pMediumSid, Attributes = SeGroupIntegrity }
                        };
                        int size = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>();
                        IntPtr pLabel = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(label, pLabel, false);
                            if (!SetTokenInformation(hRestricted, TokenIntegrityLevel, pLabel, size))
                                return null;
                        }
                        finally { Marshal.FreeHGlobal(pLabel); }

                        var si = new STARTUPINFOW
                        {
                            cb = Marshal.SizeOf<STARTUPINFOW>(),
                            dwFlags = StartfUseShowWindow,
                            wShowWindow = 0 // SW_HIDE
                        };
                        var cmd = new System.Text.StringBuilder($"\"{fileName}\" {arguments}".Trim());
                        if (!CreateProcessWithTokenW(hRestricted, LogonWithProfile, null, cmd,
                                CreateNoWindow, IntPtr.Zero, null, ref si, out var pi))
                            return null;
                        try
                        {
                            var proc = Process.GetProcessById(pi.dwProcessId);
                            CloseHandle(pi.hProcess);
                            CloseHandle(pi.hThread);
                            return proc;
                        }
                        catch
                        {
                            CloseHandle(pi.hProcess);
                            CloseHandle(pi.hThread);
                            return null;
                        }
                    }
                    finally { FreeSid(pMediumSid); }
                }
                finally { CloseHandle(hRestricted); }
            }
            finally { CloseHandle(hDup); }
        }
        finally { CloseHandle(hToken); }
    }

    #region De-elevation P/Invoke

    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint DisableMaxPrivilege = 0x1;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint CreateNoWindow = 0x08000000;
    private const uint LogonWithProfile = 0x00000001;
    private const uint StartfUseShowWindow = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existingToken, uint flags,
        uint disallowSidCount, IntPtr[]? sidsToDisallow, uint restrictSidCount, IntPtr[]? sidsToRestrict,
        uint privilegesCount, IntPtr[]? privilegesToDelete, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags,
        string? lpApplicationName, System.Text.StringBuilder lpCommandLine, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    #endregion

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
