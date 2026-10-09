using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;

namespace QuickNotes.Tools.Performance;

public sealed class SyntheticDatasetOptions
{
    public int Seed { get; init; } = 1337;
    public int NoteCount { get; init; } = 10_000;
    public long AttachmentTotalBytes { get; init; } = 500L * 1024 * 1024; // 500 MB
    public string OutputDirectory { get; init; } = string.Empty;

    public static SyntheticDatasetOptions CreateSmoke(string outputDirectory, int seed = 1337)
    {
        return new SyntheticDatasetOptions
        {
            Seed = seed,
            NoteCount = 50,
            AttachmentTotalBytes = 1024 * 1024, // 1 MB
            OutputDirectory = outputDirectory
        };
    }
}

public sealed class SyntheticDatasetSummary
{
    public int Seed { get; init; }
    public string ProfileDirectory { get; init; } = string.Empty;
    public int NoteCount { get; init; }
    public int TagCount { get; init; }
    public int RevisionCount { get; init; }
    public int AttachmentCount { get; init; }
    public long AttachmentTotalBytes { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public long GenerationElapsedMs { get; init; }

    public const string TitleQueryTerm = "QuarterlySynthesisReport";
    public const string BodyQueryTerm = "deterministic_hyperthreading_checkpoint";
    public const string TagQueryTerm = "tag:Backend AND tag:DotNet";
    public const string NoHitQueryTerm = "nonexistent_benchmark_token_xyz999";
}

public static class SyntheticDatasetGenerator
{
    public static SyntheticDatasetSummary Generate(SyntheticDatasetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("OutputDirectory must be specified.", nameof(options));
        }

        // Strict isolation validation: will throw if path points to %LOCALAPPDATA%\QuickNotes or ancestors/descendants
        QuickNotesDbContext.ValidateIsolatedProfilePath(options.OutputDirectory);

        string profileDir = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(profileDir))
        {
            Directory.Delete(profileDir, recursive: true);
        }
        Directory.CreateDirectory(profileDir);

        string attachmentsDir = Path.Combine(profileDir, "Attachments");
        string backupsDir = Path.Combine(profileDir, "Backups");
        string draftsDir = Path.Combine(profileDir, "DraftJournals");
        string logsDir = Path.Combine(profileDir, "Logs");
        Directory.CreateDirectory(attachmentsDir);
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(draftsDir);
        Directory.CreateDirectory(logsDir);

        // Safe settings file (never connect to cloud, no hotkey collisions)
        string settingsPath = Path.Combine(profileDir, "settings.json");
        var safeSettings = new AppSettings
        {
            Theme = AppTheme.Dark,
            StartMinimizedToTray = false,
            CompactCards = false,
            SortMode = NoteSortMode.Pinned,
            HotkeyCtrl = false,
            HotkeyShift = false,
            HotkeyAlt = false,
            InstantHotkeyCtrl = false,
            InstantHotkeyAlt = false,
            CloudSync = new QuickNotes.App.Models.Sync.SyncCloudSettings { Enabled = false }
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(safeSettings, new JsonSerializerOptions { WriteIndented = true }));

        var sw = Stopwatch.StartNew();
        var rng = new Random(options.Seed);

        string dbPath = Path.Combine(profileDir, "quicknotes.db");
        using var db = new QuickNotesDbContext(dbPath);
        DbInitializer.Initialize(db);

        // 1. Generate Realistic Multi-Level Tag Tree
        var (tags, synonyms) = CreateTagTree(rng);
        db.Tags.AddRange(tags);
        db.SaveChanges();

        foreach (var syn in synonyms)
        {
            db.TagSynonyms.Add(syn);
        }
        db.SaveChanges();

        // 2. Generate Notes & Revisions in Batches
        var baseDate = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        int noteCount = options.NoteCount;
        int revisionCount = 0;

        const int batchSize = 1000;
        int remaining = noteCount;
        int generated = 0;

        var allTagIds = tags.Select(t => t.Id).ToList();
        int backendTagId = tags.First(t => t.Name == "Backend").Id;
        int dotNetTagId = tags.First(t => t.Name == "DotNet").Id;

