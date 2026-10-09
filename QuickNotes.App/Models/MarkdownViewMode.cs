namespace QuickNotes.App.Models;

/// <summary>
/// Defines the display mode for markdown notes in detail and standalone editors.
/// </summary>
public enum MarkdownViewMode
{
    /// <summary>Plain markdown text editor only.</summary>
    Edit = 0,

    /// <summary>Split view with side-by-side markdown editor and live FlowDocument preview.</summary>
    Split = 1,

    /// <summary>Rendered FlowDocument preview only.</summary>
    Preview = 2
}
