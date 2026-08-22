using System.Net;
using System.Text.Json;
using EpicSetup.Services;
using Xunit;

namespace EpicSetup.Tests;

public class GitHubReleaseResolverTests
{
    private const string Tag = "v1.0.0";

    private static string BuildReleaseJson(string repo, params string[] assets)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tag_name"] = Tag,
            ["assets"] = assets.Select(name => new Dictionary<string, object>
            {
                ["name"] = name,
                ["browser_download_url"] = $"https://github.com/{repo}/releases/download/{Tag}/{name}"
            }).ToArray()
        });

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string? LastRequestUrl { get; private set; }

        public StaticHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json)
            });
        }
    }

    [Fact]
    public async Task Every_GitHub_entry_resolves_exactly_one_asset()
    {
        var catalog = CatalogFixture.Load();
        var githubApps = CatalogFixture.AllApps(catalog)
            .Where(a => a.GitHub is not null)
            .ToList();

        Assert.Equal(17, githubApps.Count);

        foreach (var app in githubApps)
        {
            var assetName = PickAssetName(app.Id);
            var handler = new StaticHandler(BuildReleaseJson(app.GitHub!.Repo, assetName,
                assetName + ".asc", "some-other-platform.exe", assetName + ".pdb"));
            using var http = new HttpClient(handler);
            var resolver = new GitHubReleaseResolver(http);

            var asset = await resolver.ResolveAsync(app.GitHub.Repo, app.GitHub.AssetPattern, false, CancellationToken.None);

            Assert.Equal(assetName, asset.Name);
            Assert.StartsWith($"https://api.github.com/repos/{app.GitHub.Repo}/releases/latest", handler.LastRequestUrl!);
        }
    }

    [Fact]
    public async Task Zero_matches_fails()
    {
        var handler = new StaticHandler(BuildReleaseJson("x/y", "unrelated.exe"));
        using var http = new HttpClient(handler);
        var resolver = new GitHubReleaseResolver(http);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => resolver.ResolveAsync("x/y", "^wanted.*\\.exe$", false, CancellationToken.None));
    }

    [Fact]
    public async Task Multiple_matches_fails()
    {
        var handler = new StaticHandler(BuildReleaseJson("x/y", "app-x64.exe", "app-arm64.exe"));
        using var http = new HttpClient(handler);
        var resolver = new GitHubReleaseResolver(http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("x/y", "^app-.*\\.exe$", false, CancellationToken.None));
    }

    private static string PickAssetName(string id) => id switch
    {
        "brave" => "BraveBrowserStandaloneSilentSetup.exe",
        "helium" => "helium_0.15.1.1_x64-installer.exe",
        "obs-studio" => "OBS-Studio-30.2.3-Windows-x64-Installer.exe",
        "mpvnet" => "mpv.net-v7.1.0-setup-x64.exe",
        "audacity" => "audacity-win-3.6.4-64bit.exe",
        "yt-dlp" => "yt-dlp.exe",
        "taiga" => "TaigaSetup_1.4.1.exe",
        "7-zip" => "7z2602-x64.exe",
        "powertoys" => "PowerToysSetup-0.84.1-x64.exe",
        "qbittorrent" => "qbittorrent_4.6.5_x64_setup.exe",
        "openrgb" => "OpenRGB_0.9_Windows_64_3c0a4d0e.msi",
        "arduino-ide" => "arduino-ide_2.3.4_Windows_64bit.msi",
        "git" => "Git-2.47.0-64-bit.exe",
        "temurin-jdk" => "OpenJDK25U-jdk_x64_windows_hotspot_25.0.1_12.msi",
        "obsidian" => "Obsidian-1.6.7.exe",
        "prism-launcher" => "PrismLauncher-Windows-MSVC-Setup-9.1.exe",
        "osu-lazer" => "install.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown GitHub app id")
    };
}
