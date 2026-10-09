using System;
using System.Collections.Generic;

namespace QuickNotes.App.Models;

public enum NoteAssemblySeparatorKind
{
    BlankLine = 0,
    ThematicBreakDash = 1,
    ThematicBreakStar = 2,
    Custom = 3
}

public enum NoteAssemblyTransactionPhase
{
    Validation = 0,
    InsertNote = 1,
    Tags = 2,
    History = 3,
    TrashSources = 4,
    Commit = 5
}

public sealed class NoteAssemblySourceSnapshot
{
    public int NoteId { get; init; }

    public int Order { get; init; }

    public DateTime UpdatedAt { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public bool IsProtected { get; init; }

    public bool IsDeleted { get; init; }

    public string DisplayTitle { get; init; } = string.Empty;
}

public sealed class NoteAssemblyTagDto
{
    public int TagId { get; init; }

    public string TagName { get; init; } = string.Empty;

    public TagOrigin Origin { get; init; }
}

public sealed class NoteAssemblyPreviewRequest
{
    public IReadOnlyList<int> SourceNoteIds { get; init; } = Array.Empty<int>();

    public string Title { get; init; } = string.Empty;

    public NoteAssemblySeparatorKind SeparatorKind { get; init; } = NoteAssemblySeparatorKind.ThematicBreakDash;

    public string? CustomSeparator { get; init; }
}

public sealed class NoteAssemblyPreviewResult
{
    public bool IsValid { get; init; }

    public string? BlockingReason { get; init; }

    public string Title { get; init; } = string.Empty;

    public string AssembledMarkdown { get; init; } = string.Empty;

    public string NormalizedSeparator { get; init; } = string.Empty;

    public NoteAssemblySeparatorKind SeparatorKind { get; init; }

    public IReadOnlyList<NoteAssemblySourceSnapshot> Sources { get; init; } = Array.Empty<NoteAssemblySourceSnapshot>();

    public IReadOnlyList<NoteAssemblyTagDto> ResultTags { get; init; } = Array.Empty<NoteAssemblyTagDto>();

    public string PlanHash { get; init; } = string.Empty;
}

public sealed class NoteAssemblyExecuteRequest
{
    public NoteAssemblyPreviewResult Preview { get; init; } = new();

    public bool MoveSourcesToTrash { get; init; }

    public bool TrashAcknowledged { get; init; }
}

public sealed class NoteAssemblyExecuteResult
{
    public bool Success { get; init; }

    public int CreatedNoteId { get; init; }

    public Guid CreatedSyncId { get; init; }

    public int SourceCount { get; init; }

    public int TrashedSourceCount { get; init; }

    public int TagCount { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public static NoteAssemblyExecuteResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
