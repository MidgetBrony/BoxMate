using System;
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
        return await JsonSerializer.DeserializeAsync<BoxMateSettings>(stream) ?? new BoxMateSettings();
    }

    public async Task SaveAsync(BoxMateSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
    }
}
