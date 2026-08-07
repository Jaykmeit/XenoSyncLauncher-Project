using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using XenoSyncLauncher.Models;
using XenoSyncLauncher.Services;
using XenoSyncLauncher.Settings;

namespace XenoSyncLauncher.MainApp;

public partial class MainWindow : Window
{
    private readonly ModCatalogService _modCatalogService = new();
    private readonly ModInstallService _modInstallService = new();
    private readonly LaunchInspectService _launchInspectService = new();
    private readonly VersionCheckService _versionCheckService = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateTaskPlanner _updateTaskPlanner = new();
    private readonly DownloadResumeService _downloadResumeService = new();
    private readonly DepotDownloaderService _depotDownloaderService = new();
    private readonly DllSwapService _dllSwapService = new();
    private readonly IniFlagService _iniFlagService = new();
    private readonly HttpDownloadService _httpDownloadService = new();
    private readonly GoogleDriveDownloadService _googleDriveDownloadService = new();
    private readonly ArchiveExtractionService _archiveExtractionService = new();
    private readonly InstalledComponentVersionService _installedVersionService = new();
    private readonly ComponentDownloadService _componentDownloadService = new();

    /// <summary>Where each component's downloaded file ended up, keyed by "xv2patcher"/"revamp". Reset each time Update starts.</summary>
    private readonly Dictionary<string, string> _componentDownloadedFiles = new();

    /// <summary>Where each component's archive was extracted to, keyed by "xv2patcher"/"revamp". Empty string means an installer .exe placed its own files. Reset each time Update starts.</summary>
    private readonly Dictionary<string, string> _componentStagingDirs = new();

    private readonly ObservableCollection<ModEntry> _mods;
    private readonly System.Windows.Data.CollectionViewSource _modsView = new();
    private readonly ObservableCollection<LogEntry> _logLines = new();

    private bool _webViewInitAttempted;
    private bool _webViewReady;
    private string? _pagePreviewLoadedUrl;
    private CancellationTokenSource? _modPreviewSlideshowCts;
    private bool _slideshowFrontIsA = true;
    private readonly DispatcherTimer _modPreviewCloseTimer;
    private bool _cursorOverPreviewTrigger;

    /// <summary>Backing ModRecord for each ModEntry shown in the UI, keyed by Id.</summary>
    private Dictionary<string, ModRecord> _modRecordsById = new();

    /// <summary>Guards against ModEntry_PropertyChanged reacting to changes we make ourselves (e.g. syncing IsChecked from the underlying ModRecord).</summary>
    private bool _isApplyingModChanges;

    private readonly InstallationStatus _status = new();
    private LauncherActivityState _activityState = LauncherActivityState.Idle;
    private LauncherSettings? _settings;
    private VersionComparison? _lastComparison;

    // --- Update pipeline state ---
    private List<UpdateTaskItem> _updateTasks = new();
    private int _currentTaskIndex;

    /// <summary>
    /// Cancelling this is how "Pause" (and closing the app mid-update) works:
    /// for the real DepotDownloader task it kills the process (which has its
    /// own resume mechanism), for simulated tasks it just breaks the delay loop.
    /// </summary>
    private CancellationTokenSource? _updateCts;

    /// <summary>
    /// Periodically re-checks for updates when Settings > Auto-Update is
    /// enabled, and starts an update automatically if one is available and
    /// nothing else is already running. The interval is arbitrary; adjust
    /// AutoUpdateCheckInterval if 30 minutes is too eager or too lax.
    /// </summary>
    private readonly DispatcherTimer _autoUpdateTimer;
    private static readonly TimeSpan AutoUpdateCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>Kept open across stdout lines while a DepotDownloader QR login is in progress, so a refreshed challenge URL updates the same window.</summary>
    private QrLoginWindow? _qrLoginWindow;

    /// <summary>True once the user explicitly cancels the QR/password sign-in prompt, so we stop the whole Update instead of pausing or re-prompting.</summary>
    private bool _loginCancelledByUser;

    private CredentialPromptWindow? _credentialPromptWindow;
    private bool _credentialPromptIsPassword;

    // Placeholder values for the mock simulation of XV2Patcher/Revamp downloads,
    // until those are wired up to real HTTP downloads. The one real task
    // (game version via DepotDownloader) does not use these.
    private const long SimulatedBytesPerTick = 10_000_000; // 10 MB per tick
    private static readonly TimeSpan SimulatedTickInterval = TimeSpan.FromMilliseconds(200);

