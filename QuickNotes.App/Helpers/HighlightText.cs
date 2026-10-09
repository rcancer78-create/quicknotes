using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuickNotes.App.Services;

namespace QuickNotes.App.Helpers;

public static class HighlightText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(HighlightText),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query",
        typeof(string),
        typeof(HighlightText),
        new PropertyMetadata(null, OnChanged));

    public static string? GetSource(DependencyObject obj) => (string?)obj.GetValue(SourceProperty);
    public static void SetSource(DependencyObject obj, string? value) => obj.SetValue(SourceProperty, value);

    public static string? GetQuery(DependencyObject obj) => (string?)obj.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject obj, string? value) => obj.SetValue(QueryProperty, value);

    private static readonly System.Windows.Media.Brush HighlightBrush = CreateHighlightBrush();
    private static readonly System.Windows.Media.Brush HighlightFgBrush = CreateHighlightFgBrush();

    private static System.Windows.Media.Brush CreateHighlightBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 240, 138));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush CreateHighlightFgBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 32, 51));
        brush.Freeze();
        return brush;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var source = GetSource(textBlock) ?? string.Empty;
        var query = GetQuery(textBlock);
        textBlock.Inlines.Clear();

        foreach (var span in SearchPreview.SplitHighlights(source, query))
        {
            var run = new Run(span.Text);
            if (span.IsMatch)
            {
                run.Background = HighlightBrush;
                run.Foreground = HighlightFgBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            textBlock.Inlines.Add(run);
        }
    }
}
