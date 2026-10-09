using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickNotes.App.Services;

public interface ITemplateExpansionService
{
    string Expand(string templateText, string? source = null, DateTime? now = null, CultureInfo? culture = null);
}

public class TemplateExpansionService : ITemplateExpansionService
{
    private static readonly Regex VariableRegex = new(
        @"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}",
        RegexOptions.Compiled);

    private readonly IDateTimeProvider _dateTimeProvider;

    public TemplateExpansionService(IDateTimeProvider? dateTimeProvider = null)
    {
        _dateTimeProvider = dateTimeProvider ?? new SystemDateTimeProvider();
    }

    public string Expand(string templateText, string? source = null, DateTime? now = null, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(templateText))
        {
            return string.Empty;
        }

        var effectiveNow = now ?? _dateTimeProvider.Now;
        var effectiveCulture = culture ?? CultureInfo.CurrentCulture;
        var effectiveSource = !string.IsNullOrWhiteSpace(source) ? source : "QuickNotes";

        return VariableRegex.Replace(templateText, match =>
        {
            var varName = match.Groups[1].Value.ToLowerInvariant();
            return varName switch
            {
                "date" => effectiveNow.ToString("d", effectiveCulture),
                "time" => effectiveNow.ToString("t", effectiveCulture),
                "datetime" => effectiveNow.ToString("g", effectiveCulture),
                "source" => effectiveSource,
                _ => match.Value
            };
        });
    }
}
