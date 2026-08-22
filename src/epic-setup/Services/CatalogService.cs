using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EpicSetup.Models;

namespace EpicSetup.Services;

public sealed class CatalogService
{
    public const string DefaultOwner = "oddepic";   // the owner's GitHub username - live catalog source
    public const string DefaultRepo = "epic-setup";
    public const string DefaultBranch = "main";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    public CatalogService() : this(Http.Client) { }
    public CatalogService(HttpClient http) => _http = http;

    // Live catalog source: defaults to the owner's repo (oddepic/epic-setup).
    // Override with env vars if the repo moves. Remote failure falls back to
    // the disk cache, then to the embedded catalog - never a third party's file.
    public string Owner => Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_OWNER") ?? DefaultOwner;
    public string Repo => Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_REPO") ?? DefaultRepo;
    public string Branch => Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_BRANCH") ?? DefaultBranch;

    public bool RemoteEnabled => true;

    /// <summary>
    /// Forced-embedded mode skips remote and cache entirely and loads the
    /// catalog baked into the executable. It is enabled by the --embedded
    /// startup argument or by either embedded environment variable.
    /// </summary>
    public static bool IsEmbeddedForced()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, "--embedded", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE") ?? "",
                "embedded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("EPICSETUP_EMBEDDED_CATALOG") ?? "",
                "1", StringComparison.OrdinalIgnoreCase);
    }

    public Uri RemoteUri => new($"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/catalog.json");

    private static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "epic-setup", "catalog.cache.json");

    public (Catalog catalog, SourceKind source) LoadSync()
    {
        return LoadAsync().GetAwaiter().GetResult();
    }

    public async Task<(Catalog catalog, SourceKind source)> LoadAsync(CancellationToken ct = default)
    {
        if (IsEmbeddedForced())
            return (LoadEmbedded(), SourceKind.Embedded);

        try
        {
            using var resp = await _http.GetAsync(RemoteUri, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var catalog = await JsonSerializer.DeserializeAsync<Catalog>(stream, JsonOpts, ct);
                if (catalog != null && catalog.Tabs.Count > 0)
                {
                    await CacheAsync(catalog, ct);
                    return (catalog, SourceKind.Remote);
                }
            }
        }
        catch
        {
            // fall through to cache/embedded
        }

        try
        {
            if (File.Exists(CachePath))
            {
                var cached = await LoadFromFileAsync(CachePath, ct);
                if (cached != null && cached.Tabs.Count > 0) return (cached, SourceKind.Cache);
            }
        }
        catch { }

        return (LoadEmbedded(), SourceKind.Embedded);
    }

    private async Task CacheAsync(Catalog catalog, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(CachePath,
                JsonSerializer.Serialize(catalog, JsonOpts), ct);
        }
        catch { /* non-fatal */ }
    }

    private static async Task<Catalog?> LoadFromFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Catalog>(fs, JsonOpts, ct);
    }

    public static Catalog LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var name = names.FirstOrDefault(n => n.EndsWith("catalog.json"))
                   ?? "epic-setup.catalog.json";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded catalog.json not found.");
        return JsonSerializer.Deserialize<Catalog>(stream, JsonOpts)
               ?? throw new InvalidOperationException("Embedded catalog.json is invalid.");
    }

    public enum SourceKind { Remote, Cache, Embedded }
}
