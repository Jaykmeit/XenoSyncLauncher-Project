using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Wizard.Pages;

public partial class Type2SetupPage : Page, IWizardPage
{
    private const string DefaultModdedFolderName = "DB Xenoverse 2 REVAMP";

    private WizardContext? _context;

    public Type2SetupPage()
    {
        InitializeComponent();
    }

    // Only the Modded path is mandatory here; Vanilla is optional.
    public bool CanGoNext => !string.IsNullOrWhiteSpace(ModdedPathTextBox.Text);

    public event EventHandler? CanGoNextChanged;

    public void Initialize(WizardContext context)
    {
        _context = context;

        ModdedPathTextBox.Text = context.ModdedPath ?? string.Empty;
        VanillaPathTextBox.Text = context.VanillaPath ?? string.Empty;
    }

    private void BrowseModdedButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the destination folder for Xenoverse 2 Modded"
        };

        if (dialog.ShowDialog() == true)
        {
            ModdedPathTextBox.Text = dialog.FolderName;
            if (_context != null) _context.ModdedPath = dialog.FolderName;
            CanGoNextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BrowseVanillaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your existing Xenoverse 2 Vanilla folder (optional)"
        };

        if (dialog.ShowDialog() == true)
        {
            VanillaPathTextBox.Text = dialog.FolderName;
            if (_context != null) _context.VanillaPath = dialog.FolderName;

            SuggestDefaultModdedPath(dialog.FolderName);
            CanGoNextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// If the user hasn't already picked a Modded folder, default it to a
    /// sibling of the Vanilla folder named "DB Xenoverse 2 REVAMP" (e.g.
    /// ".../steamapps/common/DB Xenoverse 2 REVAMP" next to
    /// ".../steamapps/common/DRAGON BALL XENOVERSE2"), and create it right
    /// away so it's visible in Explorer immediately. The user can still
    /// Browse to override this.
    /// </summary>
    private void SuggestDefaultModdedPath(string vanillaPath)
    {
        if (!string.IsNullOrWhiteSpace(ModdedPathTextBox.Text)) return;

        var parentDir = Path.GetDirectoryName(vanillaPath);
        if (string.IsNullOrWhiteSpace(parentDir)) return;

        var suggestedPath = Path.Combine(parentDir, DefaultModdedFolderName);

        try
        {
            Directory.CreateDirectory(suggestedPath);
        }
        catch
        {
            // If we can't create it (permissions, invalid path, etc.), still
            // suggest the path - it'll be created later when something is
            // actually installed into it.
        }

        ModdedPathTextBox.Text = suggestedPath;
        if (_context != null) _context.ModdedPath = suggestedPath;
    }

    private void ClearVanillaButton_Click(object sender, RoutedEventArgs e)
    {
        VanillaPathTextBox.Text = string.Empty;
        if (_context != null) _context.VanillaPath = null;
        CanGoNextChanged?.Invoke(this, EventArgs.Empty);
    }

    public Page? GetNextPage() => new EvaluationPage();
}