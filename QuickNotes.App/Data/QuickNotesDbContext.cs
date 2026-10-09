using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Data;

public class QuickNotesDbContext : DbContext
{
    private readonly string? _dbPath;

    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagSynonym> TagSynonyms => Set<TagSynonym>();
    public DbSet<NoteTag> NoteTags => Set<NoteTag>();
    public DbSet<NoteRevision> NoteRevisions => Set<NoteRevision>();
    public DbSet<NoteAttachment> NoteAttachments => Set<NoteAttachment>();
    public DbSet<NoteTemplate> NoteTemplates => Set<NoteTemplate>();
    public DbSet<NoteTemplateTag> NoteTemplateTags => Set<NoteTemplateTag>();
    public DbSet<SyncEntityState> SyncEntityStates => Set<SyncEntityState>();
    public DbSet<SyncDeviceState> SyncDeviceStates => Set<SyncDeviceState>();
    public DbSet<SyncLocalState> SyncLocalStates => Set<SyncLocalState>();
    public DbSet<SyncConflictRecord> SyncConflicts => Set<SyncConflictRecord>();

    public string DbPath => _dbPath ?? GetDefaultDbPath();

    public QuickNotesDbContext()
    {
    }

    public QuickNotesDbContext(DbContextOptions<QuickNotesDbContext> options)
        : base(options)
    {
    }

    public QuickNotesDbContext(DbContextOptions<QuickNotesDbContext> options, string dbPath)
        : base(options)
    {
        _dbPath = dbPath;
    }

    public QuickNotesDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    private static string? _profileDirectoryOverride;

    public static string? ProfileDirectoryOverride
    {
        get => _profileDirectoryOverride;
        set
        {
            if (value != null)
            {
                ValidateIsolatedProfilePath(value);
            }
            _profileDirectoryOverride = value;
        }
    }

    public static string LiveProfileDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickNotes"));

    public static void ValidateIsolatedProfilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Isolated profile path cannot be empty.", nameof(path));

        string full = IsolatedProfilePath.Normalize(path);
        string live = IsolatedProfilePath.Normalize(LiveProfileDirectory);

        RejectIfRelatedToLive(full, live);

