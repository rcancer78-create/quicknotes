using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace QuickNotes.App.Models;

public class NoteTemplate
{
    public int Id { get; set; }

    public Guid SyncId { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ICollection<NoteTemplateTag> TemplateTags { get; set; } = new List<NoteTemplateTag>();

    [NotMapped]
    public string Name
    {
        get => Title;
        set => Title = value;
    }

    [NotMapped]
    public string Content
    {
        get => Text;
        set => Text = value;
    }
}
