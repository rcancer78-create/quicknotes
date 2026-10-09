using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// In-process credentials that never touch DPAPI or the live profile.
/// Used only when a view-model is constructed without an injected store (tests).
/// </summary>
public sealed class EphemeralS3CredentialsStorage : IS3CredentialsStorage, IS3CredentialsProvider
{
    private S3Credentials? _credentials;

    public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default)
        => Task.FromResult(_credentials);

    public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default)
        => Task.FromResult(_credentials);

    public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default)
    {
        _credentials = credentials;
        return Task.CompletedTask;
    }

    public Task DeleteCredentialsAsync(CancellationToken ct = default)
    {
        _credentials = null;
        return Task.CompletedTask;
    }

    public bool HasCredentials()
        => _credentials != null && !string.IsNullOrWhiteSpace(_credentials.AccessKeyId);
}

/// <summary>
/// In-process sync password store that never touches DPAPI or the live profile.
/// </summary>
public sealed class EphemeralSyncPasswordStorage : ISyncPasswordStorage
{
    private string? _password;
    private string? _pending;

    public Task<string?> LoadPasswordAsync(CancellationToken ct = default)
        => Task.FromResult(_password);

    public Task SavePasswordAsync(string password, CancellationToken ct = default)
    {
        _password = password;
        return Task.CompletedTask;
    }

    public Task DeletePasswordAsync(CancellationToken ct = default)
    {
        _password = null;
        _pending = null;
        return Task.CompletedTask;
    }

    public bool HasPassword() => !string.IsNullOrEmpty(_password);

    public Task SavePendingPasswordAsync(string password, CancellationToken ct = default)
    {
        _pending = password;
        return Task.CompletedTask;
    }

    public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default)
        => Task.FromResult(_pending);

    public Task PromotePendingPasswordAsync(CancellationToken ct = default)
    {
        _password = _pending;
        _pending = null;
        return Task.CompletedTask;
    }

    public Task DeletePendingPasswordAsync(CancellationToken ct = default)
    {
        _pending = null;
        return Task.CompletedTask;
    }

    public bool HasPendingPassword() => !string.IsNullOrEmpty(_pending);
}
