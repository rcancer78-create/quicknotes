namespace QuickNotes.App.Models;

public sealed class ClipboardCaptureAttempt
{
    public CapturedNoteContext? Context { get; init; }
    public string? FailureReason { get; init; }
    public bool Success => Context != null && !string.IsNullOrWhiteSpace(Context.Text);
}
