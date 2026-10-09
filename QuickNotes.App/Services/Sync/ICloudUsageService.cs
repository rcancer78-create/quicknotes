using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Service for calculating the occupied storage volume of the QuickNotes object prefix.
/// Paginates through objects within the designated prefix only, sums sizes with overflow protection,
/// respects safety page/object limits, caches results, and prevents concurrent calculations.
/// </summary>
public interface ICloudUsageService : IDisposable
{
    CloudUsageResult? CachedUsage { get; }
    bool IsCalculating { get; }

    Task<CloudUsageResult> CalculateUsageAsync(
        bool forceRefresh = false,
        CancellationToken ct = default);

    void InvalidateCache();
}
