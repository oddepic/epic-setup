using System.IO;
using EpicSetup.Models;

namespace EpicSetup.Services;

public sealed class InstallUpdate
{
    public string? AppId { get; init; }
    public AppStatus Status { get; init; }
    public string? Message { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public string? Diagnostic { get; init; }
    public double DownloadFraction { get; init; }   // 0..1 byte progress of the current download
    public double Fraction => Total == 0 ? 0 : (double)Completed / Total;
}

public sealed class InstallEngine
{
    private readonly Downloader _downloader;
    private readonly GitHubReleaseResolver _github;
    private readonly SilentInstaller _runner;

    private int _succeeded;
    private int _failed;

    public InstallEngine() : this(new Downloader(),
        new GitHubReleaseResolver(), new SilentInstaller()) { }

    public InstallEngine(Downloader downloader, GitHubReleaseResolver github,
        SilentInstaller runner)
    {
        _downloader = downloader;
        _github = github;
        _runner = runner;
    }

    private static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "EpicSetup");

    public async Task RunAsync(IReadOnlyList<AppEntry> apps,
        IProgress<InstallUpdate>? progress, CancellationToken ct)
    {
        int total = apps.Count;
        int completed = 0;
        _succeeded = 0; _failed = 0;

        Report(progress, null, AppStatus.Pending, "Queueing installs...", completed, total);
        ReportDiagnostic(progress, $"queue: {total} selected app(s).", completed, total);

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();

            var type = app.ParsedType;
            ReportDiagnostic(progress, $"{app.Id}: preparing {app.Name} ({type}).", completed, total);
            if (app.NeedsReview)
                ReportDiagnostic(progress, $"{app.Id}: catalog review marker ignored; no verification is performed.", completed, total);
            if (app.NeedsUserSetup)
                ReportDiagnostic(progress, $"{app.Id}: legacy manual-setup marker ignored; automatic attempt continues.", completed, total);

            // Script / WingetUpdate run inline without a download.
            if (type == AppInstallerType.Script || type == AppInstallerType.WingetUpdate)
            {
                Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name}...", completed, total);
                ReportDiagnostic(progress, $"{app.Id}: running inline {type} command.", completed, total);
                try
                {
                    var runnerDiagnostics = new Progress<string>(detail =>
                        ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));
                    var result = await _runner.RunAsync(app, string.Empty, ct, runnerDiagnostics);
                    ReportInstallResult(progress, app, result, ref completed, total);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    completed++; _failed++;
                    Report(progress, app.Id, AppStatus.Failed, ex.Message, completed, total);
                }
                continue;
            }

            string url; string assetName;
            try
            {
                if (app.GitHub is not null && !string.IsNullOrEmpty(app.GitHub.Repo))
                {
                    ReportDiagnostic(progress,
                        $"{app.Id}: resolving GitHub release {app.GitHub.Repo} asset /{app.GitHub.AssetPattern}/.",
                        completed, total);
                    var asset = await _github.ResolveAsync(app.GitHub.Repo,
                        string.IsNullOrEmpty(app.GitHub.AssetPattern) ? ".*" : app.GitHub.AssetPattern,
                        app.GitHub.UseLatestPrerelease, ct);
                    url = asset.Url; assetName = asset.Name;
                    ReportDiagnostic(progress, $"{app.Id}: resolved asset {assetName} from {url}.", completed, total);
                }
                else if (!string.IsNullOrEmpty(app.Url))
                {
                    url = app.Url!; assetName = SafeName(url);
                    ReportDiagnostic(progress, $"{app.Id}: using catalog URL {url}.", completed, total);
                }
                else throw new InvalidOperationException(
                    $"No download source defined for '{app.Name}'.");
            }
            catch (Exception ex)
            {
                completed++; _failed++;
                Report(progress, app.Id, AppStatus.Failed, "Resolve failed: " + ex.Message,
                    completed, total, $"{app.Id}: source resolution failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var (path, localName) = PreparePath(app, url, assetName);
            ReportDiagnostic(progress, $"{app.Id}: download path {path}.", completed, total);
            try
            {
                Report(progress, app.Id, AppStatus.Downloading, $"Downloading {localName}...", completed, total,
                    $"{app.Id}: downloading {url} to {path}.");
                var downloadProgress = new Progress<DownloadProgressInfo>(dp =>
                    progress?.Report(new InstallUpdate
                    {
                        AppId = app.Id,
                        Status = AppStatus.Downloading,
                        Message = $"Downloading {localName}...",
                        Completed = completed,
                        Total = total,
                        Succeeded = _succeeded,
                        Failed = _failed,
                        DownloadFraction = dp.Fraction
                    }));
                var downloadDiagnostics = new Progress<string>(detail =>
                    ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));
                await _downloader.DownloadToFileAsync(new Uri(url), path, downloadProgress, ct, downloadDiagnostics);
                ReportDiagnostic(progress, $"{app.Id}: download complete; file ready at {path}.", completed, total);

                Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name}...", completed, total,
                    $"{app.Id}: dispatching {type} installer from {path}.");
                var runnerDiagnostics = new Progress<string>(detail =>
                    ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));
                var result = await _runner.RunAsync(app, path, ct, runnerDiagnostics);
                ReportInstallResult(progress, app, result, ref completed, total);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                completed++; _failed++;
                Report(progress, app.Id, AppStatus.Failed, ex.Message, completed, total,
                    $"{app.Id}: install pipeline failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SafeDelete(path);
                ReportDiagnostic(progress, $"{app.Id}: temporary download cleanup requested for {path}.", completed, total);
            }
        }

        if (completed == total)
        {
            Report(progress, null, AppStatus.Succeeded,
                $"Done: {_succeeded} installed, {_failed} failed.", completed, total);
            ReportDiagnostic(progress, $"complete: {_succeeded} installed, {_failed} failed.", completed, total);
        }
    }

    private void ReportInstallResult(IProgress<InstallUpdate>? progress, AppEntry app,
        SilentInstaller.RunResult result, ref int completed, int total)
    {
        completed++;
        if (result.TimedOut)
        {
            _failed++;
            var mins = (app.InstallTimeoutSeconds ?? SilentInstaller.DefaultInstallTimeoutSeconds) / 60;
            Report(progress, app.Id, AppStatus.Failed,
                $"Installer did not finish within {mins} min and was terminated.", completed, total,
                $"{app.Id}: installer timed out after {mins} minute(s); process tree terminated.");
        }
        else if (result.ExitCode is 0 or 3010 or 1641)
        {
            _succeeded++;
            Report(progress, app.Id, AppStatus.Succeeded,
                result.ExitCode is 3010 or 1641 ? "Installed - restart required" : "Installed",
                completed, total, $"{app.Id}: installer exited with code {result.ExitCode}; success.");
        }
        else
        {
            _failed++;
            Report(progress, app.Id, AppStatus.Failed,
                $"Installer exited with code {result.ExitCode} (0x{result.ExitCode & 0xFFFFFFFF:X8}).",
                completed, total,
                $"{app.Id}: installer exited with code {result.ExitCode} (0x{result.ExitCode & 0xFFFFFFFF:X8}).");
        }
    }

    private (string path, string localName) PreparePath(AppEntry app, string url, string? assetName)
    {
        var name = NormalizeFileName(!string.IsNullOrEmpty(assetName) ? assetName : SafeName(url));
        if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += app.ParsedType == AppInstallerType.Zip ? ".zip" : ".exe";

        var dir = Path.Combine(DownloadsRoot, app.Id, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, name), name);
    }

    private static string SafeName(string url)
    {
        try
        {
            var seg = new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? "setup.exe";
            return NormalizeFileName(Uri.UnescapeDataString(seg));
        }
        catch { return "setup.exe"; }
    }

    private static string NormalizeFileName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "setup.exe" : name;
    }

    private void Report(IProgress<InstallUpdate>? p, string? id, AppStatus s, string? msg,
        int completed, int total, string? diagnostic = null)
        => p?.Report(new InstallUpdate
        {
            AppId = id,
            Status = s,
            Message = msg,
            Completed = completed,
            Total = total,
            Succeeded = _succeeded,
            Failed = _failed,
            Diagnostic = diagnostic
        });

    private void ReportDiagnostic(IProgress<InstallUpdate>? p, string detail, int completed, int total)
        => p?.Report(new InstallUpdate
        {
            Status = AppStatus.Pending,
            Completed = completed,
            Total = total,
            Succeeded = _succeeded,
            Failed = _failed,
            Diagnostic = detail
        });

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir);
        }
        catch { }
    }
}
