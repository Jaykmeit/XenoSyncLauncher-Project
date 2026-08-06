using System;
using System.Windows;
using System.Windows.Controls;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class SummaryPage : Page, IWizardPage
{
    private WizardContext? _context;

    public SummaryPage()
    {
        InitializeComponent();
    }

    public bool CanGoNext => true;

    public event EventHandler? CanGoNextChanged;

    // "Next" becomes "Finish" on this last page.
    public string NextButtonLabel => "Finish";

    public void Initialize(WizardContext context)
    {
        _context = context;

        InstallTypeText.Text = context.InstallType == InstallationType.OverVanilla
            ? "Installation type: Over Vanilla (same folder converted to Modded)"
            : "Installation type: Separate directory";

        ModdedPathText.Text = $"Modded path: {context.ModdedPath}";
        VanillaPathText.Text = string.IsNullOrWhiteSpace(context.VanillaPath)
            ? "Vanilla path: (none provided)"
            : $"Vanilla path: {context.VanillaPath}";

        ActionText.Text = context.EvaluationResult switch
        {
            EvaluationAction.NoActionRequired => "Action: install XV2Patcher + Revamp directly (no version change needed).",
            EvaluationAction.DowngradeRequired => "Action: downgrade via DepotDownloader, then install XV2Patcher + Revamp.",
            EvaluationAction.FreshDownloadRequired => "Action: fresh download via DepotDownloader, then install XV2Patcher + Revamp.",
            _ => "Action: unknown (evaluation was not completed)."
        };

        // Only worth offering when the versions already match AND Vanilla/Modded
        // are genuinely different folders (Type 1 makes them the same folder,
        // so there's nothing to copy).
        bool offerCopy = context.EvaluationResult == EvaluationAction.NoActionRequired
            && context.InstallType == InstallationType.SeparateDirectory
            && !string.IsNullOrWhiteSpace(context.VanillaPath)
            && !string.Equals(context.VanillaPath, context.ModdedPath, StringComparison.OrdinalIgnoreCase);

        CopyVanillaCheckBox.Visibility = offerCopy ? Visibility.Visible : Visibility.Collapsed;
        CopyVanillaCheckBox.IsChecked = offerCopy; // default on when offered
        context.ShouldCopyVanillaToModded = offerCopy;
    }

    private void CopyVanillaCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_context is not null)
            _context.ShouldCopyVanillaToModded = CopyVanillaCheckBox.IsChecked == true;
    }

    // This is the last page: returning null tells WizardWindow to finish the wizard.
    public Page? GetNextPage() => null;
}