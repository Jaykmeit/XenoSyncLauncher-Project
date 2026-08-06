using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using XenoSyncLauncher.Models;
using XenoSyncLauncher.Services;
using XenoSyncLauncher.Wizard.Pages;

namespace XenoSyncLauncher.Wizard;

public partial class WizardWindow : Window
{
    private readonly WizardContext _context = new();
    private readonly Stack<Page> _history = new();
    private IWizardPage? CurrentWizardPage => WizardFrame.Content as IWizardPage;

    public WizardWindow()
    {
        InitializeComponent();

        // Frame.Navigate() doesn't always update Frame.Content synchronously;
        // this ensures the buttons re-sync once the page has actually loaded,
        // instead of only right after Navigate() is called (which could
        // briefly see CurrentWizardPage as null and leave Back's IsEnabled at
        // its default WPF value instead of the correct one for that page).
        WizardFrame.Navigated += (_, _) => RefreshButtons();

        NavigateTo(new WelcomePage(), pushCurrentToHistory: false);
    }

    private void NavigateTo(Page page, bool pushCurrentToHistory)
    {
        if (pushCurrentToHistory && WizardFrame.Content is Page currentPage)
            _history.Push(currentPage);

        if (page is IWizardPage wizardPage)
        {
            wizardPage.Initialize(_context);
            wizardPage.CanGoNextChanged += (_, _) => RefreshButtons();
        }

        WizardFrame.Navigate(page);
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (CurrentWizardPage is null)
        {
            NextButton.IsEnabled = false;
            return;
        }

        NextButton.IsEnabled = CurrentWizardPage.CanGoNext;
        NextButton.Content = CurrentWizardPage.NextButtonLabel;
        BackButton.IsEnabled = CurrentWizardPage.ShowBackButton && _history.Count > 0;

        int stepNumber = _history.Count + 1;
        StepIndicatorText.Text = $"Paso {stepNumber}";
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentWizardPage is null) return;

        var next = CurrentWizardPage.GetNextPage();

        if (next is null)
        {
            await FinishWizardAsync();
            return;
        }

        NavigateTo(next, pushCurrentToHistory: true);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;

        var previous = _history.Pop();
        WizardFrame.Navigate(previous);
        RefreshButtons();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "¿Seguro que quieres cancelar la configuración inicial? XenoSync Launcher se cerrará.",
            "Cancelar configuración",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            System.Windows.Application.Current.Shutdown();
    }

    private async System.Threading.Tasks.Task FinishWizardAsync()
    {
        if (_context.ShouldCopyVanillaToModded && !string.IsNullOrWhiteSpace(_context.VanillaPath) && !string.IsNullOrWhiteSpace(_context.ModdedPath))
        {
            NextButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StepIndicatorText.Text = "Copying files...";

            var copyService = new DirectoryCopyService();
            var vanillaPath = _context.VanillaPath;
            var moddedPath = _context.ModdedPath;

            await System.Threading.Tasks.Task.Run(() =>
                copyService.CopyAll(vanillaPath, moddedPath, (done, total) =>
                    Dispatcher.Invoke(() => StepIndicatorText.Text = $"Copying files... ({done}/{total})")));
        }

        // Persistimos lo decidido durante el Wizard para que la ventana
        // principal del launcher (MainWindow, fase siguiente del proyecto)
        // sepa dónde está el directorio Modded, el Vanilla, etc.
        var settingsService = new SettingsService();
        settingsService.Save(settingsService.FromWizardContext(_context));

        var mainWindow = new XenoSyncLauncher.MainApp.MainWindow();
        System.Windows.Application.Current.MainWindow = mainWindow;
        mainWindow.Show();

        Close();
    }
}