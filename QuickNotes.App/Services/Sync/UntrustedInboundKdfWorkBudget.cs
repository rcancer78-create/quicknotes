using System;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Per-sync-cycle cumulative PBKDF2 work budget for untrusted inbound cloud envelopes
/// (QNSP packages and QNBA attachment blobs). Local encrypt/push does not use this object.
///
/// Units are claimed PBKDF2 iteration counts. Reservation is overflow-safe and happens
/// before derivation. The per-envelope ceiling in <see cref="Crypto.KdfDescriptorLimits.CloudEnvelope"/>
/// is unchanged.
/// </summary>
public sealed class UntrustedInboundKdfWorkBudget
{
    /// <summary>
    /// Conservative default for one <c>RunSyncCycleAsync</c> pull.
    /// Documented normal 1 package + 100 attachments at production 100_000 is 10_100_000.
    /// 20_000_000 is ~2× that (200 production-N envelopes, or four envelopes at the 5_000_000 cap).
    /// Independent of experimental 720k/1.44m points. Production KDF defaults are unchanged.
    /// </summary>
    public const long DefaultCycleBudgetIterations = 20_000_000;

    public const string UserFacingMessage =
        "Синхронизация остановлена: превышен лимит вычислительной работы расшифровки входящих облачных объектов. Это не ошибка сети и не ошибка пароля.";

    public long Limit { get; }

    public long Consumed { get; private set; }

    public UntrustedInboundKdfWorkBudget(long limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Лимит KDF цикла должен быть положительным.");
        }

        Limit = limit;
    }

    /// <summary>
    /// Accounts claimed PBKDF2 iterations. Throws without mutating state when the next
    /// envelope would exceed the cycle limit or the addition overflows.
    /// </summary>
    public void Reserve(long claimedIterations)
    {
        if (claimedIterations < 0)
        {
            throw new SyncKdfWorkBudgetExceededException(UserFacingMessage);
        }

        if (claimedIterations == 0)
        {
            return;
        }

        if (!TryComputeNext(claimedIterations, out long next) || next > Limit)
        {
            throw new SyncKdfWorkBudgetExceededException(UserFacingMessage);
        }

        Consumed = next;
    }

    /// <summary>
    /// Fail-closed preview: does not consume. Used to reject a batch of blob envelopes
    /// before any of their PBKDF2 calls.
    /// </summary>
    public void ThrowIfWouldExceed(long additionalClaimedIterations)
    {
        if (additionalClaimedIterations < 0)
        {
            throw new SyncKdfWorkBudgetExceededException(UserFacingMessage);
        }

        if (additionalClaimedIterations == 0)
        {
            return;
        }

        if (!TryComputeNext(additionalClaimedIterations, out long next) || next > Limit)
        {
            throw new SyncKdfWorkBudgetExceededException(UserFacingMessage);
        }
    }

    private bool TryComputeNext(long additional, out long next)
    {
        next = 0;
        try
        {
            next = checked(Consumed + additional);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
