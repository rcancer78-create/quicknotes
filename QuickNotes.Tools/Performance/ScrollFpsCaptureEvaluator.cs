using System;
using System.Linq;

namespace QuickNotes.Tools.Performance;

public enum ScrollFpsVerdict
{
    Unverified = 0,
    Pass = 1,
    Fail = 2
}

public sealed class ScrollFpsEvaluateOptions
{
    public bool OperatorConfirmsManualScroll { get; init; }
    public bool UsedCompositionTarget { get; init; }
    public string ToolName { get; init; } = "PresentMon";
    public string ToolVersion { get; init; } = "unknown";
    public string RawCsvPath { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public int? DisplayRefreshHz { get; init; }
}

public sealed class ScrollFpsCaptureEvaluation
{
    public ScrollFpsVerdict Verdict { get; init; }
    public string VerdictReason { get; init; } = string.Empty;
    public PresentMonParseResult Parse { get; init; } = new();
    public MetricSampleSummary? DisplaySummary { get; init; }
    public MetricSampleSummary? PresentSummary { get; init; }
    public double? MedianDisplayedFps { get; init; }
    public int JankCount { get; init; }
    public bool UsedCompositionTarget { get; init; }
    public bool OperatorConfirmsManualScroll { get; init; }
    public string ToolName { get; init; } = "PresentMon";
    public string ToolVersion { get; init; } = "unknown";
    public string RawCsvPath { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public int? DisplayRefreshHz { get; init; }

    public const double TargetFps = 50.0;
    public const int MinDisplayedFrames = 30;
}

public static class ScrollFpsCaptureEvaluator
{
    public static ScrollFpsCaptureEvaluation Evaluate(PresentMonParseResult parse, ScrollFpsEvaluateOptions options)
    {
        ArgumentNullException.ThrowIfNull(parse);
        options ??= new ScrollFpsEvaluateOptions();

        MetricSampleSummary? displaySummary = parse.DisplayIntervalsMs.Count > 0
            ? PerformanceMetricsCalculator.ComputeSummary(parse.DisplayIntervalsMs)
            : null;
        MetricSampleSummary? presentSummary = parse.PresentIntervalsMs.Count > 0
            ? PerformanceMetricsCalculator.ComputeSummary(parse.PresentIntervalsMs)
            : null;

        int jank = 0;
        if (displaySummary != null && parse.DisplayIntervalsMs.Count > 0)
        {
            double jankThresholdMs = Math.Max(1000.0 / 30.0, displaySummary.Median * 1.8);
            jank = parse.DisplayIntervalsMs.Count(ms => ms > jankThresholdMs);
        }

        double? medianFps = displaySummary is { Median: > 0 }
            ? 1000.0 / displaySummary.Median
            : null;

        string reason;
        ScrollFpsVerdict verdict;

        if (options.UsedCompositionTarget)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = "CompositionTarget.Rendering timings are not accepted as presentation FPS.";
        }
        else if (parse.ProcessNotFound || parse.HeaderOnly)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = string.IsNullOrWhiteSpace(parse.Reason)
                ? "Target process was not found; no PresentMon frames."
                : parse.Reason;
        }
        else if (parse.Empty || parse.Malformed)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = string.IsNullOrWhiteSpace(parse.Reason)
                ? "PresentMon CSV could not be parsed."
                : parse.Reason;
        }
        else if (parse.VariableRefreshLikely)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = "Variable refresh rate (VRR/tearing Independent Flip or high display-interval spread) makes the fixed 50 fps gate unverified without a recorded refresh contract.";
        }
        else if (parse.DisplayedFrameCount < ScrollFpsCaptureEvaluation.MinDisplayedFrames || medianFps == null)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = "Fewer than " + ScrollFpsCaptureEvaluation.MinDisplayedFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " displayed PresentMon frames (MsBetweenDisplayChange > 0). MsBetweenPresents alone is not used as proof.";
        }
        else if (!options.OperatorConfirmsManualScroll)
        {
            verdict = ScrollFpsVerdict.Unverified;
            reason = "Operator did not confirm manual scrolling of the note list during capture. Idle vsync presents are not scroll smoothness.";
        }
        else if (medianFps.Value + 0.05 < ScrollFpsCaptureEvaluation.TargetFps)
        {
            verdict = ScrollFpsVerdict.Fail;
            reason = "Median displayed FPS is below the 50 fps ROADMAP gate.";
        }
        else
        {
            verdict = ScrollFpsVerdict.Pass;
            reason = "PresentMon displayed-frame median meets the 50 fps gate after confirmed manual list scroll on a non-VRR capture.";
        }

        return new ScrollFpsCaptureEvaluation
        {
            Verdict = verdict,
            VerdictReason = reason,
            Parse = parse,
            DisplaySummary = displaySummary,
            PresentSummary = presentSummary,
            MedianDisplayedFps = medianFps,
            JankCount = jank,
            UsedCompositionTarget = options.UsedCompositionTarget,
            OperatorConfirmsManualScroll = options.OperatorConfirmsManualScroll,
            ToolName = options.ToolName,
            ToolVersion = options.ToolVersion,
            RawCsvPath = options.RawCsvPath,
            OsDescription = options.OsDescription,
            DisplayDescription = options.DisplayDescription,
            DisplayRefreshHz = options.DisplayRefreshHz
        };
    }
}
