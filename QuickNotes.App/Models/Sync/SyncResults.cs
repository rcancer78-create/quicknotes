using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class SyncConflict
{
    public Guid SyncId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? LocalRevisionId { get; set; }
    public Guid IncomingRevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public SyncConflict() { }

    public SyncConflict(Guid syncId, string entityType, Guid? localRevisionId, Guid incomingRevisionId, Guid? parentRevisionId, string reason)
    {
        SyncId = syncId;
        EntityType = entityType;
        LocalRevisionId = localRevisionId;
        IncomingRevisionId = incomingRevisionId;
        ParentRevisionId = parentRevisionId;
        Reason = reason;
    }
}

public class SyncEntityCounts
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
}

public class SyncImportResult
{
    public bool Success { get; set; }
    public Guid? PackageId { get; set; }
    public Guid? SourceDeviceId { get; set; }

    public SyncEntityCounts Notes { get; set; } = new();
    public SyncEntityCounts Tags { get; set; } = new();
    public SyncEntityCounts Templates { get; set; } = new();
    public SyncEntityCounts Attachments { get; set; } = new();

    public int TotalCreated => Notes.Created + Tags.Created + Templates.Created + Attachments.Created;
    public int TotalUpdated => Notes.Updated + Tags.Updated + Templates.Updated + Attachments.Updated;
    public int TotalDeleted => Notes.Deleted + Tags.Deleted + Templates.Deleted + Attachments.Deleted;
    public int TotalSkipped => Notes.Skipped + Tags.Skipped + Templates.Skipped + Attachments.Skipped;

    public List<SyncConflict> Conflicts { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();

    public static SyncImportResult Failure(string errorMessage)
    {
        var res = new SyncImportResult { Success = false };
        res.Errors.Add(errorMessage);
        return res;
    }
}

public class SyncExportResult
{
    public bool Success { get; set; }
    public Guid PackageId { get; set; }
    public int ExportedNotesCount { get; set; }
    public int ExportedTagsCount { get; set; }
    public int ExportedTemplatesCount { get; set; }
    public int ExportedAttachmentsCount { get; set; }
    public long PackageBytes { get; set; }
    public string? PackageJson { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();

    public static SyncExportResult Failure(string errorMessage)
    {
        var res = new SyncExportResult { Success = false };
        res.Errors.Add(errorMessage);
        return res;
    }
}
