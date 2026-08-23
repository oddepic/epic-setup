using System.Text.RegularExpressions;
using EpicSetup.Models;
using Xunit;

namespace EpicSetup.Tests;

public class CatalogManifestTests
{
    [Fact]
    public void Catalog_has_expected_shape()
    {
        var catalog = CatalogFixture.Load();
        var apps = CatalogFixture.AllApps(catalog);

        Assert.Equal(2, catalog.Tabs.Count);
        Assert.Equal(46, apps.Count);

        Assert.Equal(46, apps.Select(a => a.Id).Distinct().Count());
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
    public void Every_app_has_acquisition_sources()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            if (app.ParsedType == AppInstallerType.Script)
            {
                Assert.Empty(app.EffectiveSources); // inline command, no acquisition
                continue;
            }
            if (app.Id == "msi-afterburner")
            {
                Assert.Empty(app.EffectiveSources); // manual-only entry
                continue;
            }
            Assert.True(app.EffectiveSources.Count > 0,
                $"{app.Id} has no usable acquisition source");
        }
    }

    [Fact]
    public void Winget_first_sources_are_in_place()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            var srcs = app.EffectiveSources;
            if (srcs.Count == 0) continue; // script / manual-only

            if (app.Id == "avast")
            {
                // No verified winget id yet; direct download only.
                Assert.Equal(AppSourceKind.Url, srcs[0].Kind);
                continue;
            }

            Assert.Equal(AppSourceKind.Winget, srcs[0].Kind);
            Assert.False(string.IsNullOrWhiteSpace(srcs[0].WingetId),
                $"{app.Id} winget source lacks a package id");
            Assert.True(srcs.Count >= 2, $"{app.Id} should carry a fallback source");
        }
    }

    [Fact]
    public void Every_static_url_is_https()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            foreach (var source in app.EffectiveSources.Where(s => s.Kind == AppSourceKind.Url))
            {
                Assert.True(Uri.TryCreate(source.Url, UriKind.Absolute, out var uri), $"{app.Id} URL is not absolute");
                Assert.Equal("https", uri.Scheme);
            }
        }
    }

    [Fact]
    public void Every_GitHub_source_is_well_formed()
    {
        var apps = CatalogFixture.AllApps(CatalogFixture.Load());
        foreach (var app in apps)
        {
            foreach (var gh in app.EffectiveSources.Select(s => s.GitHub).Where(g => g is not null))
            {
                Assert.Matches("^[^/]+/[^/]+$", gh!.Repo);
                Assert.False(string.IsNullOrWhiteSpace(gh.AssetPattern));
                _ = new Regex(gh.AssetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
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
