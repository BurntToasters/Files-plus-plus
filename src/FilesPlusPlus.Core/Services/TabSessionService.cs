using System.Text.Json;
using System.Diagnostics;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;
using FilesPlusPlus.Core.Utilities;

namespace FilesPlusPlus.Core.Services;

public sealed class TabSessionService : ITabSessionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionFilePath;
    private readonly string _defaultPath;

    public TabSessionService(string? sessionFilePath = null, string? defaultPath = null)
    {
        _sessionFilePath = sessionFilePath
                           ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "FilesPlusPlus",
                               "session-state.json");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fallback = Directory.Exists(documents)
            ? documents
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _defaultPath = defaultPath ?? PathUtilities.NormalizePath(fallback);
    }

    public async Task<SessionState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionFilePath))
        {
            return SessionState.CreateDefault(_defaultPath);
        }

        try
        {
            await using var stream = File.OpenRead(_sessionFilePath);
            var session = await JsonSerializer.DeserializeAsync<SessionState>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (session is null || session.SchemaVersion != SessionState.CurrentSchemaVersion || session.Tabs.Count == 0)
            {
                return SessionState.CreateDefault(_defaultPath);
            }

            return SanitizeSession(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load session state from '{_sessionFilePath}': {ex}");
            return SessionState.CreateDefault(_defaultPath);
        }
    }

    public async Task SaveAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state = SanitizeSession(state);

        var directory = Path.GetDirectoryName(_sessionFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_sessionFilePath);
        await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private SessionState SanitizeSession(SessionState state)
    {
        var tabs = state.Tabs
            .Where(tab => !string.IsNullOrWhiteSpace(tab.CurrentPath))
            .Select(tab => SanitizeTab(tab))
            .Where(tab => Directory.Exists(tab.CurrentPath))
            .ToList();

        if (tabs.Count == 0)
        {
            tabs.Add(TabState.CreateDefault(_defaultPath));
        }

        var selectedTabIndex = Math.Clamp(state.SelectedTabIndex, 0, tabs.Count - 1);
        var width = Math.Clamp(state.WindowLayout.Width, 900, 9000);
        var height = Math.Clamp(state.WindowLayout.Height, 600, 9000);
        var paneWidth = Math.Clamp(state.WindowLayout.DetailsPaneWidth, 250, 900);
        var windowLayout = new WindowLayout(width, height, state.WindowLayout.IsMaximized)
        {
            DetailsPaneWidth = paneWidth,
            IsDetailsPaneVisible = state.WindowLayout.IsDetailsPaneVisible
        };

        var pins = state.SidebarPins
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathUtilities.NormalizePath(path))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return state with
        {
            Tabs = tabs,
            SelectedTabIndex = selectedTabIndex,
            WindowLayout = windowLayout,
            SidebarPins = pins
        };
    }

    private TabState SanitizeTab(TabState tab)
    {
        var currentPath = PathUtilities.NormalizePath(tab.CurrentPath);
        var backHistory = tab.BackHistory
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathUtilities.NormalizePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        var forwardHistory = tab.ForwardHistory
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathUtilities.NormalizePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        return tab with
        {
            CurrentPath = currentPath,
            BackHistory = backHistory,
            ForwardHistory = forwardHistory
        };
    }
}
