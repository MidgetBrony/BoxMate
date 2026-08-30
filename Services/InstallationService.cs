using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
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
    private static readonly string AllowedUnityNativeRoot = Path.Combine("BOXROOM_Data", "Plugins", "x86_64");

    public static bool IsTool(ResolvedPackage package) =>
        package.Manifest.Type.Equals("tool", StringComparison.OrdinalIgnoreCase);

    public bool IsRecordedInstalled(ResolvedPackage package, string gameRoot) =>
        LoadState(package, gameRoot).Packages.Any(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));

    public string GetRecordedVersion(ResolvedPackage package, string gameRoot) =>
        LoadState(package, gameRoot).Packages.FirstOrDefault(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase))?.Version ?? string.Empty;

    public PackageInstallStatus GetPackageStatus(ResolvedPackage package, string gameRoot)
    {
        var installRoot = GetInstallRoot(package, gameRoot);
        var recorded = LoadState(package, gameRoot).Packages.FirstOrDefault(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (recorded is null) return PackageInstallStatus.NotInstalled;
        if (!string.Equals(recorded.Version, package.Version, StringComparison.OrdinalIgnoreCase)) return PackageInstallStatus.Outdated;
        if (recorded.Files.Count == 0 || recorded.Files.Any(file => !File.Exists(GetSafeDestination(package, installRoot, file))))
            return PackageInstallStatus.Modified;
        return PackageInstallStatus.Current;
    }

    public string GetToolEntryPoint(ResolvedPackage package)
    {
        if (!IsTool(package)) throw new InvalidOperationException("Only tool packages can be launched.");
        return GetSafeDestination(package, GetInstallRoot(package, string.Empty), GetEntryPoint(package));
    }

    public void LaunchTool(ResolvedPackage package)
    {
        var entryPoint = GetToolEntryPoint(package);
        if (!File.Exists(entryPoint)) throw new InvalidOperationException($"{package.Manifest.Name} is missing its launch file. Repair it first.");
        Process.Start(new ProcessStartInfo(entryPoint) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entryPoint)! });
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

    public Task<string> UninstallPackageAsync(
        IReadOnlyList<ResolvedPackage> packages, string packageId, string gameRoot,
        Action<string> progress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = packages.FirstOrDefault(item => item.Manifest.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("That package is no longer available in the catalogue.");
        var installRoot = GetInstallRoot(target, gameRoot);
        var state = LoadState(target, gameRoot);
        var installed = state.Packages.FirstOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("That package is not recorded as installed by BoxMate.");
        if (!IsTool(target))
        {
            var installedIds = state.Packages.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependant = packages.FirstOrDefault(package =>
                !package.Manifest.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                installedIds.Contains(package.Manifest.Id) &&
                package.Manifest.Dependencies.Where(item => item.Required).Any(dependency =>
                    packages.FirstOrDefault(candidate => candidate.ManifestUrl.Equals(
                        ManifestSourceHelper.NormalizeManifestIdentity(dependency.Manifest), StringComparison.OrdinalIgnoreCase))?.Manifest.Id
                        .Equals(packageId, StringComparison.OrdinalIgnoreCase) == true));
            if (dependant is not null)
                throw new InvalidOperationException($"{installed.Name} is required by installed mod {dependant.Manifest.Name}. Uninstall that mod first.");
        }

        progress($"Uninstalling {installed.Name}...");
        var sharedFiles = state.Packages.Where(item => !item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in installed.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sharedFiles.Contains(relative)) continue;
            var path = GetSafeDestination(target, installRoot, relative);
            if (File.Exists(path)) File.Delete(path);
            RemoveEmptyParents(target, installRoot, path);
        }

        state.Packages.RemoveAll(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        SaveState(state, target, gameRoot);
        return Task.FromResult(installed.Name);
    }

    private static List<ResolvedPackage> ResolveInstallOrder(IReadOnlyList<ResolvedPackage> packages, ResolvedPackage requested)
    {
        var byUrl = packages.ToDictionary(
            item => ManifestSourceHelper.NormalizeManifestIdentity(item.ManifestUrl),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedPackage>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ResolvedPackage package)
        {
            if (visited.Contains(package.Manifest.Id)) return;
            if (!visiting.Add(package.Manifest.Id)) throw new InvalidOperationException($"Circular dependency detected at {package.Manifest.Name}.");
            foreach (var dependency in package.Manifest.Dependencies.Where(item => item.Required))
                Visit(byUrl[ManifestSourceHelper.NormalizeManifestIdentity(dependency.Manifest)]);
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
        var installRoot = GetInstallRoot(package, gameRoot);
        var boxMateRoot = IsTool(package)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate")
            : Path.Combine(gameRoot, "UserData", "BoxMate");
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
                    var destination = GetSafeDestination(package, installRoot, relative);
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
                prepared.Add((download, relative, GetSafeDestination(package, installRoot, relative)));
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
            if (IsTool(package) && OperatingSystem.IsLinux())
            {
                var entryPoint = GetSafeDestination(package, installRoot, GetEntryPoint(package));
                if (File.Exists(entryPoint))
                    File.SetUnixFileMode(entryPoint, File.GetUnixFileMode(entryPoint) |
                        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
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

    private static string GetSafeDestination(ResolvedPackage package, string installRoot, string relativeDestination)
    {
        if (string.IsNullOrWhiteSpace(relativeDestination) || Path.IsPathRooted(relativeDestination))
            throw new InvalidOperationException("Package destinations must be relative paths.");
        var normalized = relativeDestination.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var firstPart = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        bool allowedModRoot = firstPart is not null && AllowedRoots.Contains(firstPart, StringComparer.OrdinalIgnoreCase);
        bool allowedUnityNativePath = normalized.Equals(AllowedUnityNativeRoot, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(AllowedUnityNativeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!IsTool(package) && !allowedModRoot && !allowedUnityNativePath)
            throw new InvalidOperationException($"Destination '{relativeDestination}' is outside BoxMate's allowed folders.");
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

    private static InstalledState LoadState(ResolvedPackage package, string gameRoot)
    {
        var path = GetStatePath(package, gameRoot);
        if (!File.Exists(path)) return new InstalledState();
        try { return JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path)) ?? new InstalledState(); }
        catch { return new InstalledState(); }
    }

    private static void RecordInstalled(ResolvedPackage package, IReadOnlyList<string> files, string gameRoot)
    {
        var state = LoadState(package, gameRoot);
        state.Packages.RemoveAll(item => item.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        state.Packages.Add(new InstalledPackage
        {
            Id = package.Manifest.Id, Name = package.Manifest.Name, Version = package.Version,
            ManifestUrl = package.ManifestUrl, AssetSha256 = package.Sha256, Files = files.ToList()
        });
        SaveState(state, package, gameRoot);
    }

    private static void SaveState(InstalledState state, ResolvedPackage package, string gameRoot)
    {
        var path = GetStatePath(package, gameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static void RemoveEmptyParents(ResolvedPackage package, string installRoot, string filePath)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var protectedRoots = (IsTool(package) ? [] : AllowedRoots.Select(name => Path.Combine(root, name))
                .Append(Path.Combine(root, AllowedUnityNativeRoot)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(directory) &&
               !directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
               !protectedRoots.Contains(directory) &&
               directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static string GetInstallRoot(ResolvedPackage package, string gameRoot) => IsTool(package)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate", "Tools", package.Manifest.Id)
        : gameRoot;

    private static string GetEntryPoint(ResolvedPackage package)
    {
        var entryPoint = OperatingSystem.IsWindows()
            ? package.Manifest.Release.EntryPointWindows ?? package.Manifest.Release.EntryPoint
            : package.Manifest.Release.EntryPointLinux ?? package.Manifest.Release.EntryPoint;
        return !string.IsNullOrWhiteSpace(entryPoint)
            ? entryPoint
            : throw new InvalidOperationException($"{package.Manifest.Name} has no launch file for this operating system.");
    }

    private static string GetStatePath(ResolvedPackage package, string gameRoot) => IsTool(package)
        ? Path.Combine(GetInstallRoot(package, gameRoot), ".boxmate-installed.json")
        : Path.Combine(gameRoot, "UserData", "BoxMate", "installed.json");
}
