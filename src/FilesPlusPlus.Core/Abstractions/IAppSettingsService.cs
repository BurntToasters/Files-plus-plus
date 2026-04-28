using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Abstractions;

public interface IAppSettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<AppSettings> ResetAsync(CancellationToken cancellationToken = default);
}
