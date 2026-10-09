using System;

namespace QuickNotes.Tests.Ci;

/// <summary>
/// Workflow YAML mutation helpers that treat LF and CRLF as equivalent.
/// Source C# raw strings follow the repository CRLF baseline; checked-in YAML may be LF.
/// </summary>
internal static class WorkflowTextMutation
{
    public static string NormalizeNewlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    public static string Replace(string text, string oldValue, string newValue)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);

        string normalizedText = NormalizeNewlines(text);
        string normalizedOld = NormalizeNewlines(oldValue);
        string normalizedNew = NormalizeNewlines(newValue);

        Assert.True(
            normalizedText.Contains(normalizedOld, StringComparison.Ordinal),
            "Requested workflow mutation needle was not found after normalizing LF/CRLF.");

        string mutated = normalizedText.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal);
        AssertChanged(normalizedText, mutated);
        return mutated;
    }

    public static string ApplyAndAssertChanged(string original, Func<string, string> mutate)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(mutate);

        string normalized = NormalizeNewlines(original);
        string mutated = NormalizeNewlines(mutate(normalized));
        AssertChanged(normalized, mutated);
        return mutated;
    }

    private static void AssertChanged(string original, string mutated)
    {
        Assert.False(
            string.Equals(original, mutated, StringComparison.Ordinal),
            "Requested workflow mutation did not change the input.");
    }
}
