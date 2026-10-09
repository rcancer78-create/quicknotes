using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ICloudCleanupService
{
    Task<CloudCleanupPreview> PreviewAsync(string encryptionPassword, CancellationToken ct = default);

    Task<CloudCleanupExecuteResult> ExecuteAsync(
        string previewToken,
        string encryptionPassword,
        CancellationToken ct = default);
}
