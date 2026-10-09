namespace QuickNotes.App.Models;

public class NoteTemplateResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public NoteTemplate? Template { get; init; }

    public static NoteTemplateResult Ok(NoteTemplate template) =>
        new() { Success = true, Template = template };

    public static NoteTemplateResult Fail(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };
}
