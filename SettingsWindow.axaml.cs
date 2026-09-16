using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BoxMate.Models;
using BoxMate.Services;

namespace BoxMate;

public partial class SettingsWindow : Window
{
    private readonly BoxMateSettings _settings;
    private readonly List<ChannelSettingRow> _rows;

    public SettingsWindow() : this(new BoxMateSettings(), []) { }

    public SettingsWindow(BoxMateSettings settings, IReadOnlyList<ResolvedPackage> packages)
    {
        InitializeComponent();
        _settings = new BoxMateSettings
        {
            GameFolder = settings.GameFolder,
            ManifestUrls = [.. settings.ManifestUrls],
            PackageChannels = new Dictionary<string, string>(settings.PackageChannels ?? [], StringComparer.OrdinalIgnoreCase)
        };
        GameFolderBox.Text = _settings.GameFolder;
        _rows = packages.Where(package => !package.IsDeprecated)
            .OrderBy(package => package.Manifest.Name)
            .Select(package => new ChannelSettingRow(package.Manifest.Id, package.Manifest.Name,
                ReleaseChannels.ForPackage(_settings, package.Manifest.Id))).ToList();
        ChannelList.ItemsSource = _rows;
        NoPackagesText.IsVisible = _rows.Count == 0;
        UpdateSourceSummary();
    }

    private async void BrowseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var choices = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your BOXROOM folder", AllowMultiple = false
        });
        if (choices.Count > 0) GameFolderBox.Text = choices[0].Path.LocalPath;
    }

    private async void ManageSourcesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var sources = await new ManifestSourcesWindow(_settings.ManifestUrls).ShowDialog<List<string>?>(this);
        if (sources is null) return;
        _settings.ManifestUrls = sources;
        UpdateSourceSummary();
    }

    private void Channel_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string packageId, SelectedItem: string selected }) return;
        var channel = ReleaseChannels.Normalize(selected);
        if (channel == "Stable") _settings.PackageChannels.Remove(packageId);
        else _settings.PackageChannels[packageId] = channel;
    }

    private void UpdateSourceSummary() => SourceSummaryText.Text = _settings.ManifestUrls.Count switch
    {
        0 => "Official catalogue only",
        1 => "Official catalogue and 1 added repository",
        _ => $"Official catalogue and {_settings.ManifestUrls.Count} added repositories"
    };

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => Close(null);

    private void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _settings.GameFolder = GameFolderBox.Text?.Trim() ?? string.Empty;
        Close(_settings);
    }
}

public sealed class ChannelSettingRow(string id, string name, string selectedChannel)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string SelectedChannel { get; } = selectedChannel;
    public IReadOnlyList<string> Choices => ReleaseChannels.Choices;
}