        while (remaining > 0)
        {
            int currentBatch = Math.Min(remaining, batchSize);
            using var tx = db.Database.BeginTransaction();

            var batchNotes = new List<Note>(currentBatch);
            for (int i = 0; i < currentBatch; i++)
            {
                int noteIndex = generated + i;
                var (title, body) = GenerateNoteContent(rng, noteIndex, noteCount);
                var createdAt = baseDate.AddMinutes(-rng.Next(10, 100_000));
                var updatedAt = createdAt.AddMinutes(rng.Next(0, 10_000));

                bool isPinned = (noteIndex % 20 == 0); // ~5% pinned
                bool isFavorite = (noteIndex % 10 == 0); // ~10% favorite
                DateTime? deletedAt = (noteIndex % 20 == 19) ? updatedAt.AddMinutes(10) : null; // ~5% deleted (trash)

                var note = new Note
                {
                    Title = title,
                    Text = body,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    IsPinned = isPinned,
                    IsFavorite = isFavorite,
                    DeletedAt = deletedAt,
                    SourceProcessName = (noteIndex % 3 == 0) ? "chrome.exe" : (noteIndex % 3 == 1) ? "devenv.exe" : null,
                    SourceWindowTitle = (noteIndex % 3 == 0) ? "Browser Knowledge Reference" : (noteIndex % 3 == 1) ? "QuickNotes - Solution" : null
                };
                batchNotes.Add(note);
            }

            db.Notes.AddRange(batchNotes);
            db.SaveChanges();

            // NoteTag Intersections
            var noteTags = new List<NoteTag>();
            var revisions = new List<NoteRevision>();

            for (int i = 0; i < batchNotes.Count; i++)
            {
                var note = batchNotes[i];
                int noteIndex = generated + i;

                // Plant specific tag intersection for tag query test:
                // Notes 100, 200, 300 have Backend AND DotNet
                if (noteIndex % 50 == 0)
                {
                    noteTags.Add(new NoteTag { NoteId = note.Id, TagId = backendTagId });
                    noteTags.Add(new NoteTag { NoteId = note.Id, TagId = dotNetTagId });
                }
                else
                {
                    int tagCountChoice = rng.Next(0, 10);
                    int countToAssign = tagCountChoice switch
                    {
                        0 => 0,          // 10% untagged
                        < 5 => 1,        // 40% 1 tag
                        < 9 => 2,        // 40% 2 tags
                        _ => 3           // 10% 3 tags
                    };

                    if (countToAssign > 0)
                    {
                        var chosen = new HashSet<int>();
                        for (int k = 0; k < countToAssign; k++)
                        {
                            int tagId = allTagIds[rng.Next(allTagIds.Count)];
                            if (chosen.Add(tagId))
                            {
                                noteTags.Add(new NoteTag { NoteId = note.Id, TagId = tagId });
                            }
                        }
                    }
                }

                // History revisions for ~30% of notes
                if (noteIndex % 3 == 0)
                {
                    int revs = (noteIndex % 6 == 0) ? 2 : 1;
                    for (int r = 1; r <= revs; r++)
                    {
                        revisions.Add(new NoteRevision
                        {
                            NoteId = note.Id,
                            Title = "Rev " + r + ": " + note.Title,
                            Text = "Revision " + r + " content:\n\n" + note.Text,
                            CreatedAt = note.CreatedAt.AddHours(-r),
                            TagsJson = "[{\"TagName\":\"RevisionHistory\"}]"
                        });
                        revisionCount++;
                    }
                }
            }

            db.NoteTags.AddRange(noteTags);
            db.NoteRevisions.AddRange(revisions);
            db.SaveChanges();

            tx.Commit();

            generated += currentBatch;
            remaining -= currentBatch;
        }

        // 3. Generate Attachments (~500 MB or requested bytes)
        var (attachmentCount, attachmentBytes) = GenerateAttachments(db, attachmentsDir, options.AttachmentTotalBytes, rng);

        // 4. Checkpoint & optimize SQLite
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        db.Database.ExecuteSqlRaw("PRAGMA optimize;");

        sw.Stop();

        long dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

