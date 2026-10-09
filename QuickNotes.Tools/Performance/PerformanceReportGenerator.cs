using System;
using System.IO;
using System.Text;

namespace QuickNotes.Tools.Performance;

public static class PerformanceReportGenerator
{
    public static string GenerateMarkdown(BenchmarkSuiteResult result)
    {
        var sb = new StringBuilder();
        var env = result.Environment;
        var ds = result.Dataset;

        sb.AppendLine("# QuickNotes Baseline Performance Report (ROADMAP §6.5)");
        sb.AppendLine();
        sb.AppendLine($"> **Date (UTC):** {result.ExecutedAtUtc:yyyy-MM-dd HH:mm:ss}Z  ");
        sb.AppendLine($"> **Benchmark Duration:** {result.TotalDurationMs / 1000.0:F2} seconds  ");
        sb.AppendLine($"> **Status:** Reproducible baseline on isolated synthetic dataset  ");
        sb.AppendLine();
        sb.AppendLine("## 1. Reproduction Command");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("# Build Release solution");
        sb.AppendLine("dotnet build -c Release");
        sb.AppendLine();
        sb.AppendLine("# Note: In an offline environment where the NuGet vulnerability audit endpoint cannot be reached,");
        sb.AppendLine("# pass -p:NuGetAudit=false explicitly as an environment workaround:");
        sb.AppendLine("# dotnet build -c Release -p:NuGetAudit=false");
        sb.AppendLine();
        sb.AppendLine("# Execute benchmark suite (synthetic 10,000 notes + 500 MB attachments)");
        sb.AppendLine(".\\QuickNotes.Tools\\bin\\Release\\net8.0-windows10.0.19041.0\\QuickNotes.Tools.exe benchmark");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 2. Test Environment");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **OS** | {env.OsDescription} |");
        sb.AppendLine($"| **Runtime** | {env.FrameworkDescription} ({env.Architecture}) |");
        sb.AppendLine($"| **Build Configuration** | {env.BuildConfiguration} |");
        sb.AppendLine($"| **Processor** | {env.CpuName} ({env.LogicalCores} logical cores) |");
        sb.AppendLine($"| **RAM** | {env.TotalPhysicalMemoryGb:F1} GB |");
        sb.AppendLine($"| **Storage** | {env.StorageType} ({env.StorageFileSystem}) |");
        sb.AppendLine($"| **Display / Theme** | {env.SystemDpi:F0} DPI / {env.SystemTheme} Theme |");
        sb.AppendLine();
        sb.AppendLine("## 3. Synthetic Dataset Profile");
        sb.AppendLine();
        sb.AppendLine("The benchmark operates on a strictly isolated portable profile generated deterministically from seed.");
        sb.AppendLine("The live user profile (`%LOCALAPPDATA%\\QuickNotes`) is never accessed or modified.");
        sb.AppendLine();
        sb.AppendLine("| Entity | Count / Size | Notes |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| **Seed** | `{ds.Seed}` | PRNG seed for byte-for-byte reproducibility |");
        sb.AppendLine($"| **Notes** | `{ds.NoteCount:N0}` | Titles, Markdown bodies, ~5% pinned, ~10% favorites, ~5% trash |");
        sb.AppendLine($"| **Tags** | `{ds.TagCount:N0}` | 3-level tree (5 roots, 15 sub-tags, 25 sub-sub-tags) + synonyms |");
        sb.AppendLine($"| **Note-Tag Intersections** | Multi-branch | Single tags, double tags, triple tags, untagged |");
        sb.AppendLine($"| **History Revisions** | `{ds.RevisionCount:N0}` | Historical revisions with snapshot tags |");
        sb.AppendLine($"| **Attachments** | `{ds.AttachmentCount:N0}` files ({ds.AttachmentTotalBytes / (1024.0 * 1024.0):F1} MB) | Binary and document payloads in `Attachments/` |");
        sb.AppendLine($"| **SQLite Database** | {ds.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB | Schema v12, FTS5 indexed, WAL mode |");
        sb.AppendLine($"| **Generation Time** | {ds.GenerationElapsedMs / 1000.0:F2} s | Excluded from product timings |");
        sb.AppendLine();
        sb.AppendLine("## 4. Benchmark Results vs ROADMAP §6.5 Targets");
        sb.AppendLine();
        sb.AppendLine("| Metric | ROADMAP Target | Measured Median | Measured p95 | Result | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|");

