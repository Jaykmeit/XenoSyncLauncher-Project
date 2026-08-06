using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class Type1SetupPage : Page, IWizardPage
{
    private WizardContext? _context;

    public Type1SetupPage()
    {
        InitializeComponent();
    }

    public bool CanGoNext =>
        AcknowledgeCheckBox.IsChecked == true &&
        !string.IsNullOrWhiteSpace(VanillaPathTextBox.Text);

    public event EventHandler? CanGoNextChanged;

    public void Initialize(WizardContext context)
    {
        _context = context;

        AcknowledgeCheckBox.IsChecked = context.ConflictWarningAcknowledged;
        VanillaPathTextBox.Text = context.VanillaPath ?? string.Empty;
    }

    private void AcknowledgeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_context != null)
            _context.ConflictWarningAcknowledged = AcknowledgeCheckBox.IsChecked == true;

        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog available in Microsoft.Win32 from .NET 8 (WPF).
        var dialog = new OpenFolderDialog
        {
            Title = "Select Xenoverse 2 Vanilla path"
        };

        if (dialog.ShowDialog() == true)
        {
            VanillaPathTextBox.Text = dialog.FolderName;

            if (_context != null)
            {
                _context.VanillaPath = dialog.FolderName;
                // Modded == Vanilla (in-place conversion).
                _context.ModdedPath = dialog.FolderName;
            }

            CanGoNextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
        e.Handled = true;
    }

    public Page? GetNextPage() => new EvaluationPage();
}
