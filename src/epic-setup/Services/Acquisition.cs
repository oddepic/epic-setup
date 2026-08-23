using System.Diagnostics;
using System.IO;
using EpicSetup.Models;

namespace EpicSetup.Services;

/// <summary>Which acquisition channel a source uses.</summary>
public enum BackendKind
{
    Url,
    GitHub,
    Winget,
    Scoop,
    Chocolatey
}

/// <summary>Result of checking whether a backend can run on this machine.</summary>
public sealed record BackendProbe(string DisplayName, bool Available, string? Detail);

/// <summary>
/// One acquisition backend. Probes availability, claims matching sources, and
/// resolves a claimed source into an install plan.
/// </summary>
public interface IAcquisitionBackend
{
    BackendKind Kind { get; }
    Task<BackendProbe> ProbeAsync(CancellationToken ct);
    bool CanResolve(AppSource source);
    Task<InstallPlan?> ResolveAsync(AppEntry app, AppSource source,
        CancellationToken ct, IProgress<string>? diagnostics);
}

/// <summary>Base class for resolved install intentions.</summary>
public abstract class InstallPlan
{
    /// <summary>Short backend tag for logs and status labels, e.g. "winget".</summary>
    public abstract string BackendTag { get; }
}

/// <summary>Download an artifact, then dispatch it through SilentInstaller.</summary>
public sealed class DownloadPlan : InstallPlan
{
    public override string BackendTag => Kind == BackendKind.GitHub ? "github" : "url";
    public BackendKind Kind { get; init; }
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
}

/// <summary>Delegate the whole install to an external tool (package manager).</summary>
public sealed class DelegatePlan : InstallPlan
{
    public override string BackendTag => "winget";
    public string Executable { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public int TimeoutSeconds { get; init; } = SilentInstaller.DefaultInstallTimeoutSeconds;
    /// <summary>Human-readable description for logs, e.g. "winget: 7zip.7zip".</summary>
    public string Description { get; init; } = "";
}

/// <summary>Tries an app's declared sources in order across all backends.</summary>
public sealed class SourceResolver
{
    private readonly IReadOnlyList<IAcquisitionBackend> _backends;

    public SourceResolver(IEnumerable<IAcquisitionBackend> backends)
        => _backends = backends.ToList();

    public static SourceResolver CreateDefault(GitHubReleaseResolver github)
        => new(new IAcquisitionBackend[]
        {
            new StaticDownloadBackend(github),
            new WingetBackend()
        });

    public IReadOnlyList<IAcquisitionBackend> Backends => _backends;