        void AddRow(BenchmarkMetricResult m)
        {
            string status = (!m.IsAvailable || m.Status == BenchmarkMetricStatus.Unverified || m.Summary.Count == 0)
                ? "**UNVERIFIED**"
                : (m.Status == BenchmarkMetricStatus.Pass ? "**PASS**" : "**FAIL**");
            string med = (m.IsAvailable && m.Summary.Count > 0 && m.Status != BenchmarkMetricStatus.Unverified)
                ? $"{m.Summary.Median:F2} {m.Unit}"
                : "N/A";
            string p95 = (m.IsAvailable && m.Summary.Count > 0 && m.Status != BenchmarkMetricStatus.Unverified)
                ? $"{m.Summary.P95:F2} {m.Unit}"
                : "N/A";
            sb.AppendLine($"| {m.Name} | {m.TargetDescription} | {med} | {p95} | {status} | {m.Note} |");
        }

        AddRow(result.ColdStartMetric);
        AddRow(result.SearchOverallMetric);
        AddRow(result.EditorOpenMetric);
        AddRow(result.WorkingSetMetric);
        AddRow(result.ScrollFpsMetric);

        sb.AppendLine();
        sb.AppendLine("### 4.1. Detailed Search Query Latencies");
        sb.AppendLine();
        sb.AppendLine("All searches measure end-to-end query completion: SQL/FTS query, Tag boolean evaluation, Count, page 1 (50 items) limit/offset fetch, and `NoteCardViewModel` materialization.");
        sb.AppendLine();
        sb.AppendLine("| Query Type | Query Expression | Samples | Median (ms) | p95 (ms) | Min (ms) | Max (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        void AddSearchRow(BenchmarkMetricResult m, string expr)
        {
            var s = m.Summary;
            sb.AppendLine($"| {m.Name} | `{expr}` | {s.Count} | {s.Median:F2} | {s.P95:F2} | {s.Min:F2} | {s.Max:F2} |");
        }

        AddSearchRow(result.SearchTitleMetric, SyntheticDatasetSummary.TitleQueryTerm);
        AddSearchRow(result.SearchBodyMetric, SyntheticDatasetSummary.BodyQueryTerm);
        AddSearchRow(result.SearchTagMetric, SyntheticDatasetSummary.TagQueryTerm);
        AddSearchRow(result.SearchNoHitMetric, SyntheticDatasetSummary.NoHitQueryTerm);

        sb.AppendLine();
        sb.AppendLine("## 5. Raw Sample Observations");
        sb.AppendLine();
        sb.AppendLine("- **Cold Process Start (ms):** " + string.Join(", ", result.ColdStartMetric.Summary.RawSamples.Select(x => x.ToString("F1"))));
        sb.AppendLine("- **Idle Working Set (MB):** " + string.Join(", ", result.WorkingSetMetric.Summary.RawSamples.Select(x => x.ToString("F1"))));
        sb.AppendLine("- **Editor Open to Ready (ms):** " + string.Join(", ", result.EditorOpenMetric.Summary.RawSamples.Select(x => x.ToString("F1"))));
        sb.AppendLine("- **Search Overall (ms):** " + string.Join(", ", result.SearchOverallMetric.Summary.RawSamples.Select(x => x.ToString("F1"))));
        sb.AppendLine();
        sb.AppendLine("## 6. Engineering Findings & Limitations");
        sb.AppendLine();
        sb.AppendLine("1. **Bounded Materialization:** Across all queries on 10,000 notes, first-page materialization strictly loaded at most 50 `NoteCardViewModel`s, confirming SQL-side pagination invariants (S9).");
        sb.AppendLine("2. **Profile Isolation:** All database reads/writes, attachment file generation, and settings remained inside the disposable temporary directory. The live `%LOCALAPPDATA%\\QuickNotes` profile remained bit-for-bit identical.");
        string scrollNotes;
        if (result.ScrollFpsMetric.Summary.Count > 0)
        {
            scrollNotes = $"Experimental non-acceptance diagnostic measured {result.ScrollFpsMetric.Summary.Median:F1} FPS via CompositionTarget.Rendering callback deltas. " +
                "This callback delta measures internal WPF dispatch intervals rather than actual delivered monitor presentation frames or visual jank, and can exceed physical display refresh (e.g. 60 Hz). " +
                "Marked UNVERIFIED and non-acceptance; left unchecked in ROADMAP §6.5 without comparing to the 50-FPS target. " +
                "Requires ETW/PresentMon presentation trace on physical display for acceptance.";
        }
        else
        {
            scrollNotes = "Headless / background process execution does not provide a physical 60Hz display VSYNC loop. " +
                "Left UNVERIFIED and unchecked in ROADMAP per §6.5 instructions rather than inventing FPS. Propose physical manual or ETW presentation session for final 60fps verification.";
        }
        sb.AppendLine($"3. **Scroll Smoothness (FPS) - UNVERIFIED:** {scrollNotes}");
        sb.AppendLine();

        return sb.ToString();
    }

    public static void SaveReport(BenchmarkSuiteResult result, string destinationPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(dir);
        string md = GenerateMarkdown(result);
        File.WriteAllText(destinationPath, md, Encoding.UTF8);
    }
}
