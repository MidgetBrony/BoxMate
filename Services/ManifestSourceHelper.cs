using System;

namespace BoxMate.Services;

public static class ManifestSourceHelper
{
    public static string NormalizeManifestIdentity(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && parts[3].Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Path = $"/{parts[0]}/{parts[1]}/refs/heads/{parts[2]}/{parts[3]}"
            };
            return builder.Uri.AbsoluteUri;
        }

        return uri.AbsoluteUri;
    }

    public static string NormalizeForStorage(string value)
    {
        value = value.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal) && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2)
            value = "https://github.com/" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Enter OWNER/REPOSITORY, a GitHub repository link, or a raw HTTPS manifest link.");
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new InvalidOperationException("GitHub repository links must contain only OWNER/REPOSITORY.");
            return $"https://github.com/{parts[0]}/{parts[1]}";
        }
        return uri.AbsoluteUri;
    }

    public static string ToDisplayName(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return uri.AbsolutePath.Trim('/');
        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $"{parts[0]}/{parts[1]}";
        }
        return value;
    }
}
