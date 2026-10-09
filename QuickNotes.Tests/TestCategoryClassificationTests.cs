using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickNotes.Tests;

public sealed class TestCategoryClassificationTests
{
    [Fact]
    public void Resolve_GivesLiveCloudPriorityOverIntegrationAndUi()
    {
        Assert.Equal(TestCategories.LiveCloud, TestCategories.Resolve(new[] { "Integration", "LiveCloud" }));
        Assert.Equal(TestCategories.UiSensitive, TestCategories.Resolve(new[] { "Integration", "UiSensitive" }));
        Assert.Equal(TestCategories.Unit, TestCategories.Resolve(Array.Empty<string>()));
    }

    [Fact]
    public void AssemblyTestClasses_HaveAtMostOneCategoryAndMatchStructuralRules()
    {
        var violations = new List<string>();
        Dictionary<string, string> sourceCategories = InferCategoriesFromSource(FindTestsRoot());

        foreach (Type type in GetTestClasses())
        {
            string declared = GetDeclaredCategory(type);
            string expected = sourceCategories.TryGetValue(type.Name, out string? inferred)
                ? inferred
                : TestCategories.Unit;

            if (!string.Equals(declared, expected, StringComparison.Ordinal))
            {
                violations.Add($"{type.Name}: declared {declared}, inferred {expected}");
            }
        }

        foreach (string name in new[] { "YandexObjectStorageLiveAcceptanceTests", "YandexObjectStorageLiveSmokeTests" })
        {
            if (!sourceCategories.TryGetValue(name, out string? live) || live != TestCategories.LiveCloud)
            {
                violations.Add(name + " must be inferred as LiveCloud");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LiveCloudTypes_AreNotCountedAsOrdinaryIntegration()
    {
        foreach (Type type in GetTestClasses())
        {
            if (GetDeclaredCategory(type) != TestCategories.LiveCloud)
            {
                continue;
            }

            var attrs = type.GetCustomAttributes<TestCategoryAttribute>(inherit: true).ToArray();
            Assert.True(attrs.Length <= 1, type.Name + " has multiple TestCategory attributes");
            Assert.DoesNotContain(attrs, a => a.Category == TestCategories.Integration);
        }
    }

    private static IEnumerable<Type> GetTestClasses()
    {
        return typeof(TestCategoryClassificationTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetMethods().Any(IsTestMethod));
    }

    private static bool IsTestMethod(MethodInfo method)
    {
        return method.GetCustomAttributes().Any(a =>
            a.GetType().Name is "FactAttribute" or "TheoryAttribute");
    }

    private static string GetDeclaredCategory(Type type)
    {
        var attr = type.GetCustomAttribute<TestCategoryAttribute>(inherit: true);
        if (attr is null)
        {
            return TestCategories.Unit;
        }

        return TestCategories.Resolve(new[] { attr.Category });
    }

    private static Dictionary<string, string> InferCategoriesFromSource(string testsRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(testsRoot, "*Tests.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            MatchCollection matches = Regex.Matches(
                text,
                @"^public (?:sealed )?class (?<name>\w+)",
                RegexOptions.Multiline);

            for (int i = 0; i < matches.Count; i++)
            {
                string name = matches[i].Groups["name"].Value;
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                string body = text.Substring(start, end - start);
                if (!body.Contains("[Fact", StringComparison.Ordinal) && !body.Contains("[Theory", StringComparison.Ordinal))
                {
                    continue;
                }

                result[name] = TestCategoryInference.Infer(name, body);
            }
        }

        return result;
    }

    private static string FindTestsRoot()
    {
        return Path.Combine(CiReportingConfigTests.FindRepoRoot(), "QuickNotes.Tests");
    }
}