    public MainWindow()
    {
        InitializeComponent();

        _mods = new ObservableCollection<ModEntry>();
        _modsView.Source = _mods;
        _modsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ModEntry.CategoryGroupName)));
        ModListBox.ItemsSource = _modsView.View;
        LogListBox.ItemsSource = _logLines;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _autoUpdateTimer = new DispatcherTimer { Interval = AutoUpdateCheckInterval };
        _autoUpdateTimer.Tick += async (_, _) => await AutoUpdateTimer_TickAsync();

        // Closes the preview flyout shortly after the cursor leaves both the mod
        // title and the flyout itself - long enough that moving from one to the
        // other (to actually interact with the embedded page) doesn't close it.
        _modPreviewCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _modPreviewCloseTimer.Tick += (_, _) => CloseModPreviewIfCursorAway();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();

        if (_settings is null)
        {
            AppendLog("No configuration found. Please complete the first-run wizard.");
            return;
        }

        AppendLog("Configuration loaded.");
        RefreshAutoUpdateTimerState();
        await LoadModsAsync();
        await RunLaunchInspectAsync();
    }

    private void RefreshAutoUpdateTimerState()
    {
        if (_settings?.AutoUpdateEnabled == true)
            _autoUpdateTimer.Start();
        else
            _autoUpdateTimer.Stop();
    }

    private async Task AutoUpdateTimer_TickAsync()
    {
        if (_settings?.AutoUpdateEnabled != true) return;
        if (_activityState != LauncherActivityState.Idle) return; // don't interrupt a manual update/pause

        await RunLaunchInspectAsync();

        if (!_status.CanRun)
        {
            AppendLog("Auto-Update: a newer version was found, starting Update automatically.");
            StartUpdate();
        }
    }

    /// <summary>
    /// If the update is stopped mid-task (Pause, or the window closing),
    /// persist progress and stop any running process before the app exits.
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _autoUpdateTimer.Stop();
        PersistActiveDownloadProgress();
        _updateCts?.Cancel();
        _qrLoginWindow?.Close();
        _credentialPromptWindow?.Close();
    }

    /// <summary>
    /// Queries local + remote versions and refreshes the Live indicator,
    /// the Launch Inspect panel, and the Run button's enabled state. Also
    /// checks whether any previously interrupted downloads are still valid
    /// for the versions currently required, discarding the ones that aren't.
    /// </summary>
    private async Task RunLaunchInspectAsync()
    {
        if (_settings?.ModdedPath is null) return;

        AppendLog("Running Launch Inspect...");

        var comparison = new VersionComparison
        {
            LatestRevampVersion = await _launchInspectService.GetLatestRevampVersionAsync(),
            LatestXv2PatcherVersion = await _launchInspectService.GetLatestXv2PatcherVersionAsync(),
            InstalledRevampVersion = await _launchInspectService.GetInstalledRevampVersionAsync(_settings.ModdedPath),
            InstalledXv2PatcherVersion = await _launchInspectService.GetInstalledXv2PatcherVersionAsync(_settings.ModdedPath)
        };

        _lastComparison = comparison;

        RevampInstalledText.Text = comparison.InstalledRevampVersion ?? "Not installed";
        RevampLatestText.Text = comparison.LatestRevampVersion ?? "-";
        PatcherInstalledText.Text = comparison.InstalledXv2PatcherVersion ?? "Not installed";
        PatcherLatestText.Text = comparison.LatestXv2PatcherVersion ?? "-";

        _status.IsXenoverse2Installed = _launchInspectService.IsGameLive(_settings.ModdedPath);
        _status.IsRevampUpToDate = comparison.RevampUpToDate;
        _status.IsXv2PatcherUpToDate = comparison.Xv2PatcherUpToDate;

        SetLiveIndicator(_status.IsXenoverse2Installed);
        RunButton.IsEnabled = _status.CanRun;

        AppendLog(_status.CanRun
            ? "Everything is up to date. Run is enabled."
            : "Some components are missing or outdated. Run is disabled until Update completes.");

        await ReevaluateGameVersionAsync();
        ReconcileResumableDownloads(comparison);
    }

    /// <summary>
    /// Re-checks whether the installed Xenoverse 2 build matches what Revamp
    /// requires, and updates _settings.NeedsGameDownload/RequiredManifestId
    /// accordingly - every time Launch Inspect runs, not just once during the
    /// Wizard. Without this, a stale value from an earlier Wizard run (or one
    /// computed before the manifest-based comparison was fixed) would
    /// silently persist forever, and UpdateTaskPlanner would never add the
    /// game-download task even when one is genuinely needed.
    ///
    /// Detection source depends on install type: for OverVanilla, Modded IS
    /// the Steam-tracked folder, so its appmanifest reflects reality directly.
    /// For SeparateDirectory, the Modded folder isn't a Steam library folder
    /// at all (no appmanifest lives there) - so instead we check XenoSync's
    /// own record of which manifest DepotDownloader last actually fetched
    /// into it (see InstalledComponentVersionService.GameManifestId).
    /// </summary>
    private async Task ReevaluateGameVersionAsync()
    {
        if (_settings?.ModdedPath is null) return;

        VersionInfo? installedVersion;

        if (_settings.InstallType == "OverVanilla")
        {
            installedVersion = await _versionCheckService.DetectInstalledVersionAsync(_settings.ModdedPath);
        }
        else
        {
            var trackedManifestId = _installedVersionService.GetInstalledGameManifestId(_settings.ModdedPath);
            installedVersion = trackedManifestId is not null ? new VersionInfo { ManifestId = trackedManifestId } : null;
        }

        var revampRequiredVersion = await _versionCheckService.GetRevampSupportedVersionAsync();
        var action = _versionCheckService.Evaluate(installedVersion, revampRequiredVersion);

        bool needsGameDownload = action is EvaluationAction.DowngradeRequired or EvaluationAction.FreshDownloadRequired;
        var requiredManifestId = needsGameDownload ? revampRequiredVersion.ManifestId : null;

        if (needsGameDownload && string.IsNullOrWhiteSpace(requiredManifestId))
        {
            AppendLog("A game version mismatch was detected, but no ManifestId is configured in the hosted version map - " +
                      "the game-download step can't run until revamp-version-map.json has a valid \"manifestId\".", LogLevel.Warning);
        }

        bool changed = _settings.NeedsGameDownload != needsGameDownload || _settings.RequiredManifestId != requiredManifestId;

        _settings.NeedsGameDownload = needsGameDownload;
        _settings.RequiredManifestId = requiredManifestId;

        if (changed)
        {
            _settingsService.Save(_settings);
            AppendLog(needsGameDownload
                ? $"Game version check: installed build doesn't match what Revamp requires (installed: {installedVersion?.Label ?? "not detected"}). A downgrade will run on the next Update."
                : "Game version check: installed build matches what Revamp requires.");
        }
    }

    /// <summary>
    /// Compares any saved DownloadResumeState files (for the simulated HTTP-style
    /// downloads only — the real DepotDownloader task manages its own resume
    /// folder and isn't tracked here) against what's currently required. A saved
    /// download is only kept if its TargetVersionLabel still matches the latest
    /// version for that component; otherwise it's stale and gets discarded.
    /// </summary>
    private void ReconcileResumableDownloads(VersionComparison comparison)
    {
        var freshPlan = _updateTaskPlanner.BuildPlan(comparison, _settings).Where(t => !t.IsRealDepotDownload).ToList();

        foreach (var savedState in _downloadResumeService.ListAll())
        {
            var matchingTask = freshPlan.FirstOrDefault(t => t.Id == savedState.TaskId);
            bool stillValid = matchingTask is not null && matchingTask.TargetVersionLabel == savedState.TargetVersionLabel;

            if (stillValid)
            {
                var percent = savedState.ExpectedTotalBytes > 0
                    ? (int)(100.0 * savedState.BytesDownloaded / savedState.ExpectedTotalBytes)
                    : 0;
                AppendLog($"Found an incomplete download for {savedState.TaskDisplayName} ({percent}% done). It will resume when you click Update.");
            }
            else
            {
                _downloadResumeService.Clear(savedState.TaskId);
                AppendLog($"A newer version of {savedState.TaskDisplayName} was detected. Discarding the incomplete previous download.", LogLevel.Warning);
            }
        }
    }

    private void SetLiveIndicator(bool isLive)
    {
        LiveIndicatorText.Text = isLive ? "Live" : "Not Found";
        LiveIndicatorDot.Fill = (System.Windows.Media.Brush)FindResource(isLive ? "BrushLive" : "BrushDanger");
    }

    /// <summary>
    /// Appends a line to the log console. Safe to call from any thread - the
    /// bound _logLines ObservableCollection can only be mutated from the UI
    /// thread; calling this from a background thread (e.g. the DepotDownloader
    /// stall-watchdog, which runs inside Task.Run) without this check crashes
    /// with "An ItemsControl is inconsistent with its items source" once the
    /// mismatch accumulates enough to trip WPF's internal generator check.
    /// </summary>
    private void AppendLog(string message, LogLevel level = LogLevel.Info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message, level));
            return;
        }

        var entry = new LogEntry { Text = $"[{DateTime.Now:HH:mm:ss}] {message}", Level = level };
        _logLines.Add(entry);
        LogListBox.ScrollIntoView(entry);
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _logLines.Select(l => l.Text)));
        AppendLog("Log copied to clipboard.");
    }

    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModEntry parent }) return;

        parent.IsExpanded = !parent.IsExpanded;

        foreach (var child in _mods.Where(m => m.ParentId == parent.Id))
            child.IsVisibleInTree = parent.IsExpanded;
    }

    private async void ModListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModListBox.SelectedItem is not ModEntry mod)
        {
            ModDetailTitle.Text = "Select a mod";
            ModDetailDescription.Text = string.Empty;
            ModDetailAuthorText.Visibility = Visibility.Collapsed;
            VisitModPageButton.IsEnabled = false;
            await ShowModPagePreviewAsync(null);
            return;
        }

        ModDetailTitle.Text = mod.Title;
        ModDetailDescription.Text = mod.Description;
        if (string.IsNullOrWhiteSpace(mod.Author))
        {
            ModDetailAuthorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModDetailAuthorText.Text = $"by {mod.Author}";
            ModDetailAuthorText.Visibility = Visibility.Visible;
        }
        VisitModPageButton.Tag = mod.PageUrl;
        VisitModPageButton.IsEnabled = !string.IsNullOrWhiteSpace(mod.PageUrl);

        await ShowModPagePreviewAsync(mod);
    }

    private async Task LoadModsAsync()
    {
        var records = await _modCatalogService.LoadAsync(_settings?.ModdedPath);
        _modRecordsById = records.ToDictionary(r => r.Id);

        _isApplyingModChanges = true;
        try
        {
            _mods.Clear();
            var parentIds = records.Where(r => r.ParentId is not null).Select(r => r.ParentId!).ToHashSet();

            foreach (var record in records)
            {
                var entry = ToModEntry(record, _modRecordsById, parentIds.Contains(record.Id));
                entry.PropertyChanged += ModEntry_PropertyChanged;
                _mods.Add(entry);
            }
        }
        finally
        {
            _isApplyingModChanges = false;
        }

        await EnsureMandatoryModsInstalledAsync();
    }

    private static ModEntry ToModEntry(ModRecord record, Dictionary<string, ModRecord> allRecords, bool hasChildren)
    {
        string? parentTitle = null;
        if (record.ParentId is not null && allRecords.TryGetValue(record.ParentId, out var parent))
            parentTitle = parent.Title;

        return new ModEntry
        {
            Id = record.Id,
            Title = record.Title,
            Description = record.Description,
            Author = record.Author,
            PageUrl = record.PageUrl,
            ScreenshotUrls = record.ScreenshotUrls,
            Category = record.Category,
            ParentId = record.ParentId,
            ParentTitle = parentTitle,
            HasChildren = hasChildren,
            IndentLevel = record.ParentId is null ? 0 : 1,
            IsChecked = record.IsEnabled
        };
    }

    /// <summary>
    /// XenoSyncCore mods are mandatory: download+install any that this device
    /// hasn't installed yet. Runs quietly in the background after mods load;
    /// each mod logs its own progress like the XV2Patcher/Revamp installs do.
    /// </summary>
    private async Task EnsureMandatoryModsInstalledAsync()
    {
        if (_settings?.ModdedPath is null) return;

        foreach (var record in _modRecordsById.Values.Where(m => m.Category == ModCategory.XenoSyncCore))
        {
            if (!string.IsNullOrWhiteSpace(record.RepositoryFolder) && record.InstalledRelativeFiles.Count > 0)
                continue; // already installed on this device

            AppendLog($"Installing mandatory mod: {record.Title}...");

            var (success, error) = await _modInstallService.EnableAsync(
                record, _settings.ModdedPath, downloadProgress: null, _settings.SpeedLimitMbps, msg => AppendLog(msg), CancellationToken.None);

            AppendLog(success
                ? $"Installed mandatory mod: {record.Title}."
                : $"Failed to install mandatory mod {record.Title}: {error}");
        }

        _modCatalogService.Save(_modRecordsById.Values.ToList());
    }

    private async void ModEntry_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModEntry.IsChecked)) return;
        if (_isApplyingModChanges) return;
        if (sender is not ModEntry entry) return;
        if (!_modRecordsById.TryGetValue(entry.Id, out var record)) return;

        if (_settings?.ModdedPath is null)
        {
            AppendLog("Set the Modded folder in Settings before enabling/disabling mods.", LogLevel.Warning);
            SyncEntryChecked(entry, record);
            return;
        }

        try
        {
            if (entry.IsChecked && !record.IsEnabled)
            {
                if (record.ParentId is not null &&
                    _modRecordsById.TryGetValue(record.ParentId, out var parentRecord) &&
                    !parentRecord.IsEnabled)
                {
                    AppendLog($"{record.Title} requires {parentRecord.Title} - enabling it first...");

                    var parentEntry = _mods.FirstOrDefault(m => m.Id == parentRecord.Id);
                    var (parentSuccess, parentError) = await _modInstallService.EnableAsync(
                        parentRecord, _settings.ModdedPath, MakeModDownloadProgress(parentEntry), _settings.SpeedLimitMbps, msg => AppendLog(msg), CancellationToken.None);
                    if (parentEntry is not null) parentEntry.IsDownloading = false;

                    if (!parentSuccess)
                    {
                        AppendLog($"Failed to enable required mod {parentRecord.Title}: {parentError}", LogLevel.Error);
                        SyncEntryChecked(entry, record);
                        return;
                    }

                    AppendLog($"Enabled mod: {parentRecord.Title}");
                    SyncModEntryCheckedById(parentRecord.Id, true);
                    if (parentEntry is not null)
                    {
                        parentEntry.IsExpanded = true;
                        foreach (var sibling in _mods.Where(m => m.ParentId == parentEntry.Id))
                            sibling.IsVisibleInTree = true;
                    }
                }

                AppendLog($"Downloading and enabling mod: {record.Title}...");
                var (success, error) = await _modInstallService.EnableAsync(
                    record, _settings.ModdedPath, MakeModDownloadProgress(entry), _settings.SpeedLimitMbps, msg => AppendLog(msg), CancellationToken.None);
                entry.IsDownloading = false;

                if (!success)
                {
                    AppendLog($"Failed to enable {record.Title}: {error}", LogLevel.Error);
                    SyncEntryChecked(entry, record);
                    return;
                }

                AppendLog($"Enabled mod: {record.Title}");
            }
            else if (!entry.IsChecked && record.IsEnabled)
            {
                _modInstallService.Disable(record, _settings.ModdedPath);
                AppendLog($"Disabled mod: {record.Title}");

                // Cascade: a mod that required this one can't keep working without it.
                foreach (var child in _modRecordsById.Values.Where(m => m.ParentId == record.Id && m.IsEnabled))
                {
                    _modInstallService.Disable(child, _settings.ModdedPath);
                    AppendLog($"Disabled mod: {child.Title} (required {record.Title}).");
                    SyncModEntryCheckedById(child.Id, false);
                }
            }

            _modCatalogService.Save(_modRecordsById.Values.ToList());
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to apply mod change for {record.Title}: {ex.Message}", LogLevel.Error);
            entry.IsDownloading = false;
            SyncEntryChecked(entry, record);
        }
    }

    /// <summary>
    /// Reports a mod's real download progress (bytes-based) onto its ModEntry's
    /// IsDownloading/DownloadPercent, which the tree's inline progress bar is
    /// bound to. Returns null if the entry isn't in the tree (shouldn't normally
    /// happen), in which case EnableAsync just runs without a progress sink.
    /// </summary>
    private IProgress<DownloadProgressInfo>? MakeModDownloadProgress(ModEntry? entry)
    {
        if (entry is null) return null;

        entry.IsDownloading = true;
        entry.DownloadPercent = 0;

        return new Progress<DownloadProgressInfo>(p =>
        {
            entry.DownloadPercent = p.TotalBytes is > 0
                ? Math.Clamp(p.BytesReceived * 100.0 / p.TotalBytes.Value, 0, 100)
                : 0;
        });
    }

    private void SyncEntryChecked(ModEntry entry, ModRecord record)
    {
        _isApplyingModChanges = true;
        entry.IsChecked = record.IsEnabled;
        _isApplyingModChanges = false;
    }

    /// <summary>Updates a ModEntry's checkbox in the UI by mod id, without re-triggering ModEntry_PropertyChanged - used when a cascade (parent/child) already applied the real enable/disable.</summary>
    private void SyncModEntryCheckedById(string modId, bool isChecked)
    {
        var entry = _mods.FirstOrDefault(m => m.Id == modId);
        if (entry is null) return;

        _isApplyingModChanges = true;
        entry.IsChecked = isChecked;
        _isApplyingModChanges = false;
    }

    private void VisitModPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (VisitModPageButton.Tag is not string url || string.IsNullOrWhiteSpace(url)) return;

        var psi = new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(psi);
    }

    // ---- Mod hover "bocadillo" (lightweight: title/author/description + screenshot slideshow, no embedded browser) ----

    private void ModTitle_MouseEnter(object sender, MouseEventArgs e)
    {
        _cursorOverPreviewTrigger = true;
        _modPreviewCloseTimer.Stop();

        if (sender is not FrameworkElement { DataContext: ModEntry entry }) return;

        ShowModPreviewBocadillo(entry);
    }

    private void ModTitle_MouseLeave(object sender, MouseEventArgs e)
    {
        _cursorOverPreviewTrigger = false;
        _modPreviewCloseTimer.Start();
    }

    private void ModPreviewPopup_MouseEnter(object sender, MouseEventArgs e)
    {
        _modPreviewCloseTimer.Stop();
    }

    private void ModPreviewPopup_MouseLeave(object sender, MouseEventArgs e)
    {
        _modPreviewCloseTimer.Start();
    }

    /// <summary>Closes the bocadillo once the delay fires, but only if the cursor is over neither the trigger title nor the bocadillo itself.</summary>
    private void CloseModPreviewIfCursorAway()
    {
        _modPreviewCloseTimer.Stop();
        if (_cursorOverPreviewTrigger || ModPreviewPopup.IsMouseOver) return;

        _modPreviewSlideshowCts?.Cancel();
        AnimateModPreview(opening: false, onComplete: () => ModPreviewPopup.IsOpen = false);
    }

    private void ShowModPreviewBocadillo(ModEntry entry)
    {
        ModPreviewTitleText.Text = entry.Title;
        ModPreviewDescriptionText.Text = entry.Description;

        if (string.IsNullOrWhiteSpace(entry.Author))
        {
            ModPreviewAuthorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModPreviewAuthorText.Text = $"by {entry.Author}";
            ModPreviewAuthorText.Visibility = Visibility.Visible;
        }

        ModPreviewPopup.IsOpen = true;
        AnimateModPreview(opening: true);

        StartModPreviewSlideshow(entry.ScreenshotUrls);
    }

    /// <summary>
    /// Cycles through a mod's curated screenshots (see ScreenshotUrls - these
    /// aren't scraped live from the mod's page, since third-party sites vary
    /// too much for reliable automated extraction) like a slideshow while the
    /// bocadillo is open. Restarted on every hover so it always starts from
    /// the first screenshot.
    /// </summary>
    private void StartModPreviewSlideshow(IReadOnlyList<string> screenshotUrls)
    {
        _modPreviewSlideshowCts?.Cancel();

        // A crossfade animation from whatever mod was hovered previously might
        // still be mid-flight (WPF animations run independently of the loop's
        // CancellationToken). While an animation clock owns Opacity, setting it
        // directly below is silently ignored, and the old animation finishing
        // afterwards can leave the wrong image visible - hence "the previous
        // mod's screenshot" bleeding into the new hover. Clearing the clocks
        // first hands Opacity back to direct control.
        ModPreviewSlideshowImageA.BeginAnimation(OpacityProperty, null);
        ModPreviewSlideshowImageB.BeginAnimation(OpacityProperty, null);

        if (screenshotUrls.Count == 0)
        {
            ModPreviewSlideshowImageA.Source = null;
            ModPreviewSlideshowImageB.Source = null;
            ModPreviewNoScreenshotsText.Visibility = Visibility.Visible;
            return;
        }

        ModPreviewNoScreenshotsText.Visibility = Visibility.Collapsed;
        _slideshowFrontIsA = true;
        ModPreviewSlideshowImageA.Opacity = 1;
        ModPreviewSlideshowImageB.Opacity = 0;
        SetModPreviewImage(ModPreviewSlideshowImageA, screenshotUrls[0]);
        if (screenshotUrls.Count == 1) return;

        var cts = new CancellationTokenSource();
        _modPreviewSlideshowCts = cts;
        _ = RunSlideshowAsync(screenshotUrls, cts.Token);
    }

    private async Task RunSlideshowAsync(IReadOnlyList<string> screenshotUrls, CancellationToken token)
    {
        var index = 0;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2.5), token); }
            catch (TaskCanceledException) { return; }

            if (token.IsCancellationRequested) return;

            index = (index + 1) % screenshotUrls.Count;
            CrossfadeToImage(screenshotUrls[index]);
        }
    }

    /// <summary>
    /// Crossfades the slideshow to a new screenshot: loads it into the
    /// currently-hidden Image, then fades that one in while fading the
    /// currently-visible one out. Avoids the instant "flash" swap of a
    /// single Image's Source changing.
    /// </summary>
    private void CrossfadeToImage(string url)
    {
        var incoming = _slideshowFrontIsA ? ModPreviewSlideshowImageB : ModPreviewSlideshowImageA;
        var outgoing = _slideshowFrontIsA ? ModPreviewSlideshowImageA : ModPreviewSlideshowImageB;
        _slideshowFrontIsA = !_slideshowFrontIsA;

        SetModPreviewImage(incoming, url);

        var duration = TimeSpan.FromMilliseconds(900);
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
    }

    private void SetModPreviewImage(Image target, string url)
    {
        try
        {
            target.Source = new BitmapImage(new Uri(url));
        }
        catch
        {
            // A single bad/unreachable screenshot URL shouldn't break the rest of the slideshow.
            target.Source = null;
        }
    }

    private void AnimateModPreview(bool opening, Action? onComplete = null)
    {
        var duration = TimeSpan.FromMilliseconds(140);
        var easing = new QuadraticEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };

        var scaleAnim = new DoubleAnimation(opening ? 0.85 : 1.0, opening ? 1.0 : 0.85, duration) { EasingFunction = easing };
        var opacityAnim = new DoubleAnimation(opening ? 0.0 : 1.0, opening ? 1.0 : 0.0, duration) { EasingFunction = easing };

        if (!opening && onComplete is not null)
            opacityAnim.Completed += (_, _) => onComplete();

        ModPreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        ModPreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        ((Border)ModPreviewPopup.Child).BeginAnimation(OpacityProperty, opacityAnim);
    }

    // ---- Mod Page Preview panel (persistent, updates on selection - not a hover popup) ----

    /// <summary>
    /// Loads the selected mod's real page into the persistent preview panel.
    /// Deliberately NOT a Popup: WebView2 doesn't compose reliably inside a
    /// transparent/layered Popup (it can escape as its own OS-chrome window,
    /// which is what caused the hover flyout to misbehave) - a normal panel
    /// that's part of the regular layout doesn't have that problem.
    /// </summary>
    private async Task ShowModPagePreviewAsync(ModEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.PageUrl))
        {
            ModPagePreviewWebView.Visibility = Visibility.Collapsed;
            ModPagePreviewFallbackText.Visibility = Visibility.Collapsed;
            ModPagePreviewAuthorText.Visibility = Visibility.Collapsed;
            ModPagePreviewEmptyText.Visibility = Visibility.Visible;
            return;
        }

        ModPagePreviewEmptyText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(entry.Author))
        {
            ModPagePreviewAuthorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModPagePreviewAuthorText.Text = $"by {entry.Author}";
            ModPagePreviewAuthorText.Visibility = Visibility.Visible;
        }

        if (!await EnsurePagePreviewWebViewReadyAsync())
        {
            ModPagePreviewWebView.Visibility = Visibility.Collapsed;
            ModPagePreviewFallbackText.Visibility = Visibility.Visible;
            return;
        }

        ModPagePreviewFallbackText.Visibility = Visibility.Collapsed;
        ModPagePreviewWebView.Visibility = Visibility.Visible;

        if (_pagePreviewLoadedUrl == entry.PageUrl) return; // avoid a needless reload flash

        try
        {
            ModPagePreviewWebView.CoreWebView2.Navigate(entry.PageUrl);
            _pagePreviewLoadedUrl = entry.PageUrl;
        }
        catch (Exception ex)
        {
            AppendLog($"Couldn't load the page preview for {entry.Title}: {ex.Message}", LogLevel.Warning);
            ModPagePreviewWebView.Visibility = Visibility.Collapsed;
            ModPagePreviewFallbackText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Injected into the mod page preview after every navigation. Combines two
    /// effects the user asked for: an horizontal "auto-fit" (shrinking the
    /// body's layout width so pages with a wide/fixed layout reflow to fit
    /// better) and a ~125% zoom that stays horizontally centered - using CSS
    /// transform with transform-origin: top center instead of WebView2's
    /// native ZoomFactor, since native zoom anchors from the top-left corner
    /// rather than centering.
    /// </summary>
    private const string PagePreviewFitScript = """
        (function() {
            try {
                document.documentElement.style.overflowX = 'hidden';
                document.body.style.width = '80%';
                document.body.style.marginLeft = 'auto';
                document.body.style.marginRight = 'auto';
                document.body.style.transformOrigin = 'top center';
                document.body.style.transform = 'scale(1.25)';
            } catch (e) { /* best-effort - some pages lock down document.body styles */ }
        })();
        """;

    /// <summary>
    /// Lazily creates the WebView2 environment on first selection rather than
    /// at startup, since a session might never select a mod. If the WebView2
    /// Runtime isn't installed, this fails once and every subsequent
    /// selection falls back to the "open in browser" message instead of
    /// retrying (and failing) each time.
    /// </summary>
    private async Task<bool> EnsurePagePreviewWebViewReadyAsync()
    {
        if (_webViewReady) return true;
        if (_webViewInitAttempted) return false;

        _webViewInitAttempted = true;
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XenoSyncLauncher", "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await ModPagePreviewWebView.EnsureCoreWebView2Async(environment);
            ModPagePreviewWebView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try { await ModPagePreviewWebView.CoreWebView2.ExecuteScriptAsync(PagePreviewFitScript); }
                catch { /* best-effort cosmetic tweak - a failed injection just leaves the page at its normal size */ }
            };
            _webViewReady = true;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Mod page previews are unavailable (WebView2 Runtime not found): {ex.Message}", LogLevel.Warning);
            return false;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previousSettings = _settings;
        var settingsWindow = new SettingsWindow(_settings ?? new LauncherSettings()) { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.ResultSettings;
            _settingsService.Save(_settings);
            AppendLog("Settings saved.");

            if (_settings.ModdedPath is not null && previousSettings?.UseDInput != _settings.UseDInput)
            {
                if (_settings.UseDInput)
                    _dllSwapService.ApplyDInput(_settings.ModdedPath);
                else
                    _dllSwapService.ApplyXInput(_settings.ModdedPath);

                AppendLog($"Controller DLL switched to {(_settings.UseDInput ? "DInput" : "XInput")}.");
            }

            if (_settings.ForceReinstallOnNextUpdate)
                AppendLog("Repair requested: XV2Patcher and Revamp will be reinstalled on the next Update.");

            RefreshAutoUpdateTimerState();
            _ = RunLaunchInspectAsync();
        }
    }

    // ------------------------------------------------------------------
    // Update pipeline
    // ------------------------------------------------------------------

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => StartUpdate();

    /// <summary>Kicks off the update pipeline. Called from the Update button, and from the Auto-Update timer.</summary>
    private void StartUpdate()
    {
        if (_lastComparison is null) return;
        if (_activityState != LauncherActivityState.Idle) return; // already updating/paused

        _updateTasks = _updateTaskPlanner.BuildPlan(_lastComparison, _settings);

        if (_settings is { ForceReinstallOnNextUpdate: true })
        {
            _settings.ForceReinstallOnNextUpdate = false;
            _settingsService.Save(_settings);
        }

        if (_updateTasks.Count == 0)
        {
            AppendLog("Nothing to update — all components are already at the latest version.");
            return;
        }

        // Apply any still-valid saved progress so the simulated downloads resume
        // instead of restarting from zero. (The real DepotDownloader task needs
        // no equivalent here: it resumes on its own via its staging folder.)
        foreach (var task in _updateTasks.Where(t => t.Phase == UpdatePhase.Download && !t.IsRealDepotDownload))
        {
            var saved = _downloadResumeService.Load(task.Id);
            if (saved is not null && saved.TargetVersionLabel == task.TargetVersionLabel)
                task.BytesDownloaded = saved.BytesDownloaded;
        }

        _currentTaskIndex = 0;
        _activityState = LauncherActivityState.Updating;
        _componentDownloadedFiles.Clear();
        _componentStagingDirs.Clear();

        UpdateButton.Visibility = Visibility.Collapsed;
        RunButton.Visibility = Visibility.Collapsed;
        PauseResumeButton.Visibility = Visibility.Visible;
        PauseResumeButton.Content = "Pause";
        UpdateStatusPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Visibility = Visibility.Visible;

        AppendLog($"Update started ({_updateTasks.Count} task(s) planned).");
        RefreshUpdateProgressUi();
        _ = RunUpdatePipelineAsync();
    }

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activityState == LauncherActivityState.Updating)
        {
            _activityState = LauncherActivityState.Paused;
            PauseResumeButton.Content = "Resume";
            PersistActiveDownloadProgress();
            _updateCts?.Cancel(); // kills the DepotDownloader process if that's the active task, or breaks the simulated loop
            AppendLog("Update paused. Any in-progress download has been saved and will resume from where it left off.");
        }
        else if (_activityState == LauncherActivityState.Paused)
        {
            _activityState = LauncherActivityState.Updating;
            PauseResumeButton.Content = "Pause";
            AppendLog("Update resumed.");
            _ = RunUpdatePipelineAsync();
        }
    }

    /// <summary>
    /// Runs tasks sequentially starting at _currentTaskIndex. Returns (without
    /// throwing) as soon as a task is interrupted, leaving _currentTaskIndex and
    /// the task's own progress fields exactly where they were so Resume/Update
    /// can continue seamlessly.
    /// </summary>
    private async Task RunUpdatePipelineAsync()
    {
        while (_currentTaskIndex < _updateTasks.Count)
        {
            var task = _updateTasks[_currentTaskIndex];
            _updateCts = new CancellationTokenSource();

            bool completed = await RunSingleTaskAsync(task, _updateCts.Token);

            if (!completed)
                return; // paused or cancelled during login — state is preserved for next time

            task.IsCompleted = true;

            if (task.Id == "install-xv2patcher" && _settings?.ModdedPath is not null)
            {
                DefaultPatcherFlags.ApplyTo(_settings.ModdedPath, _iniFlagService);
                AppendLog("Applied default XV2Patcher flags (all stages unlocked).");
            }

            if (task.Phase == UpdatePhase.Download && !task.IsRealDepotDownload)
                _downloadResumeService.Clear(task.Id);

            if (task.IsRealDepotDownload && _settings is not null)
            {
                _settings.NeedsGameDownload = false;
                _settingsService.Save(_settings);

                if (task.ManifestId is not null)
                    _installedVersionService.SetInstalledGameManifestId(_settings.ModdedPath, task.ManifestId);
            }

            AppendLog($"Completed: {task.PhaseLabel} {task.DisplayName}.");
            _currentTaskIndex++;
            RefreshUpdateProgressUi();
        }

        await FinishUpdateAsync();
    }

    /// <summary>Runs the real DepotDownloader process for the game-version task. Returns false on pause/cancel or failure.</summary>
    private async Task<bool> RunRealDepotTaskAsync(UpdateTaskItem task, CancellationToken token)
    {
        if (_settings?.ModdedPath is null || string.IsNullOrWhiteSpace(_settings.DepotDownloaderPath))
        {
            AppendLog("Cannot download the game files: Modded path or DepotDownloader path is not configured in Settings.", LogLevel.Error);
            return false;
        }

        _loginCancelledByUser = false;

        var loginMethod = _settings.SteamLoginMethod == "Credentials" ? SteamLoginMethod.Credentials : SteamLoginMethod.QrCode;

        var request = new DepotDownloadRequest
        {
            AppId = task.DepotAppId ?? _settings.GameAppId,
            DepotId = task.DepotId,
            ManifestId = task.ManifestId!,
            InstallDirectory = _settings.ModdedPath,
            LoginMethod = loginMethod,
            SteamUsername = _settings.SteamUsername
        };

        DateTime lastActivityUtc = DateTime.UtcNow;
        bool stallWarningLogged = false;

        var progress = new Progress<DepotDownloadProgress>(p =>
        {
            lastActivityUtc = DateTime.UtcNow;
            stallWarningLogged = false;

            if (p.PercentComplete >= 0)
            {
                task.RealTimeProgressPercent = p.PercentComplete;

                // Login succeeded and the actual download is under way — the QR
                // window / credential window (if any) has served its purpose.
                if (_qrLoginWindow is not null)
                {
                    _qrLoginWindow.Close();
                    _qrLoginWindow = null;
                }

                if (_credentialPromptWindow is not null)
                {
                    _credentialPromptWindow.Close();
                    _credentialPromptWindow = null;
                }

                RefreshUpdateProgressUi();
            }
            else if (!string.IsNullOrWhiteSpace(p.StatusLine))
            {
                // Log every other line DepotDownloader prints too - otherwise
                // an unrecognized prompt (different wording than we expect)
                // just sits there silently instead of giving anyone a clue.
                AppendLog($"[DepotDownloader] {p.StatusLine}");
            }
        });

        void OnQrAsciiBlock(string[] lines)
        {
            // Once the user has cancelled, ignore any further QR blocks that
            // were already in flight - otherwise a block DepotDownloader had
            // already queued before the kill took effect could pop the window
            // back open right after Cancel closed it.
            if (_loginCancelledByUser) return;

            if (_qrLoginWindow is null)
            {
                _qrLoginWindow = new QrLoginWindow(lines, onCancel: () =>
                {
                    _loginCancelledByUser = true;
                    _updateCts?.Cancel();
                })
                { Owner = this };
                _qrLoginWindow.Closed += (_, _) => _qrLoginWindow = null;
                _qrLoginWindow.Show();
            }
            else
            {
                // DepotDownloader issued a fresh challenge (the previous one expired unscanned).
                _qrLoginWindow.SetQrAsciiBlock(lines);
            }
        }

        using var stallWatchdogCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watchdogTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stallWatchdogCts.Token);

                    if (!stallWarningLogged && DateTime.UtcNow - lastActivityUtc > TimeSpan.FromSeconds(20))
                    {
                        stallWarningLogged = true;
                        AppendLog("No output from DepotDownloader in over 20 seconds. It may be waiting for a " +
                                  "prompt XenoSync Launcher doesn't recognize (different wording than expected), " +
                                  "or it may have stalled. Check the lines above for anything unanswered, or try Pause/Resume.", LogLevel.Warning);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: cancelled once the actual download finishes.
            }
        });

        var result = await _depotDownloaderService.RunAsync(
            _settings.DepotDownloaderPath,
            request,
            OnQrAsciiBlock,
            PromptForSteamPasswordAsync,
            PromptForSteamGuardCodeAsync,
            progress,
            token);

        stallWatchdogCts.Cancel();
        try { await watchdogTask; } catch (OperationCanceledException) { }

        _qrLoginWindow?.Close();
        _qrLoginWindow = null;

        _credentialPromptWindow?.Close();
        _credentialPromptWindow = null;

        switch (result.Outcome)
        {
            case DepotDownloadOutcome.Success:
                return true;
            case DepotDownloadOutcome.Cancelled:
                if (_loginCancelledByUser)
                {
                    _loginCancelledByUser = false;
                    StopUpdateEntirely("Sign-in was cancelled. The Update has been stopped - click Update to try again.");
                }
                return false;
            case DepotDownloadOutcome.Failed:
                AppendLog($"Download failed: {result.ErrorMessage}", LogLevel.Error);
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Fully stops the current update (as opposed to Pause, which preserves
    /// progress and shows Resume). Used when the user cancels the QR/password
    /// sign-in prompt: the UI reverts to Update/Run, requiring a fresh click
    /// on Update to try again. Any bytes DepotDownloader already fetched
    /// aren't lost - they stay in its own staging folder and resume next time.
    /// </summary>
    private void StopUpdateEntirely(string message, LogLevel level = LogLevel.Warning)
    {
        _updateCts?.Cancel();
        _activityState = LauncherActivityState.Idle;

        UpdateStatusPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        PauseResumeButton.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Visible;
        RunButton.Visibility = Visibility.Visible;

        AppendLog(message, level);
    }

    private Task<string?> PromptForSteamPasswordAsync() => GetCredentialAsync(
        "DepotDownloader needs your Steam password to continue. It is never stored on disk.", isPassword: true);

    private Task<string?> PromptForSteamGuardCodeAsync() => GetCredentialAsync(
        "Enter the Steam Guard code that was just sent to your email or mobile app.", isPassword: false);

    /// <summary>
    /// DepotDownloaderService's stdout-reading loop doesn't guarantee it's
    /// still on the UI thread when it calls these prompts (Process stream
    /// reads don't always preserve the original SynchronizationContext) -
    /// showing a WPF Window from the wrong thread doesn't throw a clean
    /// exception, it just hangs, so window creation/access always goes
    /// through Dispatcher.Invoke here.
    ///
    /// Being asked again for the SAME kind of credential (password again, or
    /// code again) means the previous attempt was wrong - DepotDownloader
    /// only re-prompts like that on failure. In that case the existing window
    /// is reused to show an inline error and let the user retry, instead of
    /// closing it and opening a new one. A different kind of prompt (e.g.
    /// Steam Guard code right after password succeeded) opens a fresh window
    /// for its own purpose.
    ///
    /// Returns null if the user cancels, and marks _loginCancelledByUser so
    /// the caller stops the whole Update instead of sending an empty answer,
    /// which DepotDownloader would just reject and re-prompt for again.
    /// </summary>
    private async Task<string?> GetCredentialAsync(string message, bool isPassword)
    {
        bool isRetryOfSameKind = _credentialPromptWindow is not null && _credentialPromptIsPassword == isPassword;

        if (isRetryOfSameKind)
        {
            Dispatcher.Invoke(() => _credentialPromptWindow!.ShowIncorrectError(
                isPassword ? "Incorrect password. Please try again." : "Incorrect code. Please try again."));
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                _credentialPromptWindow?.Close();
                _credentialPromptWindow = new CredentialPromptWindow(message, isPassword) { Owner = this };
                _credentialPromptWindow.Closed += (_, _) => _credentialPromptWindow = null;
                _credentialPromptWindow.Show();
            });
            _credentialPromptIsPassword = isPassword;
        }

        var waitTask = Dispatcher.Invoke(() => _credentialPromptWindow!.WaitForSubmitAsync());
        var result = await waitTask;

        if (result is null)
        {
            _loginCancelledByUser = true;
            _updateCts?.Cancel();
            Dispatcher.Invoke(() => _credentialPromptWindow?.Close());
            _credentialPromptWindow = null;
        }

        return result;
    }

    /// <summary>
    /// Routes each planned task to its real implementation. Falls back to the
    /// mock simulation only for task ids this dispatcher doesn't recognize.
    /// </summary>
    private async Task<bool> RunSingleTaskAsync(UpdateTaskItem task, CancellationToken token)
    {
        if (task.IsRealDepotDownload)
            return await RunRealDepotTaskAsync(task, token);

        return task.Id switch
        {
            "download-xv2patcher" => await RunHttpDownloadTaskAsync(task, "xv2patcher", await _componentDownloadService.GetXv2PatcherDownloadUrlAsync(), token),
            "download-revamp" => await RunRevampDownloadTaskAsync(task, "revamp", await _componentDownloadService.GetRevampGoogleDriveFileIdAsync(), token),
            "extract-xv2patcher" => await RunExtractOrLaunchTaskAsync("xv2patcher", token),
            "extract-revamp" => await RunExtractOrLaunchTaskAsync("revamp", token),
            "install-xv2patcher" => await RunInstallTaskAsync("xv2patcher", token),
            "install-revamp" => await RunInstallTaskAsync("revamp", token),
            _ => await SimulateTaskAsync(task, token)
        };
    }

    private async Task<bool> RunHttpDownloadTaskAsync(UpdateTaskItem task, string componentKey, string url, CancellationToken token)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Components", $"{componentKey}.download");

        var progress = new Progress<DownloadProgressInfo>(p =>
        {
            task.ExpectedTotalBytes = p.TotalBytes ?? Math.Max(task.ExpectedTotalBytes, p.BytesReceived);
            task.BytesDownloaded = p.BytesReceived;
            RefreshUpdateProgressUi();
        });

        var (success, error) = await _httpDownloadService.DownloadAsync(url, tempFile, progress, _settings?.SpeedLimitMbps, token);

        if (!success)
        {
            if (error != "Cancelled") AppendLog($"Download of {task.DisplayName} failed: {error}", LogLevel.Error);
            return false;
        }

        task.BytesDownloaded = task.ExpectedTotalBytes;
        _componentDownloadedFiles[componentKey] = tempFile;
        return true;
    }

    /// <summary>
    /// Tries Google Drive first (the "official" mirror); if it fails for any
    /// reason other than being cancelled (quota exceeded, confirmation page
    /// changed again, etc.), automatically retries via the direct fallback
    /// URL (a confirmed-public link, e.g. the Patreon mirror) using the plain
    /// HTTP downloader instead.
    /// </summary>
    private async Task<bool> RunRevampDownloadTaskAsync(UpdateTaskItem task, string componentKey, string fileId, CancellationToken token)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Components", $"{componentKey}.download");

        var progress = new Progress<DownloadProgressInfo>(p =>
        {
            task.ExpectedTotalBytes = p.TotalBytes ?? Math.Max(task.ExpectedTotalBytes, p.BytesReceived);
            task.BytesDownloaded = p.BytesReceived;
            RefreshUpdateProgressUi();
        });

        var (success, error) = await _googleDriveDownloadService.DownloadAsync(fileId, tempFile, progress, _settings?.SpeedLimitMbps, token);

        if (!success && error != "Cancelled")
        {
            AppendLog($"Google Drive download failed ({error}). Trying the direct fallback mirror instead...", LogLevel.Warning);

            // Google Drive may have left a partial/HTML file behind - start clean for the direct-link attempt.
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore, HttpDownloadService will just overwrite it */ }

            var fallbackUrl = await _componentDownloadService.GetRevampFallbackDownloadUrlAsync();
            task.BytesDownloaded = 0;
            task.ExpectedTotalBytes = 0;
            (success, error) = await _httpDownloadService.DownloadAsync(fallbackUrl, tempFile, progress, _settings?.SpeedLimitMbps, token);
        }

        if (!success)
        {
            if (error != "Cancelled") AppendLog($"Download of {task.DisplayName} failed: {error}", LogLevel.Error);
            return false;
        }

        task.BytesDownloaded = task.ExpectedTotalBytes;
        _componentDownloadedFiles[componentKey] = tempFile;
        return true;
    }

    /// <summary>
    /// Extracts the downloaded file if it's a real ZIP/RAR archive. If it
    /// turns out to be a self-extracting installer .exe instead (Revamp's
    /// case is unverified — its distributor calls it an "installer"), launches
    /// it and waits for the user to complete it, since guessing silent-install
    /// flags for an unknown installer would be unreliable.
    /// </summary>
    private async Task<bool> RunExtractOrLaunchTaskAsync(string componentKey, CancellationToken token)
    {
        if (!_componentDownloadedFiles.TryGetValue(componentKey, out var downloadedFile) || !File.Exists(downloadedFile))
        {
            AppendLog($"Cannot extract {componentKey}: the downloaded file wasn't found.", LogLevel.Error);
            return false;
        }

        var kind = _archiveExtractionService.DetectKind(downloadedFile);

        if (kind == ArchiveKind.Unknown)
        {
            AppendLog($"'{Path.GetFileName(downloadedFile)}' looks like an installer rather than a plain archive. " +
                      "Launching it now — please complete the installer, then XenoSync Launcher will continue automatically once it closes.");

            try
            {
                using var process = Process.Start(new ProcessStartInfo(downloadedFile) { UseShellExecute = true });
                if (process is null)
                {
                    AppendLog("Failed to launch the installer.");
                    return false;
                }

                await using var registration = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                });

                await process.WaitForExitAsync(CancellationToken.None);

                if (token.IsCancellationRequested) return false;
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to run the installer: {ex.Message}", LogLevel.Error);
                return false;
            }

            // The installer placed its own files — nothing left to extract/move.
            _componentStagingDirs[componentKey] = string.Empty;
            return true;
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), "XenoSyncLauncher", "Components", $"{componentKey}-staging");

        try
        {
            await Task.Run(() =>
            {
                _archiveExtractionService.Extract(downloadedFile, stagingDir, (done, total) => RefreshUpdateProgressUi());
            }, token);

            _componentStagingDirs[componentKey] = stagingDir;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Extraction of {componentKey} failed: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// Moves the extracted files into their final location.
    /// Both XV2Patcher's and Revamp's archives wrap their payload in a single
    /// top-level folder; that wrapper is flattened (see FlattenSingleWrapperFolder)
    /// and its contents merged directly into the Modded root. Revamp's install is
    /// additionally confirmed via its key file (see IsRevampInstalledCorrectly)
    /// before being recorded - the process exiting isn't proof by itself.
    /// </summary>
    private async Task<bool> RunInstallTaskAsync(string componentKey, CancellationToken token)
    {
        if (_settings?.ModdedPath is null) return false;

        if (!_componentStagingDirs.TryGetValue(componentKey, out var stagingDir))
        {
            AppendLog($"Cannot install {componentKey}: nothing was extracted.", LogLevel.Error);
            return false;
        }

        if (stagingDir.Length == 0)
        {
            // An installer .exe already placed its own files during the extract step -
            // but the installer closing isn't proof it succeeded, so confirm the key
            // file is actually there before trusting it (see IsRevampInstalledCorrectly).
            if (componentKey == "revamp" && !IsRevampInstalledCorrectly(_settings.ModdedPath))
            {
                AppendLog("Revamp's installer closed, but its key file (data/LB Mod Installer/revamp xenoverse 2 project_revamp team.xml) " +
                          "wasn't found afterwards. Treating this as a failed install.", LogLevel.Error);
                return false;
            }

            RecordInstalledVersion(componentKey);
            return true;
        }

        try
        {
            // Both XV2Patcher's and Revamp's archives wrap their real payload in a
            // single top-level folder (e.g. "XV2Patcher_4.64/..."). Flatten that one
            // level so the actual files land directly in the Modded root, rather than
            // nesting them under an extra subfolder that XV2Patcher/Revamp never expect.
            var effectiveSourceDir = FlattenSingleWrapperFolder(stagingDir);

            await Task.Run(() => MergeDirectory(effectiveSourceDir, _settings.ModdedPath), token);

            if (componentKey == "revamp" && !IsRevampInstalledCorrectly(_settings.ModdedPath))
            {
                AppendLog("Revamp's key file (data/LB Mod Installer/revamp xenoverse 2 project_revamp team.xml) " +
                          "wasn't found after copying its files. The install did not complete correctly.", LogLevel.Error);
                return false;
            }

            RecordInstalledVersion(componentKey);

            // Now that the install is confirmed, the staging copy is no longer needed.
            try { Directory.Delete(stagingDir, recursive: true); }
            catch (Exception ex) { AppendLog($"Installed {componentKey}, but couldn't clean up the temp extraction folder: {ex.Message}", LogLevel.Warning); }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Installing {componentKey} failed: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// If <paramref name="dir"/> contains exactly one entry and it's a subfolder
    /// (the typical "everything wrapped in one top folder" archive layout), returns
    /// that subfolder's path instead, so callers merge its *contents* rather than
    /// re-creating that wrapper folder inside the destination. Otherwise returns
    /// <paramref name="dir"/> unchanged.
    /// </summary>
    private static string FlattenSingleWrapperFolder(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];

        return dir;
    }

    /// <summary>
    /// Revamp's installer/archive is confirmed to have actually placed its files
    /// once this file exists - it's created by the Revamp team's own installer and
    /// isn't something XenoSync Launcher writes itself, so its presence is a
    /// reliable signal (unlike just trusting that the installer process exited).
    /// </summary>
    private static bool IsRevampInstalledCorrectly(string moddedPath) =>
        File.Exists(Path.Combine(moddedPath, "data", "LB Mod Installer", "revamp xenoverse 2 project_revamp team.xml"));

    /// <summary>
    /// Writes XenoSync's own version bookkeeping (see InstalledComponentVersionService)
    /// right after a component finishes installing, using the latest version
    /// we just fetched for it. This is what makes Launch Inspect stop saying
    /// "Not installed" once a real install actually completes.
    /// </summary>
    private void RecordInstalledVersion(string componentKey)
    {
        if (_settings?.ModdedPath is null || _lastComparison is null) return;

        if (componentKey == "xv2patcher" && _lastComparison.LatestXv2PatcherVersion is not null)
            _installedVersionService.SetInstalledXv2PatcherVersion(_settings.ModdedPath, _lastComparison.LatestXv2PatcherVersion);
        else if (componentKey == "revamp" && _lastComparison.LatestRevampVersion is not null)
            _installedVersionService.SetInstalledRevampVersion(_settings.ModdedPath, _lastComparison.LatestRevampVersion);
    }

    private static void MergeDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>Runs the mock simulation for a non-real task (any task id not handled above).</summary>
    private static async Task<bool> SimulateTaskAsync(UpdateTaskItem task, CancellationToken token)
    {
        try
        {
            if (task.Phase == UpdatePhase.Download)
            {
                while (task.BytesDownloaded < task.ExpectedTotalBytes)
                {
                    await Task.Delay(SimulatedTickInterval, token);
                    task.BytesDownloaded = Math.Min(task.ExpectedTotalBytes, task.BytesDownloaded + SimulatedBytesPerTick);
                }
            }
            else
            {
                while (task.SubTicksCompleted < task.TotalSubTicks)
                {
                    await Task.Delay(SimulatedTickInterval, token);
                    task.SubTicksCompleted++;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the current task's download progress to the temp directory, if
    /// the task in progress right now is a simulated Download that hasn't
    /// completed yet. Not needed for the real DepotDownloader task, which
    /// tracks its own resume state on disk independently.
    /// </summary>
    private void PersistActiveDownloadProgress()
    {
        if (_activityState is not (LauncherActivityState.Updating or LauncherActivityState.Paused)) return;
        if (_currentTaskIndex >= _updateTasks.Count) return;

        var task = _updateTasks[_currentTaskIndex];
        if (task.IsRealDepotDownload || task.Phase != UpdatePhase.Download || task.IsCompleted) return;

        _downloadResumeService.Save(new DownloadResumeState
        {
            TaskId = task.Id,
            TaskDisplayName = task.DisplayName,
            TargetVersionLabel = task.TargetVersionLabel ?? string.Empty,
            TempFilePath = task.TempFilePath ?? string.Empty,
            ExpectedTotalBytes = task.ExpectedTotalBytes,
            BytesDownloaded = task.BytesDownloaded,
            LastUpdatedUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Recomputes the overall percentage as (completed tasks + current task's
    /// own fractional progress) / total tasks, and updates the task label.
    /// </summary>
    /// <summary>
    /// Safe to call from any thread - the extraction/install steps run their
    /// progress callbacks on a background thread (via Task.Run), and touching
    /// UpdateProgressBar/UpdatePercentText/UpdateTaskText directly from there
    /// throws "the calling thread cannot access this object" once WPF's
    /// thread-ownership check trips.
    /// </summary>
    private void RefreshUpdateProgressUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshUpdateProgressUi);
            return;
        }

        if (_updateTasks.Count == 0) return;

        var completedCount = _updateTasks.Count(t => t.IsCompleted);
        var currentTask = _currentTaskIndex < _updateTasks.Count ? _updateTasks[_currentTaskIndex] : null;
        var currentFraction = currentTask?.FractionComplete ?? 0;

        var overallPercent = 100.0 * (completedCount + currentFraction) / _updateTasks.Count;

        UpdateProgressBar.Value = overallPercent;
        UpdatePercentText.Text = $"{(int)overallPercent}%";

        UpdateTaskText.Text = currentTask is not null
            ? $"{currentTask.PhaseLabel} {currentTask.DisplayName}... ({completedCount}/{_updateTasks.Count} tasks done)"
            : $"{completedCount}/{_updateTasks.Count} tasks done";
    }

    private async Task FinishUpdateAsync()
    {
        _activityState = LauncherActivityState.Idle;

        UpdateStatusPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        PauseResumeButton.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Visible;
        RunButton.Visibility = Visibility.Visible;

        AppendLog("Update finished.");
        await LoadModsAsync();
        await RunLaunchInspectAsync();
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings?.ModdedPath is null) return;

        var exePath = Path.Combine(_settings.ModdedPath, "bin", "DBXV2.exe");

        if (!File.Exists(exePath))
        {
            AppendLog($"Cannot launch: '{exePath}' was not found.", LogLevel.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.Combine(_settings.ModdedPath, "bin"),
                UseShellExecute = true
            });
            AppendLog("Xenoverse 2 launched.");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to launch: {ex.Message}", LogLevel.Error);
        }
    }
}