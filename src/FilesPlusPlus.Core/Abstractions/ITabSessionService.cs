using FilesPlusPlus.Core.Models;

namespace FilesPlusPlus.Core.Abstractions;

public interface ITabSessionService
{
    Task<SessionState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionState state, CancellationToken cancellationToken = default);
}
