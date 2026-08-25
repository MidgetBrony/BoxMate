using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BoxMate.Models;

public sealed class ModManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "mod";
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("repository")] public string Repository { get; set; } = string.Empty;
    [JsonPropertyName("requirements")] public ModRequirements Requirements { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ManifestDependency> Dependencies { get; set; } = [];
    [JsonPropertyName("release")] public ReleaseDefinition Release { get; set; } = new();
    [JsonPropertyName("mods")] public List<CollectionMod> Mods { get; set; } = [];
    [JsonPropertyName("deprecatedMods")] public List<DeprecatedMod> DeprecatedMods { get; set; } = [];
}

public sealed class DeprecatedMod
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("repository")] public string Repository { get; set; } = string.Empty;
    [JsonPropertyName("replacement")] public string Replacement { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

public sealed class CollectionMod
{
    [JsonPropertyName("repository")] public string Repository { get; set; } = string.Empty;
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
}

public sealed class ModRequirements
{
    [JsonPropertyName("melonLoader")] public string? MelonLoader { get; set; }
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
}

public sealed class ManifestDependency
{
    [JsonPropertyName("manifest")] public string Manifest { get; set; } = string.Empty;
    [JsonPropertyName("minimumVersion")] public string? MinimumVersion { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}

public sealed class ReleaseDefinition
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "github";
    [JsonPropertyName("asset")] public string Asset { get; set; } = string.Empty;
    [JsonPropertyName("assetWindows")] public string? AssetWindows { get; set; }
    [JsonPropertyName("assetLinux")] public string? AssetLinux { get; set; }
    [JsonPropertyName("checksumAsset")] public string? ChecksumAsset { get; set; }
    [JsonPropertyName("destination")] public string? Destination { get; set; }
    [JsonPropertyName("entryPoint")] public string? EntryPoint { get; set; }
    [JsonPropertyName("entryPointWindows")] public string? EntryPointWindows { get; set; }
    [JsonPropertyName("entryPointLinux")] public string? EntryPointLinux { get; set; }
}

public sealed class ResolvedPackage
{
    public required ModManifest Manifest { get; init; }
    public required string ManifestUrl { get; init; }
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required string AssetName { get; init; }
    public required string Sha256 { get; init; }
    public bool IsCatalogueEntry { get; set; }
    public bool IsDeprecated { get; init; }
    public string Replacement { get; init; } = string.Empty;
}

public sealed class BoxMateSettings
{
    public string GameFolder { get; set; } = string.Empty;
    public List<string> ManifestUrls { get; set; } = [];
}

public sealed class InstalledState
{
    public List<InstalledPackage> Packages { get; set; } = [];
}

public sealed class InstalledPackage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string AssetSha256 { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
}
