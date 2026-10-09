using System.Collections.Generic;
using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public sealed class ImportDuplicateChoice
{
    public ImportDuplicateAction Action { get; init; }
    public string Label { get; init; } = string.Empty;

    public static IReadOnlyList<ImportDuplicateChoice> All { get; } = new[]
    {
        new ImportDuplicateChoice { Action = ImportDuplicateAction.Skip, Label = "Пропустить" },
        new ImportDuplicateChoice { Action = ImportDuplicateAction.ImportSeparate, Label = "Импортировать отдельно" },
        new ImportDuplicateChoice { Action = ImportDuplicateAction.Replace, Label = "Заменить существующую" }
    };

    public static IReadOnlyList<ImportDuplicateChoice> WithoutReplace { get; } = new[]
    {
        All[0],
        All[1]
    };
}
