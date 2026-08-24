using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BoxMate.Models;

namespace BoxMate.Services;

public enum PackageInstallStatus { NotConfigured, NotInstalled, Current, Outdated, Modified }

public sealed class InstallationService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly string[] AllowedRoots = ["Mods", "Plugins", "UserLibs", "UserData"];

    public PackageInstallStatus GetPackageStatus(ResolvedPackage package, string gameRoot)
    {
        var recorded = LoadState(gameRoot).Packages.FirstOrDefault(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (recorded is null) return PackageInstallStatus.NotInstalled;
        if (!string.Equals(recorded.Version, package.Version, StringComparison.OrdinalIgnoreCase)) return PackageInstallStatus.Outdated;
        if (recorded.Files.Count == 0 || recorded.Files.Any(file => !File.Exists(GetSafeDestination(gameRoot, file))))
            return PackageInstallStatus.Modified;
        return PackageInstallStatus.Current;
    }

    public async Task<IReadOnlyList<string>> InstallPackageAsync(
        IReadOnlyList<ResolvedPackage> packages, string packageId, string gameRoot,
        Action<string> progress, CancellationToken cancellationToken = default)
    {
        var requested = packages.FirstOrDefault(item => item.Manifest.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Package '{packageId}' was not found.");
        var order = ResolveInstallOrder(packages, requested);
        var completed = new List<string>();
        foreach (var package in order)
        {
            if (GetPackageStatus(package, gameRoot) == PackageInstallStatus.Current) continue;
            progress($"Installing {package.Manifest.Name}...");
            var files = await InstallOneAsync(package, gameRoot, progress, cancellationToken);
            RecordInstalled(package, files, gameRoot);
            completed.Add(package.Manifest.Name);
        }
        return completed;
    }

    private static List<ResolvedPackage> ResolveInstallOrder(IReadOnlyList<ResolvedPackage> packages, ResolvedPackage requested)
    {
        var byUrl = packages.ToDictionary(item => item.ManifestUrl, StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedPackage>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ResolvedPackage package)
        {
            if (visited.Contains(package.Manifest.Id)) return;
            if (!visiting.Add(package.Manifest.Id)) throw new InvalidOperationException($"Circular dependency detected at {package.Manifest.Name}.");
            foreach (var dependency in package.Manifest.Dependencies.Where(item => item.Required))
                Visit(byUrl[new Uri(dependency.Manifest).AbsoluteUri]);
            visiting.Remove(package.Manifest.Id);
            visited.Add(package.Manifest.Id);
            result.Add(package);
        }
        Visit(requested);
        return result;
    }

    private static async Task<IReadOnlyList<string>> InstallOneAsync(
        ResolvedPackage package, string gameRoot, Action<string> progress, CancellationToken cancellationToken)
    {
        ValidateRequirements(package.Manifest, gameRoot);
        var boxMateRoot = Path.Combine(gameRoot, "UserData", "BoxMate");
        var workRoot = Path.Combine(boxMateRoot, "Downloads", package.Manifest.Id + "-" + Guid.NewGuid().ToString("N"));
        var stageRoot = Path.Combine(workRoot, "stage");
        var backupRoot = Path.Combine(boxMateRoot, "Backups", package.Manifest.Id, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var download = Path.Combine(workRoot, package.AssetName);
            progress($"Downloading {package.AssetName}...");
            using (var response = await Client.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(download);
                await input.CopyToAsync(output, cancellationToken);
            }
            if (!HashMatches(download, package.Sha256)) throw new InvalidOperationException($"SHA-256 verification failed for {package.AssetName}.");

            var prepared = new List<(string Source, string Relative, string Destination)>();
            if (Path.GetExtension(package.AssetName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(stageRoot);
                using var archive = ZipFile.OpenRead(download);
                foreach (var entry in archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name)))
                {
                    var archiveRelative = entry.FullName.Replace('\\', '/').TrimStart('/');
                    var prefix = package.Manifest.Release.Destination?.Replace('\\', '/').Trim('/') ?? string.Empty;
                    var relative = string.IsNullOrWhiteSpace(prefix) ? archiveRelative : $"{prefix}/{archiveRelative}";
                    var destination = GetSafeDestination(gameRoot, relative);
                    var staged = Path.GetFullPath(Path.Combine(stageRoot, archiveRelative.Replace('/', Path.DirectorySeparatorChar)));
                    var stagePrefix = Path.GetFullPath(stageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!staged.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"ZIP entry '{entry.FullName}' escapes its package.");
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    entry.ExtractToFile(staged, true);
                    prepared.Add((staged, relative, destination));
                }
                if (prepared.Count == 0) throw new InvalidOperationException("The release ZIP contains no files.");
            }
            else
            {
                var relative = package.Manifest.Release.Destination;
                if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException($"{package.Manifest.Name} must set release.destination for a non-ZIP asset.");
                prepared.Add((download, relative, GetSafeDestination(gameRoot, relative)));
            }

            var duplicate = prepared.GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null) throw new InvalidOperationException($"Package contains duplicate destination '{duplicate.Key}'.");
            var replaced = new List<(string Destination, string? Backup)>();
            try
            {
                foreach (var item in prepared)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                    string? backup = null;
                    if (File.Exists(item.Destination))
                    {
                        backup = Path.Combine(backupRoot, item.Relative.Replace('/', Path.DirectorySeparatorChar));
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
            return prepared.Select(item => item.Relative).ToList();
        }
        finally
        {
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true);
        }
    }

    private static void ValidateRequirements(ModManifest manifest, string gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Requirements.MelonLoader) && !Directory.Exists(Path.Combine(gameRoot, "MelonLoader")))
            throw new InvalidOperationException($"{manifest.Name} requires MelonLoader {manifest.Requirements.MelonLoader}+ but MelonLoader was not found.");
    }

    private static string GetSafeDestination(string gameRoot, string relativeDestination)
    {
        if (string.IsNullOrWhiteSpace(relativeDestination) || Path.IsPathRooted(relativeDestination))
            throw new InvalidOperationException("Package destinations must be relative paths.");
        var normalized = relativeDestination.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var firstPart = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstPart is null || !AllowedRoots.Contains(firstPart, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Destination '{relativeDestination}' is outside BoxMate's allowed folders.");
        var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, normalized));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Destination '{relativeDestination}' escapes the BOXROOM folder.");
        return destination;
    }

    private static bool HashMatches(string filePath, string expected)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static InstalledState LoadState(string gameRoot)
    {
        var path = GetStatePath(gameRoot);
        if (!File.Exists(path)) return new InstalledState();
        try { return JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path)) ?? new InstalledState(); }
        catch { return new InstalledState(); }
    }

    private static void RecordInstalled(ResolvedPackage package, IReadOnlyList<string> files, string gameRoot)
    {
        var state = LoadState(gameRoot);
        state.Packages.RemoveAll(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        state.Packages.Add(new InstalledPackage
        {
            Id = package.Manifest.Id, Name = package.Manifest.Name, Version = package.Version,
            ManifestUrl = package.ManifestUrl, AssetSha256 = package.Sha256, Files = files.ToList()
        });
        var path = GetStatePath(gameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static string GetStatePath(string gameRoot) => Path.Combine(gameRoot, "UserData", "BoxMate", "installed.json");
}
