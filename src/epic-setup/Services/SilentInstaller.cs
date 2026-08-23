using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    public const int DefaultInstallTimeoutSeconds = 180;
    public const int LaunchProbeSeconds = 10;

    public sealed class RunResult
    {
        public int ExitCode { get; init; }
        public bool TimedOut { get; init; }
    }

    public async Task<RunResult> RunAsync(AppEntry app, string localPath, CancellationToken ct,
        IProgress<string>? diagnostics = null, bool launchOnly = false)
    {
        void Detail(string message) => diagnostics?.Report(message);

        if (app.CloseApps.Count > 0)
            Detail($"closeApps: terminating {string.Join(", ", app.CloseApps)} if running.");
        CloseRunning(app.CloseApps);

        var type = app.ParsedType;
        if (type == AppInstallerType.Auto)
        {
            type = localPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? AppInstallerType.Msi
                : AppInstallerType.Nsis; // reasonable default for `.exe`
        }

        int timeout = app.InstallTimeoutSeconds ?? DefaultInstallTimeoutSeconds;
        Detail($"dispatch: type={type}, timeout={timeout}s.");

        switch (type)
        {
            case AppInstallerType.Portable:
                Detail($"portable destination: {PortableRoot(app)}.");
                return DeployPortable(app, localPath);

            case AppInstallerType.Zip:
                Detail($"zip destination: {PortableRoot(app)}.");
                return DeployZip(app, localPath);

            case AppInstallerType.Script:
                Detail($"script command: {app.SilentArgs ?? string.Empty}");
                return await RunScriptAsync(app.SilentArgs ?? "", timeout, ct);

            case AppInstallerType.WingetUpdate:
                Detail("running hard-coded winget update command.");
                return await RunScriptAsync(
                    "winget source update --disable-interactivity; " +
                    "winget upgrade --id Microsoft.AppInstaller -e --silent " +
                    "--accept-source-agreements --accept-package-agreements",
                    timeout, ct);
        }

        // Process-based installer: use the catalog's intended install scope.
        var log = InstallerLogPath(app);
        string exe; string args;
        switch (type)
        {
            case AppInstallerType.Msi:
                exe = "msiexec.exe";
                args = $"/i \"{localPath}\" {app.SilentArgs?.Trim() ?? "/quiet /norestart"} /l*v \"{log}\"";
                break;

            case AppInstallerType.Inno:
                exe = localPath;
                args = WithLogFlag(app.SilentArgs?.Trim().Length > 0
                    ? app.SilentArgs
                    : "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-", log, "/LOG=\"{0}\"");
                break;

            case AppInstallerType.Nsis:
                exe = localPath;
                args = app.SilentArgs?.Trim().Length > 0 ? app.SilentArgs : "/S";
                break;

            case AppInstallerType.Burn:
                exe = localPath;
                args = WithLogFlag(app.SilentArgs ?? "/quiet", log, "/log \"{0}\"");
                break;

            default: // Exe (or Auto resolved to Exe-like)
                exe = localPath;
                args = app.SilentArgs ?? "";
                break;
        }

        var scope = app.RunAsUser ? "interactive-user" : "elevated";
        Detail($"launch: scope={scope}, executable=\"{exe}\", arguments={args}");
        if (launchOnly)
        {
            // Launch the interactive installer, then wait a short window so an
            // installer that fails immediately (e.g. missing dependency, bad
            // argument) is detected and not counted as launched. After the
            // window, the batch moves on and the user completes setup by hand.
            var p = app.RunAsUser ? StartAsInteractiveUser(exe, args) : StartProcess(exe, args);
            if (p is null)
            {
                Detail("launch-only: FAILED to start installer process.");
                return new RunResult { ExitCode = -1, TimedOut = false };
            }
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(LaunchProbeSeconds));
            try
            {
                await p.WaitForExitAsync(probeCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Still running after the probe window -> launch accepted.
                Detail("launch-only: installer running after probe window; not waiting (manual setup).");
                return new RunResult { ExitCode = 0, TimedOut = false };
            }
            var code = p.ExitCode;
            Detail(code == 0
                ? $"launch-only: installer exited quickly with code 0 within {LaunchProbeSeconds}s."
                : $"launch-only: installer FAILED early with exit code {code} within {LaunchProbeSeconds}s.");
            return new RunResult { ExitCode = code, TimedOut = false };
        }
        var result = app.RunAsUser
            ? await RunAsInteractiveUserAsync(exe, args, timeout, ct)
            : await RunElevatedAsync(exe, args, timeout, ct);
        Detail($"process result: exitCode={result.ExitCode}, timedOut={result.TimedOut}.");
        return result;
    }

    private static string InstallerLogPath(AppEntry app)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "epic-setup", "logs");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, app.Id + ".log");
    }

    private static string WithLogFlag(string args, string logPath, string fmt)
    {
        if (string.IsNullOrEmpty(args)) args = "";
        if (args.IndexOf("/log", StringComparison.OrdinalIgnoreCase) >= 0) return args;
        return (args + " " + string.Format(fmt, logPath)).Trim();
    }

    /// <summary>Kills any running process matching the catalog's closeApps names.</summary>
    internal static void CloseRunning(IReadOnlyList<string> names)
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

    /// <summary>
    /// Runs an arbitrary command with output captured for diagnostics. Used by
    /// delegated installs (package-manager backends).
    /// </summary>
    public async Task<RunResult> RunCommandAsync(string executable, IReadOnlyList<string> arguments,
        int timeoutSeconds, CancellationToken ct, IProgress<string>? diagnostics = null)
    {
        void Detail(string message) => diagnostics?.Report(message);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var shownArgs = string.Join(' ', arguments);
        Detail($"command: \"{executable}\" {shownArgs}");
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex)
        {
            Detail($"failed to start: {ex.Message}");
            return new RunResult { ExitCode = -1 };
        }

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
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
        Detail($"process result: exitCode={p.ExitCode}.");
        return new RunResult { ExitCode = p.ExitCode };
    }

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

    private static async Task<RunResult> RunElevatedAsync(string fileName, string arguments,
        int timeoutSeconds, CancellationToken ct)
    {
        var p = StartProcess(fileName, arguments)
            ?? throw new InvalidOperationException($"Could not start installer '{fileName}'.");
        return await WaitForExitAsync(p, timeoutSeconds, ct);
    }

    private static Process? StartProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        return Process.Start(psi);
    }

    private static async Task<RunResult> RunAsInteractiveUserAsync(string fileName, string arguments,
        int timeoutSeconds, CancellationToken ct)
    {
        var p = StartAsInteractiveUser(fileName, arguments ?? string.Empty);
        if (p is null)
            return new RunResult { ExitCode = -1 };
        return await WaitForExitAsync(p, timeoutSeconds, ct);
    }

    private static async Task<RunResult> WaitForExitAsync(Process p, int timeoutSeconds, CancellationToken ct)
    {
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

    /// <summary>
    /// Gets the medium-integrity token from the user's Explorer process. The
    /// application itself is elevated, so duplicating its own token does not
    /// produce a real per-user process.
    /// </summary>
    private static Process? StartAsInteractiveUser(string fileName, string arguments)
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var shell in Process.GetProcessesByName("explorer"))
        {
            using (shell)
            {
                try
                {
                    if (shell.SessionId != sessionId) continue;

                    var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, shell.Id);
                    if (hProcess == IntPtr.Zero) continue;
                    try
                    {
                        if (!OpenProcessToken(hProcess, TokenDuplicate | TokenQuery, out var hToken))
                            continue;
                        try
                        {
                            if (!DuplicateTokenEx(hToken, TokenProcessCreation, IntPtr.Zero,
                                    SecurityImpersonation, TokenPrimary, out var hUserToken))
                                continue;
                            try
                            {
                                var startup = new STARTUPINFOW
                                {
                                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                                    lpDesktop = "winsta0\\default",
                                    dwFlags = StartfUseShowWindow,
                                    wShowWindow = 0
                                };
                                var command = new System.Text.StringBuilder(
                                    $"\"{fileName}\" {arguments}".Trim());
                                var currentDirectory = Path.GetDirectoryName(fileName);
                                if (!CreateProcessWithTokenW(hUserToken, LogonWithProfile, fileName,
                                        command, CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero,
                                        currentDirectory, ref startup, out var processInfo))
                                    continue;
                                try
                                {
                                    return Process.GetProcessById(processInfo.dwProcessId);
                                }
                                finally
                                {
                                    CloseHandle(processInfo.hProcess);
                                    CloseHandle(processInfo.hThread);
                                }
                            }
                            finally { CloseHandle(hUserToken); }
                        }
                        finally { CloseHandle(hToken); }
                    }
                    finally { CloseHandle(hProcess); }
                }
                catch
                {
                    // The shell can exit while its token is being opened.
                }
            }
        }
        return null;
    }

    #region Process token P/Invoke

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint TokenProcessCreation = TokenAssignPrimary | TokenDuplicate | TokenQuery |
                                               TokenAdjustDefault | TokenAdjustSessionId;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint LogonWithProfile = 0x00000001;
    private const uint StartfUseShowWindow = 0x00000001;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags,
        string? lpApplicationName, System.Text.StringBuilder lpCommandLine, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion

    private static RunResult DeployPortable(AppEntry app, string localPath)
    {
        var root = PortableRoot(app);
        Directory.CreateDirectory(root);
        var dest = Path.Combine(root, Path.GetFileName(localPath));
        File.Copy(localPath, dest, true);
        return new RunResult { ExitCode = 0 };
    }

    /// <summary>Extracts a .zip archive to %LOCALAPPDATA%\Programs\{portableSubdir}.</summary>
    private static RunResult DeployZip(AppEntry app, string localPath)
    {
        var root = PortableRoot(app);
        Directory.CreateDirectory(root);

        var fullRoot = Path.GetFullPath(root);
        using var zip = ZipFile.OpenRead(localPath);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            // zip-slip guard: never write outside the destination folder
            if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return new RunResult { ExitCode = 0 };
    }

    private static string PortableRoot(AppEntry app)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", app.PortableSubdir ?? app.Id);
}
