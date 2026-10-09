using System;
using System.IO;
using QuickNotes.Tools.Performance;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class PresentMonCsvParserTests
{
    [Fact]
    public void Empty_IsUnverified()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.Parse(string.Empty);
        Assert.True(parsed.Empty);
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed());
        Assert.Equal(ScrollFpsVerdict.Unverified, eval.Verdict);
        Assert.DoesNotContain("CompositionTarget", eval.VerdictReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_IsUnverified()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("malformed.csv"));
        Assert.True(parsed.Malformed);
        Assert.Equal(ScrollFpsVerdict.Unverified, ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed()).Verdict);
    }

    [Fact]
    public void HeaderOnly_IsProcessNotFoundStyleUnverified()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("header-only.csv"));
        Assert.True(parsed.HeaderOnly);
        Assert.Equal(0, parsed.DataRowCount);
        Assert.Equal(ScrollFpsVerdict.Unverified, ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed()).Verdict);
    }

    [Fact]
    public void ProcessNotFoundLog_IsUnverified()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("process-not-found.txt"));
        Assert.True(parsed.ProcessNotFound);
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed());
        Assert.Equal(ScrollFpsVerdict.Unverified, eval.Verdict);
        Assert.Contains("process was not found", eval.VerdictReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VariableRefresh_IsUnverifiedEvenWithOperatorConfirm()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("vrr.csv"));
        Assert.True(parsed.VariableRefreshLikely);
        Assert.True(parsed.DisplayedFrameCount >= 30);
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed());
        Assert.Equal(ScrollFpsVerdict.Unverified, eval.Verdict);
        Assert.Contains("Variable refresh", eval.VerdictReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("| **Result** | **UNVERIFIED** |", ScrollFpsPresentMonReportGenerator.GenerateMarkdown(eval), StringComparison.Ordinal);
    }

    [Fact]
    public void Healthy60Hz_WithoutOperatorConfirm_IsUnverified()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("healthy-60hz.csv"));
        Assert.False(parsed.VariableRefreshLikely);
        Assert.True(parsed.DisplayedFrameCount >= 30);
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, new ScrollFpsEvaluateOptions
        {
            OperatorConfirmsManualScroll = false,
            UsedCompositionTarget = false,
            ToolName = "PresentMon",
            ToolVersion = "fixture",
            RawCsvPath = "healthy-60hz.csv",
            OsDescription = "test-os",
            DisplayDescription = "96 DPI / 60 Hz"
        });
        Assert.Equal(ScrollFpsVerdict.Unverified, eval.Verdict);
        Assert.Contains("manual scroll", eval.VerdictReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Healthy60Hz_WithOperatorConfirm_IsPassAndReportListsStats()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("healthy-60hz.csv"));
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed("healthy-60hz.csv"));
        Assert.Equal(ScrollFpsVerdict.Pass, eval.Verdict);
        Assert.True(eval.MedianDisplayedFps >= 50);
        string md = ScrollFpsPresentMonReportGenerator.GenerateMarkdown(eval);
        Assert.Contains("| **Result** | **PASS** |", md, StringComparison.Ordinal);
        Assert.Contains("Tool version", md, StringComparison.Ordinal);
        Assert.Contains("test-os", md, StringComparison.Ordinal);
        Assert.Contains("healthy-60hz.csv", md, StringComparison.Ordinal);
        Assert.Contains("Dropped column count", md, StringComparison.Ordinal);
        Assert.Contains("scroll the note list", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompositionTarget.Rendering timings as proof", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Below50Fps_WithConfirm_IsFail()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("below-50fps.csv"));
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed());
        Assert.Equal(ScrollFpsVerdict.Fail, eval.Verdict);
        Assert.True(eval.MedianDisplayedFps < 50);
    }

    [Fact]
    public void DroppedAndJank_AreCounted()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("dropped-jank.csv"));
        Assert.True(parsed.DroppedCount >= 1);
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, Confirmed());
        Assert.True(eval.JankCount >= 1);
        Assert.Contains("Jank frames", ScrollFpsPresentMonReportGenerator.GenerateMarkdown(eval), StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionTargetSource_NeverPasses()
    {
        PresentMonParseResult parsed = PresentMonCsvParser.ParseFile(Fixture("healthy-60hz.csv"));
        var eval = ScrollFpsCaptureEvaluator.Evaluate(parsed, new ScrollFpsEvaluateOptions
        {
            OperatorConfirmsManualScroll = true,
            UsedCompositionTarget = true
        });
        Assert.Equal(ScrollFpsVerdict.Unverified, eval.Verdict);
        Assert.Contains("CompositionTarget.Rendering", eval.VerdictReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentMonPath_UrlAndWrongName_AreRejected()
    {
        var url = Assert.Throws<InvalidOperationException>(() =>
            PresentMonExecutableGuard.NormalizeAndValidate("https://example.invalid/PresentMon.exe"));
        Assert.Contains("Download URLs are refused", url.Message, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering", PresentMonExecutableGuard.MissingMessage(), StringComparison.Ordinal);
        Assert.Contains("will not download", PresentMonExecutableGuard.MissingMessage(), StringComparison.OrdinalIgnoreCase);

        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (File.Exists(notepad))
        {
            var wrong = Assert.Throws<InvalidOperationException>(() => PresentMonExecutableGuard.NormalizeAndValidate(notepad));
            Assert.Contains("PresentMon", wrong.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ScrollFpsEvaluateOptions Confirmed(string csv = "fixture.csv")
    {
        return new ScrollFpsEvaluateOptions
        {
            OperatorConfirmsManualScroll = true,
            UsedCompositionTarget = false,
            ToolName = "PresentMon",
            ToolVersion = "fixture-1",
            RawCsvPath = csv,
            OsDescription = "test-os",
            DisplayDescription = "1920x1080 60 Hz",
            DisplayRefreshHz = 60
        };
    }

    private static string Fixture(string name)
    {
        return Path.Combine(CiReportingConfigTests.FindRepoRoot(), "QuickNotes.Tests", "Fixtures", "presentmon", name);
    }
}