        string resolved = IsolatedProfilePath.ResolveExistingChain(full);
        string resolvedLive = Directory.Exists(live) || File.Exists(live)
            ? IsolatedProfilePath.ResolveExistingChain(live)
            : live;
        RejectIfRelatedToLive(resolved, resolvedLive);
    }

    private static void RejectIfRelatedToLive(string candidate, string live)
    {
        if (IsolatedProfilePath.IsSameOrRelated(candidate, live))
        {
            throw new InvalidOperationException(
                $"Isolated profile must not point to the live profile directory '{live}' (including via a junction or symlink).");
        }
    }

    public static string GetDefaultProfileDirectory()
        => _profileDirectoryOverride ?? LiveProfileDirectory;

    public static string GetDefaultDbPath()
    {
        var dir = GetDefaultProfileDirectory();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "quicknotes.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var path = _dbPath ?? GetDefaultDbPath();
            optionsBuilder.UseSqlite($"Data Source={path};Pooling=False");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // NoteTag composite key
        modelBuilder.Entity<NoteTag>()
            .HasKey(nt => new { nt.NoteId, nt.TagId });

        modelBuilder.Entity<NoteTag>()
            .HasOne(nt => nt.Note)
            .WithMany(n => n.NoteTags)
            .HasForeignKey(nt => nt.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteTag>()
            .HasOne(nt => nt.Tag)
            .WithMany(t => t.NoteTags)
            .HasForeignKey(nt => nt.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tag hierarchy
        modelBuilder.Entity<Tag>()
            .HasOne(t => t.ParentTag)
            .WithMany(t => t.Children)
            .HasForeignKey(t => t.ParentTagId)
            .OnDelete(DeleteBehavior.Restrict);

        // Tag synonyms
        modelBuilder.Entity<TagSynonym>()
            .HasOne(ts => ts.Tag)
            .WithMany(t => t.Synonyms)
            .HasForeignKey(ts => ts.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes and property defaults for performance
        modelBuilder.Entity<Note>()
            .Property(n => n.Title)
            .HasDefaultValue(string.Empty);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.CreatedAt);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.IsPinned);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.IsFavorite);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.IsInbox);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.DeletedAt);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.CapturedAt);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.SourceProcessName);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.IsProtected);
        modelBuilder.Entity<Note>()
            .HasIndex(n => n.ImportSourceFingerprint);

        modelBuilder.Entity<Tag>()
            .HasIndex(t => t.Name);

        // Note revisions
        modelBuilder.Entity<NoteRevision>()
            .Property(nr => nr.Title)
            .HasDefaultValue(string.Empty);
        modelBuilder.Entity<NoteRevision>()
            .HasOne(nr => nr.Note)
            .WithMany(n => n.Revisions)
            .HasForeignKey(nr => nr.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteRevision>()
            .HasIndex(nr => nr.NoteId);

        modelBuilder.Entity<NoteRevision>()
            .HasIndex(nr => nr.CreatedAt);

        // Note attachments
        modelBuilder.Entity<NoteAttachment>()
            .HasOne(a => a.Note)
            .WithMany(n => n.Attachments)
            .HasForeignKey(a => a.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteAttachment>()
            .HasIndex(a => a.NoteId);

        modelBuilder.Entity<NoteAttachment>()
            .HasIndex(a => a.Sha256);

        modelBuilder.Entity<NoteAttachment>()
            .HasIndex(a => a.StoredFileName);

        // Note templates
        modelBuilder.Entity<NoteTemplateTag>()
            .HasKey(tt => new { tt.TemplateId, tt.TagId });

        modelBuilder.Entity<NoteTemplateTag>()
            .HasOne(tt => tt.Template)
            .WithMany(t => t.TemplateTags)
            .HasForeignKey(tt => tt.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteTemplateTag>()
            .HasOne(tt => tt.Tag)
            .WithMany(t => t.TemplateTags)
            .HasForeignKey(tt => tt.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteTemplate>(b =>
        {
            b.Property(t => t.Title)
                .UseCollation("NOCASE");

            b.HasIndex(t => t.Title)
                .IsUnique();

            b.HasIndex(t => t.CreatedAt);
            b.HasIndex(t => t.UpdatedAt);
        });

        modelBuilder.Entity<NoteTemplateTag>()
            .HasIndex(tt => tt.TagId);

        // SyncId unique constraints/indexes and protection nullability
        modelBuilder.Entity<Note>(b =>
        {
            b.Property(n => n.SyncId).UseCollation("NOCASE");
            b.HasIndex(n => n.SyncId).IsUnique();
            b.Property(n => n.ProtectedSaltBase64).IsRequired(false);
            b.Property(n => n.ProtectedNonceBase64).IsRequired(false);
            b.Property(n => n.ProtectedTagBase64).IsRequired(false);
            b.Property(n => n.ProtectedCiphertextBase64).IsRequired(false);
        });

        modelBuilder.Entity<NoteRevision>(b =>
        {
            b.Property(nr => nr.ProtectedSaltBase64).IsRequired(false);
            b.Property(nr => nr.ProtectedNonceBase64).IsRequired(false);
            b.Property(nr => nr.ProtectedTagBase64).IsRequired(false);
            b.Property(nr => nr.ProtectedCiphertextBase64).IsRequired(false);
        });

        modelBuilder.Entity<Tag>(b =>
        {
            b.Property(t => t.SyncId).UseCollation("NOCASE");
            b.HasIndex(t => t.SyncId).IsUnique();
        });

        modelBuilder.Entity<NoteTemplate>(b =>
        {
            b.Property(nt => nt.SyncId).UseCollation("NOCASE");
            b.HasIndex(nt => nt.SyncId).IsUnique();
        });

        modelBuilder.Entity<NoteAttachment>(b =>
        {
            b.Property(na => na.SyncId).UseCollation("NOCASE");
            b.HasIndex(na => na.SyncId).IsUnique();
            b.Property(na => na.ProtectedSaltBase64).IsRequired(false);
            b.Property(na => na.ProtectedNonceBase64).IsRequired(false);
            b.Property(na => na.ProtectedTagBase64).IsRequired(false);
            b.Property(na => na.ProtectedCiphertextBase64).IsRequired(false);
        });

        modelBuilder.Entity<SyncEntityState>(b =>
        {
            b.HasKey(s => s.SyncId);
            b.Property(s => s.SyncId).UseCollation("NOCASE");
            b.HasIndex(s => s.EntityType);
            b.HasIndex(s => s.RevisionId);
        });

        modelBuilder.Entity<SyncDeviceState>(b =>
        {
            b.HasKey(s => s.DeviceId);
            b.Property(s => s.DeviceId).UseCollation("NOCASE");
            b.Property(s => s.LatestProcessedPackageId).UseCollation("NOCASE");
        });

        modelBuilder.Entity<SyncLocalState>(b =>
        {
            b.HasKey(s => s.DeviceId);
            b.Property(s => s.DeviceId).UseCollation("NOCASE");
            b.Property(s => s.LastUploadedPackageId).UseCollation("NOCASE");
            b.Property(s => s.PendingPackageId).UseCollation("NOCASE");
            b.Property(s => s.PendingPayloadBytes);
        });

        modelBuilder.Entity<SyncConflictRecord>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.SyncId).UseCollation("NOCASE");
            b.Property(c => c.LocalRevisionId).UseCollation("NOCASE");
            b.Property(c => c.RemoteRevisionId).UseCollation("NOCASE");
            b.Property(c => c.ParentRevisionId).UseCollation("NOCASE");
            b.Property(c => c.SourceDeviceId).UseCollation("NOCASE");
            b.Property(c => c.SourcePackageId).UseCollation("NOCASE");
            b.HasIndex(c => c.SyncId);
            b.HasIndex(c => c.IsResolved);
            b.HasIndex(c => new { c.SyncId, c.RemoteRevisionId }).IsUnique();
        });
    }
}
