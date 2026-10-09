using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ICloudRetentionService
{
    Task<CloudRetentionPreview> PreviewPackageRetentionAsync(CancellationToken ct = default);

    Task<CloudCleanupExecuteResult> ExecutePackageRetentionAsync(string previewToken, CancellationToken ct = default);

    Task<CloudRetentionPreview> PreviewOldGenerationAsync(CancellationToken ct = default);

    Task<CloudCleanupExecuteResult> ExecuteOldGenerationCleanupAsync(string previewToken, CancellationToken ct = default);
}
