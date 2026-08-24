using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BoxMate.Services;

public sealed record BoxMateUpdate(string Version, string DownloadUrl, string AssetName, string Sha256, string PageUrl);

public sealed class SelfUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MidgetBrony/BoxMate/releases/latest";
    private static readonly HttpClient Client = CreateClient();
    private static string? GitHubToken;
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static void SetGitHubToken(string? token) => GitHubToken = string.IsNullOrWhiteSpace(token) ? null : token;

    public async Task<BoxMateUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        if (!string.IsNullOrWhiteSpace(GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty BoxMate release.");
        var availableText = release.TagName.TrimStart('v', 'V');
        if (!Version.TryParse(availableText, out var available) ||
            !Version.TryParse(CurrentVersion, out var current) || available <= current) return null;

        var assetName = OperatingSystem.IsWindows() ? "BoxMate-windows-x64.zip" :
            OperatingSystem.IsLinux() ? "BoxMate-linux-x64.tar.gz" :
            throw new PlatformNotSupportedException("Automatic BoxMate updates support Windows and Linux x64.");
        var asset = release.Assets.SingleOrDefault(item => item.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"BoxMate {availableText} does not contain {assetName}.");
        var sha256 = ParseDigest(asset.Digest);
        if (sha256 is null)
        {
            var sums = release.Assets.SingleOrDefault(item => item.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The BoxMate release has no SHA-256 checksum file.");
            sha256 = await ReadChecksumAsync(sums.BrowserDownloadUrl, assetName, cancellationToken);
        }
        return new BoxMateUpdate(availableText, asset.BrowserDownloadUrl, assetName, sha256, release.HtmlUrl);
    }

    public async Task StartUpdateAsync(BoxMateUpdate update, Action<string> progress, CancellationToken cancellationToken = default)
    {
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("BoxMate could not locate its executable.");
        var executableName = Path.GetFileName(executable);
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoxMate", "Updates", update.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, update.AssetName);
        var extracted = Path.Combine(staging, "app");
        Directory.CreateDirectory(extracted);

        progress($"Downloading BoxMate {update.Version}...");
        using (var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(archivePath);
            await input.CopyToAsync(output, cancellationToken);
        }
        await using (var stream = File.OpenRead(archivePath))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The BoxMate update failed SHA-256 verification.");
        }

        progress("Preparing update...");
        if (update.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(archivePath, extracted, true);
        else
        {
            await using var archive = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extracted, true);
        }

        var stagedExecutable = Path.Combine(extracted, executableName);
        if (!File.Exists(stagedExecutable)) throw new InvalidOperationException("The update archive does not contain the BoxMate executable.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stagedExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var start = new ProcessStartInfo { FileName = stagedExecutable, UseShellExecute = false, WorkingDirectory = extracted };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(extracted);
        start.ArgumentList.Add(appDirectory);
        start.ArgumentList.Add(executableName);
        _ = Process.Start(start) ?? throw new InvalidOperationException("The BoxMate update helper could not start.");
    }

    public static void ApplyStagedUpdate(int parentProcessId, string sourceDirectory, string targetDirectory, string executableName)
    {
        try { Process.GetProcessById(parentProcessId).WaitForExit(120_000); }
        catch (ArgumentException) { }
        Directory.CreateDirectory(targetDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var destination = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
        var installedExecutable = Path.Combine(targetDirectory, executableName);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(installedExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Process.Start(new ProcessStartInfo { FileName = installedExecutable, WorkingDirectory = targetDirectory, UseShellExecute = true });
    }

    private static async Task<string> ReadChecksumAsync(string url, string assetName, CancellationToken cancellationToken)
    {
        var text = await Client.GetStringAsync(url, cancellationToken);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && Path.GetFileName(parts[^1].TrimStart('*')).Equals(assetName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)) return parts[0];
        }
        throw new InvalidOperationException($"The checksum file has no entry for {assetName}.");
    }

    private static string? ParseDigest(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        digest[7..].Length == 64 && digest[7..].All(Uri.IsHexDigit) ? digest[7..] : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BoxMate", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
