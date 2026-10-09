using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ISyncCloudCoordinator
{
    Task<CloudSyncConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);
    Task<CloudSyncUploadResult> UploadLatestPackageAsync(QuickNotesDbContext db, string encryptionPassword, CancellationToken ct = default);
    Task<CloudSyncDownloadResult> DownloadAndImportPackageAsync(string packageKey, QuickNotesDbContext db, string encryptionPassword, CancellationToken ct = default);
    Task<CloudSyncListResult> ListRemotePackagesAsync(CancellationToken ct = default);
}
