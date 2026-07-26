using System;
using System.IO;
using System.Net.Http;

namespace EpicSetup.Services;

public sealed class Downloader
{
    private readonly HttpClient _http;
    public Downloader() : this(Http.Client) { }
    public Downloader(HttpClient http) => _http = http;

    public async Task DownloadToFileAsync(Uri url, string destPath, IProgress<long>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

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
                progress?.Report(read);
            }
        }

        if (read == 0)
            throw new IOException($"Downloaded file is empty: {url}");
    }

    public static string SafeFileNameFromUrl(Uri url)
    {
        var seg = url.Segments.LastOrDefault()?.Trim('/') ?? "";
        if (string.IsNullOrWhiteSpace(seg)) seg = "setup.exe";
        return Uri.UnescapeDataString(seg);
    }
}