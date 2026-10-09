using System.Collections.Generic;

namespace QuickNotes.App.Models;

public class Tag
{
    public int Id { get; set; }

    public Guid SyncId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int? ParentTagId { get; set; }
    public Tag? ParentTag { get; set; }

    public ICollection<Tag> Children { get; set; } = new List<Tag>();
    public ICollection<TagSynonym> Synonyms { get; set; } = new List<TagSynonym>();
    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
    public ICollection<NoteTemplateTag> TemplateTags { get; set; } = new List<NoteTemplateTag>();
}
