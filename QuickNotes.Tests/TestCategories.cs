using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace QuickNotes.Tests;

public static class TestCategories
{
    public const string TraitName = "Category";
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string UiSensitive = "UiSensitive";
    public const string LiveCloud = "LiveCloud";

    public static readonly string[] ReportingOrder =
    {
        Unit,
        Integration,
        UiSensitive,
        LiveCloud
    };

    public static string Resolve(IEnumerable<string>? traits)
    {
        var set = new HashSet<string>(traits ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (set.Contains(LiveCloud))
        {
            return LiveCloud;
        }

        if (set.Contains(UiSensitive))
        {
            return UiSensitive;
        }

        if (set.Contains(Integration))
        {
            return Integration;
        }

        return Unit;
    }

    public static IEnumerable<string> NormalizeTraitValues(IEnumerable<string>? raw)
    {
        foreach (string? item in raw ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string value = item.Trim();
            int bracket = value.IndexOf('[', StringComparison.Ordinal);
            int close = value.IndexOf(']', StringComparison.Ordinal);
            if (bracket >= 0 && close > bracket)
            {
                value = value.Substring(bracket + 1, close - bracket - 1).Trim();
            }
            else if (value.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("Category:".Length).Trim();
            }

            if (value.Equals(Unit, StringComparison.OrdinalIgnoreCase)
                || value.Equals(Integration, StringComparison.OrdinalIgnoreCase)
                || value.Equals(UiSensitive, StringComparison.OrdinalIgnoreCase)
                || value.Equals(LiveCloud, StringComparison.OrdinalIgnoreCase))
            {
                yield return value;
            }
        }
    }
}

[TraitDiscoverer("QuickNotes.Tests.TestCategoryDiscoverer", "QuickNotes.Tests")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class TestCategoryAttribute : Attribute, ITraitAttribute
{
    public TestCategoryAttribute(string category)
    {
        Category = category;
    }

    public string Category { get; }
}

public sealed class TestCategoryDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        string? category = traitAttribute.GetNamedArgument<string>(nameof(TestCategoryAttribute.Category));
        if (string.IsNullOrWhiteSpace(category))
        {
            category = traitAttribute.GetConstructorArguments().OfType<string>().FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            yield break;
        }

        yield return new KeyValuePair<string, string>(TestCategories.TraitName, category);
    }
}
