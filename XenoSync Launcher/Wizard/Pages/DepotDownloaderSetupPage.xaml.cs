using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XenoSyncLauncher.Models;
using XenoSyncLauncher.Services;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class DepotDownloaderSetupPage : Page, IWizardPage
{
    private readonly DepotDownloaderInstaller _installer = new();
    private WizardContext? _context;
    private bool _autoInstallCompleted;

    public DepotDownloaderSetupPage()
    {
        InitializeComponent();
        AutoInstallRadio.IsChecked = true;
        QrLoginRadio.IsChecked = true;
    }

    public bool CanGoNext =>
        ((AutoInstallRadio.IsChecked == true && _autoInstallCompleted) ||
         (ExistingPathRadio.IsChecked == true && !string.IsNullOrWhiteSpace(ExistingPathTextBox.Text))) &&
        (QrLoginRadio.IsChecked == true ||
         (CredentialsLoginRadio.IsChecked == true && !string.IsNullOrWhiteSpace(SteamUsernameTextBox.Text)));

    public event EventHandler? CanGoNextChanged;

    public void Initialize(WizardContext context)
    {
        _context = context;

        // Restore previous choice if the user navigated Back and returned here.
        if (_installer.IsInstalledAt(context.DepotDownloaderPath) && context.DepotDownloaderPath == DepotDownloaderInstaller.DefaultExecutablePath)
        {
            _autoInstallCompleted = true;
            InstallStatusText.Text = "Already installed.";
        }
        else if (_installer.IsInstalledAt(context.DepotDownloaderPath))
        {
            ExistingPathRadio.IsChecked = true;
            ExistingPathTextBox.Text = context.DepotDownloaderPath!;
        }

        if (context.SteamLoginMethod == "Credentials")
        {
            CredentialsLoginRadio.IsChecked = true;
        }
        else
        {
            QrLoginRadio.IsChecked = true;
        }

        SteamUsernameTextBox.Text = context.SteamUsername ?? string.Empty;
        UpdateSteamUsernameFieldVisibility();
    }

    private void SourceOptionChanged(object sender, RoutedEventArgs e)
    {
        bool useExisting = ExistingPathRadio.IsChecked == true;
        ExistingPathTextBox.IsEnabled = useExisting;
        BrowseButton.IsEnabled = useExisting;
        InstallButton.IsEnabled = !useExisting;

        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoginMethodOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_context is not null)
            _context.SteamLoginMethod = CredentialsLoginRadio.IsChecked == true ? "Credentials" : "QrCode";

        UpdateSteamUsernameFieldVisibility();
        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the username field (and its "editable later in Settings" hint) only when Credentials is the chosen login method - it's meaningless (and left null) for QrCode.</summary>
    private void UpdateSteamUsernameFieldVisibility()
    {
        var visibility = CredentialsLoginRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SteamUsernameLabel.Visibility = visibility;
        SteamUsernameTextBox.Visibility = visibility;
        SteamUsernameHintText.Visibility = visibility;
    }

    private void SteamUsernameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_context is not null)
            _context.SteamUsername = string.IsNullOrWhiteSpace(SteamUsernameTextBox.Text) ? null : SteamUsernameTextBox.Text;

        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallProgressBar.Visibility = Visibility.Visible;

        var progress = new Progress<string>(status => InstallStatusText.Text = status);
        var (success, error) = await _installer.InstallDefaultAsync(progress, CancellationToken.None);

        InstallProgressBar.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = true;

        if (success)
        {
            _autoInstallCompleted = true;
            if (_context is not null)
                _context.DepotDownloaderPath = DepotDownloaderInstaller.DefaultExecutablePath;
        }
        else
        {
            InstallStatusText.Text = $"Install failed: {error}";
        }

        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DepotDownloader.exe",
            Filter = "DepotDownloader executable (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            ExistingPathTextBox.Text = dialog.FileName;
            if (_context is not null)
                _context.DepotDownloaderPath = dialog.FileName;

            CanGoNextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Page? GetNextPage() => new SummaryPage();
}