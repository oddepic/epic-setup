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
    Script,     // run a script (powershell) - trusted source only
    WingetUpdate
}

public sealed class AppEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("size")] public int? Size { get; set; }   // estimated full app size in MB (for the hover tooltip)
    [JsonPropertyName("icon")] public string? Icon { get; set; }   // optional explicit icon URL; else derived from homepage
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }   // expected signer subject CN substring
    [JsonPropertyName("signers")] public List<string> Signers { get; set; } = new(); // alternative accepted signers
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("github")] public GitHubReleaseSource? GitHub { get; set; }
    [JsonPropertyName("installerType")] public string? InstallerTypeName { get; set; } = "Auto";
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("portableSubdir")] public string? PortableSubdir { get; set; } // e.g. Programs\<id>
    [JsonPropertyName("closeApps")] public List<string> CloseApps { get; set; } = new(); // process names (no .exe) killed before install if running
    [JsonPropertyName("installTimeoutSeconds")] public int? InstallTimeoutSeconds { get; set; } // installer wait bound; default 600
    [JsonPropertyName("arch")] public string? Arch { get; set; } = "x64";
    [JsonPropertyName("unverified")] public bool Unverified { get; set; }   // unsigned-but-pinned (badge)
    [JsonPropertyName("needsReview")] public bool NeedsReview { get; set; }   // URL/signer not yet confirmed (warning)
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }

    [JsonIgnore] public AppInstallerType ParsedType =>
        Enum.TryParse(InstallerTypeName, true, out AppInstallerType t) ? t : AppInstallerType.Auto;
}