        return new SyntheticDatasetSummary
        {
            Seed = options.Seed,
            ProfileDirectory = profileDir,
            NoteCount = noteCount,
            TagCount = tags.Count,
            RevisionCount = revisionCount,
            AttachmentCount = attachmentCount,
            AttachmentTotalBytes = attachmentBytes,
            DatabaseSizeBytes = dbSizeBytes,
            GenerationElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private static (List<Tag> Tags, List<TagSynonym> Synonyms) CreateTagTree(Random rng)
    {
        var tags = new List<Tag>();
        var synonyms = new List<TagSynonym>();

        // Level 1: Root tags
        string[] rootNames = { "Work", "Personal", "Projects", "Tech", "Archive" };
        var roots = new List<Tag>();
        foreach (var name in rootNames)
        {
            var root = new Tag { Name = name };
            roots.Add(root);
            tags.Add(root);
        }

        // Level 2 & 3 hierarchy
        var hierarchy = new Dictionary<string, (string[] Children, Dictionary<string, string[]> SubChildren)>
        {
            ["Work"] = (
                new[] { "Engineering", "Product", "Design", "Operations", "Meetings" },
                new()
                {
                    ["Engineering"] = new[] { "Backend", "Frontend", "DevOps", "Performance", "Testing" }
                }
            ),
            ["Personal"] = (
                new[] { "Finance", "Health", "Travel", "Learning", "Home" },
                new()
                {
                    ["Finance"] = new[] { "Taxes", "Investments", "Budget", "Receipts" }
                }
            ),
            ["Projects"] = (
                new[] { "Apollo", "Gemini", "Orion", "Pegasus", "Zenith" },
                new()
                {
                    ["Apollo"] = new[] { "Roadmap", "Specs", "Bugs", "Releases" }
                }
            ),
            ["Tech"] = (
                new[] { "DotNet", "SQLite", "WPF", "Architecture", "Security" },
                new()
                {
                    ["DotNet"] = new[] { "Runtime", "EFCore", "GC", "Async" },
                    ["Security"] = new[] { "Cryptography", "Auditing", "Keys" }
                }
            ),
            ["Archive"] = (
                new[] { "2024", "2025", "Legacy" },
                new()
            )
        };

        foreach (var root in roots)
        {
            if (!hierarchy.TryGetValue(root.Name, out var data)) continue;

            foreach (var childName in data.Children)
            {
                var childTag = new Tag { Name = childName, ParentTag = root };
                tags.Add(childTag);

                if (data.SubChildren.TryGetValue(childName, out var subChildren))
                {
                    foreach (var subName in subChildren)
                    {
                        var subTag = new Tag { Name = subName, ParentTag = childTag };
                        tags.Add(subTag);
                    }
                }
            }
        }

        // Add a few realistic synonyms
        var dotnetTag = tags.First(t => t.Name == "DotNet");
        var devopsTag = tags.First(t => t.Name == "DevOps");
        var perfTag = tags.First(t => t.Name == "Performance");
        var cryptoTag = tags.First(t => t.Name == "Cryptography");

        synonyms.Add(new TagSynonym { Tag = dotnetTag, Value = "dotnet" });
        synonyms.Add(new TagSynonym { Tag = devopsTag, Value = "k8s" });
        synonyms.Add(new TagSynonym { Tag = perfTag, Value = "perf" });
        synonyms.Add(new TagSynonym { Tag = cryptoTag, Value = "crypto" });

        return (tags, synonyms);
    }

    private static (string Title, string Body) GenerateNoteContent(Random rng, int index, int totalNotes)
    {
        // Deterministic keyword targets for benchmarks:
        // 1. Title query keyword (20 notes across dataset)
        if (index > 0 && index % (totalNotes / 20) == 0)
        {
            return (
                $"{SyntheticDatasetSummary.TitleQueryTerm} #{index}: System Architecture and Performance",
                $"Detailed technical synthesis report discussing memory budgets, SQLite paging, and UI responsiveness.\n\nSection {index} highlights optimization guidelines."
            );
        }

        // 2. Body query keyword (30 notes across dataset)
        if (index > 0 && index % (totalNotes / 30) == 1)
        {
            return (
                $"Investigation #{index}: Engine Profiling",
                $"Observed state: {SyntheticDatasetSummary.BodyQueryTerm} active on worker threads.\n\n" +
                "Further analysis shows zero contention on database read queries during background synchronization."
            );
        }

        // Standard realistic note templates
        string[] titlePrefixes = { "Meeting Notes", "Design Doc", "Bug Analysis", "Task List", "Architecture Review", "Performance Report", "Sprint Review", "Research Spike", "Customer Feedback", "Operations Checklist" };
        string[] titleTopics = { "SQLite Paged Query", "WPF Virtualization", "FlowDocument Highlighting", "Draft Journal Resilience", "Encrypted Archive Recovery", "Tag Hierarchy Filtering", "Local Sync Coordinator", "Search Debounce Mechanism", "Memory Working Set", "Cold Start Optimization" };

        string prefix = titlePrefixes[rng.Next(titlePrefixes.Length)];
        string topic = titleTopics[rng.Next(titleTopics.Length)];
        string title = $"{prefix}: {topic} #{index}";

        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"Created for reproducible baseline measurement with seed. Item index: {index}.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine("QuickNotes local knowledge base engine ensures fast capture, deterministic search, and zero plaintext leaks.");
        sb.AppendLine();
        sb.AppendLine("## Action Items");
        sb.AppendLine("- [ ] Verify bounded paging across 10,000 notes");
        sb.AppendLine("- [x] Measure cold process start with isolated profile");
        sb.AppendLine("- [ ] Monitor idle working set stabilization");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine($"// Sample reference snippet {index}");
        sb.AppendLine("var note = searchService.QueryNotes(db, query, tagId, skip: 0, take: 50);");
        sb.AppendLine("```");

        if (index % 5 == 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Extended Context");
            sb.AppendLine("Дополнительный русскоязычный контекст для проверки поиска и подсветки синтаксиса в UTF-8.");
            sb.AppendLine("Инварианты целостности гарантируют сохранность всех версий и отсутствие гонок при локальном коммите.");
        }

        return (title, sb.ToString());
    }

    private static (int Count, long TotalBytes) GenerateAttachments(QuickNotesDbContext db, string attachmentsDir, long targetTotalBytes, Random rng)
    {
        if (targetTotalBytes <= 0) return (0, 0);

        // Preallocate reusable buffer for high-throughput deterministic writes
        const int chunkSize = 1024 * 1024; // 1 MB buffer
        byte[] buffer = new byte[chunkSize];
        for (int i = 0; i < chunkSize; i++)
        {
            buffer[i] = (byte)((i * 31 + 17) & 0xFF);
        }

        // Determine attachment sizes to achieve ~targetTotalBytes
        var sizes = new List<long>();
        long accumulated = 0;

        if (targetTotalBytes <= 5 * 1024 * 1024) // Smoke mode
        {
            sizes.Add(targetTotalBytes);
            accumulated = targetTotalBytes;
        }
        else
        {
            // Distribution for ~500 MB:
            // 5 large files (35-45 MB each)
            // 15 medium files (10-20 MB each)
            // 30 smaller files (2-5 MB each)
            // Remaining small files (~250 KB each)
            while (accumulated < targetTotalBytes)
            {
                long remaining = targetTotalBytes - accumulated;
                long size;
                if (sizes.Count < 5 && remaining >= 40 * 1024 * 1024)
                {
                    size = 40 * 1024 * 1024;
                }
                else if (sizes.Count < 20 && remaining >= 15 * 1024 * 1024)
                {
                    size = 15 * 1024 * 1024;
                }
                else if (sizes.Count < 50 && remaining >= 3 * 1024 * 1024)
                {
                    size = 3 * 1024 * 1024;
                }
                else
                {
                    size = Math.Min(remaining, 256 * 1024);
                }

                sizes.Add(size);
                accumulated += size;
            }
        }

        var notes = db.Notes.Take(sizes.Count + 10).Select(n => n.Id).ToList();
        if (notes.Count == 0) return (0, 0);

        string[] extensions = { "pdf", "png", "zip", "bin", "log", "docx" };

        var noteAttachments = new List<NoteAttachment>(sizes.Count);
        long writtenTotal = 0;

        using var sha256 = SHA256.Create();

        for (int i = 0; i < sizes.Count; i++)
        {
            long size = sizes[i];
            string ext = extensions[i % extensions.Length];
            string origName = $"attachment_payload_{i + 1}.{ext}";
            string storedName = Guid.NewGuid().ToString("N") + "." + ext;
            string destFile = Path.Combine(attachmentsDir, storedName);

            // Write payload deterministically
            using (var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, chunkSize, FileOptions.WriteThrough))
            {
                long toWrite = size;
                while (toWrite > 0)
                {
                    int chunk = (int)Math.Min(toWrite, chunkSize);
                    fs.Write(buffer, 0, chunk);
                    toWrite -= chunk;
                }
                fs.Flush();
            }

            // Compute hash for first chunk (or full file)
            byte[] hashBytes = sha256.ComputeHash(buffer, 0, Math.Min((int)size, chunkSize));
            string hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            int noteId = notes[i % notes.Count];
            noteAttachments.Add(new NoteAttachment
            {
                NoteId = noteId,
                OriginalFileName = origName,
                StoredFileName = storedName,
                RelativePath = Path.Combine("Attachments", storedName),
                ContentType = ext == "png" ? "image/png" : ext == "pdf" ? "application/pdf" : "application/octet-stream",
                Size = size,
                Sha256 = hashHex,
                CreatedAt = DateTime.UtcNow
            });

            writtenTotal += size;
        }

        db.NoteAttachments.AddRange(noteAttachments);
        db.SaveChanges();

        return (noteAttachments.Count, writtenTotal);
    }
}
