using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace EpicSetup.Services;

public sealed class GitHubReleaseResolver
{
    public sealed class Asset
    {
        public string Url { get; }
        public string Name { get; }

        public Asset(string url, string name)
        {
            Url = url;
            Name = name;
        }
    }

    private readonly HttpClient _http;
    public GitHubReleaseResolver() : this(Http.Client) { }
    public GitHubReleaseResolver(HttpClient http) => _http = http;

    public async Task<Asset> ResolveAsync(string repo, string assetPattern, bool prerelease, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("EPICSETUP_GH_TOKEN");
        var req = new HttpRequestMessage(HttpMethod.Get,
            prerelease ? $"https://api.github.com/repos/{repo}/releases"
                       : $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub releases API returned {resp.StatusCode} for {repo}.");

        var pattern = new Regex(assetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        string? url = null, name = null;
        var matched = new System.Collections.Generic.List<string>();
        using var doc = JsonDocument.Parse(body);

        void Consider(JsonElement a)
        {
            var an = a.GetProperty("name").GetString();
            var au = a.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrEmpty(an) || string.IsNullOrEmpty(au) || !pattern.IsMatch(an)) return;
            matched.Add(an);
            if (url is null) { url = au; name = an; }
        }

        if (prerelease)
        {
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray()) Consider(a);
                if (url is not null) break;
            }
        }
        else
        {
            var rel = doc.RootElement;
            if (rel.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray()) Consider(a);
        }

        if (url is null)
            throw new FileNotFoundException(
                $"No GitHub release asset matched pattern '{assetPattern}' in {repo}.");

        // Ambiguous regex = catalog bug (could silently pick the wrong arch/build).
        if (matched.Count > 1)
            throw new InvalidOperationException(
                $"GitHub asset pattern '{assetPattern}' in {repo} matched {matched.Count} assets: " +
                string.Join(", ", matched) + " - tighten the regex to exactly one.");

        return new Asset(url, name!);
    }
}