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

/// <summary>Outcome of one acquisition attempt for an app.</summary>
internal enum AttemptOutcome
{
    /// <summary>App finished successfully; stop trying further sources.</summary>
    Committed,
    /// <summary>This source failed; try the entry's next source.</summary>
    RetryNextSource,
    /// <summary>This source failed terminally; do not fall back.</summary>
    Abort
}

public sealed class InstallEngine
{
    private readonly Downloader _downloader;
    private readonly SilentInstaller _runner;
    private readonly SourceResolver _resolver;

    private int _succeeded;
    private int _failed;

    public InstallEngine() : this(new Downloader(),
        new GitHubReleaseResolver(), new SilentInstaller()) { }

    public InstallEngine(Downloader downloader, GitHubReleaseResolver github,
        SilentInstaller runner)
        : this(downloader, runner, SourceResolver.CreateDefault(github)) { }

    public InstallEngine(Downloader downloader, SilentInstaller runner, SourceResolver resolver)
    {
        _downloader = downloader;
        _runner = runner;
        _resolver = resolver;
    }

    private static string DownloadsRoot => Path.Combine(Path.GetTempPath(), "epic-setup");

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

            // Script / WingetUpdate run inline without any acquisition step.
            if (type == AppInstallerType.Script || type == AppInstallerType.WingetUpdate)
            {
                Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name}...", completed, total);
                ReportDiagnostic(progress, $"{app.Id}: running inline {type} command.", completed, total);
                try
                {
                    var runnerDiagnostics = new Progress<string>(detail =>
                        ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));
                    var result = await _runner.RunAsync(app, string.Empty, ct, runnerDiagnostics);
                    CommitInstallResult(progress, app, result, ref completed, total);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    completed++; _failed++;
                    Report(progress, app.Id, AppStatus.Failed, ex.Message, completed, total);
                }
                continue;
            }

            await InstallThroughSourcesAsync(app, progress, completed, total, ct);
        }

        if (completed == total)
        {
            Report(progress, null, AppStatus.Succeeded,
                $"Done: {_succeeded} installed, {_failed} failed.", completed, total);
            ReportDiagnostic(progress, $"complete: {_succeeded} installed, {_failed} failed.", completed, total);
        }
    }

    /// <summary>
    /// Resolves every declared source into plans, then attempts them in order
    /// until one succeeds. Download-level and installer-exit failures fall back
    /// to the next source; installer timeouts abort the app.
    /// </summary>
    private async Task InstallThroughSourcesAsync(AppEntry app,
        IProgress<InstallUpdate>? progress, int completed, int total, CancellationToken ct)
    {
        // Every exit path below advances the completion count exactly once.
        var diagnostics = new Progress<string>(detail =>
            ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));

        IReadOnlyList<InstallPlan> plans;
        try
        {
            plans = await _resolver.ResolveAsync(app, ct, diagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            completed++; _failed++;
            Report(progress, app.Id, AppStatus.Failed, "Resolve failed: " + ex.Message,
                completed, total, $"{app.Id}: source resolution failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (plans.Count == 0)
        {
            completed++; _failed++;
            Report(progress, app.Id, AppStatus.Failed, $"No usable install source for '{app.Name}'.",
                completed, total, $"{app.Id}: no backend produced an install plan.");
            return;
        }

        var reasons = new List<string>();
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            AttemptOutcome outcome;
            try
            {
                outcome = plan switch
                {
                    DownloadPlan d => await AttemptDownloadAsync(app, d, progress, completed, total, ct),
                    DelegatePlan dp => await AttemptDelegateAsync(app, dp, progress, completed, total),
                    _ => AttemptOutcome.RetryNextSource
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                reasons.Add($"{plan.BackendTag}: {ex.GetType().Name}: {ex.Message}");
                ReportDiagnostic(progress, $"{app.Id}: attempt via {plan.BackendTag} failed: {ex.Message}",
                    completed, total);
                continue;
            }

            switch (outcome)
            {
                case AttemptOutcome.Committed:
                    return; // counters already advanced by the attempt

                case AttemptOutcome.Abort:
                    completed++; _failed++;
                    Report(progress, app.Id, AppStatus.Failed,
                        reasons.Count > 0 ? reasons[^1] : "Installer did not finish.",
                        completed, total,
                        $"{app.Id}: giving up after {plans.Count} source(s); last reason: {reasons.LastOrDefault() ?? "installer timeout"}.");
                    return;
            }
        }

        completed++; _failed++;
        Report(progress, app.Id, AppStatus.Failed,
            $"All {plans.Count} source(s) failed for '{app.Name}'.",
            completed, total,
            $"{app.Id}: exhausted sources. Reasons: {string.Join(" | ", reasons)}");
    }

    private async Task<AttemptOutcome> AttemptDownloadAsync(AppEntry app, DownloadPlan plan,
        IProgress<InstallUpdate>? progress, int completed, int total, CancellationToken ct)
    {
        var type = app.ParsedType;
        var (path, localName) = PreparePath(app, plan.Url, plan.FileName);
        ReportDiagnostic(progress, $"{app.Id}: download path {path}.", completed, total);
        try
        {
            Report(progress, app.Id, AppStatus.Downloading, $"Downloading {localName}...", completed, total,
                $"{app.Id}: downloading {plan.Url} to {path}.");
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
            await _downloader.DownloadToFileAsync(new Uri(plan.Url), path, downloadProgress, ct, downloadDiagnostics);
            ReportDiagnostic(progress, $"{app.Id}: download complete; file ready at {path}.", completed, total);

            Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name}...", completed, total,
                $"{app.Id}: dispatching {type} installer from {path}.");
            var runnerDiagnostics = new Progress<string>(detail =>
                ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));

            if (app.NeedsUserSetup)
            {
                // Manual-setup apps: download + launch the installer for the
                // user to complete interactively, then continue the batch
                // without waiting or killing it.
                var launchResult = await _runner.RunAsync(app, path, ct, runnerDiagnostics, launchOnly: true);
                if (launchResult.ExitCode == 0)
                {
                    completed++; _succeeded++;
                    Report(progress, app.Id, AppStatus.Skipped, "Launched", completed, total);
                    return AttemptOutcome.Committed;
                }
                return RecordFailure($"launch failed with code {launchResult.ExitCode}");
            }

            var result = await _runner.RunAsync(app, path, ct, runnerDiagnostics);
            if (result.TimedOut)
                return AttemptOutcome.Abort; // a hanging installer will hang again from another mirror
            if (result.ExitCode is 0 or 3010 or 1641)
            {
                CommitSuccess(progress, app, result.ExitCode is 3010 or 1641
                    ? "Installed - restart required" : "Installed", ref completed, total,
                    $"{app.Id}: installer exited with code {result.ExitCode}; success.");
                return AttemptOutcome.Committed;
            }
            return RecordFailure($"installer exited with code {result.ExitCode}");
        }
        finally
        {
            if (!app.NeedsUserSetup)
            {
                SafeDelete(path);
                ReportDiagnostic(progress, $"{app.Id}: temporary download cleanup requested for {path}.",
                    completed, total);
            }
        }

        AttemptOutcome RecordFailure(string reason)
        {
            ReportDiagnostic(progress, $"{app.Id}: {reason}.", completed, total);
            return AttemptOutcome.RetryNextSource;
        }
    }

    private async Task<AttemptOutcome> AttemptDelegateAsync(AppEntry app, DelegatePlan plan,
        IProgress<InstallUpdate>? progress, int completed, int total)
    {
        if (app.CloseApps.Count > 0)
            SilentInstaller.CloseRunning(app.CloseApps);

        Report(progress, app.Id, AppStatus.Installing, $"Installing {app.Name} ({plan.BackendTag})...",
            completed, total, $"{app.Id}: delegating install -> {plan.Description}.");

        var diagnostics = new Progress<string>(detail =>
            ReportDiagnostic(progress, $"{app.Id}: {detail}", completed, total));
        var result = await _runner.RunCommandAsync(plan.Executable, plan.Arguments,
            plan.TimeoutSeconds, CancellationToken.None, diagnostics);

        if (result.TimedOut)
        {
            ReportDiagnostic(progress, $"{app.Id}: {plan.BackendTag} timed out.", completed, total);
            return AttemptOutcome.Abort;
        }
        if (result.ExitCode is 0 or 3010 or 1641)
        {
            CommitSuccess(progress, app,
                $"Installed (via {plan.BackendTag})", ref completed, total,
                $"{app.Id}: {plan.Description} exited with code {result.ExitCode}; success.");
            return AttemptOutcome.Committed;
        }
        return AttemptOutcome.RetryNextSource;
    }

    private void CommitSuccess(IProgress<InstallUpdate>? progress, AppEntry app, string message,
        ref int completed, int total, string? diagnostic)
    {
        completed++; _succeeded++;
        Report(progress, app.Id, AppStatus.Succeeded, message, completed, total, diagnostic);
    }

    private void CommitInstallResult(IProgress<InstallUpdate>? progress, AppEntry app,
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
        var name = SourceNaming.NormalizeFileName(!string.IsNullOrEmpty(assetName) ? assetName : SourceNaming.SafeName(url));
        if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += app.ParsedType == AppInstallerType.Zip ? ".zip" : ".exe";

        var dir = Path.Combine(DownloadsRoot, app.Id, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, name), name);
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
