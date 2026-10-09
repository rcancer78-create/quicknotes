using System;
using System.Linq;
using System.Security.Cryptography;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

/// <summary>
/// Resolves a previously produced locator against current <see cref="Note.Text"/>.
/// Unique fingerprints may follow a line-number shift. Duplicate fingerprints may
/// use exact-line only when the in-memory note snapshot proof still matches;
/// any snapshot change falls back (open the note, do not jump to a different
/// checkbox). OccurrenceIndex is not used as identity.
/// </summary>
public static class MarkdownTaskNavigator
{
    public static TaskNavigationResult Resolve(string? text, MarkdownTaskLocator? locator)
    {
        string body = text ?? string.Empty;
        int textLength = body.Length;
        if (locator == null || string.IsNullOrEmpty(locator.Fingerprint))
        {
            return TaskNavigationResult.Fallback;
        }

        var current = MarkdownTaskParser.Parse(body, locator.NoteId, locator.NoteSyncId, locator.NoteTitle, locator.NoteUpdatedAt);
        var matches = current.Where(t => string.Equals(t.Fingerprint, locator.Fingerprint, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            return TaskNavigationResult.Fallback;
        }

        bool locatorWasUnique = locator.SameFingerprintCount <= 1;
        if (!locatorWasUnique)
        {
            if (SnapshotProofMatches(locator.NoteSnapshotProof, body))
            {
                var exactLine = matches.FirstOrDefault(t => t.LineNumber == locator.LineNumber);
                if (exactLine != null)
                {
                    return TaskNavigationResult.Hit(exactLine, textLength);
                }
            }

            return TaskNavigationResult.Fallback;
        }

        var uniqueExact = matches.FirstOrDefault(t => t.LineNumber == locator.LineNumber);
        if (uniqueExact != null)
        {
            return TaskNavigationResult.Hit(uniqueExact, textLength);
        }

        if (matches.Count == 1)
        {
            return TaskNavigationResult.Hit(matches[0], textLength);
        }

        return TaskNavigationResult.Fallback;
    }

    private static bool SnapshotProofMatches(byte[]? indexedProof, string currentText)
    {
        if (indexedProof == null || indexedProof.Length != 32)
        {
            return false;
        }

        byte[] current = MarkdownTaskParser.ComputeNoteSnapshotProof(currentText);
        return CryptographicOperations.FixedTimeEquals(indexedProof, current);
    }
}
