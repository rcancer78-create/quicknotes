using System;
using System.IO;
using System.Net.Http;
using System.Text;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

public sealed class HtmlToMarkdownConverterTests
{
    [Fact]
    public void ConvertsHeadingsParagraphsBreaksEmphasisCodeListsLinksTablesQuotesAndCheckboxes()
    {
        const string html = """
            <html><body>
            <h1>Заголовок</h1>
            <p>Абзац с <strong>жирным</strong> и <em>курсивом</em> и <code>кодом</code>.</p>
            Line<br>break
            <pre><code>int x = 1;</code></pre>
            <ul><li>один</li><li><input type="checkbox" checked> готово</li></ul>
            <ol><li>первый</li></ol>
            <blockquote><p>цитата</p></blockquote>
            <a href="notes/local.md">локально</a>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            </body></html>
            """;

        var result = HtmlToMarkdownConverter.Convert(html);
        Assert.False(result.HasBlocking);
        Assert.Contains("# Заголовок", result.Markdown);
        Assert.Contains("**жирным**", result.Markdown);
        Assert.Contains("*курсивом*", result.Markdown);
        Assert.Contains("`кодом`", result.Markdown);
        Assert.Contains("```", result.Markdown);
        Assert.Contains("- один", result.Markdown);
        Assert.Contains("- [x]", result.Markdown);
        Assert.Contains("1. первый", result.Markdown);
        Assert.Contains("> ", result.Markdown);
        Assert.Contains("[локально](notes/local.md)", result.Markdown);
        Assert.Contains("| A | B |", result.Markdown);
        Assert.Contains("| --- | --- |", result.Markdown);
    }

    [Fact]
    public void CyrillicAndMalformedHtml_StillYieldsText()
    {
        var result = HtmlToMarkdownConverter.Convert("<p>Привет <b>мир</i> <span>ещё");
        Assert.Contains("Привет", result.Markdown);
        Assert.Contains("мир", result.Markdown);
    }

    [Fact]
    public void DropsScriptStyleIframeObjectAndExternalOrDataImages()
    {
        var result = HtmlToMarkdownConverter.Convert("""
            <p>ok</p>
            <script>alert(1)</script>
            <style>body{color:red}</style>
            <iframe src="https://evil"></iframe>
            <object data="x"></object>
            <img src="https://cdn.example/a.png">
            <img src="data:image/png;base64,AAAA">
            <a href="javascript:alert(1)">x</a>
            """);
        Assert.DoesNotContain("alert", result.Markdown);
        Assert.DoesNotContain("cdn.example", result.Markdown);
        Assert.Contains(result.LostElements, s => s.Contains("script", StringComparison.OrdinalIgnoreCase) || s.Contains("Небезопасный"));
        Assert.Contains("ok", result.Markdown);
    }

    [Fact]
    public void DoesNotUseHttpClient()
    {
        Assert.Null(typeof(HtmlToMarkdownConverter).Assembly.GetType("System.Net.Http.HttpClient"));
        var result = HtmlToMarkdownConverter.Convert("<img src=\"https://example.com/x.png\"><p>offline</p>");
        Assert.Contains("offline", result.Markdown);
        Assert.DoesNotContain("example.com", result.Markdown);
    }

    [Fact]
    public void NestingLimit_IsBlocking()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ImportLimits.MaxHtmlNestingDepth + 3; i++)
        {
            sb.Append("<div>");
        }

        sb.Append("x");
        for (int i = 0; i < ImportLimits.MaxHtmlNestingDepth + 3; i++)
        {
            sb.Append("</div>");
        }

        var result = HtmlToMarkdownConverter.Convert(sb.ToString());
        Assert.True(result.HasBlocking);
    }

    [Fact]
    public void HttpClientTypeIsUnusedByConverter()
    {
        Assert.DoesNotContain(typeof(HtmlToMarkdownConverter).GetMethods(), m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(HttpClient));
    }
}
