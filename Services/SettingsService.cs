using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BoxMate.Models;

namespace BoxMate.Services;

public sealed class SettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public async Task<BoxMateSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
            return new BoxMateSettings();

        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<BoxMateSettings>(stream) ?? new BoxMateSettings();
        settings.ManifestUrls ??= [];
        settings.PackageChannels = new Dictionary<string, string>(settings.PackageChannels ?? [], StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    public async Task SaveAsync(BoxMateSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
