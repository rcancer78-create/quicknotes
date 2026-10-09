using System;
using QuickNotes.Tests.Ci;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class WorkflowTextMutationTests
{
    [Fact]
    public void Replace_MatchesCrlfNeedleAgainstLfDocument()
    {
        const string lfDocument = "path: |\n  a\n  b\n";
        const string crlfNeedle = "path: |\r\n  a\r\n  b\r\n";
        string mutated = WorkflowTextMutation.Replace(lfDocument, crlfNeedle, "path: **\n");
        Assert.Equal("path: **\n", mutated);
    }

    [Fact]
    public void Replace_MatchesLfNeedleAgainstCrlfDocument()
    {
        const string crlfDocument = "on:\r\n  workflow_dispatch:\r\n";
        const string lfNeedle = "on:\n  workflow_dispatch:\n";
        string mutated = WorkflowTextMutation.Replace(crlfDocument, lfNeedle, "on:\n  push:\n");
        Assert.Equal("on:\n  push:\n", mutated);
    }

    [Fact]
    public void Replace_FailsWhenNeedleIsMissing()
    {
        Exception? ex = Record.Exception(static () =>
            WorkflowTextMutation.Replace("on:\n  workflow_dispatch:\n", "missing-needle", "x"));
        Assert.NotNull(ex);
        Assert.Contains("needle was not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAndAssertChanged_FailsWhenMutationIsANoOp()
    {
        Exception? ex = Record.Exception(static () =>
            WorkflowTextMutation.ApplyAndAssertChanged("timeout-minutes: 30\n", static text => text));
        Assert.NotNull(ex);
        Assert.Contains("did not change the input", ex.Message, StringComparison.Ordinal);
    }
}
