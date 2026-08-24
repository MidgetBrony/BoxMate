using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BoxMate.Services;

namespace BoxMate;

public partial class ManifestSourcesWindow : Window
{
    private readonly ObservableCollection<string> _sources;

    public ManifestSourcesWindow() : this([]) { }

    public ManifestSourcesWindow(IEnumerable<string> sources)
    {
        InitializeComponent();
        _sources = new ObservableCollection<string>(sources.Select(ManifestSourceHelper.ToDisplayName));
        SourcesList.ItemsSource = _sources;
    }

    private void AddButton_OnClick(object? sender, RoutedEventArgs e) => AddSource();
    private void SourceInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddSource(); e.Handled = true; }
    }

    private void AddSource()
    {
        try
        {
            var normalized = ManifestSourceHelper.NormalizeForStorage(SourceInput.Text ?? string.Empty);
            var display = ManifestSourceHelper.ToDisplayName(normalized);
            if (!_sources.Contains(display, StringComparer.OrdinalIgnoreCase)) _sources.Add(display);
            SourceInput.Clear();
            DialogStatus.Text = string.Empty;
        }
        catch (Exception exception) { DialogStatus.Text = exception.Message; }
    }

    private void RemoveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is string selected) _sources.Remove(selected);
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => Close(null);
    private void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Close(_sources.Select(ManifestSourceHelper.NormalizeForStorage).ToList());
        }
        catch (Exception exception) { DialogStatus.Text = exception.Message; }
    }
}
