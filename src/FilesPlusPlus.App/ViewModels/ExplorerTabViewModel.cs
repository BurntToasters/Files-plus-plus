using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FilesPlusPlus.App.Extensions;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.App.ViewModels;

public sealed partial class ExplorerTabViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ISearchService _searchService;
    private readonly IAppSettingsService? _appSettingsService;
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private List<FileItem> _folderItems = new();
    private List<FileItem> _searchItems = new();
    private int _busyOperations;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _navigationOperationCts;
    private CancellationTokenSource? _searchOperationCts;
    private CancellationTokenSource? _previewOperationCts;
    private bool _isDisposed;

    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".jfif", ".heic", ".heif", ".avif"
    };

    private static readonly HashSet<string> PreviewTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".yml", ".yaml", ".ini", ".cs", ".xaml", ".js", ".ts", ".ps1", ".psm1", ".psd1",
        ".html", ".htm", ".css", ".scss", ".sql", ".bat", ".cmd", ".toml", ".config", ".editorconfig", ".gitignore", ".gitattributes", ".env"
    };

    private static readonly HashSet<string> PreviewMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".ts", ".m2ts", ".mts", ".mpeg", ".mpg", ".3gp", ".3g2",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma"
    };

    private static readonly HashSet<string> PreviewPdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    private static readonly HashSet<string> PreviewOfficeOpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm",
        ".xlsx", ".xlsm", ".xltx", ".xltm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"
    };

    private const int MaxPreviewTextBytes = 256 * 1024;
    private const int MaxPreviewTextCharacters = 12_000;
    private const int MaxSniffBytes = 32 * 1024;
    private const int MaxHexPreviewBytes = 512;

    public ExplorerTabViewModel(IFileSystemService fileSystemService, ISearchService searchService)
        : this(fileSystemService, searchService, appSettingsService: null)
    {
    }

    public ExplorerTabViewModel(
        IFileSystemService fileSystemService,
        ISearchService searchService,
        IAppSettingsService? appSettingsService)
    {
        _fileSystemService = fileSystemService;
        _searchService = searchService;
        _appSettingsService = appSettingsService;
    }

    public ObservableCollection<FileItem> Items { get; } = new();

    public ObservableCollection<FileItem> SelectedItems { get; } = new();

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = new();

    [ObservableProperty]
    private string _header = "New Tab";

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private FolderViewState _viewState = FolderViewState.Default;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private FileItem? _primarySelection;

    [ObservableProperty]
    private string _selectionSummary = "No item selected";

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private string? _previewText;

    [ObservableProperty]
    private string? _previewImagePath;

    [ObservableProperty]
    private string? _previewMediaPath;

    [ObservableProperty]
    private string _previewMessage = "Select a file to preview.";

    [ObservableProperty]
    private bool _isSelected;

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public event EventHandler? SelectionChanged;

    public async Task RestoreFromStateAsync(TabState state, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(state);
        ViewState = state.ViewState;
        RestoreStack(_backHistory, state.BackHistory);
        RestoreStack(_forwardHistory, state.ForwardHistory);

        await NavigateToAsync(state.CurrentPath, pushCurrentToHistory: false, cancellationToken);

        if (!string.IsNullOrWhiteSpace(state.ViewState.SearchText))
        {
            await SearchAsync(state.ViewState.SearchText, cancellationToken);
        }
    }

    public TabState ToState() =>
        new(
            CurrentPath,
            ViewState,
            BackHistory: _backHistory.ToList(),
            ForwardHistory: _forwardHistory.ToList());

    public async Task NavigateToAsync(
        string path,
        bool pushCurrentToHistory = true,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        var normalizedPath = _fileSystemService.NormalizePath(path);
        if (!Directory.Exists(normalizedPath))
        {
            ErrorMessage = $"Directory not found: {normalizedPath}";
            return;
        }

        CancelOperation(ref _searchOperationCts);

        if (pushCurrentToHistory && !string.IsNullOrWhiteSpace(CurrentPath))
        {
            _backHistory.Push(CurrentPath);
            _forwardHistory.Clear();
        }

        CurrentPath = normalizedPath;
        Header = BuildHeader(normalizedPath);
        ViewState = ViewState.WithSearch(null);
        _searchItems.Clear();
        ErrorMessage = null;

        Breadcrumbs.ResetWith(PathUtilities.BuildBreadcrumb(normalizedPath));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        await LoadFolderAsync(cancellationToken);
    }

    public async Task NavigateBackAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!CanGoBack)
        {
            return;
        }

        var destination = _backHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            _forwardHistory.Push(CurrentPath);
        }

        await NavigateToAsync(destination, pushCurrentToHistory: false, cancellationToken);
    }

    public async Task NavigateForwardAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!CanGoForward)
        {
            return;
        }

        var destination = _forwardHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            _backHistory.Push(CurrentPath);
        }

        await NavigateToAsync(destination, pushCurrentToHistory: false, cancellationToken);
    }

    public async Task NavigateUpAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        var parent = Directory.GetParent(CurrentPath);
        if (parent is null)
        {
            return;
        }

        await NavigateToAsync(parent.FullName, pushCurrentToHistory: true, cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        await LoadFolderAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(ViewState.SearchText))
        {
            await SearchAsync(ViewState.SearchText, cancellationToken);
        }
    }

    public async Task SearchAsync(string? searchText, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        ViewState = ViewState.WithSearch(searchText);
        _searchItems.Clear();

        if (string.IsNullOrWhiteSpace(ViewState.SearchText))
        {
            CancelOperation(ref _searchOperationCts);
            ApplyCurrentView();
            return;
        }

        var searchToken = ReplaceOperationToken(ref _searchOperationCts, cancellationToken);

        BeginBusy();
        try
        {
            var searchPath = CurrentPath;
            var query = new SearchQuery(searchPath, ViewState.SearchText, MaxResults: 300);
            var searchResults = await Task.Run(
                async () =>
                {
                    var items = new List<FileItem>();

                    await foreach (var result in _searchService.SearchAsync(query, searchToken).ConfigureAwait(false))
                    {
                        var stat = await _fileSystemService.StatAsync(result.FullPath, searchToken).ConfigureAwait(false);
                        if (stat is not null)
                        {
                            items.Add(stat);
                        }
                    }

                    return items;
                },
                searchToken);

            if (searchToken.IsCancellationRequested)
            {
                return;
            }

            _searchItems = searchResults;
            ApplyCurrentView();
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            // Why: A newer search request supersedes the current one.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    public void ToggleSort(SortColumn sortColumn)
    {
        if (_isDisposed)
        {
            return;
        }

        ViewState = ViewState.ToggleSort(sortColumn);
        ApplyCurrentView();
    }

    public void ToggleDirectoryGrouping()
    {
        if (_isDisposed)
        {
            return;
        }

        ViewState = ViewState with { GroupDirectoriesFirst = !ViewState.GroupDirectoriesFirst };
        ApplyCurrentView();
    }

    public void SetViewMode(FolderViewMode viewMode)
    {
        if (_isDisposed || ViewState.ViewMode == viewMode)
        {
            return;
        }

        ViewState = ViewState.WithViewMode(viewMode);
    }

    public void SetDetailsPaneVisibility(bool isVisible)
    {
        if (_isDisposed || ViewState.IsDetailsPaneVisible == isVisible)
        {
            return;
        }

        ViewState = ViewState.WithDetailsPaneVisibility(isVisible);
    }

    public void SetDetailsPaneWidth(double width)
    {
        if (_isDisposed || width <= 0)
        {
            return;
        }

        if (Math.Abs(ViewState.DetailsPaneWidth - width) < 0.5)
        {
            return;
        }

        ViewState = ViewState.WithDetailsPaneWidth(width);
    }

    public void UpdateSelection(IEnumerable<FileItem> selectedItems)
    {
        if (_isDisposed)
        {
            return;
        }

        SelectedItems.ResetWith(selectedItems);
        PrimarySelection = SelectedItems.Count == 0 ? null : SelectedItems[0];
        SelectionSummary = BuildSelectionSummary();

        _ = RefreshPreviewAsync();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadFolderAsync(CancellationToken cancellationToken)
    {
        var navigationToken = ReplaceOperationToken(ref _navigationOperationCts, cancellationToken);

        BeginBusy();

        try
        {
            var folderPath = CurrentPath;
            var freshItems = await Task.Run(
                async () =>
                {
                    var items = new List<FileItem>();

                    var enumerationOptions = BuildEnumerationOptions();
                    await foreach (var item in _fileSystemService.EnumerateItemsAsync(folderPath, enumerationOptions, navigationToken).ConfigureAwait(false))
                    {
                        items.Add(item);
                    }

                    return items;
                },
                navigationToken);

            if (navigationToken.IsCancellationRequested)
            {
                return;
            }

            _folderItems = freshItems;
            ApplyCurrentView();
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            // Why: Navigation refresh cancels earlier folder enumeration requests.
        }
        catch (Exception ex)
        {
            Items.Clear();
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private void BeginBusy()
    {
        _busyOperations++;
        IsBusy = true;
    }

    private void EndBusy()
    {
        if (_busyOperations > 0)
        {
            _busyOperations--;
        }

        IsBusy = _busyOperations > 0;
    }

    private void ApplyCurrentView()
    {
        var source = string.IsNullOrWhiteSpace(ViewState.SearchText)
            ? _folderItems
            : _searchItems;

        var sorted = FileItemSorter.Sort(source, ViewState);
        Items.ResetWith(sorted);

        // Why: Selection objects from prior result sets are invalid after source replacement.
        UpdateSelection(Array.Empty<FileItem>());
    }

    private static void RestoreStack(Stack<string> target, List<string> sourceTopFirst)
    {
        target.Clear();

        for (var i = sourceTopFirst.Count - 1; i >= 0; i--)
        {
            target.Push(sourceTopFirst[i]);
        }
    }

    private static string BuildHeader(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    public void Dispose()
    {
        CancellationTokenSource? navigationCts;
        CancellationTokenSource? searchCts;
        CancellationTokenSource? previewCts;

        lock (_operationGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            navigationCts = _navigationOperationCts;
            searchCts = _searchOperationCts;
            previewCts = _previewOperationCts;
            _navigationOperationCts = null;
            _searchOperationCts = null;
            _previewOperationCts = null;
        }

        if (navigationCts is not null)
        {
            navigationCts.Cancel();
            navigationCts.Dispose();
        }

        if (searchCts is not null)
        {
            searchCts.Cancel();
            searchCts.Dispose();
        }

        if (previewCts is not null)
        {
            previewCts.Cancel();
            previewCts.Dispose();
        }

        SelectionChanged = null;
    }

    private CancellationToken ReplaceOperationToken(ref CancellationTokenSource? operationSource, CancellationToken cancellationToken)
    {
        var next = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        CancellationTokenSource? previous;

        lock (_operationGate)
        {
            if (_isDisposed)
            {
                next.Cancel();
                return next.Token;
            }

            previous = operationSource;
            operationSource = next;
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return next.Token;
    }

    private void CancelOperation(ref CancellationTokenSource? operationSource)
    {
        CancellationTokenSource? existing;

        lock (_operationGate)
        {
            existing = operationSource;
            operationSource = null;
        }

        if (existing is not null)
        {
            existing.Cancel();
            existing.Dispose();
        }
    }

    private string BuildSelectionSummary()
    {
        if (SelectedItems.Count == 0)
        {
            return "No item selected";
        }

        if (SelectedItems.Count == 1)
        {
            var item = SelectedItems[0];
            if (item.IsDirectory)
            {
                return "1 folder selected";
            }

            return string.IsNullOrWhiteSpace(item.SizeDisplay)
                ? "1 file selected"
                : $"1 file selected ({item.SizeDisplay})";
        }

        var fileCount = SelectedItems.Count(item => !item.IsDirectory);
        var folderCount = SelectedItems.Count - fileCount;

        var parts = new List<string>();
        if (folderCount > 0)
        {
            parts.Add(folderCount == 1 ? "1 folder" : $"{folderCount} folders");
        }

        if (fileCount > 0)
        {
            parts.Add(fileCount == 1 ? "1 file" : $"{fileCount} files");
        }

        return $"{SelectedItems.Count} selected ({string.Join(", ", parts)})";
    }

    private async Task RefreshPreviewAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        var selectedItem = PrimarySelection;
        if (selectedItem is null)
        {
            CancelOperation(ref _previewOperationCts);
            IsPreviewLoading = false;
            ResetPreviewContent();
            PreviewMessage = "Select a file to preview.";
            return;
        }

        if (selectedItem.IsDirectory)
        {
            CancelOperation(ref _previewOperationCts);
            IsPreviewLoading = false;
            ResetPreviewContent();
            PreviewMessage = "Folder preview is not available.";
            return;
        }

        var extension = Path.GetExtension(selectedItem.Name);

        if (PreviewImageExtensions.Contains(extension))
        {
            CancelOperation(ref _previewOperationCts);
            IsPreviewLoading = false;
            PreviewText = null;
            PreviewMediaPath = null;
            PreviewImagePath = selectedItem.FullPath;
            PreviewMessage = "Image preview";
            return;
        }

        if (PreviewMediaExtensions.Contains(extension))
        {
            CancelOperation(ref _previewOperationCts);
            IsPreviewLoading = false;
            PreviewImagePath = null;
            PreviewText = null;
            PreviewMediaPath = selectedItem.FullPath;
            PreviewMessage = "Media preview";
            return;
        }

        if (LooksLikeMediaContainer(selectedItem.FullPath))
        {
            CancelOperation(ref _previewOperationCts);
            IsPreviewLoading = false;
            PreviewImagePath = null;
            PreviewText = null;
            PreviewMediaPath = selectedItem.FullPath;
            PreviewMessage = "Media preview (detected by file signature)";
            return;
        }

        var previewToken = ReplaceOperationToken(ref _previewOperationCts, CancellationToken.None);
        IsPreviewLoading = true;
        PreviewImagePath = null;
        PreviewText = null;
        PreviewMediaPath = null;
        PreviewMessage = "Loading preview...";

        try
        {
            var fileInfo = new FileInfo(selectedItem.FullPath);
            if (!fileInfo.Exists)
            {
                PreviewMessage = "File no longer exists.";
                return;
            }

            var extensionForPreview = extension ?? string.Empty;
            var previewResult = await Task.Run(
                () => BuildFallbackPreview(selectedItem.FullPath, extensionForPreview, fileInfo.Length, previewToken),
                previewToken);
            if (previewToken.IsCancellationRequested)
            {
                return;
            }

            PreviewText = previewResult.PreviewText;
            PreviewMessage = previewResult.PreviewMessage;
        }
        catch (OperationCanceledException)
        {
            // Why: Preview work is intentionally canceled when selection changes.
        }
        catch (Exception ex)
        {
            PreviewText = null;
            PreviewMessage = $"Preview failed: {ex.Message}";
        }
        finally
        {
            if (!previewToken.IsCancellationRequested)
            {
                IsPreviewLoading = false;
            }
        }
    }

    private void ResetPreviewContent()
    {
        PreviewImagePath = null;
        PreviewText = null;
        PreviewMediaPath = null;
    }

    private static (string PreviewText, string PreviewMessage) BuildFallbackPreview(
        string path,
        string extension,
        long fileSize,
        CancellationToken cancellationToken)
    {
        if (PreviewTextExtensions.Contains(extension))
        {
            return (ReadPreviewText(path, cancellationToken), "Text preview");
        }

        if (PreviewPdfExtensions.Contains(extension))
        {
            return (BuildPdfFallbackPreview(path, fileSize, cancellationToken), "PDF fallback preview");
        }

        if (PreviewOfficeOpenXmlExtensions.Contains(extension))
        {
            try
            {
                return (BuildOfficeOpenXmlFallbackPreview(path, fileSize, cancellationToken), "Office fallback preview");
            }
            catch (InvalidDataException)
            {
                return (BuildHexFallbackPreview(path, fileSize, cancellationToken), "Binary fallback (hex preview)");
            }
        }

        if (TryBuildSmartTextPreview(path, cancellationToken, out var detectedText))
        {
            return (detectedText, "Detected text preview");
        }

        return (BuildHexFallbackPreview(path, fileSize, cancellationToken), "Binary fallback (hex preview)");
    }

    private static string ReadPreviewText(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[Math.Min(MaxPreviewTextCharacters, 4096)];
        var builder = new StringBuilder();
        var totalCharacters = 0;

        while (!reader.EndOfStream && totalCharacters < MaxPreviewTextCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var charactersToRead = Math.Min(buffer.Length, MaxPreviewTextCharacters - totalCharacters);
            var read = reader.Read(buffer, 0, charactersToRead);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
            totalCharacters += read;
        }

        if (!reader.EndOfStream)
        {
            builder.AppendLine();
            builder.Append("... (preview truncated)");
        }

        return builder.ToString();
    }

    private static string BuildPdfFallbackPreview(string path, long fileSize, CancellationToken cancellationToken)
    {
        var bytes = ReadPrefixBytes(path, MaxSniffBytes, cancellationToken);
        var ascii = Encoding.ASCII.GetString(bytes);
        var builder = new StringBuilder();

        builder.AppendLine("PDF quick summary");
        builder.AppendLine($"Size: {FormatBytes(fileSize)}");

        if (TryGetPdfVersion(ascii) is { } version)
        {
            builder.AppendLine($"Version: {version}");
        }

        AppendPdfMetadataValue(builder, ascii, "Title");
        AppendPdfMetadataValue(builder, ascii, "Author");
        AppendPdfMetadataValue(builder, ascii, "Subject");

        if (TryExtractReadableSnippet(ascii, 420) is { } snippet)
        {
            builder.AppendLine();
            builder.AppendLine("Readable snippet:");
            builder.AppendLine(snippet);
        }
        else
        {
            builder.AppendLine();
            builder.Append("No readable text found near the start of the file.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildOfficeOpenXmlFallbackPreview(string path, long fileSize, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        cancellationToken.ThrowIfCancellationRequested();

        var builder = new StringBuilder();
        var kind = GetOfficePackageKind(archive);
        builder.AppendLine($"{kind} package summary");
        builder.AppendLine($"Size: {FormatBytes(fileSize)}");
        builder.AppendLine($"Archive entries: {archive.Entries.Count}");

        if (TryReadZipEntryText(archive, "docProps/core.xml", MaxSniffBytes, cancellationToken, out var coreXml))
        {
            AppendCoreMetadata(builder, coreXml);
        }

        if (kind == "Word" &&
            TryReadZipEntryText(archive, "word/document.xml", MaxPreviewTextBytes, cancellationToken, out var wordXml))
        {
            var wordSnippet = ExtractWordSnippet(wordXml, 420);
            if (!string.IsNullOrWhiteSpace(wordSnippet))
            {
                builder.AppendLine();
                builder.AppendLine("Document text snippet:");
                builder.AppendLine(wordSnippet);
            }
        }

        if (kind == "Excel" &&
            TryReadZipEntryText(archive, "xl/workbook.xml", MaxSniffBytes, cancellationToken, out var workbookXml))
        {
            var sheetNames = ExtractSheetNames(workbookXml);
            if (sheetNames.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Sheets:");
                builder.AppendLine(string.Join(", ", sheetNames));
            }
        }

        if (kind == "PowerPoint")
        {
            var slideCount = archive.Entries.Count(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            builder.AppendLine($"Slides: {slideCount}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildHexFallbackPreview(string path, long fileSize, CancellationToken cancellationToken)
    {
        var bytes = ReadPrefixBytes(path, MaxHexPreviewBytes, cancellationToken);
        var builder = new StringBuilder();

        builder.AppendLine("Binary fallback preview");
        builder.AppendLine($"Size: {FormatBytes(fileSize)}");
        builder.AppendLine($"Signature: {DescribeSignature(bytes)}");
        builder.AppendLine();
        builder.AppendLine("Hex dump:");
        builder.Append(BuildHexDump(bytes));

        return builder.ToString().TrimEnd();
    }

    private static bool TryBuildSmartTextPreview(string path, CancellationToken cancellationToken, out string preview)
    {
        preview = string.Empty;
        var bytes = ReadPrefixBytes(path, MaxSniffBytes, cancellationToken);
        if (bytes.Length == 0 || !LooksLikeText(bytes))
        {
            return false;
        }

        preview = ReadPreviewText(path, cancellationToken);
        return !string.IsNullOrWhiteSpace(preview);
    }

    private static bool LooksLikeMediaContainer(string path)
    {
        try
        {
            var bytes = ReadPrefixBytes(path, 64, CancellationToken.None);
            if (bytes.Length < 4)
            {
                return false;
            }

            // Why: Container signatures cover media files whose extensions are missing or misleading.
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
            {
                return true;
            }

            if (bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
            {
                return true;
            }

            if (bytes[0] == 0x4F && bytes[1] == 0x67 && bytes[2] == 0x67 && bytes[3] == 0x53)
            {
                return true;
            }

            if (bytes.Length >= 12 &&
                bytes[4] == 0x66 &&
                bytes[5] == 0x74 &&
                bytes[6] == 0x79 &&
                bytes[7] == 0x70)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadPrefixBytes(string path, int maxBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, maxBytes);
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = stream.Read(buffer, totalRead, length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead == buffer.Length)
        {
            return buffer;
        }

        return buffer[..totalRead];
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return true;
        }

        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        var disallowedControlCount = 0;
        foreach (var value in bytes)
        {
            if (value == 0x00)
            {
                return false;
            }

            if (value < 0x09 || (value > 0x0D && value < 0x20))
            {
                disallowedControlCount++;
            }
        }

        return disallowedControlCount * 100 <= bytes.Length * 3;
    }

    private static string BuildHexDump(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return "(empty file)";
        }

        var builder = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var sliceLength = Math.Min(16, bytes.Length - offset);
            var slice = bytes.Slice(offset, sliceLength);

            var hex = string.Join(" ", slice.ToArray().Select(value => value.ToString("X2")));
            var paddedHex = hex.PadRight(16 * 3 - 1);

            var asciiChars = new char[sliceLength];
            for (var i = 0; i < sliceLength; i++)
            {
                var value = slice[i];
                asciiChars[i] = value is >= 32 and <= 126 ? (char)value : '.';
            }

            builder.AppendLine($"{offset:X8}  {paddedHex}  |{new string(asciiChars)}|");
        }

        return builder.ToString().TrimEnd();
    }

    private static string DescribeSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 5 &&
            bytes[0] == 0x25 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x44 &&
            bytes[3] == 0x46 &&
            bytes[4] == 0x2D)
        {
            return "PDF";
        }

        if (bytes.Length >= 4 &&
            bytes[0] == 0x50 &&
            bytes[1] == 0x4B &&
            bytes[2] == 0x03 &&
            bytes[3] == 0x04)
        {
            return "ZIP container (often Office Open XML)";
        }

        if (bytes.Length >= 4 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47)
        {
            return "PNG image";
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return "JPEG image";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46 &&
            bytes[3] == 0x38)
        {
            return "GIF image";
        }

        return "Unknown";
    }

    private static string? TryGetPdfVersion(string ascii)
    {
        var markerIndex = ascii.IndexOf("%PDF-", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + 5;
        var end = ascii.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
        {
            end = Math.Min(start + 8, ascii.Length);
        }

        if (end <= start)
        {
            return null;
        }

        var version = ascii[start..end].Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static void AppendPdfMetadataValue(StringBuilder builder, string ascii, string key)
    {
        var token = $"/{key} (";
        var start = ascii.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        start += token.Length;
        var end = ascii.IndexOf(')', start);
        if (end <= start)
        {
            return;
        }

        var rawValue = ascii[start..end];
        var value = NormalizePreviewText(rawValue, 180);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine($"{key}: {value}");
    }

    private static string? TryExtractReadableSnippet(string ascii, int maxCharacters)
    {
        var best = string.Empty;
        var current = new StringBuilder();

        foreach (var ch in ascii)
        {
            if (ch is >= ' ' and <= '~')
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > best.Length)
            {
                best = current.ToString();
            }

            current.Clear();
        }

        if (current.Length > best.Length)
        {
            best = current.ToString();
        }

        best = NormalizePreviewText(best, maxCharacters);
        return best.Length >= 28 ? best : null;
    }

    private static string GetOfficePackageKind(ZipArchive archive)
    {
        var hasWord = archive.Entries.Any(entry => entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase));
        if (hasWord)
        {
            return "Word";
        }

        var hasExcel = archive.Entries.Any(entry => entry.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase));
        if (hasExcel)
        {
            return "Excel";
        }

        var hasPowerPoint = archive.Entries.Any(entry => entry.FullName.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase));
        if (hasPowerPoint)
        {
            return "PowerPoint";
        }

        return "Office";
    }

    private static bool TryReadZipEntryText(
        ZipArchive archive,
        string entryName,
        int maxChars,
        CancellationToken cancellationToken,
        out string text)
    {
        text = string.Empty;
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(4096, maxChars)];
        var builder = new StringBuilder();
        var totalChars = 0;

        while (!reader.EndOfStream && totalChars < maxChars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var charsToRead = Math.Min(buffer.Length, maxChars - totalChars);
            var read = reader.Read(buffer, 0, charsToRead);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
            totalChars += read;
        }

        text = builder.ToString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static void AppendCoreMetadata(StringBuilder builder, string coreXml)
    {
        try
        {
            var xml = XDocument.Parse(coreXml);
            var values = xml.Root?
                .Elements()
                .Where(element => !string.IsNullOrWhiteSpace(element.Value))
                .Select(element => (element.Name.LocalName, Value: NormalizePreviewText(element.Value, 160)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToDictionary(item => item.LocalName, item => item.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AppendMetadataLine(builder, values, "title", "Title");
            AppendMetadataLine(builder, values, "creator", "Author");
            AppendMetadataLine(builder, values, "description", "Description");
            AppendMetadataLine(builder, values, "subject", "Subject");
        }
        catch
        {
            // Why: Metadata extraction should degrade gracefully for malformed archives.
        }
    }

    private static void AppendMetadataLine(
        StringBuilder builder,
        IReadOnlyDictionary<string, string> values,
        string key,
        string label)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine($"{label}: {value}");
    }

    private static string ExtractWordSnippet(string wordXml, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(wordXml))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        var index = 0;

        while (index < wordXml.Length)
        {
            var startTag = wordXml.IndexOf("<w:t", index, StringComparison.OrdinalIgnoreCase);
            if (startTag < 0)
            {
                break;
            }

            var contentStart = wordXml.IndexOf('>', startTag);
            if (contentStart < 0)
            {
                break;
            }

            var endTag = wordXml.IndexOf("</w:t>", contentStart, StringComparison.OrdinalIgnoreCase);
            if (endTag < 0)
            {
                break;
            }

            var rawValue = wordXml.Substring(contentStart + 1, endTag - contentStart - 1);
            var decoded = WebUtility.HtmlDecode(rawValue);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                segments.Add(decoded);
            }

            if (segments.Count >= 200)
            {
                break;
            }

            index = endTag + 6;
        }

        return JoinSnippet(segments, maxCharacters);
    }

    private static List<string> ExtractSheetNames(string workbookXml)
    {
        try
        {
            var xml = XDocument.Parse(workbookXml);
            var names = xml
                .Descendants()
                .Where(element => element.Name.LocalName == "sheet")
                .Select(element => element.Attribute("name")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Take(8)
                .ToList();

            return names;
        }
        catch
        {
            return [];
        }
    }

    private static string JoinSnippet(IEnumerable<string> segments, int maxCharacters)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (builder.Length >= maxCharacters)
            {
                break;
            }

            var normalized = NormalizePreviewText(segment, maxCharacters);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var remaining = maxCharacters - builder.Length;
            if (normalized.Length > remaining)
            {
                builder.Append(normalized[..remaining]);
                break;
            }

            builder.Append(normalized);
        }

        var joined = builder.ToString().Trim();
        if (joined.Length == 0)
        {
            return string.Empty;
        }

        return joined.Length < maxCharacters ? joined : $"{joined}...";
    }

    private static string NormalizePreviewText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength]}...";
    }

    private FileEnumerationOptions BuildEnumerationOptions()
    {
        var current = _appSettingsService?.Current;
        if (current is null)
        {
            return FileEnumerationOptions.Default;
        }

        return new FileEnumerationOptions(current.View.ShowHiddenFiles, current.View.ShowFileExtensions);
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
}
