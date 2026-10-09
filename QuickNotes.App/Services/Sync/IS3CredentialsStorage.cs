using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface IS3CredentialsStorage
{
    Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default);
    Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default);
    Task DeleteCredentialsAsync(CancellationToken ct = default);
    bool HasCredentials();
}

public interface IS3CredentialsProvider
{
    Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default);
}

public class FixedCredentialsProvider : IS3CredentialsProvider
{
    private readonly S3Credentials? _credentials;

    public FixedCredentialsProvider(S3Credentials? credentials)
    {
        _credentials = credentials;
    }

    public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_credentials);
    }
}

public class StorageCredentialsProvider : IS3CredentialsProvider
{
    private readonly IS3CredentialsStorage _storage;

    public StorageCredentialsProvider(IS3CredentialsStorage storage)
    {
        _storage = storage ?? throw new System.ArgumentNullException(nameof(storage));
    }

    public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default)
    {
        return _storage.LoadCredentialsAsync(ct);
    }
}
