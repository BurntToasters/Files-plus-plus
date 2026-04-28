using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using FilesPlusPlus.App.Extensions;
using FilesPlusPlus.App.Models;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FilesPlusPlus.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int MaxOperationHistory = 120;
    private readonly IFileSystemService _fileSystemService;
    private readonly IFileOperationService _fileOperationService;
    private readonly ISearchService _searchService;
    private readonly ITabSessionService _tabSessionService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly SynchronizationContext? _uiContext;
    private readonly ConcurrentDictionary<Guid, FileOperationEntry> _operationLookup = new();
    private bool _isInitialized;
    private ClipboardMode _clipboardMode = ClipboardMode.None;
    private readonly List<string> _clipboardPaths = [];

    private sealed record ClipboardPayload(ClipboardMode Mode, List<string> Paths, bool IsInternalSource);

    private enum ClipboardMode
    {
        None,
        Copy,
        Cut
    }

    public enum FileConflictResolution
    {
        Replace,
        Skip,
        KeepBoth
    }

    public MainWindowViewModel(
        IFileSystemService fileSystemService,
        IFileOperationService fileOperationService,
        ISearchService searchService,
        ITabSessionService tabSessionService,
        IAppSettingsService appSettingsService)
    {
        _fileSystemService = fileSystemService;
        _fileOperationService = fileOperationService;
        _searchService = searchService;
        _tabSessionService = tabSessionService;
        _appSettingsService = appSettingsService;
        _uiContext = SynchronizationContext.Current;

        _fileOperationService.OperationCompleted += OnFileOperationCompleted;
        _fileOperationService.ProgressChanged += OnFileOperationProgressChanged;
        _appSettingsService.Changed += OnAppSettingsChanged;
        ApplySettingsToViewModel(_appSettingsService.Current);
        SeedSidebar();
    }

    public IAppSettingsService AppSettings => _appSettingsService;

    public ObservableCollection<ExplorerTabViewModel> Tabs { get; } = new();

    public ObservableCollection<SidebarLocation> SidebarLocations { get; } = new();
    public ObservableCollection<FileOperationEntry> FileOperationHistory { get; } = new();

    public Func<FileConflictPrompt, Task<FileConflictResolution>>? ResolveFileConflictAsync { get; set; }

    public bool HasClipboardItems => _clipboardMode != ClipboardMode.None && _clipboardPaths.Count > 0;

    [ObservableProperty]
    private int _pendingOperationsCount;

    [ObservableProperty]
    private int _runningOperationsCount;

    [ObservableProperty]
    private int _failedOperationsCount;

    [ObservableProperty]
    private string _operationsSummary = "Ops P:0 R:0 F:0";

    public bool CanPasteFromClipboard()
    {
        if (HasClipboardItems)
        {
            return true;
        }

        try
        {
            var clipboardData = Clipboard.GetContent();
            return clipboardData.Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
        }
    }

    [ObservableProperty]
    private ExplorerTabViewModel? _selectedTab;

    [ObservableProperty]
    private SidebarLocation? _selectedSidebar;

    [ObservableProperty]
    private string _statusLine = "Ready.";

    [ObservableProperty]
    private WindowLayout _startupLayout = new(1360, 860, IsMaximized: false);

    [ObservableProperty]
    private Microsoft.UI.Xaml.Thickness _rowPadding = new(16, 7, 16, 7);

    [ObservableProperty]
    private double _rowFontSize = 13;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Thickness _sidebarPadding = new(10, 7, 10, 7);

    public AppOpenItemMode OpenItemMode => _appSettingsService.Current.Navigation.OpenItemMode;

    public bool ConfirmDelete => _appSettingsService.Current.Navigation.ConfirmDelete;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        var session = await _tabSessionService.LoadAsync(cancellationToken);
        StartupLayout = session.WindowLayout;

        var settings = _appSettingsService.Current;

        if (session.SidebarPins.Count > 0)
        {
            var labelsByPath = SidebarLocations.ToDictionary(location => location.Path, location => location.Label, StringComparer.OrdinalIgnoreCase);
            var glyphByPath = SidebarLocations.ToDictionary(location => location.Path, location => location.Glyph, StringComparer.OrdinalIgnoreCase);
            var rebuilt = session.SidebarPins
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .Select(path => new SidebarLocation(
                    labelsByPath.TryGetValue(path, out var label) ? label : Path.GetFileName(path),
                    path,
                    glyphByPath.TryGetValue(path, out var glyph) ? glyph : "\uE838",
                    IsPinned: true))
                .ToList();

            if (rebuilt.Count > 0)
            {
                SidebarLocations.ResetWith(rebuilt);
            }
        }

        if (settings.Navigation.StartupBehavior == AppStartupBehavior.RestoreTabs)
        {
            foreach (var tabState in session.Tabs)
            {
                var tab = CreateTab();
                await tab.RestoreFromStateAsync(tabState, cancellationToken);

                if (string.IsNullOrWhiteSpace(tab.CurrentPath) || !Directory.Exists(tab.CurrentPath))
                {
                    tab.Dispose();
                    continue;
                }

                Tabs.Add(tab);
            }
        }

        if (Tabs.Count == 0)
        {
            var startupPath = ResolveStartupPath(settings);
            await AddTabAsync(startupPath, cancellationToken: cancellationToken);
        }
        else
        {
            var clampedIndex = Math.Clamp(session.SelectedTabIndex, 0, Tabs.Count - 1);
            SelectedTab = Tabs[clampedIndex];
            UpdateStatusLine();
        }

        _isInitialized = true;
    }

    private string? ResolveStartupPath(AppSettings settings)
    {
        switch (settings.Navigation.StartupBehavior)
        {
            case AppStartupBehavior.SpecificPath when !string.IsNullOrWhiteSpace(settings.Navigation.StartupPath)
                                                       && Directory.Exists(settings.Navigation.StartupPath):
                return settings.Navigation.StartupPath;
            case AppStartupBehavior.HomeFolder:
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            default:
                return null;
        }
    }

    public void ApplySidebarFromSettings(IEnumerable<SidebarLocation> pins)
    {
        var orderedPins = pins
            .GroupBy(pin => pin.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var entries = new List<SidebarLocation>(orderedPins);
        if (_appSettingsService.Current.Sidebar.ShowDrives)
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new SidebarLocation(drive.Name, drive.RootDirectory.FullName, "\uE7C3"));
            entries.AddRange(drives);
        }

        SidebarLocations.ResetWith(entries.DistinctBy(location => location.Path));
    }

    private void OnAppSettingsChanged(object? sender, AppSettings settings)
    {
        if (_uiContext is null)
        {
            ApplySettingsToViewModel(settings);
            return;
        }

        _uiContext.Post(_ =>
        {
            ApplySettingsToViewModel(settings);
            RefreshTabsForSettings();
        }, null);
    }

    private void ApplySettingsToViewModel(AppSettings settings)
    {
        switch (settings.Appearance.Density)
        {
            case AppDensity.Compact:
                RowPadding = new Microsoft.UI.Xaml.Thickness(12, 4, 12, 4);
                RowFontSize = 12;
                SidebarPadding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4);
                break;
            case AppDensity.Spacious:
                RowPadding = new Microsoft.UI.Xaml.Thickness(20, 11, 20, 11);
                RowFontSize = 14;
                SidebarPadding = new Microsoft.UI.Xaml.Thickness(12, 10, 12, 10);
                break;
            default:
                RowPadding = new Microsoft.UI.Xaml.Thickness(16, 7, 16, 7);
                RowFontSize = 13;
                SidebarPadding = new Microsoft.UI.Xaml.Thickness(10, 7, 10, 7);
                break;
        }

        OnPropertyChanged(nameof(OpenItemMode));
        OnPropertyChanged(nameof(ConfirmDelete));
    }

    private void RefreshTabsForSettings()
    {
        foreach (var tab in Tabs)
        {
            try
            {
                _ = tab.RefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh tab after settings change: {ex}");
            }
        }
    }

    public async Task AddTabAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var startupPath = path;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fallback = Directory.Exists(documents)
            ? documents
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(startupPath))
        {
            startupPath = SelectedTab?.CurrentPath ?? fallback;
        }

        if (string.IsNullOrWhiteSpace(startupPath) || !Directory.Exists(startupPath))
        {
            startupPath = fallback;
        }

        var tab = CreateTab();
        Tabs.Add(tab);
        SelectedTab = tab;

        await tab.NavigateToAsync(startupPath, pushCurrentToHistory: false, cancellationToken);
        UpdateStatusLine();
    }

    public Task CloseTabAsync(ExplorerTabViewModel tab, CancellationToken cancellationToken = default)
    {
        if (!Tabs.Contains(tab))
        {
            return Task.CompletedTask;
        }

        var closingIndex = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count == 0)
        {
            return AddTabAsync(cancellationToken: cancellationToken);
        }

        var nextIndex = Math.Clamp(closingIndex, 0, Tabs.Count - 1);
        SelectedTab = Tabs[nextIndex];
        UpdateStatusLine();
        return Task.CompletedTask;
    }

    public async Task NavigateToLocationAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            PublishStatus("No location selected.");
            return;
        }

        if (SelectedTab is null)
        {
            await AddTabAsync(path, cancellationToken);
            return;
        }

        try
        {
            await SelectedTab.NavigateToAsync(path, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            PublishStatus($"Unable to open location: {ex.Message}");
        }
    }

    public Task NavigateBackAsync(CancellationToken cancellationToken = default) =>
        SelectedTab?.NavigateBackAsync(cancellationToken) ?? Task.CompletedTask;

    public Task NavigateForwardAsync(CancellationToken cancellationToken = default) =>
        SelectedTab?.NavigateForwardAsync(cancellationToken) ?? Task.CompletedTask;

    public Task NavigateUpAsync(CancellationToken cancellationToken = default) =>
        SelectedTab?.NavigateUpAsync(cancellationToken) ?? Task.CompletedTask;

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        SelectedTab?.RefreshAsync(cancellationToken) ?? Task.CompletedTask;

    public Task SearchCurrentTabAsync(string searchText, CancellationToken cancellationToken = default) =>
        SelectedTab?.SearchAsync(searchText, cancellationToken) ?? Task.CompletedTask;

    public Task CopySelectionAsync(CancellationToken cancellationToken = default) =>
        CaptureSelectionToClipboardAsync(ClipboardMode.Copy, cancellationToken);

    public Task CutSelectionAsync(CancellationToken cancellationToken = default) =>
        CaptureSelectionToClipboardAsync(ClipboardMode.Cut, cancellationToken);

    public async Task PasteIntoCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTab is null || string.IsNullOrWhiteSpace(SelectedTab.CurrentPath))
        {
            PublishStatus("No destination selected.");
            return;
        }

        var clipboardPayload = await ResolveClipboardPayloadAsync(cancellationToken);
        if (clipboardPayload is null || clipboardPayload.Paths.Count == 0)
        {
            PublishStatus("Clipboard is empty.");
            return;
        }

        var (queuedCount, skippedCount) = await QueueTransfersAsync(
            SelectedTab.CurrentPath,
            clipboardPayload.Paths,
            moveItems: clipboardPayload.Mode == ClipboardMode.Cut,
            cancellationToken);

        if (queuedCount == 0)
        {
            PublishStatus(skippedCount > 0 ? "No items were queued to paste." : "Clipboard is empty.");
            return;
        }

        if (clipboardPayload.Mode == ClipboardMode.Cut && clipboardPayload.IsInternalSource)
        {
            ClearClipboard();
        }

        var operationLabel = clipboardPayload.Mode == ClipboardMode.Cut ? "move" : "copy";
        PublishStatus($"Queued {operationLabel} for {queuedCount} item(s)." +
                      (skippedCount > 0 ? $" Skipped {skippedCount}." : string.Empty));
    }

    public Task QueueTransferIntoCurrentAsync(
        IEnumerable<string> sourcePaths,
        bool moveItems,
        CancellationToken cancellationToken = default)
    {
        if (SelectedTab is null || string.IsNullOrWhiteSpace(SelectedTab.CurrentPath))
        {
            PublishStatus("No destination selected.");
            return Task.CompletedTask;
        }

        return QueueTransferIntoCurrentInternalAsync(sourcePaths, moveItems, cancellationToken);
    }

    private async Task QueueTransferIntoCurrentInternalAsync(
        IEnumerable<string> sourcePaths,
        bool moveItems,
        CancellationToken cancellationToken)
    {
        if (SelectedTab is null || string.IsNullOrWhiteSpace(SelectedTab.CurrentPath))
        {
            PublishStatus("No destination selected.");
            return;
        }

        var (queuedCount, skippedCount) = await QueueTransfersAsync(
            SelectedTab.CurrentPath,
            sourcePaths,
            moveItems,
            cancellationToken);

        if (queuedCount == 0)
        {
            PublishStatus(skippedCount > 0 ? "No items were queued." : "No transferable items found.");
            return;
        }

        var operationLabel = moveItems ? "move" : "copy";
        PublishStatus($"Queued {operationLabel} for {queuedCount} item(s)." +
                      (skippedCount > 0 ? $" Skipped {skippedCount}." : string.Empty));
    }

    public void SortCurrent(SortColumn sortColumn) => SelectedTab?.ToggleSort(sortColumn);

    public void ToggleDirectoryGrouping() => SelectedTab?.ToggleDirectoryGrouping();

    public void SetCurrentViewMode(FolderViewMode viewMode) => SelectedTab?.SetViewMode(viewMode);

    public void SetCurrentDetailsPaneVisibility(bool isVisible) => SelectedTab?.SetDetailsPaneVisibility(isVisible);

    public void SetCurrentDetailsPaneWidth(double width) => SelectedTab?.SetDetailsPaneWidth(width);

    public string ApplyCurrentDetailsPaneStateToAllTabs()
    {
        if (SelectedTab is null || Tabs.Count == 0)
        {
            const string noActiveTabMessage = "No active tab to sync pane layout from.";
            PublishStatus(noActiveTabMessage);
            return noActiveTabMessage;
        }

        var sourceHeader = string.IsNullOrWhiteSpace(SelectedTab.Header)
            ? "Current tab"
            : SelectedTab.Header;
        var sourceState = SelectedTab.ViewState;
        var updatedTabs = 0;

        foreach (var tab in Tabs)
        {
            var tabState = tab.ViewState;
            var visibilityChanged = tabState.IsDetailsPaneVisible != sourceState.IsDetailsPaneVisible;
            var widthChanged = Math.Abs(tabState.DetailsPaneWidth - sourceState.DetailsPaneWidth) > 0.01;

            tab.SetDetailsPaneVisibility(sourceState.IsDetailsPaneVisible);
            tab.SetDetailsPaneWidth(sourceState.DetailsPaneWidth);

            if (visibilityChanged || widthChanged)
            {
                updatedTabs++;
            }
        }

        var syncMessage = $"Synced pane from \"{sourceHeader}\" to {Tabs.Count} tab(s); updated {updatedTabs}.";
        PublishStatus(syncMessage);
        return syncMessage;
    }

    public async Task OpenItemAsync(FileItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsDirectory)
        {
            await NavigateToLocationAsync(item.FullPath, cancellationToken);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(item.FullPath)
            {
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            PublishStatus($"Unable to open {item.Name}: {ex.Message}");
        }
    }

    public void UpdateSelection(IEnumerable<FileItem> selectedItems)
    {
        SelectedTab?.UpdateSelection(selectedItems);
        UpdateStatusLine();
    }

    private async Task CaptureSelectionToClipboardAsync(ClipboardMode mode, CancellationToken cancellationToken)
    {
        if (SelectedTab is null || SelectedTab.SelectedItems.Count == 0)
        {
            PublishStatus("No items selected.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var selectedPaths = SelectedTab.SelectedItems
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedPaths.Count == 0)
        {
            PublishStatus("No items selected.");
            return;
        }

        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(selectedPaths);
        _clipboardMode = mode;

        await SyncSystemClipboardAsync(selectedPaths, mode, cancellationToken);

        var actionLabel = mode == ClipboardMode.Copy ? "Copied" : "Cut";
        PublishStatus($"{actionLabel} {selectedPaths.Count} item(s).");
    }

    public Task DeleteSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTab is null || SelectedTab.SelectedItems.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var item in SelectedTab.SelectedItems.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fileOperationService.Enqueue(new OperationRequest(
                FileOperationType.Delete,
                item.FullPath,
                DestinationPath: null,
                Overwrite: false,
                UseRecycleBin: true));
        }

        PublishStatus($"Queued delete for {SelectedTab.SelectedItems.Count} item(s).");
        return Task.CompletedTask;
    }

    public Task RenameSelectionAsync(string newName, CancellationToken cancellationToken = default)
    {
        if (SelectedTab is null || SelectedTab.SelectedItems.Count != 1)
        {
            return Task.CompletedTask;
        }

        var selected = SelectedTab.SelectedItems[0];
        var parent = Path.GetDirectoryName(selected.FullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return Task.CompletedTask;
        }

        var destination = Path.Combine(parent, newName);
        _fileOperationService.Enqueue(new OperationRequest(
            FileOperationType.Rename,
            selected.FullPath,
            destination,
            Overwrite: false,
            UseRecycleBin: false));

        PublishStatus($"Queued rename: {selected.Name} -> {newName}");
        return Task.CompletedTask;
    }

    public async Task CreateFolderAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTab is null || string.IsNullOrWhiteSpace(SelectedTab.CurrentPath))
        {
            return;
        }

        var basePath = SelectedTab.CurrentPath;
        var folderName = "New Folder";
        var candidate = Path.Combine(basePath, folderName);
        var index = 1;

        while (Directory.Exists(candidate))
        {
            index++;
            candidate = Path.Combine(basePath, $"{folderName} ({index})");
        }

        Directory.CreateDirectory(candidate);
        await SelectedTab.RefreshAsync(cancellationToken);
        PublishStatus($"Created folder: {Path.GetFileName(candidate)}");
    }

    public async Task PersistSessionAsync(WindowLayout windowLayout, CancellationToken cancellationToken = default)
    {
        var tabs = Tabs.Select(tab => tab.ToState()).ToList();
        var selectedIndex = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        var pinnedPaths = SidebarLocations
            .Where(location => location.IsPinned)
            .Select(location => location.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var session = new SessionState(
            SessionState.CurrentSchemaVersion,
            tabs,
            selectedIndex,
            windowLayout,
            pinnedPaths,
            DateTimeOffset.UtcNow);

        await _tabSessionService.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    partial void OnSelectedTabChanged(ExplorerTabViewModel? value)
    {
        UpdateSelectedTabFlags(value);
        UpdateStatusLine();
    }

    private void UpdateSelectedTabFlags(ExplorerTabViewModel? selectedTab)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = ReferenceEquals(tab, selectedTab);
        }
    }

    private ExplorerTabViewModel CreateTab()
    {
        var tab = new ExplorerTabViewModel(_fileSystemService, _searchService, _appSettingsService);
        tab.SelectionChanged += (_, _) => UpdateStatusLine();

        var defaults = _appSettingsService.Current.View;
        var defaultState = FolderViewState.Default
            .WithViewMode(defaults.DefaultViewMode);
        defaultState = defaultState with
        {
            SortColumn = defaults.DefaultSortColumn,
            SortDirection = defaults.DefaultSortDescending ? SortDirection.Descending : SortDirection.Ascending,
            GroupDirectoriesFirst = defaults.GroupByDirectory
        };
        tab.ViewState = defaultState;

        return tab;
    }

    private void SeedSidebar()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var downloads = Path.Combine(profile, "Downloads");

        var defaults = new List<SidebarLocation>
        {
            new("Home", profile, "\uE80F"),
            new("Desktop", desktop, "\uE7F1"),
            new("Documents", documents, "\uE8A5"),
            new("Downloads", downloads, "\uE896"),
            new("Music", music, "\uE8D6"),
            new("Pictures", pictures, "\uE91B")
        };

        var showDrives = _appSettingsService.Current.Sidebar.ShowDrives;
        var entries = (IEnumerable<SidebarLocation>)defaults;

        if (showDrives)
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new SidebarLocation(drive.Name, drive.RootDirectory.FullName, "\uE7C3"));
            entries = entries.Concat(drives);
        }

        SidebarLocations.ResetWith(entries.DistinctBy(location => location.Path));
    }

    private async void OnFileOperationCompleted(object? sender, OperationResult result)
    {
        if (_operationLookup.TryGetValue(result.OperationId, out var existing))
        {
            var nextState = result.Succeeded ? FileOperationState.Succeeded : FileOperationState.Failed;
            UpdateOperationEntry(existing with
            {
                State = nextState,
                ErrorMessage = result.ErrorMessage,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!result.Succeeded)
        {
            PublishStatus($"{result.Type} failed: {result.ErrorMessage}");
            return;
        }

        PublishStatus($"{result.Type} completed.");

        if (_uiContext is null)
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Refresh after operation failed: {ex}");
            }

            return;
        }

        _uiContext.Post(
            _ =>
            {
                _ = RefreshAfterOperationAsync();
            },
            null);
    }

    private async Task RefreshAfterOperationAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Refresh after operation failed: {ex}");
            PublishStatus($"Refresh after operation failed: {ex.Message}");
        }
    }

    private void UpdateStatusLine()
    {
        if (SelectedTab is null)
        {
            StatusLine = "Ready.";
            return;
        }

        var selectedCount = SelectedTab.SelectedItems.Count;
        var totalSize = SelectedTab.SelectedItems.Sum(item => item.SizeBytes ?? 0);
        var selectedSegment = selectedCount == 0
            ? "No file selected"
            : $"{selectedCount} selected ({FormatBytes(totalSize)})";

        var storageSegment = BuildStorageSegment(SelectedTab.CurrentPath);
        StatusLine = $"{selectedSegment}  |  {storageSegment}";
    }

    private static string BuildStorageSegment(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return "Storage: n/a";
            }

            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(candidate =>
                    candidate.IsReady &&
                    string.Equals(candidate.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase));

            if (drive is null)
            {
                return "Storage: n/a";
            }

            return $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}";
        }
        catch
        {
            return "Storage: n/a";
        }
    }

    private void PublishStatus(string message)
    {
        if (_uiContext is null)
        {
            StatusLine = message;
            return;
        }

        _uiContext.Post(_ => StatusLine = message, null);
    }

    private void ClearClipboard()
    {
        _clipboardMode = ClipboardMode.None;
        _clipboardPaths.Clear();
    }

    private async Task<ClipboardPayload?> ResolveClipboardPayloadAsync(CancellationToken cancellationToken)
    {
        if (HasClipboardItems)
        {
            return new ClipboardPayload(_clipboardMode, _clipboardPaths.ToList(), IsInternalSource: true);
        }

        return await TryReadSystemClipboardPayloadAsync(cancellationToken);
    }

    private static async Task<ClipboardPayload?> TryReadSystemClipboardPayloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clipboardData = Clipboard.GetContent();
            if (!clipboardData.Contains(StandardDataFormats.StorageItems))
            {
                return null;
            }

            var storageItems = await clipboardData.GetStorageItemsAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var paths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                return null;
            }

            var mode = clipboardData.RequestedOperation == DataPackageOperation.Move
                ? ClipboardMode.Cut
                : ClipboardMode.Copy;

            return new ClipboardPayload(mode, paths, IsInternalSource: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard read failed: {ex}");
            return null;
        }
    }

    private static async Task SyncSystemClipboardAsync(
        IReadOnlyCollection<string> selectedPaths,
        ClipboardMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var storageItems = await ResolveStorageItemsAsync(selectedPaths, cancellationToken);
            if (storageItems.Count == 0)
            {
                return;
            }

            var package = new DataPackage();
            package.SetStorageItems(storageItems);
            package.RequestedOperation = mode == ClipboardMode.Cut
                ? DataPackageOperation.Move
                : DataPackageOperation.Copy;

            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard sync failed: {ex}");
        }
    }

    private static async Task<List<IStorageItem>> ResolveStorageItemsAsync(
        IEnumerable<string> selectedPaths,
        CancellationToken cancellationToken)
    {
        var storageItems = new List<IStorageItem>();

        foreach (var path in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    continue;
                }

                if (File.Exists(path))
                {
                    storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to add storage item '{path}' to clipboard payload: {ex.Message}");
            }
        }

        return storageItems;
    }

    private async Task<(int QueuedCount, int SkippedCount)> QueueTransfersAsync(
        string destinationFolder,
        IEnumerable<string> sourcePaths,
        bool moveItems,
        CancellationToken cancellationToken)
    {
        var queuedCount = 0;
        var skippedCount = 0;

        foreach (var rawSourcePath in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = _fileSystemService.NormalizePath(rawSourcePath);
            var isDirectory = Directory.Exists(sourcePath);
            var isFile = File.Exists(sourcePath);

            if (!isDirectory && !isFile)
            {
                skippedCount++;
                continue;
            }

            if (moveItems)
            {
                var sourceParent = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceParent) && DirectoryPathEquals(sourceParent, destinationFolder))
                {
                    skippedCount++;
                    continue;
                }
            }

            if (isDirectory && IsDestinationWithinDirectory(sourcePath, destinationFolder))
            {
                skippedCount++;
                continue;
            }

            var destinationPath = BuildDestinationPath(destinationFolder, sourcePath, isDirectory);
            if (PathExists(destinationPath))
            {
                var resolution = await ResolveConflictAsync(
                    sourcePath,
                    destinationPath,
                    moveItems ? FileOperationType.Move : FileOperationType.Copy,
                    cancellationToken);

                if (resolution == FileConflictResolution.Skip)
                {
                    skippedCount++;
                    continue;
                }

                if (resolution == FileConflictResolution.KeepBoth)
                {
                    destinationPath = BuildUniqueDestinationPath(destinationPath, isDirectory);
                }
            }

            var operationType = moveItems ? FileOperationType.Move : FileOperationType.Copy;

            var operationId = _fileOperationService.Enqueue(new OperationRequest(
                operationType,
                sourcePath,
                destinationPath,
                Overwrite: true,
                UseRecycleBin: false));
            RegisterQueuedOperation(operationId, operationType, sourcePath, destinationPath);

            queuedCount++;
        }

        return (queuedCount, skippedCount);
    }

    private async Task<FileConflictResolution> ResolveConflictAsync(
        string sourcePath,
        string destinationPath,
        FileOperationType operationType,
        CancellationToken cancellationToken)
    {
        if (ResolveFileConflictAsync is null)
        {
            return FileConflictResolution.KeepBoth;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var prompt = new FileConflictPrompt(
            operationType,
            sourcePath,
            destinationPath,
            IsDirectory: Directory.Exists(sourcePath));

        return await ResolveFileConflictAsync(prompt);
    }

    private static string BuildDestinationPath(string destinationFolder, string sourcePath, bool isDirectory)
    {
        var destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
        if (!PathExists(destinationPath))
        {
            return destinationPath;
        }

        return BuildUniqueDestinationPath(destinationPath, isDirectory);
    }

    private static string BuildUniqueDestinationPath(string destinationPath, bool isDirectory)
    {
        var destinationFolder = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var fileName = Path.GetFileName(destinationPath);
        var baseName = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var extension = isDirectory ? string.Empty : Path.GetExtension(fileName);

        var candidate = Path.Combine(destinationFolder, $"{baseName} - Copy{extension}");
        var index = 2;

        while (PathExists(candidate))
        {
            candidate = Path.Combine(destinationFolder, $"{baseName} - Copy ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private static bool DirectoryPathEquals(string leftPath, string rightPath)
    {
        try
        {
            var leftNormalized = NormalizeDirectoryPath(leftPath);
            var rightNormalized = NormalizeDirectoryPath(rightPath);
            return string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsDestinationWithinDirectory(string sourceDirectoryPath, string destinationDirectoryPath)
    {
        try
        {
            var sourceNormalized = NormalizeDirectoryPath(sourceDirectoryPath) + Path.DirectorySeparatorChar;
            var destinationNormalized = NormalizeDirectoryPath(destinationDirectoryPath) + Path.DirectorySeparatorChar;
            return destinationNormalized.StartsWith(sourceNormalized, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private void OnFileOperationProgressChanged(object? sender, OperationProgress progress)
    {
        if (!_operationLookup.TryGetValue(progress.OperationId, out var existing))
        {
            return;
        }

        var nextState = progress.IsCompleted ? existing.State : FileOperationState.Running;
        UpdateOperationEntry(existing with
        {
            State = nextState,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void RegisterQueuedOperation(Guid operationId, FileOperationType type, string sourcePath, string destinationPath)
    {
        var entry = new FileOperationEntry(
            operationId,
            type,
            sourcePath,
            destinationPath,
            FileOperationState.Pending,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ErrorMessage: null);

        _operationLookup[operationId] = entry;
        PostToUi(() =>
        {
            FileOperationHistory.Insert(0, entry);
            TrimOperationHistory();
            RecomputeOperationCounters();
        });
    }

    private void UpdateOperationEntry(FileOperationEntry updated)
    {
        _operationLookup[updated.OperationId] = updated;
        PostToUi(() =>
        {
            var index = FileOperationHistory
                .Select((entry, idx) => (entry, idx))
                .FirstOrDefault(pair => pair.entry.OperationId == updated.OperationId)
                .idx;

            if (index >= 0 && index < FileOperationHistory.Count)
            {
                FileOperationHistory[index] = updated;
            }
            else
            {
                FileOperationHistory.Insert(0, updated);
            }

            TrimOperationHistory();
            RecomputeOperationCounters();
        });
    }

    private void TrimOperationHistory()
    {
        while (FileOperationHistory.Count > MaxOperationHistory)
        {
            var last = FileOperationHistory[^1];
            FileOperationHistory.RemoveAt(FileOperationHistory.Count - 1);
            _operationLookup.TryRemove(last.OperationId, out _);
        }
    }

    private void RecomputeOperationCounters()
    {
        PendingOperationsCount = FileOperationHistory.Count(entry => entry.State == FileOperationState.Pending);
        RunningOperationsCount = FileOperationHistory.Count(entry => entry.State == FileOperationState.Running);
        FailedOperationsCount = FileOperationHistory.Count(entry => entry.State == FileOperationState.Failed);
        OperationsSummary = $"Ops P:{PendingOperationsCount} R:{RunningOperationsCount} F:{FailedOperationsCount}";
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }
}

public enum FileOperationState
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public sealed record FileOperationEntry(
    Guid OperationId,
    FileOperationType Type,
    string SourcePath,
    string DestinationPath,
    FileOperationState State,
    DateTimeOffset QueuedAt,
    DateTimeOffset UpdatedAt,
    string? ErrorMessage);

public sealed record FileConflictPrompt(
    FileOperationType Type,
    string SourcePath,
    string DestinationPath,
    bool IsDirectory);
