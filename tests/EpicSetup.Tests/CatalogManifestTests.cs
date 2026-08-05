using System.Text.RegularExpressions;
using EpicSetup.Models;
using Xunit;

namespace EpicSetup.Tests;

public class CatalogManifestTests
{
    private static readonly IReadOnlyList<string> StaticIds = new[]
    {
        "chrome", "waterfox", "vivaldi",
        "discord", "slack", "zoom", "telegram", "teams",
        "spotify", "blender", "ffmpeg", "medal",
        "wiztree", "winrar",
        "vscode", "cursor", "antigravity", "jetbrains-toolbox",
        "python", "dotnet-desktop-runtime",
        "notion", "libreoffice", "bitdefender", "malwarebytes", "avast",
        "steam", "epic-games", "riot-client", "rockstar-launcher"
    };

    private static readonly IReadOnlyList<string> GitHubIds = new[]
    {
        "brave", "helium", "obs-studio", "mpvnet", "audacity", "yt-dlp", "taiga",
        "7-zip", "powertoys", "qbittorrent", "openrgb",
        "arduino-ide", "git", "temurin-jdk", "obsidian",
        "prism-launcher", "osu-lazer"
    };

    [Fact]
    public void Catalog_has_expected_shape()
    {
        var catalog = CatalogFixture.Load();
        var apps = CatalogFixture.AllApps(catalog);

        Assert.Equal(2, catalog.Tabs.Count);
        Assert.Equal(48, apps.Count);

        Assert.Equal(48, apps.Select(a => a.Id).Distinct().Count());
        foreach (var app in apps)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Id));
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.False(string.IsNullOrWhiteSpace(app.Description));
            Assert.False(string.IsNullOrWhiteSpace(app.Homepage));
            Assert.True(app.Size is null or >= 0, $"{app.Id} has a negative size");
            Assert.True(Regex.IsMatch(app.Id, "^[a-z0-9]+(-[a-z0-9]+)*$"), $"{app.Id} is not kebab-case");
        }
    }

    [Fact]
    public void Every_app_has_exactly_one_download_path()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            if (app.Id == "dotnet-sdk")
            {
                Assert.Equal(AppInstallerType.Script, app.ParsedType);
                Assert.True(string.IsNullOrEmpty(app.Url));
                Assert.Null(app.GitHub);
                continue;
            }

            if (app.Id == "msi-afterburner")
            {
                Assert.True(string.IsNullOrEmpty(app.Url));
                Assert.Null(app.GitHub);
                continue;
            }

            var hasUrl = !string.IsNullOrEmpty(app.Url);
            var hasGitHub = app.GitHub is not null && !string.IsNullOrEmpty(app.GitHub.Repo);
            Assert.True(hasUrl ^ hasGitHub, $"{app.Id} must have exactly one of url/github");
        }
    }

    [Fact]
    public void Static_and_GitHub_sources_match_expected_sets()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());

        var staticIds = apps.Where(a => !string.IsNullOrEmpty(a.Url)).Select(a => a.Id).OrderBy(x => x).ToArray();
        var githubIds = apps.Where(a => a.GitHub is not null && !string.IsNullOrEmpty(a.GitHub.Repo))
            .Select(a => a.Id).OrderBy(x => x).ToArray();

        Assert.Equal(StaticIds.OrderBy(x => x), staticIds);
        Assert.Equal(GitHubIds.OrderBy(x => x), githubIds);
    }

    [Fact]
    public void Every_static_url_is_https()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps.Where(a => !string.IsNullOrEmpty(a.Url)))
        {
            Assert.True(Uri.TryCreate(app.Url, UriKind.Absolute, out var uri), $"{app.Id} URL is not absolute");
            Assert.Equal("https", uri.Scheme);
        }
    }

    [Fact]
    public void Every_GitHub_source_is_well_formed()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps.Where(a => a.GitHub is not null))
        {
            Assert.Matches("^[^/]+/[^/]+$", app.GitHub!.Repo);
            Assert.False(string.IsNullOrWhiteSpace(app.GitHub.AssetPattern));
            _ = new Regex(app.GitHub.AssetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    [Fact]
    public void Installer_types_are_recognized()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            if (app.Id == "msi-afterburner") continue; // manual-only, no installer type
            Assert.NotEqual(AppInstallerType.Auto, app.ParsedType);
        }
    }

    [Fact]
    public void Portable_and_timeout_metadata_are_valid()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        var portableIds = apps.Where(a => a.ParsedType == AppInstallerType.Portable ||
                                          a.ParsedType == AppInstallerType.Zip)
            .Select(a => a.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "ffmpeg", "yt-dlp" }, portableIds);

        foreach (var app in apps.Where(a => a.ParsedType is AppInstallerType.Portable or AppInstallerType.Zip))
        {
            Assert.False(string.IsNullOrWhiteSpace(app.PortableSubdir));
            var full = Path.GetFullPath(Path.Combine("%LOCALAPPDATA%", "Programs", app.PortableSubdir!));
            Assert.StartsWith(Path.GetFullPath("%LOCALAPPDATA%\\Programs"), full, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var app in apps.Where(a => a.InstallTimeoutSeconds is not null))
        {
            Assert.True(app.InstallTimeoutSeconds > 0, $"{app.Id} has a non-positive timeout");
        }

        foreach (var app in apps)
        {
            foreach (var process in app.CloseApps)
            {
                Assert.False(process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    $"{app.Id} closeApps contains an .exe name");
            }
        }
    }
}
