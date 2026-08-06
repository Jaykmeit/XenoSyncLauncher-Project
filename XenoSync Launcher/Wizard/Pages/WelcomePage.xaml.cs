using System;
using System.Windows.Controls;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class WelcomePage : Page, IWizardPage
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    public bool CanGoNext => true;

    public event EventHandler? CanGoNextChanged;

    public bool ShowBackButton => false;

    public void Initialize(WizardContext context)
    {
        // Doesn't need to save anything in context yet.
    }

    public Page? GetNextPage() => new InstallTypeSelectionPage();
}
