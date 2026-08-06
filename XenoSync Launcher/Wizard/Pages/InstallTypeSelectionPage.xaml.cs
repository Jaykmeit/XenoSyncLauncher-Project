using System;
using System.Windows.Controls;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class InstallTypeSelectionPage : Page, IWizardPage
{
    private WizardContext? _context;

    public InstallTypeSelectionPage()
    {
        InitializeComponent();
    }

    public bool CanGoNext => OverVanillaRadio.IsChecked == true || SeparateDirectoryRadio.IsChecked == true;

    public event EventHandler? CanGoNextChanged;

    public void Initialize(WizardContext context)
    {
        _context = context;

        OverVanillaRadio.IsChecked = context.InstallType == InstallationType.OverVanilla;
        SeparateDirectoryRadio.IsChecked = context.InstallType == InstallationType.SeparateDirectory;
    }

    private void OptionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_context is null) return;

        _context.InstallType = OverVanillaRadio.IsChecked == true
            ? InstallationType.OverVanilla
            : InstallationType.SeparateDirectory;

        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    public Page? GetNextPage()
    {
        return _context?.InstallType switch
        {
            InstallationType.OverVanilla => new Type1SetupPage(),
            InstallationType.SeparateDirectory => new Type2SetupPage(),
            _ => null
        };
    }
}
