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
