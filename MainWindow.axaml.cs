using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BoxMate.Models;
using BoxMate.Services;

namespace BoxMate;

public partial class MainWindow : Window
{
    private const string OfficialCatalogue = "https://github.com/MidgetBrony/BoxMate-Mods";
    private readonly SettingsService _settingsService = new();
    private readonly ManifestService _manifestService = new();
    private readonly InstallationService _installationService = new();
    private readonly MelonLoaderService _melonLoaderService = new();
    private readonly GitHubAuthService _gitHubAuthService = new();
    private readonly SelfUpdateService _selfUpdateService = new();
    private string? _gitHubToken;
    private BoxMateUpdate? _availableUpdate;
    private BoxMateSettings _settings = new();
    private IReadOnlyList<ResolvedPackage> _packages = [];

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_OnOpened;
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        _gitHubToken = _gitHubAuthService.LoadToken();
        ApplyGitHubAuthentication();
        GameFolderBox.Text = _settings.GameFolder;
        UpdateSourceSummary();
        UpdateMelonLoaderStatus();
        UpdateGitHubStatus();
        LinuxSetupPanel.IsVisible = OperatingSystem.IsLinux();
        await RefreshManifestsAsync();
        await CheckForBoxMateUpdateAsync();
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e) => await RefreshManifestsAsync();

    private async void ManageSourcesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReadSettingsFromForm();
        var dialog = new ManifestSourcesWindow(_settings.ManifestUrls);
        var sources = await dialog.ShowDialog<List<string>?>(this);
        if (sources is null) return;
        _settings.ManifestUrls = sources;
        await _settingsService.SaveAsync(_settings);
        UpdateSourceSummary();
        await RefreshManifestsAsync();
    }

    private async void ChooseFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var choices = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your BOXROOM folder", AllowMultiple = false
        });
        if (choices.Count > 0)
        {
            GameFolderBox.Text = choices[0].Path.LocalPath;
            UpdateMelonLoaderStatus();
        }
    }

    private async void InstallMelonLoaderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReadSettingsFromForm();
        if (!Directory.Exists(_settings.GameFolder) || !File.Exists(Path.Combine(_settings.GameFolder, "BOXROOM.exe")))
        {
            SetStatus("Choose the folder containing BOXROOM.exe first.");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var version = await _melonLoaderService.InstallLatestAsync(_settings.GameFolder,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
            UpdateMelonLoaderStatus();
            RenderPackages();
            if (OperatingSystem.IsLinux())
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(MelonLoaderService.ProtonLaunchOption);
                SetStatus($"MelonLoader {version} installed. Proton launch option copied; paste it into BOXROOM's Steam properties.");
            }
            else SetStatus($"MelonLoader {version} installed. Launch BOXROOM once before installing mods.");
        });
    }

    private async void CopyProtonLaunchOptionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { SetStatus("Clipboard is unavailable."); return; }
        await clipboard.SetTextAsync(MelonLoaderService.ProtonLaunchOption);
        SetStatus("Copied the Proton launch option. Paste it into Steam > BOXROOM > Properties > Launch Options.");
    }

    private async void GitHubSignInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_gitHubToken))
        {
            _gitHubAuthService.SignOut();
            _gitHubToken = null;
            ApplyGitHubAuthentication();
            UpdateGitHubStatus();
            SetStatus("Signed out of GitHub. Cached anonymous access remains available.");
            return;
        }

        await RunBusyAsync(async () =>
        {
            _gitHubToken = await _gitHubAuthService.SignInAsync(prompt =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ToolTip.SetTip(GitHubSignInButton, $"Enter code {prompt.UserCode} in the browser");
                    SetStatus($"GitHub code {prompt.UserCode} copied. Complete sign-in in your browser.");
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard is not null) _ = clipboard.SetTextAsync(prompt.UserCode);
                });
            });
            ApplyGitHubAuthentication();
            UpdateGitHubStatus();
            SetStatus("Signed in to GitHub. Release lookup limit is now 5,000 per hour.");
            await RefreshManifestsAsync();
        });
    }

    private async void InstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string packageId }) return;
        ReadSettingsFromForm();
        if (!ValidateGameFolder(_settings.GameFolder))
        {
            SetStatus("Choose a valid BOXROOM folder before installing.");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var needsMelonLoader = _packages.Any(package => !string.IsNullOrWhiteSpace(package.Manifest.Requirements.MelonLoader));
            if (needsMelonLoader && !_melonLoaderService.IsInstalled(_settings.GameFolder))
            {
                SetStatus("Installing required MelonLoader...");
                await _melonLoaderService.InstallLatestAsync(_settings.GameFolder,
                    message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
                UpdateMelonLoaderStatus();
            }
            SetStatus("Resolving required mods...");
            var installed = await _installationService.InstallPackageAsync(
                _packages, packageId, _settings.GameFolder,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
            SetStatus(installed.Count == 0 ? "Everything is already current." : $"Installed {string.Join(", ", installed)}.");
            RenderPackages();
        });
    }

    private async void UninstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string packageId }) return;
        var package = _packages.FirstOrDefault(item => item.Manifest.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        if (package is null || !await ConfirmAsync("Uninstall mod?",
                $"Remove {package.Manifest.Name} from BOXROOM? Only files recorded by BoxMate will be removed.")) return;
        ReadSettingsFromForm();
        await RunBusyAsync(async () =>
        {
            var removed = await _installationService.UninstallPackageAsync(_packages, packageId, _settings.GameFolder,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
            SetStatus($"Uninstalled {removed}.");
            RenderPackages();
        });
    }

    private async void UpdateBoxMateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        await RunBusyAsync(async () =>
        {
            await _selfUpdateService.StartUpdateAsync(_availableUpdate,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
            SetStatus("Update ready. Restarting BoxMate...");
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }

    private async Task RefreshManifestsAsync()
    {
        ReadSettingsFromForm();
        await RunBusyAsync(async () =>
        {
            var sources = new[] { OfficialCatalogue }.Concat(_settings.ManifestUrls);
            _packages = await _manifestService.ResolveAllAsync(sources,
                message => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(message)));
            await _settingsService.SaveAsync(_settings);
            RenderPackages();
            SetStatus("Manifests and GitHub releases refreshed.");
        });
    }

    private async Task CheckForBoxMateUpdateAsync()
    {
        try
        {
            _availableUpdate = await _selfUpdateService.CheckAsync();
            UpdateBoxMateButton.IsVisible = _availableUpdate is not null;
            if (_availableUpdate is not null)
            {
                UpdateBoxMateButton.Content = $"Update BoxMate · v{_availableUpdate.Version}";
                ToolTip.SetTip(UpdateBoxMateButton, "Download, verify, install, and restart BoxMate");
            }
        }
        catch (Exception ex)
        {
            UpdateBoxMateButton.IsVisible = false;
            ToolTip.SetTip(UpdateBoxMateButton, $"Update check failed: {ex.Message}");
        }
    }

    private void RenderPackages()
    {
        var validRoot = ValidateGameFolder(_settings.GameFolder);
        bool IsSubscribed(ResolvedPackage package) => _settings.ManifestUrls.Any(source =>
            source.Equals(package.ManifestUrl, StringComparison.OrdinalIgnoreCase) ||
            source.TrimEnd('/').Equals(package.Manifest.Repository.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var visiblePackages = _packages.Where(package => !package.IsDeprecated ||
            (validRoot && _installationService.IsRecordedInstalled(package.Manifest.Id, _settings.GameFolder)));
        var cards = visiblePackages.OrderBy(package => package.IsDeprecated ? 0 : package.IsCatalogueEntry || IsSubscribed(package) ? 1 : 2)
            .ThenBy(package => package.Manifest.Name)
            .Select(package => PackageCard.From(package, IsSubscribed(package), validRoot
                ? _installationService.GetPackageStatus(package, _settings.GameFolder)
                : PackageInstallStatus.NotConfigured,
                validRoot ? _installationService.GetRecordedVersion(package.Manifest.Id, _settings.GameFolder) : string.Empty,
                _packages)).ToList();
        PackageList.ItemsSource = cards;
        ManifestSummaryText.Text = $"{cards.Count} mod{(cards.Count == 1 ? string.Empty : "s")}, including required dependencies";
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        BusyProgress.IsVisible = true;
        RefreshButton.IsEnabled = false;
        try { await work(); }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
        finally { BusyProgress.IsVisible = false; RefreshButton.IsEnabled = true; }
    }

    private void ReadSettingsFromForm()
    {
        _settings.GameFolder = GameFolderBox.Text?.Trim() ?? string.Empty;
    }

    private void UpdateSourceSummary()
    {
        SourceSummaryText.Text = _settings.ManifestUrls.Count switch
        {
            0 => "Official catalogue",
            1 => "Official catalogue + 1 repository",
            _ => $"Official catalogue + {_settings.ManifestUrls.Count} repositories"
        };
    }

    private void UpdateMelonLoaderStatus()
    {
        var root = GameFolderBox.Text?.Trim() ?? string.Empty;
        MelonLoaderStatusText.Text = _melonLoaderService.IsInstalled(root) ? "MelonLoader is installed" : "MelonLoader is not installed";
    }

    private void ApplyGitHubAuthentication()
    {
        ManifestService.SetGitHubToken(_gitHubToken);
        MelonLoaderService.SetGitHubToken(_gitHubToken);
        SelfUpdateService.SetGitHubToken(_gitHubToken);
    }

    private void UpdateGitHubStatus()
    {
        var signedIn = !string.IsNullOrWhiteSpace(_gitHubToken);
        GitHubIconPath.Fill = new SolidColorBrush(Color.Parse(signedIn ? "#70D6B2" : "#687386"));
        ToolTip.SetTip(GitHubSignInButton, signedIn ? "GitHub signed in — click to sign out" : "Sign in with GitHub");
    }

    private static bool ValidateGameFolder(string path) => Directory.Exists(path) &&
        (File.Exists(Path.Combine(path, "BOXROOM.exe")) || Directory.Exists(Path.Combine(path, "MelonLoader")));
    private void SetStatus(string message) => StatusText.Text = message;

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title, Width = 440, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#191E29"))
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var confirm = new Button { Content = "Uninstall", MinWidth = 100 };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22), Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10, Children = { cancel, confirm }
                }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }
}

