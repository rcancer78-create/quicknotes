using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Secure storage for the end-to-end cloud sync encryption password.
/// Stored strictly separated from SQLite database, AppSettings, logs, and export payloads.
/// </summary>
public interface ISyncPasswordStorage
{
    Task<string?> LoadPasswordAsync(CancellationToken ct = default);
    Task SavePasswordAsync(string password, CancellationToken ct = default);
    Task DeletePasswordAsync(CancellationToken ct = default);
    bool HasPassword();

    /// <summary>DPAPI slot for a new password before generation switch. Does not replace the active password.</summary>
    Task SavePendingPasswordAsync(string password, CancellationToken ct = default);
    Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default);
    /// <summary>Atomically replace the active password with the pending slot and clear pending.</summary>
    Task PromotePendingPasswordAsync(CancellationToken ct = default);
    Task DeletePendingPasswordAsync(CancellationToken ct = default);
    bool HasPendingPassword();
}

public interface ISyncPasswordProvider
{
    Task<string?> GetPasswordAsync(CancellationToken ct = default);
}
