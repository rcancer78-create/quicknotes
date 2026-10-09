using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Cross-service exclusive lock so sync, cleanup, and password rotation never overlap.
/// </summary>
public sealed class SyncCloudExclusiveLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> TryAcquireAsync(bool waitIfBusy, CancellationToken ct)
    {
        if (waitIfBusy)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }

        return await _gate.WaitAsync(0, ct).ConfigureAwait(false);
    }

    public Task AcquireAsync(CancellationToken ct) => _gate.WaitAsync(ct);

    public void Release() => _gate.Release();
}
