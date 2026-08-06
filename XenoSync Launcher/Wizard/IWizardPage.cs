using System;
using System.Windows.Controls;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard;

public interface IWizardPage
{
    void Initialize(WizardContext context);

    bool CanGoNext { get; }

    event EventHandler? CanGoNextChanged;

    Page? GetNextPage();

    string NextButtonLabel => "Next";

    bool ShowBackButton => true;
}
