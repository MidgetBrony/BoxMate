using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BoxMate.Models;

public static class ReleaseChannels
{
    public static IReadOnlyList<string> Choices { get; } = ["Stable", "Beta", "Alpha", "Experimental"];

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "beta" => "Beta",
        "alpha" => "Alpha",
        "experimental" => "Experimental",
        _ => "Stable"
    };

    public static int Rank(string channel) => Normalize(channel) switch
    {
        "Beta" => 1,
        "Alpha" => 2,
        "Experimental" => 3,
        _ => 0
    };

    public static string Classify(string tag, bool prerelease)
    {
        var suffix = Regex.Match(tag, @"(?:^|[-._])(beta|alpha|experimental)(?:[-._\d]|$)", RegexOptions.IgnoreCase);
        if (suffix.Success) return Normalize(suffix.Groups[1].Value);
        return prerelease ? "Experimental" : "Stable";
    }

    public static string ForPackage(BoxMateSettings settings, string packageId) =>
        settings.PackageChannels is not null && settings.PackageChannels.TryGetValue(packageId, out var channel)
            ? Normalize(channel)
            : "Stable";
}
