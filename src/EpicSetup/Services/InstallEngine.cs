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
    public double DownloadFraction { get; init; }   // 0..1 byte progress of the current download
    public double Fraction => Total == 0 ? 0 : (double)Completed / Total;
}

public sealed class InstallEngine
{
    private readonly Downloader _downloader;
    private readonly GitHubReleaseResolver _github;
    private readonly SignatureVerifier _verifier;
    private readonly SilentInstaller _runner;

    private int _succeeded;
    private int _failed;

    public InstallEngine() : this(new Downloader(),
        new GitHubReleaseResolver(), new SilentInstaller(), new SignatureVerifier()) { }

    public InstallEngine(Downloader downloader, GitHubReleaseResolver github,
        SilentInstaller runner, SignatureVerifier verifier)
    {
        _downloader = downloader;
        _github = github;
        _runner = runner;
        _verifier = verifier;
    }

    private static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "EpicSetup");

    public async Task RunAsync(IReadOnlyList<AppEntry> apps,
        IProgress<InstallUpdate>? progress, CancellationToken ct)
    {
        int total = apps.Count;
        int completed = 0;
        _succeeded = 0; _failed = 0;

        Report(progress, null, AppStatus.Pending, "Queueing installs...", completed, total);

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();

            string url; string assetName;
            try
            {
                if (app.GitHub is not null && !string.IsNullOrEmpty(app.GitHub.Repo))
                {
                    var asset = await _github.ResolveAsync(app.GitHub.Repo,
                        string.IsNullOrEmpty(app.GitHub.AssetPattern) ? ".*" : app.GitHub.AssetPattern,
                        app.GitHub.UseLatestPrerelease, ct);
                    url = asset.Url; assetName = asset.Name;
                }
                else if (!string.IsNullOrEmpty(app.Url))
                {
                    url = app.Url!; assetName = SafeName(url);
                }
                else throw new InvalidOperationException(
                    $"No download source defined for '{app.Name}'.");
            }
            catch (Exception ex)
            {
                completed++; _failed++;
                Report(progress, app.Id, AppStatus.Failed, "Resolve failed: " + ex.Message, completed, total);
                continue;
            }

            var (path, localName) = PreparePath(app, url, assetName);
            try
            {
                Report(progress, app.Id, AppStatus.Downloading, $"Downloading {localName}...", completed, total);
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
                await _downloader.DownloadToFileAsync(new Uri(url), path, downloadProgress, ct);

                Report(progress, app.Id, AppStatus.Verifying, "Verifying digital signature...", completed, total);
                var v = _verifier.Verify(path);
                if (!v.Trusted || v.Error != null)
                {
                    completed++; _failed++;
                    Report(progress, app.Id, AppStatus.Failed,
                        "Signature check failed: " + (v.Error ?? "untrusted"), completed, total);
                    SafeDelete(path);
                    continue;
                }

                var expected = new[] { app.Publisher }.Where(s => !string.IsNullOrEmpty(s)).Cast<string>()
                    .Concat(app.Signers).ToList();
                if (expected.Count > 0 &&
                    !SignatureVerifier.MatchesAnySigner(v, expected))
                {
                    completed++; _failed++;
                    Report(progress, app.Id, AppStatus.Failed,
                        $"Unexpected publisher: CN='{v.Signer ?? "(none)"}', O='{v.Organization ?? "(none)"}'. Expected one of: {string.Join(", ", expected)}.",
                        completed, total);
                    SafeDelete(path);
                    continue;
                }

                Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name}...", completed, total);
                int code = await Task.Run(() => _runner.Run(app, path, ct), ct);

                completed++;
                if (code == 0 || code == 3010)
                {
                    _succeeded++;
                    Report(progress, app.Id, AppStatus.Succeeded,
                        code == 3010 ? "Installed - restart required" : "Installed",
                        completed, total);
                }
                else
                {
                    _failed++;
                    Report(progress, app.Id, AppStatus.Failed,
                        $"Installer exited with code {code}.", completed, total);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                completed++; _failed++;
                Report(progress, app.Id, AppStatus.Failed, ex.Message, completed, total);
            }
            finally
            {
                if (app.ParsedType != AppInstallerType.Portable)
                    SafeDelete(path);
            }
        }

        if (completed == total)
            Report(progress, null, AppStatus.Succeeded,
                $"Done: {_succeeded} installed, {_failed} failed.", completed, total);
    }

    private (string path, string localName) PreparePath(AppEntry app, string url, string? assetName)
    {
        var dir = Path.Combine(DownloadsRoot, app.Id);
        try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f); }
        catch { }
        Directory.CreateDirectory(dir);

        var name = !string.IsNullOrEmpty(assetName) ? assetName : SafeName(url);
        var isExecutableType = app.ParsedType == AppInstallerType.Script
                            || app.ParsedType == AppInstallerType.WingetUpdate;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
            !isExecutableType)
        {
            name += ".exe";
        }
        return (Path.Combine(dir, name), name);
    }

    private static string SafeName(string url)
    {
        try
        {
            var seg = new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? "setup.exe";
            return Uri.UnescapeDataString(seg);
        }
        catch { return "setup.exe"; }
    }

    private void Report(IProgress<InstallUpdate>? p, string? id, AppStatus s, string? msg,
        int completed, int total)
        => p?.Report(new InstallUpdate
        {
            AppId = id,
            Status = s,
            Message = msg,
            Completed = completed,
            Total = total,
            Succeeded = _succeeded,
            Failed = _failed
        });

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}