using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BoxMate.Models;

namespace BoxMate.Services;

public sealed class GitHubAuthenticationException(string message) : InvalidOperationException(message);

public sealed class ManifestService
{
    private static readonly HttpClient Client = CreateClient();
    private static string? GitHubToken;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static void SetGitHubToken(string? token) => GitHubToken =
        string.IsNullOrWhiteSpace(token) ? null : token;

    public async Task<IReadOnlyList<ResolvedPackage>> ResolveAllAsync(
        IEnumerable<string> roots, Action<string> progress, CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task VisitAsync(string manifestUrl, bool catalogueEntry = false)
        {
            var sourceKey = ValidateHttpsUrl(manifestUrl, "manifest or repository").AbsoluteUri;
            if (!visiting.Add(sourceKey)) throw new InvalidOperationException($"Circular manifest dependency detected at {sourceKey}.");

            progress($"Reading {sourceKey}...");
            var (normalized, manifest) = await LoadManifestSourceAsync(sourceKey, cancellationToken);
            var alreadyResolved = resolved.Values.FirstOrDefault(package => package.ManifestUrl.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (alreadyResolved is not null)
            {
                alreadyResolved.IsCatalogueEntry |= catalogueEntry;
                visiting.Remove(sourceKey);
                return;
            }

            if (manifest.Type.Equals("collection", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var member in manifest.Mods)
                    await VisitAsync(NormalizeCollectionRepository(member.Repository), true);
                foreach (var deprecated in manifest.DeprecatedMods)
                {
                    var deprecatedId = GetDeprecatedId(deprecated);
                    if (resolved.ContainsKey(deprecatedId)) continue;
                    resolved[deprecatedId] = new ResolvedPackage
                    {
                        Manifest = new ModManifest
                        {
                            SchemaVersion = 1,
                            Id = deprecatedId,
                            Name = deprecated.Name,
                            Author = manifest.Author,
                            Description = deprecated.Reason,
                            Repository = NormalizeCollectionRepository(deprecated.Repository)
                        },
                        ManifestUrl = normalized + "#deprecated-" + Uri.EscapeDataString(deprecatedId),
                        Version = string.Empty,
                        DownloadUrl = string.Empty,
                        AssetName = string.Empty,
                        Sha256 = string.Empty,
                        IsCatalogueEntry = true,
                        IsDeprecated = true,
                        Replacement = deprecated.Replacement
                    };
                }
                visiting.Remove(sourceKey);
                return;
            }
            foreach (var dependency in manifest.Dependencies.Where(item => item.Required))
                await VisitAsync(dependency.Manifest);

            var package = await ResolveReleaseAsync(manifest, normalized, cancellationToken);
            package.IsCatalogueEntry = catalogueEntry;
            if (resolved.TryGetValue(manifest.Id, out var existing) &&
                !existing.ManifestUrl.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Two manifests use the package id '{manifest.Id}'.");

            foreach (var dependency in manifest.Dependencies.Where(item => item.Required && !string.IsNullOrWhiteSpace(item.MinimumVersion)))
            {
                var dependencyUrl = ValidateHttpsUrl(dependency.Manifest, "dependency manifest").AbsoluteUri;
                var target = resolved.Values.First(item => item.ManifestUrl.Equals(dependencyUrl, StringComparison.OrdinalIgnoreCase));
                if (!VersionSatisfies(target.Version, dependency.MinimumVersion))
                    throw new InvalidOperationException($"{manifest.Name} requires {target.Manifest.Name} {dependency.MinimumVersion} or newer, but {target.Version} is available.");
            }

            visiting.Remove(sourceKey);
            resolved[manifest.Id] = package;
        }

        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            await VisitAsync(root.Trim());

        return resolved.Values.ToList();
    }

    private static async Task<(string Url, ModManifest Manifest)> LoadManifestSourceAsync(string source, CancellationToken cancellationToken)
    {
        var uri = ValidateHttpsUrl(source, "manifest or repository");
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return (uri.AbsoluteUri, await DownloadManifestAsync(uri.AbsoluteUri, cancellationToken));

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException("GitHub mod sources must be repository links such as https://github.com/OWNER/REPOSITORY.");
        foreach (var branch in new[] { "main", "master" })
        {
            var raw = $"https://raw.githubusercontent.com/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/refs/heads/{branch}/manifest.json";
            try { return (raw, await DownloadManifestAsync(raw, cancellationToken)); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        throw new InvalidOperationException($"No manifest.json was found at the root of {uri} on main or master.");
    }

    private static async Task<ModManifest> DownloadManifestAsync(string url, CancellationToken cancellationToken)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var requestUrl = $"{url}{separator}boxmate_refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true &&
            request.RequestUri.AbsolutePath.Contains("/contents/", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
            if (!string.IsNullOrWhiteSpace(GitHubToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
        }
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new GitHubAuthenticationException("GitHub rejected the saved sign-in while reading a manifest.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Could not read manifest {url}: {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<ModManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The manifest was empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static async Task<ResolvedPackage> ResolveReleaseAsync(ModManifest manifest, string manifestUrl, CancellationToken cancellationToken)
    {
        if (!manifest.Release.Provider.Equals("github", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{manifest.Name} uses unsupported release provider '{manifest.Release.Provider}'.");

        var repository = ValidateHttpsUrl(manifest.Repository, "repository");
        if (!repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{manifest.Name} must use a github.com repository.");
        var parts = repository.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new InvalidOperationException($"{manifest.Name} has an invalid GitHub repository URL.");

        var apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest";
        var release = await LoadGitHubReleaseAsync(apiUrl, manifest.Name, cancellationToken);
        var assetPattern = OperatingSystem.IsWindows()
            ? manifest.Release.AssetWindows ?? manifest.Release.Asset
            : manifest.Release.AssetLinux ?? manifest.Release.Asset;
        if (string.IsNullOrWhiteSpace(assetPattern))
            throw new InvalidOperationException($"{manifest.Name} does not publish an asset for this operating system.");
        var matchingAssets = release.Assets.Where(item => AssetMatches(item.Name, assetPattern)).ToList();
        if (matchingAssets.Count == 0)
            throw new InvalidOperationException($"Latest {manifest.Name} release does not contain an asset matching '{assetPattern}'.");
        if (matchingAssets.Count > 1)
            throw new InvalidOperationException($"Latest {manifest.Name} release contains more than one asset matching '{assetPattern}'. Use a more specific pattern.");
        var asset = matchingAssets[0];

        var sha256 = ParseDigest(asset.Digest);
        if (sha256 is null && !string.IsNullOrWhiteSpace(manifest.Release.ChecksumAsset))
        {
            var checksum = release.Assets.SingleOrDefault(item => item.Name.Equals(manifest.Release.ChecksumAsset, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Latest {manifest.Name} release does not contain checksum asset '{manifest.Release.ChecksumAsset}'.");
            sha256 = await ReadChecksumAsync(checksum.BrowserDownloadUrl, asset.Name, cancellationToken);
        }
        if (sha256 is null)
            throw new InvalidOperationException($"{asset.Name} has no GitHub SHA-256 digest. Publish a checksum asset and set release.checksumAsset.");

        return new ResolvedPackage
        {
            Manifest = manifest,
            ManifestUrl = manifestUrl,
            Version = release.TagName.TrimStart('v', 'V'),
            DownloadUrl = asset.BrowserDownloadUrl,
            AssetName = asset.Name,
            Sha256 = sha256
        };
    }

    private static async Task<string> ReadChecksumAsync(string url, string assetName, CancellationToken cancellationToken)
    {
        var text = await Client.GetStringAsync(url, cancellationToken);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 1 && IsSha256(columns[0]) &&
                (columns.Length == 1 || Path.GetFileName(columns[^1].TrimStart('*')).Equals(assetName, StringComparison.OrdinalIgnoreCase)))
                return columns[0];
        }
        throw new InvalidOperationException($"Checksum file does not contain a valid SHA-256 entry for {assetName}.");
    }

    private static async Task<GitHubRelease> LoadGitHubReleaseAsync(string apiUrl, string packageName, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate", "ReleaseCache");
        var cacheName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiUrl))) + ".json";
        var cachePath = Path.Combine(cacheDirectory, cacheName);
        GitHubReleaseCache? cached = null;
        if (File.Exists(cachePath))
        {
            try { cached = JsonSerializer.Deserialize<GitHubReleaseCache>(await File.ReadAllTextAsync(cachePath, cancellationToken), JsonOptions); }
            catch { cached = null; }
        }

        if (cached is not null && DateTimeOffset.UtcNow - cached.SavedAt < TimeSpan.FromMinutes(15))
            return cached.Release;

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (!string.IsNullOrWhiteSpace(GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
        if (!string.IsNullOrWhiteSpace(cached?.ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new GitHubAuthenticationException($"GitHub rejected the saved sign-in while reading {packageName}.");
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
        {
            cached.SavedAt = DateTimeOffset.UtcNow;
            await SaveReleaseCacheAsync(cachePath, cached, cancellationToken);
            return cached.Release;
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        {
            if (cached is not null) return cached.Release;
            var resetText = "later";
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var resetSeconds))
                resetText = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime().ToString("t");
            throw new InvalidOperationException($"GitHub's public lookup limit was reached. Try Refresh again after {resetText}.");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not read the latest GitHub release for {packageName}: {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions)
            ?? throw new InvalidOperationException($"GitHub returned an empty release for {packageName}.");
        await SaveReleaseCacheAsync(cachePath, new GitHubReleaseCache
        {
            SavedAt = DateTimeOffset.UtcNow,
            ETag = response.Headers.ETag?.ToString(),
            Release = release
        }, cancellationToken);
        return release;
    }

    private static async Task SaveReleaseCacheAsync(string path, GitHubReleaseCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(cache, JsonOptions), cancellationToken);
        File.Move(temporary, path, true);
    }

    private static void ValidateManifest(ModManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidOperationException($"Unsupported manifest schema version {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidOperationException("Every manifest requires an id and name.");
        if (manifest.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidOperationException($"Manifest id '{manifest.Id}' contains invalid characters.");
        if (manifest.Type.Equals("collection", StringComparison.OrdinalIgnoreCase))
        {
            if (manifest.Mods.Count == 0) throw new InvalidOperationException($"{manifest.Name} collection contains no mods.");
            foreach (var member in manifest.Mods) NormalizeCollectionRepository(member.Repository);
            foreach (var deprecated in manifest.DeprecatedMods)
            {
                if (string.IsNullOrWhiteSpace(deprecated.Name))
                    throw new InvalidOperationException($"{manifest.Name} has a deprecated mod without a name.");
                var deprecatedId = GetDeprecatedId(deprecated);
                if (deprecatedId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                    throw new InvalidOperationException($"Deprecated mod id '{deprecatedId}' contains invalid characters.");
                NormalizeCollectionRepository(deprecated.Repository);
            }
            return;
        }
        if (!manifest.Type.Equals("mod", StringComparison.OrdinalIgnoreCase) &&
            !manifest.Type.Equals("tool", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{manifest.Name} uses unsupported manifest type '{manifest.Type}'.");
        if (string.IsNullOrWhiteSpace(manifest.Repository) ||
            (string.IsNullOrWhiteSpace(manifest.Release.Asset) &&
             string.IsNullOrWhiteSpace(manifest.Release.AssetWindows) &&
             string.IsNullOrWhiteSpace(manifest.Release.AssetLinux)))
            throw new InvalidOperationException($"{manifest.Name} requires repository and release.asset values.");
        if (manifest.Type.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(manifest.Release.EntryPoint) &&
            string.IsNullOrWhiteSpace(manifest.Release.EntryPointWindows) &&
            string.IsNullOrWhiteSpace(manifest.Release.EntryPointLinux))
            throw new InvalidOperationException($"{manifest.Name} tool requires release.entryPoint.");
        foreach (var dependency in manifest.Dependencies.Where(item => item.Required))
            ValidateHttpsUrl(dependency.Manifest, "dependency manifest");
    }

    private static string NormalizeCollectionRepository(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://github.com/" + trimmed.Trim('/');
        var uri = ValidateHttpsUrl(trimmed, "collection repository");
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2)
            throw new InvalidOperationException("Collection entries must be GitHub repositories such as OWNER/REPOSITORY.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string GetDeprecatedId(DeprecatedMod deprecated)
    {
        if (!string.IsNullOrWhiteSpace(deprecated.Id)) return deprecated.Id.Trim();
        var repository = deprecated.Repository.Trim().TrimEnd('/');
        var slug = repository.Split('/').LastOrDefault() ?? string.Empty;
        if (slug.Equals("Boxroom-Plus", StringComparison.OrdinalIgnoreCase)) return "boxroom-plus";
        if (slug.Equals("Boxroom-Plus-Posters", StringComparison.OrdinalIgnoreCase)) return "boxroom-plus-posters";
        throw new InvalidOperationException($"Deprecated mod '{deprecated.Name}' requires an id.");
    }

    private static Uri ValidateHttpsUrl(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"Enter a valid HTTPS {label} URL.");
        return uri;
    }

    private static bool VersionSatisfies(string available, string? minimum) =>
        string.IsNullOrWhiteSpace(minimum) ||
        (Version.TryParse(available, out var availableVersion) && Version.TryParse(minimum.TrimStart('v', 'V'), out var minimumVersion) && availableVersion >= minimumVersion);

    private static string? ParseDigest(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]) ? digest[7..] : null;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool AssetMatches(string assetName, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return assetName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(assetName, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BoxMate", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseCache
    {
        public DateTimeOffset SavedAt { get; set; }
        public string? ETag { get; set; }
        public GitHubRelease Release { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
