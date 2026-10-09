using System;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public interface ISyncPackageImporter
{
    Task<SyncImportResult> ImportPackageAsync(QuickNotesDbContext db, string packageJson, string password);

    Task<SyncImportResult> ImportPackageAsync(
        QuickNotesDbContext db,
        string packageJson,
        string password,
        Func<SyncPackagePayload, Task>? beforeApply,
        UntrustedInboundKdfWorkBudget? inboundKdfBudget = null);

    Task<SyncImportResult> ImportFromFileAsync(QuickNotesDbContext db, string filePath, string password);
}