public sealed class PackageCard
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string VersionLabel { get; init; }
    public required string RequirementsLabel { get; init; }
    public required string DependencyLabel { get; init; }
    public required string SourceLabel { get; init; }
    public required string Status { get; init; }
    public required string ActionLabel { get; init; }
    public required IBrush CardBrush { get; init; }
    public required IBrush NameBrush { get; init; }
    public required IBrush BadgeBrush { get; init; }
    public required IBrush StatusBrush { get; init; }
    public bool ShowInstallAction { get; init; }
    public bool CanInstall { get; init; }
    public bool CanUninstall { get; init; }

    public static PackageCard From(ResolvedPackage package, bool subscribed, PackageInstallStatus status, string recordedVersion,
        IReadOnlyCollection<ResolvedPackage> allPackages)
    {
        var manifest = package.Manifest;
        var requirements = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.Requirements.MelonLoader)) requirements.Add($"MelonLoader {manifest.Requirements.MelonLoader}+");
        if (!string.IsNullOrWhiteSpace(manifest.Requirements.GameVersion)) requirements.Add($"BOXROOM {manifest.Requirements.GameVersion}");
        var requiredMods = manifest.Dependencies
            .Where(dependency => dependency.Required)
            .Select(dependency => ResolveDependencyName(dependency.Manifest, allPackages))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PackageCard
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = package.IsDeprecated && !string.IsNullOrWhiteSpace(package.Replacement)
                ? $"{manifest.Description} Replacement: {package.Replacement}."
                : manifest.Description,
            VersionLabel = package.IsDeprecated ? "DEPRECATED" : $"v{package.Version}",
            RequirementsLabel = requirements.Count == 0 ? "No declared runtime requirements" : "Runtime: " + string.Join(" · ", requirements),
            DependencyLabel = requiredMods.Count == 0 ? "No required mods" : "Requires " + string.Join(" · ", requiredMods),
            SourceLabel = subscribed ? $"Added repository · {manifest.Author}" :
                package.IsCatalogueEntry ? $"Official catalogue · {manifest.Author}" : $"Required dependency · {manifest.Author}",
            Status = package.IsDeprecated ? $"Installed v{recordedVersion} · no longer supported" : status switch
            {
                PackageInstallStatus.Current => "Installed and current",
                PackageInstallStatus.Outdated => "Update available",
                PackageInstallStatus.Modified => "Installed files are missing",
                PackageInstallStatus.NotConfigured => "Choose your BOXROOM folder",
                _ => "Not installed"
            },
            ActionLabel = status == PackageInstallStatus.Current ? "Installed" : status == PackageInstallStatus.Outdated ? "Update" : status == PackageInstallStatus.Modified ? "Repair" : "Install",
            CardBrush = new SolidColorBrush(Color.Parse(package.IsDeprecated ? "#3A2026" : "#242B39")),
            NameBrush = new SolidColorBrush(Color.Parse(package.IsDeprecated ? "#FF7B86" : "#F3F5F8")),
            BadgeBrush = new SolidColorBrush(Color.Parse(package.IsDeprecated ? "#7A2933" : "#314238")),
            StatusBrush = new SolidColorBrush(Color.Parse(package.IsDeprecated ? "#FF7B86" : "#70D6B2")),
            ShowInstallAction = !package.IsDeprecated,
            CanInstall = !package.IsDeprecated && status is not (PackageInstallStatus.Current or PackageInstallStatus.NotConfigured),
            CanUninstall = package.IsDeprecated || status is PackageInstallStatus.Current or PackageInstallStatus.Outdated or PackageInstallStatus.Modified
        };
    }

    private static string ResolveDependencyName(string manifestUrl, IReadOnlyCollection<ResolvedPackage> allPackages)
    {
        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var dependencyUri))
        {
            var resolved = allPackages.FirstOrDefault(candidate =>
                Uri.TryCreate(candidate.ManifestUrl, UriKind.Absolute, out var candidateUri) &&
                Uri.Compare(dependencyUri, candidateUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0);
            if (resolved is not null)
                return resolved.Manifest.Name;

            var segments = dependencyUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var repositoryName = dependencyUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                    ? segments[1]
                    : segments[^1];
                if (!repositoryName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(repositoryName).Replace('-', ' ');
            }
        }

        return "Unknown required mod";
    }
}
