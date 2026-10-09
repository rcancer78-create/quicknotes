using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuickNotes.Tools.Performance;

public static class ScrollFpsPresentMonReportGenerator
{
    public static string GenerateMarkdown(ScrollFpsCaptureEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var sb = new StringBuilder();
        sb.AppendLine("# QuickNotes physical scroll FPS (PresentMon)");
        sb.AppendLine();
        sb.AppendLine("Opt-in ROADMAP §6.7 / §6.5 list-scroll gate. `CompositionTarget.Rendering` is not used as FPS proof.");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| **Result** | **" + evaluation.Verdict.ToString().ToUpperInvariant() + "** |");
        sb.AppendLine("| Tool | " + Escape(evaluation.ToolName) + " |");
        sb.AppendLine("| Tool version | " + Escape(evaluation.ToolVersion) + " |");
        sb.AppendLine("| OS | " + Escape(evaluation.OsDescription) + " |");
        sb.AppendLine("| Display | " + Escape(evaluation.DisplayDescription) + " |");
        sb.AppendLine("| Display refresh (GDI) | " + (evaluation.DisplayRefreshHz?.ToString(CultureInfo.InvariantCulture) ?? "n/a") + " Hz |");
        sb.AppendLine("| Raw CSV | `" + Escape(evaluation.RawCsvPath) + "` |");
        sb.AppendLine("| Operator confirmed manual scroll | " + evaluation.OperatorConfirmsManualScroll.ToString() + " |");
        sb.AppendLine("| Used CompositionTarget.Rendering | " + evaluation.UsedCompositionTarget.ToString() + " |");
        sb.AppendLine();
        sb.AppendLine("## Manual action required during capture");
        sb.AppendLine();
        sb.AppendLine("While PresentMon is recording, **scroll the note list** with the mouse wheel or scrollbar for the full capture interval. The harness does not inject scroll, key, or click input and does not touch cloud credentials. Idle desktop presents are not scroll smoothness.");
        sb.AppendLine();
        sb.AppendLine("## Reason");
        sb.AppendLine();
        sb.AppendLine(evaluation.VerdictReason);
        sb.AppendLine();
        sb.AppendLine("## Frame-time statistics (MsBetweenDisplayChange)");
        sb.AppendLine();
        sb.AppendLine("| Statistic | Value |");
        sb.AppendLine("|---|---|");
        var parse = evaluation.Parse;
        sb.AppendLine("| CSV data rows | " + parse.DataRowCount.ToString(CultureInfo.InvariantCulture) + " |");
        sb.AppendLine("| Displayed frames | " + parse.DisplayedFrameCount.ToString(CultureInfo.InvariantCulture) + " |");
        if (evaluation.DisplaySummary != null)
        {
            var s = evaluation.DisplaySummary;
            sb.AppendLine("| Median display interval | " + s.Median.ToString("F3", CultureInfo.InvariantCulture) + " ms |");
            sb.AppendLine("| p95 display interval | " + s.P95.ToString("F3", CultureInfo.InvariantCulture) + " ms |");
            sb.AppendLine("| Min / max display interval | " + s.Min.ToString("F3", CultureInfo.InvariantCulture) + " / " + s.Max.ToString("F3", CultureInfo.InvariantCulture) + " ms |");
            sb.AppendLine("| Mean / stddev | " + s.Mean.ToString("F3", CultureInfo.InvariantCulture) + " / " + s.StdDev.ToString("F3", CultureInfo.InvariantCulture) + " ms |");
        }
        else
        {
            sb.AppendLine("| Median display interval | n/a |");
        }

        sb.AppendLine("| Median displayed FPS | " + (evaluation.MedianDisplayedFps?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a") + " |");
        sb.AppendLine("| Present intervals (diagnostic only) | " + parse.PresentIntervalsMs.Count.ToString(CultureInfo.InvariantCulture) + " |");
        sb.AppendLine();
        sb.AppendLine("## Dropped / jank indicators");
        sb.AppendLine();
        sb.AppendLine("| Indicator | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Dropped column count | " + parse.DroppedCount.ToString(CultureInfo.InvariantCulture) + " |");
        sb.AppendLine("| Jank frames (display interval > max(33.3 ms, 1.8 × median)) | " + evaluation.JankCount.ToString(CultureInfo.InvariantCulture) + " |");
        sb.AppendLine("| AllowsTearing count | " + parse.AllowsTearingCount.ToString(CultureInfo.InvariantCulture) + " |");
        sb.AppendLine("| Variable refresh likely | " + parse.VariableRefreshLikely.ToString() + " |");
        sb.AppendLine("| PresentMode sample | " + Escape(parse.PresentModeSample) + " |");
        sb.AppendLine();
        sb.AppendLine("ROADMAP list-scroll ≥ 50 fps stays open unless Result above is PASS from a real PresentMon capture with confirmed manual scrolling.");
        sb.AppendLine();
        return sb.ToString();
    }

    public static void Save(ScrollFpsCaptureEvaluation evaluation, string destinationPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(destinationPath, GenerateMarkdown(evaluation), Encoding.UTF8);
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("|", "/", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");
    }
}
