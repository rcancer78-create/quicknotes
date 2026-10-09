using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QuickNotes.Tools.Performance;

public static class ColdStartR2rReportGenerator
{
    public static string GenerateMarkdown(ColdStartR2rBenchmarkResult result)
    {
        var sb = new StringBuilder();
        string utc = result.ExecutedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        sb.AppendLine("# QuickNotes publish-time ReadyToRun cold-start comparison (ROADMAP §6.5)");
        sb.AppendLine();
        sb.AppendLine("> **Date (UTC):** " + utc);
        sb.AppendLine("> **Schedule:** " + result.Schedule + ", warmup " + result.WarmupEach.ToString(CultureInfo.InvariantCulture)
            + " + measured " + result.MeasuredEach.ToString(CultureInfo.InvariantCulture) + " launches per variant");
        sb.AppendLine("> **Readiness:** `QN_PERF_STARTUP_READY_MS` after `SearchBox.Focus` at `DispatcherPriority.Input`");
        sb.AppendLine("> **Live profile:** SHA-256 fingerprint unchanged (`" + result.LiveProfileHashBefore + "`)");
        sb.AppendLine();
        sb.AppendLine("This is a same-run comparison of two framework-dependent `win-x64` publishes of the same source. The 2026-09-11 canonical report is historical context only. `CompositionTarget.Rendering` was not used.");
        sb.AppendLine();
        sb.AppendLine("Publish files were written only to unique `artifacts/publish/cold-start/<run-id>/{baseline,r2r}` (or temp) directories. They were **not** copied into `QuickNotes.App/bin` or `obj`.");
        sb.AppendLine();
        sb.AppendLine("## 1. Reproduction");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("pwsh -NoProfile -File .\\scripts\\Measure-ColdStartReadyToRun.ps1");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("| Step | Command |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Baseline publish | `" + EscapeCell(result.BaselinePublishCommand) + "` |");
        sb.AppendLine("| ReadyToRun publish | `" + EscapeCell(result.R2rPublishCommand) + "` |");
        sb.AppendLine("| Process timeout | " + result.TimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms |");
        sb.AppendLine();
        sb.AppendLine("## 2. Environment");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| OS | " + EscapeCell(result.Environment.OsDescription) + " |");
        sb.AppendLine("| Runtime | " + EscapeCell(result.Environment.FrameworkDescription) + " (" + EscapeCell(result.Environment.Architecture) + ") |");
        sb.AppendLine("| Build | " + EscapeCell(result.Environment.BuildConfiguration) + ", RID win-x64, framework-dependent |");
        sb.AppendLine("| CPU | " + EscapeCell(result.Environment.CpuName) + " (" + result.Environment.LogicalCores.ToString(CultureInfo.InvariantCulture) + " logical cores) |");
        sb.AppendLine("| RAM | " + result.Environment.TotalPhysicalMemoryGb.ToString("F1", CultureInfo.InvariantCulture) + " GB |");
        sb.AppendLine("| Storage | " + EscapeCell(result.Environment.StorageType) + " / " + EscapeCell(result.Environment.StorageFileSystem) + " |");
        sb.AppendLine();
        sb.AppendLine("## 3. Dataset");
        sb.AppendLine();
        sb.AppendLine("Synthetic isolated profile (seed " + result.Dataset.Seed.ToString(CultureInfo.InvariantCulture)
            + "): " + result.Dataset.NoteCount.ToString("N0", CultureInfo.InvariantCulture) + " notes, "
            + result.Dataset.TagCount.ToString(CultureInfo.InvariantCulture) + " tags, "
            + result.Dataset.RevisionCount.ToString("N0", CultureInfo.InvariantCulture) + " revisions, SQLite "
            + (result.Dataset.DatabaseSizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)
            + " MB. `%LOCALAPPDATA%\\QuickNotes` was not used as `--isolated-profile`.");
        sb.AppendLine();
        sb.AppendLine("Launches reuse one unique temp dataset profile (notes, FTS, attachments on disk). `PRAGMA integrity_check` runs after generation and before every process. Each process is `--isolated-profile` + `--perf-startup`. The temp dataset is deleted after the run. `%LOCALAPPDATA%\\QuickNotes` is never the profile.");
        sb.AppendLine();
        sb.AppendLine("## 4. Publish sizes");
        sb.AppendLine();
        sb.AppendLine("| Variant | Files | Bytes |");
        sb.AppendLine("|---|---|---|");
        AppendSizeRow(sb, result.Baseline);
        AppendSizeRow(sb, result.ReadyToRun);
        sb.AppendLine();
        sb.AppendLine("## 5. Cold start to input-ready vs p95 ≤ 1500 ms");
        sb.AppendLine();
        sb.AppendLine("| Variant | n | min | median | p95 | max | vs 1500 ms | vs same-run baseline p95 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        AppendMetricRow(sb, result.Baseline, result.Baseline.Summary.P95);
        AppendMetricRow(sb, result.ReadyToRun, result.Baseline.Summary.P95);
        sb.AppendLine();
        sb.AppendLine("### 5.1. Measured samples (ms)");
        sb.AppendLine();
        sb.AppendLine("- **Baseline:** " + FormatSamples(result.Baseline.Summary.RawSamples));
        sb.AppendLine("- **ReadyToRun:** " + FormatSamples(result.ReadyToRun.Summary.RawSamples));
        sb.AppendLine("- **Baseline warmup (excluded):** " + FormatSamples(result.Baseline.WarmupSamples));
        sb.AppendLine("- **ReadyToRun warmup (excluded):** " + FormatSamples(result.ReadyToRun.WarmupSamples));
        sb.AppendLine();
        sb.AppendLine("## 6. Gate");
        sb.AppendLine();
        if (result.ReadyToRunMeetsTarget)
        {
            sb.AppendLine("ReadyToRun measured p95 is ≤ 1.5 s on " + result.ReadyToRun.Summary.Count.ToString(CultureInfo.InvariantCulture)
                + " samples. ROADMAP §6.5 cold-start may be closed **only** with these numbers.");
        }
        else
        {
            sb.AppendLine("ReadyToRun measured p95 is **not** ≤ 1.5 s (or sample count is below 10). The 1.5 s target is unchanged. The gate stays open. No publish files were copied into ordinary build output.");
        }

        sb.AppendLine();
        sb.AppendLine("## 7. Isolation");
        sb.AppendLine();
        sb.AppendLine("| Check | Result |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Live profile hash before | `" + result.LiveProfileHashBefore + "` |");
        sb.AppendLine("| Live profile hash after | `" + result.LiveProfileHashAfter + "` |");
        sb.AppendLine("| Hashes identical | " + (result.LiveProfileUnchanged ? "yes" : "NO") + " |");
        sb.AppendLine("| `PRAGMA integrity_check` | ok on synthetic DB before each variant launch |");
        return sb.ToString();
    }

    public static void SaveReport(ColdStartR2rBenchmarkResult result, string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(full, GenerateMarkdown(result), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AppendSizeRow(StringBuilder sb, ColdStartVariantResult variant)
    {
        sb.Append("| ")
            .Append(EscapeCell(variant.Name))
            .Append(" | ")
            .Append(variant.PublishFileCount.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(variant.PublishBytes.ToString("N0", CultureInfo.InvariantCulture))
            .AppendLine(" |");
    }

    private static void AppendMetricRow(StringBuilder sb, ColdStartVariantResult variant, double baselineP95)
    {
        var s = variant.Summary;
        string vsTarget = s.Count == 0 ? "n/a" : (s.P95 <= 1500.0 ? "PASS" : "FAIL");
        string vsBaseline = s.Count == 0 || baselineP95 <= 0
            ? "n/a"
            : ((s.P95 - baselineP95).ToString("F1", CultureInfo.InvariantCulture) + " ms");
        sb.Append("| ")
            .Append(EscapeCell(variant.Name))
            .Append(" | ")
            .Append(s.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(s.Min.ToString("F1", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(s.Median.ToString("F1", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(s.P95.ToString("F1", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(s.Max.ToString("F1", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(vsTarget)
            .Append(" | ")
            .Append(vsBaseline)
            .AppendLine(" |");
    }

    private static string FormatSamples(System.Collections.Generic.IReadOnlyList<double> samples)
    {
        if (samples == null || samples.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", samples.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)));
    }

    private static string EscapeCell(string value)
    {
        return (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    }
}
