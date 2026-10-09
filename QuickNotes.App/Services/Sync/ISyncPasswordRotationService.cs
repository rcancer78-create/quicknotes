using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ISyncPasswordRotationService
{
    Task<PasswordRotationPreview> PreviewAsync(string oldPassword, CancellationToken ct = default);

    Task<PasswordRotationExecuteResult> ExecuteAsync(
        string previewToken,
        string oldPassword,
        string newPassword,
        IProgress<PasswordRotationProgress>? progress = null,
        CancellationToken ct = default);
}
