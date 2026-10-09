using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.Models;

/// <summary>
/// Versioned export package format for JSON export/import.
/// </summary>
public class QuickNotesExportPackage
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = "1.0";
    public List<ExportTagDto> Tags { get; set; } = new();
    public List<ExportNoteDto> Notes { get; set; } = new();
}

public class ExportTagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid SyncId { get; set; }
    public int? ParentTagId { get; set; }
    public string? ParentTagName { get; set; }
    public List<string> Synonyms { get; set; } = new();
}

public class ExportNoteDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsInbox { get; set; }
    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAt { get; set; }
    public List<ExportNoteTagDto> Tags { get; set; } = new();
}

public class ExportNoteTagDto
{
    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;
    public TagOrigin Origin { get; set; } = TagOrigin.Auto;
    public bool IsSuppressed { get; set; }
}

public class ImportPreviewResult
{
    public int TotalFilesDiscovered { get; set; }
    public int TotalNotesToImport { get; set; }
    public int TotalSkippedOrErroneous { get; set; }
    public int TotalConflicts { get; set; }
    public int NewTagsCount { get; set; }
    public string ConflictPolicyDescription { get; set; } = "Политика при конфликтах: пропускать существующие заметки (без перезаписи)";
    public List<ImportItemPreview> Items { get; set; } = new();
    public List<ImportDiagnosticItem> Diagnostics { get; set; } = new();
    public List<ExportTagDto> PackageTags { get; set; } = new();
    public List<ImportFolderMappingEntry> FolderMappings { get; set; } = new();
    public string? ImportRootPath { get; set; }
    public string PlanHash { get; set; } = string.Empty;
    public string? ArchivePath { get; set; }
    public bool IsPortableArchive { get; set; }
    public bool LossesAcknowledged { get; set; }
    public bool HasBlockingDiagnostics => Diagnostics.Exists(d => d.IsBlocking);
    public bool HasNonBlockingLosses =>
        Items.Exists(i => i.HasLosses) || Diagnostics.Exists(d => d.IsLoss && !d.IsBlocking);
    public bool CanConfirm =>
        TotalNotesToImport > 0
        && !HasBlockingDiagnostics
        && (!HasNonBlockingLosses || LossesAcknowledged);
}

public enum ImportDuplicateKind
{
    None = 0,
    ExactSource = 1,
    IdenticalContent = 2,
    OriginalId = 3,
    SameTitle = 4
}

public enum ImportDuplicateAction
{
    Skip = 0,
    ImportSeparate = 1,
    Replace = 2
}

public class ImportFolderMappingEntry : INotifyPropertyChanged
{
    private string _proposedTagName = string.Empty;

    public string RelativeFolder { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? ParentTagName { get; set; }
    public int Depth { get; set; }
    public bool IsAmbiguous { get; set; }

    public string ProposedTagName
    {
        get => _proposedTagName;
        set
        {
            if (_proposedTagName == value)
            {
                return;
            }

            _proposedTagName = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ImportItemPreview : INotifyPropertyChanged
{
    private ImportDuplicateAction _duplicateAction = ImportDuplicateAction.Skip;
    private string _status = "Готова к импорту";

    public string SourceFileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceRelativePath { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public int? OriginalId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string DisplayTitle => Helpers.NoteTitleHelper.GetDisplayTitle(Title, Text);
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsInbox { get; set; }
    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAt { get; set; }
    public List<ExportNoteTagDto> Tags { get; set; } = new();
    public List<ArchiveAttachmentEntry> ArchiveAttachments { get; set; } = new();
    public Guid? SyncId { get; set; }
    public bool IsConflict { get; set; }
    public string ConflictReason { get; set; } = string.Empty;
    public ImportDuplicateKind DuplicateKind { get; set; }
    public bool CanReplace { get; set; }
    public int? MatchingNoteId { get; set; }
    public bool HasAmbiguousFolderMapping { get; set; }
    public bool HasBlockingIssue { get; set; }
    public string ProposedRootTag { get; set; } = string.Empty;
    public string ProposedSectionTag { get; set; } = string.Empty;
    public List<string> LostElements { get; set; } = new();
    public List<string> AttachmentIssues { get; set; } = new();
    public List<ImportLocalAttachment> PendingAttachments { get; set; } = new();
    public bool HasLosses => LostElements.Count > 0 || AttachmentIssues.Count > 0 || HasAmbiguousFolderMapping;
    public string LostElementsDisplay => LostElements.Count == 0 && AttachmentIssues.Count == 0
        ? "—"
        : string.Join("; ", LostElements.Concat(AttachmentIssues));
    public string MappingDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProposedSectionTag))
            {
                return string.IsNullOrWhiteSpace(ProposedRootTag) ? "—" : ProposedRootTag;
            }

            return ProposedRootTag + " / " + ProposedSectionTag;
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public ImportDuplicateAction DuplicateAction
    {
        get => _duplicateAction;
        set
        {
            if (_duplicateAction == value)
            {
                return;
            }

            _duplicateAction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuplicateActionLabel));
        }
    }

    public string DuplicateActionLabel => DuplicateAction switch
    {
        ImportDuplicateAction.ImportSeparate => "Импортировать отдельно",
        ImportDuplicateAction.Replace => "Заменить существующую",
        _ => "Пропустить"
    };

    public string TagsDisplay => Tags.Count == 0
        ? "—"
        : string.Join(", ", Enumerable.Select(Tags, t => t.TagName));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ImportLocalAttachment
{
    public string SourceFullPath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string MarkdownPlaceholder { get; set; } = string.Empty;
}

public class ImportDiagnosticItem
{
    public string FileName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool IsBlocking { get; set; }
    public bool IsLoss { get; set; }
}

public class ImportExecutionResult
{
    public bool Success { get; set; }
    public int ImportedNotesCount { get; set; }
    public int CreatedTagsCount { get; set; }
    public int ReplacedNotesCount { get; set; }
    public int SkippedNotesCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public enum ImportTransactionPhase
{
    Parse = 0,
    Validation = 1,
    Database = 2,
    History = 3,
    Attachment = 4,
    Publish = 5
}

public class ExportResult
{
    public bool Success { get; set; }
    public int ExportedNotesCount { get; set; }
    public int ExportedTagCount { get; set; }
    public int ExportedAttachmentCount { get; set; }
    public int SkippedProtectedCount { get; set; }
    public int SkippedMissingAttachmentCount { get; set; }
    public int ErrorCount { get; set; }
    public int FormatVersion { get; set; }
    public DateTime? ExportedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public string? CompletenessSummary { get; set; }
    public string? ExportPath { get; set; }
    public string? ErrorMessage { get; set; }
}

public class OpenExportManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FormatName = "quicknotes-open-export";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Format { get; set; } = FormatName;
    public DateTime ExportedAtUtc { get; set; }
    public OpenExportEntityCounts EntityCounts { get; set; } = new();
    public int SkippedProtectedNotes { get; set; }
    public int SkippedMissingAttachments { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<OpenExportFileChecksum> Files { get; set; } = new();
}

public class OpenExportEntityCounts
{
    public int Notes { get; set; }
    public int Tags { get; set; }
    public int Attachments { get; set; }
    public int Files { get; set; }
}

public class OpenExportFileChecksum
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
