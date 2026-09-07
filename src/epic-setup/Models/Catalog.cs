using System.Text.Json.Serialization;

namespace EpicSetup.Models;

public sealed class Catalog
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("tabs")] public List<TabDefinition> Tabs { get; set; } = new();
}

public sealed class TabDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("categories")] public List<CategoryDefinition> Categories { get; set; } = new();
}

public sealed class CategoryDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("hint")] public string? Hint { get; set; }   // shown in parens muted after the name
    [JsonPropertyName("apps")] public List<AppEntry> Apps { get; set; } = new();
}

public sealed class GitHubReleaseSource
{
    [JsonPropertyName("repo")] public string Repo { get; set; } = string.Empty;       // "owner/repo"
    [JsonPropertyName("asset")] public string AssetPattern { get; set; } = string.Empty; // regex
    [JsonPropertyName("useLatestPrerelease")] public bool UseLatestPrerelease { get; set; }
}

public enum AppInstallerType
{
    Auto,       // infer from file extension/.exe name pattern
    Msi,
    Nsis,       // /S
    Inno,       // /VERYSILENT /NORESTART /SUPPRESSMSGBOXES
    Burn,       // WiX bundles /quiet
    Exe,        // generic: pass SilentArgs only
    Portable,   // just extract/copy exe to a folder (no installer)
    Zip,        // extract an archive to a folder (no installer)
    Script,     // run a script directly through PowerShell
    WingetUpdate
}

public sealed class AppEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("size")] public int? Size { get; set; }   // estimated full app size in MB (for the hover tooltip)
    [JsonPropertyName("icon")] public string? Icon { get; set; }   // optional explicit icon URL; else derived from homepage
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("github")] public GitHubReleaseSource? GitHub { get; set; }
    [JsonPropertyName("sources")] public List<AppSource>? Sources { get; set; }  // ordered acquisition sources; null/empty -> legacy fields

    [JsonPropertyName("installerType")] public string? InstallerTypeName { get; set; } = "Auto";
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("portableSubdir")] public string? PortableSubdir { get; set; } // e.g. Programs\<id>
    [JsonPropertyName("closeApps")] public List<string> CloseApps { get; set; } = new(); // process names (no .exe) killed before install if running
    [JsonPropertyName("installTimeoutSeconds")] public int? InstallTimeoutSeconds { get; set; } // installer wait bound; default 180
    [JsonPropertyName("runAsUser")] public bool RunAsUser { get; set; } // launch through the interactive user's token for per-user installers
    [JsonPropertyName("arch")] public string? Arch { get; set; } = "x64";
    [JsonPropertyName("needsReview")] public bool NeedsReview { get; set; }   // legacy UI warning metadata
    [JsonPropertyName("needsUserSetup")] public bool NeedsUserSetup { get; set; } // legacy metadata; selected entries are still attempted
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new(); // e.g. "cli", "tui" (shown as row badges)

    [JsonIgnore] public AppInstallerType ParsedType =>
        Enum.TryParse(InstallerTypeName, true, out AppInstallerType t) ? t : AppInstallerType.Auto;

    /// <summary>
    /// The entry's ordered acquisition sources. Explicit "sources" win;
    /// otherwise the legacy url/github fields become a one-element list.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AppSource> EffectiveSources
    {
        get
        {
            if (Sources is { Count: > 0 })
                return Sources;
            if (!string.IsNullOrWhiteSpace(Url))
                return new[] { AppSource.FromUrl(Url) };
            if (GitHub is not null && !string.IsNullOrWhiteSpace(GitHub.Repo))
                return new[] { AppSource.FromGitHub(GitHub) };
            return Array.Empty<AppSource>();
        }
    }
}

/// <summary>The kind of acquisition channel a source uses.</summary>
public enum AppSourceKind
{
    Url,
    GitHub,
    Winget,
    Scoop,
    Chocolatey
}

/// <summary>One acquisition source on a catalog entry.</summary>
public sealed class AppSource
{
    /// <summary>"url" | "github" | "winget" | "scoop" | "choco" | "chocolatey" (case-insensitive).</summary>
    [JsonPropertyName("kind")] public string KindName { get; set; } = string.Empty;

    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("github")] public GitHubReleaseSource? GitHub { get; set; }
    [JsonPropertyName("id")] public string? PackageId { get; set; }

    [JsonIgnore] public AppSourceKind Kind => ParseKind(KindName);
    [JsonIgnore] public string? WingetId => Kind == AppSourceKind.Winget ? PackageId : null;
    [JsonIgnore] public string? ScoopId => Kind == AppSourceKind.Scoop ? PackageId : null;
    [JsonIgnore] public string? ChocoId => Kind == AppSourceKind.Chocolatey ? PackageId : null;

    public static AppSource FromUrl(string url) => new() { KindName = "url", Url = url };
    public static AppSource FromGitHub(GitHubReleaseSource gh) => new() { KindName = "github", GitHub = gh };
    public static AppSource FromWinget(string packageId) => new() { KindName = "winget", PackageId = packageId };

    public static AppSourceKind ParseKind(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "url" or "download" => AppSourceKind.Url,
        "github" or "gh" => AppSourceKind.GitHub,
        "winget" => AppSourceKind.Winget,
        "scoop" => AppSourceKind.Scoop,
        "choco" or "chocolatey" => AppSourceKind.Chocolatey,
        _ => AppSourceKind.Url
    };
}
