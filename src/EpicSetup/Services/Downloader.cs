using System;
using System.IO;
using System.Net.Http;

namespace EpicSetup.Services;

public readonly struct DownloadProgressInfo
{
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public double Fraction => Total > 0 ? Math.Min(1.0, (double)Downloaded / Total) : 0;
}

public sealed class Downloader
{
    private const int MaxAttempts = 3;
    private const int RetryDelayMs = 2000;

    private readonly HttpClient _http;
    public Downloader() : this(Http.Client) { }
    public Downloader(HttpClient http) => _http = http;

    /// <summary>
    /// Downloads a URL to a file with retries on transient network failures.
    /// The downloaded file is passed directly to the selected installer.
    /// </summary>
    public async Task DownloadToFileAsync(Uri url, string destPath, IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct, IProgress<string>? diagnostics = null)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        Exception? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(RetryDelayMs, ct);
            diagnostics?.Report($"download attempt {attempt}/{MaxAttempts}: {url}");
            try
            {
                await DownloadOnceAsync(url, destPath, progress, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                last = ex;
                diagnostics?.Report($"download attempt {attempt} failed: {ex.Message}" +
                    (attempt < MaxAttempts ? "; retrying." : "."));
                TryDelete(destPath);
            }
        }
        throw last ?? new IOException($"Failed to download {url}");
    }

    private async Task DownloadOnceAsync(Uri url, string destPath,
        IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1;
        long read = 0;

        await using (var remote = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                     81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await remote.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                progress?.Report(new DownloadProgressInfo { Downloaded = read, Total = total });
            }
        }

    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string SafeFileNameFromUrl(Uri url)
    {
        var seg = url.Segments.LastOrDefault()?.Trim('/') ?? "";
        if (string.IsNullOrWhiteSpace(seg)) seg = "setup.exe";
        return Uri.UnescapeDataString(seg);
    }
}
