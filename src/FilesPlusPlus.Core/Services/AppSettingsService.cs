using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilesPlusPlus.Core.Abstractions;
using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Services;

public sealed class AppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsFilePath;
    private AppSettings _current = AppSettings.CreateDefault();

    public AppSettingsService(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath
                           ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "FilesPlusPlus",
                               "settings.json");
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            _current = AppSettings.CreateDefault();
            RaiseChanged();
            return _current;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (settings is null || settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                _current = AppSettings.CreateDefault();
            }
            else
            {
                _current = settings;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load app settings from '{_settingsFilePath}': {ex}");
            _current = AppSettings.CreateDefault();
        }

        RaiseChanged();
        return _current;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = File.Create(_settingsFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        _current = settings;
        RaiseChanged();
    }

    public async Task<AppSettings> ResetAsync(CancellationToken cancellationToken = default)
    {
        var defaults = AppSettings.CreateDefault();
        await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
        return defaults;
    }

    private void RaiseChanged() => Changed?.Invoke(this, _current);
}
