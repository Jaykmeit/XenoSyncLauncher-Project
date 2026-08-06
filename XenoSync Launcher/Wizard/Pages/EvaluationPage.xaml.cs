using System;
using System.Windows;
using System.Windows.Controls;
using XenoSyncLauncher.Models;
using XenoSyncLauncher.Services;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class EvaluationPage : Page, IWizardPage
{
    private readonly VersionCheckService _versionCheckService = new();
    private WizardContext? _context;
    private bool _evaluationCompleted;

    public EvaluationPage()
    {
        InitializeComponent();
        Loaded += EvaluationPage_Loaded;
    }

    public bool CanGoNext => _evaluationCompleted;

    public event EventHandler? CanGoNextChanged;

    public void Initialize(WizardContext context)
    {
        _context = context;
    }

    private async void EvaluationPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Avoid re-running the evaluation if the user navigates Back and Forward again.
        if (_evaluationCompleted) return;
        if (_context is null) return;

        try
        {
            StatusText.Text = "Fetching Revamp-supported version...";

            var revampVersion = await _versionCheckService.GetRevampSupportedVersionAsync();
            var vanillaVersion = await _versionCheckService.DetectInstalledVersionAsync(_context.VanillaPath ?? string.Empty);

            _context.RevampSupportedVersion = revampVersion;
            _context.DetectedVanillaVersion = vanillaVersion;

            VanillaVersionText.Text = vanillaVersion?.Label ?? "Not detected / not installed";
            RevampVersionText.Text = revampVersion.Label;
            VersionDetailsGrid.Visibility = Visibility.Visible;

            var action = _versionCheckService.Evaluate(vanillaVersion, revampVersion);
            _context.EvaluationResult = action;
            _context.EvaluationCompleted = true;

            ResultText.Text = DescribeAction(action);
            ResultBanner.Visibility = Visibility.Visible;

            ProgressIndicator.IsIndeterminate = false;
            ProgressIndicator.Value = 100;
            StatusText.Text = "Evaluation complete.";

            _evaluationCompleted = true;
            CanGoNextChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Evaluation failed: {ex.Message}";
            ProgressIndicator.IsIndeterminate = false;

            // TODO: add a "Retry" button here instead of leaving the user stuck
            // if the network check to Revamp's hosted version map fails.
        }
    }

    private static string DescribeAction(EvaluationAction action) => action switch
    {
        EvaluationAction.NoActionRequired =>
            "Your installed version already matches the one supported by Revamp. Only XV2Patcher and Revamp will be installed.",
        EvaluationAction.DowngradeRequired =>
            "A downgrade is required. XenoSync will use DepotDownloader to fetch the Revamp-supported build.",
        EvaluationAction.FreshDownloadRequired =>
            "No existing Vanilla installation was found. The Revamp-supported build will be downloaded from scratch via DepotDownloader.",
        _ => "Unknown evaluation result."
    };

    public Page? GetNextPage()
    {
        bool needsDepotDownloader = _context?.EvaluationResult is EvaluationAction.DowngradeRequired or EvaluationAction.FreshDownloadRequired;
        return needsDepotDownloader ? new DepotDownloaderSetupPage() : new SummaryPage();
    }
}