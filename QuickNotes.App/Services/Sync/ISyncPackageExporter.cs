using System;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ISyncPackageExporter
{
    Task<SyncExportResult> ExportPackageAsync(QuickNotesDbContext db, string password, Guid? deviceId = null, Guid? packageId = null);
    Task<SyncExportResult> ExportToFileAsync(QuickNotesDbContext db, string filePath, string password, Guid? deviceId = null, Guid? packageId = null);
    SyncPackagePayload BuildDeterministicPayload(QuickNotesDbContext db, Guid deviceId, Guid? packageId = null);
}
