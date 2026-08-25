namespace EpicSetup.Services;

/// <summary>
/// Translates winget CLI exit codes (HRESULTs from AppInstallerErrors.h) into
/// readable outcomes, and adapts to older winget builds that reject newer
/// command-line flags. Reference: winget-cli doc/windows/package-manager/
/// winget/returnCodes.md.
/// </summary>
public static class WingetResult
{
    public const int UpdateNotApplicable = -1978335189;   // 0x8A15002B
    public const int InvalidCommandLine = -1978335230;    // 0x8A150002
    public const int PackageAlreadyInstalled = -1978335213; // 0x8A15000B is sources; this maps 0x8A150023? keep explicit below

    /// <summary>True when the code means "nothing left to do" (installed / up to date).</summary>
    public static bool IsNoOpSuccess(int exitCode) => exitCode switch
    {
        0 => true,
        UpdateNotApplicable => true,
        _ => false
    };

    /// <summary>Human-readable explanation for a non-zero winget exit code.</summary>
    public static string Explain(int exitCode) => exitCode switch
    {
        -1978335231 => "winget internal error",
        InvalidCommandLine => "winget rejected the command line (older winget build?)",
        -1978335229 => "winget command failed",
        -1978335228 => "manifest could not be opened",
        -1978335226 => "ShellExecute install failed",
        -1978335224 => "installer download failed",
        -1978335217 => "no applicable installer for this system",
        -1978335215 => "installer hash mismatch",
        -1978335206 => "package not found in any source",
        -1978335205 => "Microsoft Store access blocked by policy",
        -1978335183 => "package already installed (newer or equal version)",
        UpdateNotApplicable => "already installed; no upgrade available",
        -1978335168 => "install blocked: package requires interaction or is pinned",
        _ => $"winget error 0x{exitCode & 0xFFFFFFFF:X8}"
    };

    /// <summary>Arguments that old winget builds may not recognise.</summary>
    public static readonly string[] ModernOnlyArgs = { "--disable-interactivity" };

    /// <summary>Removes flags unknown to older winget builds.</summary>
    public static IReadOnlyList<string> StripModernArgs(IReadOnlyList<string> args)
    {
        var kept = new List<string>(args.Count);
        foreach (var a in args)
            if (!ModernOnlyArgs.Contains(a)) kept.Add(a);
        return kept;
    }
}
