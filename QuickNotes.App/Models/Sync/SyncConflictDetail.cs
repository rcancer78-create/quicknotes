using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models.Sync;

public class SyncConflictResolutionResult
{
    public bool Success { get; set; }
    public int ConflictId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    public static SyncConflictResolutionResult Succeeded(int conflictId, string action) =>
        new() { Success = true, ConflictId = conflictId, Action = action };

    public static SyncConflictResolutionResult Failure(int conflictId, string errorMessage) =>
        new() { Success = false, ConflictId = conflictId, ErrorMessage = errorMessage };
}

public class SyncConflictDetail
{
    public int ConflictId { get; set; }
    public Guid SyncId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public DateTime DetectedAtUtc { get; set; }
    public Guid SourceDeviceId { get; set; }
    public Guid? SourcePackageId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsResolved { get; set; }

    public Guid LocalDeviceId { get; set; }
    public Guid RemoteDeviceId { get; set; }
    public string LocalDeviceDisplay { get; set; } = "устройство неизвестно";
    public string RemoteDeviceDisplay { get; set; } = "устройство неизвестно";
    public DateTime? LocalUpdatedAtUtc { get; set; }
    public string LocalUpdatedDisplay { get; set; } = "время версии неизвестно";
    public string RemoteUpdatedDisplay { get; set; } = "время версии неизвестно";
    public string ReasonDisplay { get; set; } = string.Empty;

    // Local snapshot info
    public string? LocalTitle { get; set; }
    public string? LocalText { get; set; }
    public bool? LocalIsPinned { get; set; }
    public bool? LocalIsFavorite { get; set; }
    public bool? LocalIsInbox { get; set; }
    public bool LocalIsDeleted { get; set; }
    public bool LocalIsProtected { get; set; }
    public List<string> LocalTags { get; set; } = new();
    public List<string> LocalAttachmentNames { get; set; } = new();
    public string AttachmentsRuleDisplay { get; set; } = "Вложения этой заметки не входят в конфликт заметки и разрешаются отдельно.";
    public bool IsLocalCorrupted { get; set; }
    public string? LocalCorruptionError { get; set; }
    public string LocalProtectionNotice { get; set; } = string.Empty;

    // Remote snapshot info
    public string? RemoteTitle { get; set; }
    public string? RemoteText { get; set; }
    public bool? RemoteIsPinned { get; set; }
    public bool? RemoteIsFavorite { get; set; }
    public bool? RemoteIsInbox { get; set; }
    public bool RemoteIsDeleted { get; set; }
    public bool RemoteIsProtected { get; set; }
    public List<string> RemoteTags { get; set; } = new();
    public DateTime? RemoteUpdatedAtUtc { get; set; }
    public bool IsRemoteCorrupted { get; set; }
    public string? RemoteCorruptionError { get; set; }
    public string RemoteProtectionNotice { get; set; } = string.Empty;

    // Initial merged text draft for note merge
    public string InitialMergedText { get; set; } = string.Empty;
    public string MergeMetadataRuleDisplay { get; set; } =
        "Объединение меняет только Markdown. Заголовок и теги выбираются явно (локальные или облачные), без объединения списков. Заметка остаётся активной; удаление принимается через «Оставить локальную» или «Оставить облачную». Вложения не смешиваются.";

    public bool CanMerge =>
        EntityType == "Note"
        && !IsRemoteCorrupted
        && !IsLocalCorrupted
        && !LocalIsProtected
        && !RemoteIsProtected
        && !RemoteIsDeleted;

    public bool CanKeepBoth =>
        EntityType != "NoteAttachment"
        && !IsRemoteCorrupted
        && !RemoteIsDeleted;
}

public enum SyncConflictFieldChoice
{
    Unspecified = 0,
    Local = 1,
    Remote = 2
}

public sealed class SyncConflictMergeChoices
{
    public static readonly SyncConflictMergeChoices KeepLocalMetadata = new()
    {
        Title = SyncConflictFieldChoice.Local,
        Tags = SyncConflictFieldChoice.Local
    };

    public SyncConflictFieldChoice Title { get; init; } = SyncConflictFieldChoice.Local;
    public SyncConflictFieldChoice Tags { get; init; } = SyncConflictFieldChoice.Local;
}
