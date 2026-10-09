using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickNotes.Tests;

/// <summary>
/// Guards live-cloud object registration and deletion so a run can only touch
/// keys under its unique prefix.
/// </summary>
internal static class LiveCloudPrefixGuard
{
    private const string AcceptanceRoot = "quicknotes-live-acceptance";
    private const string SmokeRoot = "quicknotes-live-smoke";

    private static readonly Regex RunPrefixPattern = new(
        @"^(quicknotes-live-acceptance|quicknotes-live-smoke)/([0-9]{8})/([^/]+)/$",
        RegexOptions.CultureInvariant);

    public static bool IsSafeLivePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        if (prefix.Contains("..", StringComparison.Ordinal) || prefix.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        Match match = RunPrefixPattern.Match(prefix);
        if (!match.Success)
        {
            return false;
        }

        string root = match.Groups[1].Value;
        if (!string.Equals(root, AcceptanceRoot, StringComparison.Ordinal)
            && !string.Equals(root, SmokeRoot, StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                match.Groups[2].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        string runId = match.Groups[3].Value;
        if (!TryParseRunId(runId, out Guid parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        return true;
    }

    public static string RedactLiveDiagnostics(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = Regex.Replace(
            text,
            @"quicknotes-live-(acceptance|smoke)/[0-9]{8}/[0-9a-fA-F-]+/",
            "live-prefix/",
            RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "[id]",
            RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            @"[0-9a-fA-F]{32}",
            "[id]",
            RegexOptions.CultureInvariant);
        return redacted;
    }

    public static string ClassifyObjectKind(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "none";
        }

        if (key.EndsWith("generation.json", StringComparison.Ordinal))
        {
            return "generation";
        }

        if (key.Contains("/packages/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal))
        {
            return "package";
        }

        if (key.EndsWith("/pointer.json", StringComparison.Ordinal))
        {
            return "pointer";
        }

        if (key.Contains("/blobs/", StringComparison.Ordinal))
        {
            return "blob";
        }

        if (key.Contains("/devices/", StringComparison.Ordinal))
        {
            return "device-object";
        }

        return "object";
    }

    public static bool IsKeyInsidePrefix(string prefix, string? key)
    {
        if (!IsSafeLivePrefix(prefix) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Contains("..", StringComparison.Ordinal) || key.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return key.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static void RegisterCreatedKey(ISet<string> registry, string prefix, string key)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        if (!IsKeyInsidePrefix(prefix, key))
        {
            throw new InvalidOperationException(
                "Refusing to register a cloud object outside the unique live-test prefix.");
        }

        registry.Add(key);
    }

    public static IEnumerable<string> FilterDeletableKeys(string prefix, IEnumerable<string> keys)
    {
        if (keys == null)
        {
            yield break;
        }

        foreach (string key in keys)
        {
            if (IsKeyInsidePrefix(prefix, key))
            {
                yield return key;
            }
        }
    }

    private static bool TryParseRunId(string runId, out Guid parsed)
    {
        if (Guid.TryParseExact(runId, "D", out parsed))
        {
            return true;
        }

        return Guid.TryParseExact(runId, "N", out parsed);
    }
}
