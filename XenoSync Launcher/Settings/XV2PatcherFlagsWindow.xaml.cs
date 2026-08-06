using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using XenoSyncLauncher.Models;
using XenoSyncLauncher.Services;

namespace XenoSyncLauncher.Settings;

public partial class XV2PatcherFlagsWindow : Window
{
    private readonly IniFlagService _iniFlagService = new();
    private readonly string _iniPath;
    private readonly ObservableCollection<PatcherFlagViewModel> _flags = new();

    public XV2PatcherFlagsWindow(string? moddedPath)
    {
        InitializeComponent();

        _iniPath = Path.Combine(moddedPath ?? string.Empty, "XV2PATCHER", "xv2patcher.ini");
        FlagsItemsControl.ItemsSource = _flags;

        LoadFlags();
    }

    private void LoadFlags()
    {
        if (!File.Exists(_iniPath))
        {
            PathStatusText.Text = $"xv2patcher.ini not found at '{_iniPath}'. Install XV2Patcher first.";
            SaveButton.IsEnabled = false;
            return;
        }

        PathStatusText.Text = _iniPath;

        var lines = _iniFlagService.ReadLines(_iniPath);

        foreach (var definition in PatcherFlagCatalog.All)
        {
            var currentValue = _iniFlagService.GetBoolValue(lines, definition.Key) ?? false;

            _flags.Add(new PatcherFlagViewModel
            {
                Key = definition.Key,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                IsChecked = currentValue
            });
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var lines = _iniFlagService.ReadLines(_iniPath);

        foreach (var flag in _flags)
            _iniFlagService.SetBoolValue(lines, flag.Key, flag.IsChecked);

        _iniFlagService.SaveLines(_iniPath, lines);

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
