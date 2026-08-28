using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BoxMate.Services;

public static partial class SteamGameLocator
{
    private const string BoxroomAppId = "4335460";

    public static string? FindBoxroomFolder()
    {
        try
        {
            foreach (var steamRoot in GetSteamRoots())
            {
                foreach (var steamApps in GetSteamAppsFolders(steamRoot))
                {
                    var manifestPath = Path.Combine(steamApps, $"appmanifest_{BoxroomAppId}.acf");
                    var installDirectory = ReadVdfValue(manifestPath, "installdir");
                    if (string.IsNullOrWhiteSpace(installDirectory)) continue;

                    var gameFolder = Path.Combine(steamApps, "common", installDirectory);
                    if (File.Exists(Path.Combine(gameFolder, "BOXROOM.exe")))
                        return Path.GetFullPath(gameFolder);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var candidates = new List<string>();
        var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsLinux())
        {
            candidates.Add(Path.Combine(userFolder, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(userFolder, ".steam", "steam"));
            candidates.Add(Path.Combine(userFolder, ".steam", "root"));
            candidates.Add(Path.Combine(userFolder, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        }
        else if (OperatingSystem.IsWindows())
        {
            AddIfPresent(candidates, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString());
            AddIfPresent(candidates, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString());
            AddIfPresent(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath)) yield return fullPath;
        }
    }

    private static IEnumerable<string> GetSteamAppsFolders(string steamRoot)
    {
        var folders = new List<string> { Path.Combine(steamRoot, "steamapps") };
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            foreach (Match match in VdfPathRegex().Matches(File.ReadAllText(libraryFile)))
            {
                var libraryRoot = match.Groups[1].Value.Replace(@"\\", @"\");
                folders.Add(Path.Combine(libraryRoot, "steamapps"));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            var fullPath = Path.GetFullPath(folder);
            if (seen.Add(fullPath)) yield return fullPath;
        }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        if (!File.Exists(path)) return null;
        var match = Regex.Match(File.ReadAllText(path), $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]+)\\\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();
}
