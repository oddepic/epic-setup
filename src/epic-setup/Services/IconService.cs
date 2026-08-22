using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EpicSetup.Models;

namespace EpicSetup.Services;

public sealed class IconService
{
    private readonly HttpClient _http;
    public IconService() : this(Http.Client) { }
    public IconService(HttpClient http) => _http = http;

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "epic-setup", "icons");

    // Bundled icon overrides keyed by homepage domain (for apps that need an explicit icon URL).
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // future: {"helium.computer", "https://..."}
    };

    // Clearbit gives cleaner brand logos than favicon; Google favicon as fallback.
    private IEnumerable<string> CandidateUrls(AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.Icon)) { yield return app.Icon; yield break; }
        if (string.IsNullOrWhiteSpace(app.Homepage)) yield break;
        string host;
        try { host = new Uri(app.Homepage).Host; }
        catch { yield break; }
        if (Overrides.TryGetValue(host, out var ov) && !string.IsNullOrEmpty(ov)) yield return ov;
        yield return $"https://logo.clearbit.com/{host}?size=128";
        yield return $"https://www.google.com/s2/favicons?domain={host}&sz=64";
    }

    public ImageSource? LoadIcon(AppEntry app)
    {
        // 1) Bundled PNG resource takes priority: offline, crisp, transparent.
        var bundled = LoadBundled(app.Id);
        if (bundled != null) return bundled;

        // 2) Network (explicit icon URL / Clearbit / favicon), cached to disk.
        var cached = TryReadCache(app.Id);
        if (cached != null) return cached;

        _ = LoadNetworkAsync(app); // fire-and-forget; sets cache then VM refreshes
        return null; // letter chip shown meanwhile
    }

    public async Task LoadNetworkAsync(AppEntry app, CancellationToken ct = default)
    {
        var urls = CandidateUrls(app).ToList();
        if (urls.Count == 0) return;
        Directory.CreateDirectory(CacheDir);

        var cachePath = Path.Combine(CacheDir, app.Id + ".png");
        bool got = false;
        foreach (var url in urls)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                await using var fs = File.Create(cachePath);
                await (await resp.Content.ReadAsStreamAsync(ct)).CopyToAsync(fs, ct);
                if (new FileInfo(cachePath).Length == 0) { try { File.Delete(cachePath); } catch { } continue; }
                got = true;
                break;
            }
            catch { try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { } }
        }
        if (!got) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (LoadBundled(app.Id) != null) return; // never override a bundled one
            var bmp = TryReadBitmap(cachePath);
            if (bmp != null) OnIconLoaded?.Invoke(app.Id, bmp);
        });
    }

    public event Action<string, ImageSource>? OnIconLoaded;

    private static ImageSource? LoadBundled(string id)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{id}.png", UriKind.Absolute);
            using var s = Application.GetResourceStream(uri)?.Stream;
            if (s == null) return null;
            return BitmapFromStream(s);
        }
        catch { return null; }
    }

    private static ImageSource? TryReadCache(string id)
    {
        var path = Path.Combine(CacheDir, id + ".png");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            using var fs = File.OpenRead(path);
            return BitmapFromStream(fs);
        }
        catch { return null; }
    }

    private static ImageSource? TryReadBitmap(string path)
    {
        try { using var fs = File.OpenRead(path); return BitmapFromStream(fs); }
        catch { return null; }
    }

    private static BitmapImage? BitmapFromStream(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            if (bmp.PixelWidth == 0) return null;
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}