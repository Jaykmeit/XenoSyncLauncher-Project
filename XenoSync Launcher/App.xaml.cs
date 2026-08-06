using System.Windows;
using XenoSyncLauncher.Services;

namespace XenoSyncLauncher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        // A valid, already-completed installation has a Modded path set.
        // Anything else (no config.json yet, or one missing that field) means
        // first-run setup hasn't actually finished, so the Wizard runs again.
        Window startupWindow = !string.IsNullOrWhiteSpace(settings?.ModdedPath)
            ? new XenoSyncLauncher.MainApp.MainWindow()
            : new XenoSyncLauncher.Wizard.WizardWindow();

        MainWindow = startupWindow;
        startupWindow.Show();
    }
}
