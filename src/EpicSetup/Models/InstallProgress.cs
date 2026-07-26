namespace EpicSetup.Models;

public enum AppStatus
{
    Pending,
    Downloading,
    Verifying,
    Installing,
    Succeeded,
    Failed,
    Skipped
}

public sealed class AppResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AppStatus Status { get; set; } = AppStatus.Pending;
    public string? Message { get; set; }     // status text or error reason
    public int? ExitCode { get; set; }
    public string? ResolvedUrl { get; set; }
    public string? Signer { get; set; }
    public bool Signed { get; set; }
}

public sealed class InstallProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string? CurrentAppName { get; set; }
    public string? CurrentStatusText { get; set; }
    public AppStatus CurrentStatus { get; set; }
    public double OverallFraction => Total == 0 ? 0 : (double)Completed / Total;
}