    /// <summary>
    /// Returns one plan per resolvable source, in the entry's declared order.
    /// Sources whose backend is unavailable or fails to resolve are skipped so
    /// the next source gets its chance.
    /// </summary>
    public async Task<IReadOnlyList<InstallPlan>> ResolveAsync(AppEntry app,
        CancellationToken ct, IProgress<string>? diagnostics)
    {
        void Detail(string m) => diagnostics?.Report(m);
        var plans = new List<InstallPlan>();

        foreach (var source in app.EffectiveSources)
        {
            ct.ThrowIfCancellationRequested();
            var backend = _backends.FirstOrDefault(b => b.CanResolve(source));
            if (backend is null)
            {
                Detail($"{app.Id}: no backend claims source kind '{source.KindName}'.");
                continue;
            }

            var probe = await backend.ProbeAsync(ct);
            if (!probe.Available)
            {
                Detail($"{app.Id}: backend {probe.DisplayName} unavailable; skipping its source.");
                continue;
            }

            try
            {
                var plan = await backend.ResolveAsync(app, source, ct, diagnostics);
                if (plan is not null)
                {
                    Detail($"{app.Id}: resolved via {plan.BackendTag} -> {Describe(plan)}.");
                    plans.Add(plan);
                }
                else
                {
                    Detail($"{app.Id}: {backend.Kind} backend could not resolve its source.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Detail($"{app.Id}: {backend.Kind} resolution failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return plans;
    }

    private static string Describe(InstallPlan plan) => plan switch
    {
        DownloadPlan d => d.Url,
        DelegatePlan d => d.Description,
        _ => plan.GetType().Name
    };
}

/// <summary>The catalog's classic channels: pinned URLs and GitHub releases.</summary>
public sealed class StaticDownloadBackend : IAcquisitionBackend
{
    private readonly GitHubReleaseResolver _github;

    public StaticDownloadBackend(GitHubReleaseResolver github) => _github = github;

    public BackendKind Kind => BackendKind.Url;

    public Task<BackendProbe> ProbeAsync(CancellationToken ct)
        => Task.FromResult(new BackendProbe("direct download", true, null));

    public bool CanResolve(AppSource source) => source.Kind switch
    {
        AppSourceKind.Url => !string.IsNullOrWhiteSpace(source.Url),
        AppSourceKind.GitHub => source.GitHub is not null
            && !string.IsNullOrWhiteSpace(source.GitHub.Repo),
        _ => false
    };

    public async Task<InstallPlan?> ResolveAsync(AppEntry app, AppSource source,
        CancellationToken ct, IProgress<string>? diagnostics)
    {
        if (source.Kind == AppSourceKind.GitHub && source.GitHub is not null)
        {
            var gh = source.GitHub;
            diagnostics?.Report($"{app.Id}: resolving GitHub release {gh.Repo} asset /{gh.AssetPattern}/.");
            var asset = await _github.ResolveAsync(gh.Repo,
                string.IsNullOrEmpty(gh.AssetPattern) ? ".*" : gh.AssetPattern,
                gh.UseLatestPrerelease, ct);
            diagnostics?.Report($"{app.Id}: resolved asset {asset.Name} from {asset.Url}.");
            return new DownloadPlan { Kind = BackendKind.GitHub, Url = asset.Url, FileName = asset.Name };
        }

        if (string.IsNullOrWhiteSpace(source.Url)) return null;
        return new DownloadPlan
        {
            Kind = BackendKind.Url,
            Url = source.Url,
            FileName = SourceNaming.SafeName(source.Url)
        };
    }
}

/// <summary>Shared filename helpers for download plans.</summary>
public static class SourceNaming
{
    public static string SafeName(string url)
    {
        try
        {
            var seg = new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? "setup.exe";
            return NormalizeFileName(Uri.UnescapeDataString(seg));
        }
        catch { return "setup.exe"; }
    }

    public static string NormalizeFileName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "setup.exe" : name;
    }
}

/// <summary>
/// Installs through the winget CLI. Chosen over the COM API because COM
/// activation hard-crashes for elevated, unpackaged, self-contained executables
/// (winget-cli #4377), which is exactly this app's binary shape.
/// </summary>
public sealed class WingetBackend : IAcquisitionBackend
{
    public const string StabilityArgs = "--accept-package-agreements --accept-source-agreements --disable-interactivity";

    private static readonly string[] CandidatePaths =
    {
        "winget",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "WindowsApps", "winget.exe")
    };

    private BackendProbe? _probe;
    private string? _exe;

    public BackendKind Kind => BackendKind.Winget;

    public async Task<BackendProbe> ProbeAsync(CancellationToken ct)
    {
        if (_probe is not null) return _probe;
        foreach (var candidate in CandidatePaths)
        {
            var (ok, detail) = await TryVersionAsync(candidate, ct);
            if (ok)
            {
                _exe = candidate;
                _probe = new BackendProbe("winget", true, detail);
                return _probe;
            }
        }
        _probe = new BackendProbe("winget", false, "winget executable not found");
        return _probe;
    }

    public bool CanResolve(AppSource source)
        => source.Kind == AppSourceKind.Winget && !string.IsNullOrWhiteSpace(source.WingetId);

    public Task<InstallPlan?> ResolveAsync(AppEntry app, AppSource source,
        CancellationToken ct, IProgress<string>? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(source.WingetId)) return Task.FromResult<InstallPlan?>(null);
        var plan = new DelegatePlan
        {
            Executable = _exe ?? "winget",
            Arguments = new[]
            {
                "install", "--id", source.WingetId.Trim(), "--exact", "--source", "winget",
                "--silent", StabilityArgs
            },
            TimeoutSeconds = app.InstallTimeoutSeconds ?? SilentInstaller.DefaultInstallTimeoutSeconds,
            Description = $"winget: {source.WingetId}"
        };
        return Task.FromResult<InstallPlan?>(plan);
    }

    /// <summary>Builds the winget argument list; exposed for tests.</summary>
    public static IReadOnlyList<string> BuildInstallArguments(string wingetId)
        => new[]
        {
            "install", "--id", wingetId.Trim(), "--exact", "--source", "winget",
            "--silent", StabilityArgs
        };

    private static async Task<(bool ok, string? version)> TryVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, null);
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, null);
            }
            if (p.ExitCode != 0) return (false, null);
            var version = (await outTask).Trim();
            return version.Length > 0 ? (true, version) : (false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (false, null); }
    }
}
