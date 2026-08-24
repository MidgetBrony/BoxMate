using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BoxMate.Services;

public sealed class MelonLoaderService
{
    public const string ProtonLaunchOption = "WINEDLLOVERRIDES=\"version=n,b\" %command%";
    private const string ReleasesApi = "https://api.github.com/repos/LavaGang/MelonLoader/releases/latest";
    private const string WindowsAsset = "MelonLoader.x64.zip";
    private static readonly HttpClient Client = CreateClient();

    public static void SetGitHubToken(string? token) => Client.DefaultRequestHeaders.Authorization =
        string.IsNullOrWhiteSpace(token) ? null : new AuthenticationHeaderValue("Bearer", token);

    public bool IsInstalled(string gameRoot) =>
        Directory.Exists(Path.Combine(gameRoot, "MelonLoader")) && File.Exists(Path.Combine(gameRoot, "version.dll"));

    public async Task<string> InstallLatestAsync(string gameRoot, Action<string> progress, CancellationToken cancellationToken = default)
    {
        if (System.Diagnostics.Process.GetProcessesByName("BOXROOM").Length > 0)
            throw new InvalidOperationException("Close BOXROOM before installing or updating MelonLoader.");

        progress("Finding the latest official MelonLoader release...");
        using var releaseResponse = await Client.GetAsync(ReleasesApi, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(releaseStream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty MelonLoader release.");
        var asset = release.Assets.SingleOrDefault(item => item.Name.Equals(WindowsAsset, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The latest official MelonLoader release has no {WindowsAsset} asset.");
        var expectedHash = ParseDigest(asset.Digest)
            ?? throw new InvalidOperationException("GitHub did not publish a SHA-256 digest for the official MelonLoader archive.");

        var workRoot = Path.Combine(gameRoot, "UserData", "BoxMate", "Downloads", "melonloader-" + Guid.NewGuid().ToString("N"));
        var stageRoot = Path.Combine(workRoot, "stage");
        var archivePath = Path.Combine(workRoot, WindowsAsset);
        var backupRoot = Path.Combine(gameRoot, "UserData", "BoxMate", "Backups", "melonloader", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(stageRoot);
        try
        {
            progress($"Downloading official MelonLoader {release.TagName}...");
            using (var response = await Client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archivePath);
                await input.CopyToAsync(output, cancellationToken);
            }
            using (var input = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(SHA256.HashData(input));
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("MelonLoader SHA-256 verification failed.");
            }

            var prepared = new List<(string Source, string Relative, string Destination)>();
            using (var archive = ZipFile.OpenRead(archivePath))
            foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name)))
            {
                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!(relative.StartsWith("MelonLoader/", StringComparison.OrdinalIgnoreCase) ||
                      relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Official archive contains unexpected root entry '{entry.FullName}'.");
                var staged = SafeCombine(stageRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                entry.ExtractToFile(staged, true);
                prepared.Add((staged, relative, SafeCombine(gameRoot, relative)));
            }

            var replaced = new List<(string Destination, string? Backup)>();
            try
            {
                foreach (var item in prepared)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                    string? backup = null;
                    if (File.Exists(item.Destination))
                    {
                        backup = SafeCombine(backupRoot, item.Relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(item.Destination, backup, true);
                    }
                    File.Copy(item.Source, item.Destination, true);
                    replaced.Add((item.Destination, backup));
                }
            }
            catch
            {
                foreach (var item in replaced.AsEnumerable().Reverse())
                {
                    if (item.Backup is not null && File.Exists(item.Backup)) File.Copy(item.Backup, item.Destination, true);
                    else if (File.Exists(item.Destination)) File.Delete(item.Destination);
                }
                throw;
            }
            if (OperatingSystem.IsLinux())
                await File.WriteAllTextAsync(Path.Combine(gameRoot, "BoxMate-Linux-Setup.txt"),
                    "BOXROOM requires this Steam launch option for MelonLoader under Proton:\n\n" + ProtonLaunchOption +
                    "\n\nSteam > BOXROOM > Properties > General > Launch Options\n", cancellationToken);
            return release.TagName;
        }
        finally
        {
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true);
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Archive path '{relative}' escapes its destination.");
        return result;
    }

    private static string? ParseDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hash = digest[7..];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BoxMate", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
