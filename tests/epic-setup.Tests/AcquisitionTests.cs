using EpicSetup.Models;
using EpicSetup.Services;
using Xunit;

namespace EpicSetup.Tests;

public class AcquisitionTests
{
    // --- schema / EffectiveSources ---

    [Fact]
    public void Legacy_url_field_becomes_single_url_source()
    {
        var app = new AppEntry { Id = "x", Url = "https://example.com/app.exe" };
        var srcs = app.EffectiveSources;
        Assert.Single(srcs);
        Assert.Equal(AppSourceKind.Url, srcs[0].Kind);
        Assert.Equal("https://example.com/app.exe", srcs[0].Url);
    }

    [Fact]
    public void Legacy_github_field_becomes_single_github_source()
    {
        var app = new AppEntry
        {
            Id = "x",
            GitHub = new GitHubReleaseSource { Repo = "o/r", AssetPattern = "setup\\.exe$" }
        };
        var srcs = app.EffectiveSources;
        Assert.Single(srcs);
        Assert.Equal(AppSourceKind.GitHub, srcs[0].Kind);
        Assert.Equal("o/r", srcs[0].GitHub!.Repo);
    }

    [Fact]
    public void Explicit_sources_take_precedence_over_legacy_fields()
    {
        var app = new AppEntry
        {
            Id = "x",
            Url = "https://legacy.example.com/app.exe",
            Sources = new List<AppSource>
            {
                new() { KindName = "winget", PackageId = "Some.Package" },
                new() { KindName = "url", Url = "https://new.example.com/a.exe" }
            }
        };
        var srcs = app.EffectiveSources;
        Assert.Equal(2, srcs.Count);
        Assert.Equal(AppSourceKind.Winget, srcs[0].Kind);
        Assert.Equal("Some.Package", srcs[0].WingetId);
        Assert.Equal(AppSourceKind.Url, srcs[1].Kind);
    }

    [Fact]
    public void Empty_sources_list_falls_back_to_legacy_fields()
    {
        var app = new AppEntry
        {
            Id = "x",
            Url = "https://example.com/app.exe",
            Sources = new List<AppSource>()
        };
        Assert.Single(app.EffectiveSources);
    }

    [Theory]
    [InlineData("url", AppSourceKind.Url)]
    [InlineData("github", AppSourceKind.GitHub)]
    [InlineData("gh", AppSourceKind.GitHub)]
    [InlineData("winget", AppSourceKind.Winget)]
    [InlineData("WINGET", AppSourceKind.Winget)]
    [InlineData("scoop", AppSourceKind.Scoop)]
    [InlineData("choco", AppSourceKind.Chocolatey)]
    [InlineData("chocolatey", AppSourceKind.Chocolatey)]
    [InlineData("bogus", AppSourceKind.Url)]
    public void Kind_names_parse_case_insensitively(string name, AppSourceKind expected)
        => Assert.Equal(expected, AppSource.ParseKind(name));

    // --- winget argument building ---

    [Fact]
    public void Winget_arguments_are_exact_silent_and_noninteractive()
    {
        var args = string.Join(' ', WingetBackend.BuildInstallArguments("7zip.7zip"));
        Assert.Contains("--id 7zip.7zip", args);
        Assert.Contains("--exact", args);
        Assert.Contains("--silent", args);
        Assert.Contains("--source winget", args);
        Assert.Contains("--accept-package-agreements", args);
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.DoesNotContain("--version", args); // decision: always latest official
    }

    // --- resolver behavior ---

    private sealed class NullWingetBackend : IAcquisitionBackend
    {
        public BackendKind Kind => BackendKind.Winget;
        public bool Available { get; set; } = true;
        public Task<BackendProbe> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new BackendProbe("winget", Available, null));
        public bool CanResolve(AppSource source)
            => source.Kind == AppSourceKind.Winget && !string.IsNullOrWhiteSpace(source.WingetId);
        public Task<InstallPlan?> ResolveAsync(AppEntry app, AppSource source,
            CancellationToken ct, IProgress<string>? diagnostics)
            => Task.FromResult<InstallPlan?>(Available
                ? new DelegatePlan { Description = $"winget: {source.WingetId}" }
                : null);
    }

    private static AppEntry AppWith(params AppSource[] sources)
        => new() { Id = "x", Name = "X", Sources = sources.ToList() };

    [Fact]
    public async Task Resolver_returns_plans_in_declared_order()
    {
        var app = AppWith(
            new AppSource { KindName = "winget", PackageId = "Some.Package" },
            new AppSource { KindName = "url", Url = "https://example.com/a.exe" });
        var resolver = new SourceResolver(new IAcquisitionBackend[]
        {
            new NullWingetBackend(),
            new StaticDownloadBackend(new GitHubReleaseResolver())
        });

        var plans = await resolver.ResolveAsync(app, CancellationToken.None, null);

        Assert.Equal(2, plans.Count);
        Assert.IsType<DelegatePlan>(plans[0]);
        var download = Assert.IsType<DownloadPlan>(plans[1]);
        Assert.Equal("url", download.BackendTag);
    }

    [Fact]
    public async Task Unavailable_backend_is_skipped_without_breaking_the_chain()
    {
        var app = AppWith(
            new AppSource { KindName = "winget", PackageId = "Some.Package" },
            new AppSource { KindName = "url", Url = "https://example.com/a.exe" });
        var resolver = new SourceResolver(new IAcquisitionBackend[]
        {
            new NullWingetBackend { Available = false },
            new StaticDownloadBackend(new GitHubReleaseResolver())
        });

        var plans = await resolver.ResolveAsync(app, CancellationToken.None, null);

        var plan = Assert.Single(plans);
        Assert.IsType<DownloadPlan>(plan);
    }

    [Fact]
    public async Task Unknown_source_kind_yields_no_plans()
    {
        var app = AppWith(new AppSource { KindName = "mystery", PackageId = "?" });
        var resolver = new SourceResolver(new IAcquisitionBackend[]
        {
            new NullWingetBackend(),
            new StaticDownloadBackend(new GitHubReleaseResolver())
        });

        var plans = await resolver.ResolveAsync(app, CancellationToken.None, null);

        Assert.Empty(plans);
    }
}
