using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Manual retention of historic full-snapshot packages and retired password-rotation generations.
/// Never enables bucket versioning. Deletes only after an explicit preview token.
/// Latest pointer package and pending crash-recovery objects are never candidates.
/// </summary>
public class CloudRetentionService : ICloudRetentionService
{
    public const int KeepLatestPackagesPerDevice = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ICloudObjectStoreTransport _transport;
    private readonly SyncObjectKeyHelper _keyHelper;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CloudRetentionPreview? _packagePreview;
    private Dictionary<string, CloudCleanupCandidate>? _packageSnapshot;
    private CloudRetentionPreview? _generationPreview;
    private Dictionary<string, CloudCleanupCandidate>? _generationSnapshot;

    public CloudRetentionService(ICloudObjectStoreTransport transport, SyncObjectKeyHelper keyHelper)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _keyHelper = keyHelper ?? throw new ArgumentNullException(nameof(keyHelper));
    }

    public async Task<CloudRetentionPreview> PreviewPackageRetentionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gen = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
            if (!gen.Success)
            {
                return Blocked($"Не удалось прочитать поколение: {gen.Error}");
            }

            var candidates = new List<CloudCleanupCandidate>();
            var snapshot = new Dictionary<string, CloudCleanupCandidate>(StringComparer.Ordinal);
            var devicesPrefix = _keyHelper.GetDevicesPrefix();
            var listed = await ListAllAsync(devicesPrefix, ct).ConfigureAwait(false);
            if (listed == null)
            {
                return Blocked("Неполное перечисление объектов — очистка пакетов не выполняется.");
            }

            var byDevice = listed
                .Where(o => _keyHelper.TryParsePackageKey(o.Key, out _, out _))
                .Select(o =>
                {
                    _keyHelper.TryParsePackageKey(o.Key, out var deviceId, out var packageId);
                    return (Obj: o, DeviceId: deviceId, PackageId: packageId);
                })
                .GroupBy(x => x.DeviceId);

            foreach (var group in byDevice)
            {
                ct.ThrowIfCancellationRequested();
                string pointerKey = _keyHelper.GetDevicePointerKey(group.Key);
                var pointerObj = await _transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);
                Guid latest = Guid.Empty;
                if (pointerObj?.Content != null)
                {
                    var pointer = JsonSerializer.Deserialize<DevicePointerPayload>(pointerObj.Content, JsonOptions);
                    latest = pointer?.LatestPackageId ?? Guid.Empty;
                }

                foreach (var item in group)
                {
                    if (item.PackageId == latest && latest != Guid.Empty)
                    {
                        continue;
                    }

                    var candidate = new CloudCleanupCandidate
                    {
                        Key = item.Obj.Key,
                        ETag = item.Obj.ETag ?? string.Empty,
                        Size = item.Obj.Size,
                        Reason = latest == Guid.Empty
                            ? "Пакет не является текущим снимком устройства."
                            : "Устаревший полный снимок: актуальный пакет указан в pointer.json."
                    };
                    candidates.Add(candidate);
                    snapshot[candidate.Key] = candidate;
                }
            }

            var preview = new CloudRetentionPreview
            {
                Token = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                IsComplete = true,
                Candidates = candidates,
                ReclaimableBytes = candidates.Sum(c => c.Size),
                Summary = candidates.Count == 0
                    ? "Лишних пакетов нет: хранится только актуальный снимок каждого устройства."
                    : $"Можно удалить {candidates.Count} устаревших пакетов (~{FormatBytes(candidates.Sum(c => c.Size))}). Tombstone остаются в актуальном снимке."
            };
            _packagePreview = preview;
            _packageSnapshot = snapshot;
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CloudCleanupExecuteResult> ExecutePackageRetentionAsync(string previewToken, CancellationToken ct = default)
        => ExecuteAsync(previewToken, () => _packagePreview, () => _packageSnapshot, ct);

    public async Task<CloudRetentionPreview> PreviewOldGenerationAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gen = await SyncGenerationResolver.ResolveAndApplyAsync(_transport, _keyHelper, ct).ConfigureAwait(false);
            if (!gen.Success)
            {
                return Blocked($"Не удалось прочитать поколение: {gen.Error}", generation: true);
            }

            var previous = gen.Pointer.PreviousGenerationId;
            if (!previous.HasValue || previous.Value == Guid.Empty)
            {
                var empty = new CloudRetentionPreview
                {
                    Token = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow,
                    IsComplete = true,
                    IsGenerationCleanup = true,
                    Summary = "Старого поколения после ротации пароля нет."
                };
                _generationPreview = empty;
                _generationSnapshot = new Dictionary<string, CloudCleanupCandidate>(StringComparer.Ordinal);
                return empty;
            }

            if (previous.Value == gen.Pointer.ActiveGenerationId)
            {
                return Blocked("Предыдущее поколение совпадает с активным — удаление запрещено.", generation: true);
            }

            var oldHelper = _keyHelper.ForGeneration(previous.Value);
            var listed = await ListAllAsync(oldHelper.VersionedPrefix, ct).ConfigureAwait(false);
            if (listed == null)
            {
                return Blocked("Неполное перечисление старого поколения — удаление не выполняется.", generation: true);
            }

            var candidates = new List<CloudCleanupCandidate>();
            var snapshot = new Dictionary<string, CloudCleanupCandidate>(StringComparer.Ordinal);
            foreach (var obj in listed)
            {
                if (obj.Key.Equals(_keyHelper.GetGenerationPointerKey(), StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = new CloudCleanupCandidate
                {
                    Key = obj.Key,
                    ETag = obj.ETag ?? string.Empty,
                    Size = obj.Size,
                    Reason = "Объект принадлежит предыдущему поколению шифрования и недостижим для текущего пароля."
                };
                candidates.Add(candidate);
                snapshot[candidate.Key] = candidate;
            }

            var preview = new CloudRetentionPreview
            {
                Token = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                IsComplete = true,
                IsGenerationCleanup = true,
                Candidates = candidates,
                ReclaimableBytes = candidates.Sum(c => c.Size),
                Summary = candidates.Count == 0
                    ? "В старом поколении нет объектов для удаления."
                    : $"Старое поколение недоступно текущему паролю. Можно освободить ~{FormatBytes(candidates.Sum(c => c.Size))} ({candidates.Count} объектов). Удаление необратимо."
            };
            _generationPreview = preview;
            _generationSnapshot = snapshot;
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CloudCleanupExecuteResult> ExecuteOldGenerationCleanupAsync(string previewToken, CancellationToken ct = default)
        => ExecuteAsync(previewToken, () => _generationPreview, () => _generationSnapshot, ct);

    private async Task<CloudCleanupExecuteResult> ExecuteAsync(
        string previewToken,
        Func<CloudRetentionPreview?> getPreview,
        Func<Dictionary<string, CloudCleanupCandidate>?> getSnapshot,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var preview = getPreview();
            var snapshot = getSnapshot();
            if (preview == null || snapshot == null || preview.Token != previewToken)
            {
                return new CloudCleanupExecuteResult
                {
                    TokenAccepted = false,
                    Summary = "Предварительный просмотр устарел. Повторите просмотр, затем подтвердите удаление."
                };
            }

            int deleted = 0, skipped = 0, failed = 0;
            long bytes = 0;
            var items = new List<CloudCleanupItemResult>();
            foreach (var candidate in preview.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var head = await _transport.HeadObjectAsync(candidate.Key, ct).ConfigureAwait(false);
                    if (head == null)
                    {
                        skipped++;
                        items.Add(new CloudCleanupItemResult { Key = candidate.Key, Outcome = "skipped", Detail = "Объект уже отсутствует." });
                        continue;
                    }

                    if (!string.IsNullOrEmpty(candidate.ETag) &&
                        !string.Equals(StorageObjectMetadata.NormalizeETag(head.ETag), StorageObjectMetadata.NormalizeETag(candidate.ETag), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        items.Add(new CloudCleanupItemResult { Key = candidate.Key, Outcome = "skipped", Detail = "ETag изменился." });
                        continue;
                    }

                    bool ok = await _transport.DeleteObjectAsync(candidate.Key, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        deleted++;
                        bytes += candidate.Size;
                        items.Add(new CloudCleanupItemResult { Key = candidate.Key, Outcome = "deleted" });
                    }
                    else
                    {
                        failed++;
                        items.Add(new CloudCleanupItemResult { Key = candidate.Key, Outcome = "failed" });
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    items.Add(new CloudCleanupItemResult { Key = candidate.Key, Outcome = "failed", Detail = ErrorLogService.Sanitize(ex.Message) });
                }
            }

            return new CloudCleanupExecuteResult
            {
                TokenAccepted = true,
                Deleted = deleted,
                Skipped = skipped,
                Failed = failed,
                DeletedBytes = bytes,
                Items = items,
                Summary = $"Удалено: {deleted}, пропущено: {skipped}, ошибок: {failed}. Освобождено примерно {FormatBytes(bytes)}."
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<StorageObjectSummary>?> ListAllAsync(string prefix, CancellationToken ct)
    {
        var all = new List<StorageObjectSummary>();
        string? token = null;
        int pages = 0;
        do
        {
            var page = await _transport.ListObjectsV2Async(new StorageListRequest
            {
                Prefix = prefix,
                MaxKeys = 1000,
                ContinuationToken = token
            }, ct).ConfigureAwait(false);
            if (page.IsTruncated && string.IsNullOrEmpty(page.NextContinuationToken) && (page.Objects == null || page.Objects.Count == 0))
            {
                return null;
            }

            if (page.Objects != null)
            {
                all.AddRange(page.Objects);
            }

            token = page.NextContinuationToken;
            pages++;
            if (pages > CloudUsageService.DefaultMaxPages || all.Count > CloudUsageService.DefaultMaxObjects)
            {
                return null;
            }
        } while (!string.IsNullOrEmpty(token));

        return all;
    }

    private static CloudRetentionPreview Blocked(string reason, bool generation = false) => new()
    {
        IsComplete = false,
        BlockReason = reason,
        IsGenerationCleanup = generation,
        Summary = reason
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes / (1024.0 * 1024.0):0.#} МБ";
    }
}
