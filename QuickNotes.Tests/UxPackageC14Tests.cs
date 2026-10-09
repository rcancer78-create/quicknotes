using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class UxPackageC14Tests
{
    [Fact]
    public void CardGestureCopy_NamesExplicitExpandAndOpenKeys()
    {
        var card = new NoteCardViewModel(new Note { Title = "A", Text = "body" });
        Assert.Equal(CardGestureCopy.Expand, card.ExpandButtonText);
        Assert.Equal(CardGestureCopy.ExpandAutomationName, card.ExpandButtonAutomationName);
        Assert.Contains("Enter", card.ExpandButtonTooltip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+E", card.ExpandButtonTooltip, StringComparison.Ordinal);

        card.IsExpanded = true;
        Assert.Equal(CardGestureCopy.Collapse, card.ExpandButtonText);
        Assert.Equal(CardGestureCopy.CollapseAutomationName, card.ExpandButtonAutomationName);
        Assert.True(card.ToggleExpandedCommand.CanExecute(null));
        card.ToggleExpandedCommand.Execute(null);
        Assert.False(card.IsExpanded);
    }

    [Fact]
    public void MainWindow_UsesExplicitExpand_NotHiddenDoubleClick()
    {
        string root = FindSolutionRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml.cs"));
        string help = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "HelpWindow.xaml"));
        string shortcuts = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Helpers", "ShortcutCatalog.cs"));

        int start = xaml.IndexOf("DataType=\"{x:Type vm:NoteCardViewModel}\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        string card = xaml.Substring(start, end - start);

        Assert.Contains("x:Name=\"CardBorder\"", card, StringComparison.Ordinal);
        Assert.Contains("MouseLeftButtonDown=\"CardSurface_MouseLeftButtonDown\"", card, StringComparison.Ordinal);
        Assert.Contains("ToggleExpandedCommand", card, StringComparison.Ordinal);
        Assert.Contains("ExpandButtonText", card, StringComparison.Ordinal);
        Assert.Contains("ExpandButtonAutomationName", card, StringComparison.Ordinal);
        Assert.Contains("CardGestureCopy.HeaderTooltip", card, StringComparison.Ordinal);
        Assert.Contains("CardGestureCopy.BodyTooltip", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CardHeader_MouseLeftButtonDown", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CardBody_MouseLeftButtonDown", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Двойной клик", card, StringComparison.Ordinal);

        Assert.DoesNotContain("CardHeader_MouseLeftButtonDown", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CardBody_MouseLeftButtonDown", code, StringComparison.Ordinal);
        Assert.Contains("CardSurface_MouseLeftButtonDown", code, StringComparison.Ordinal);
        Assert.Contains("OpenNoteInDetailCommand", code, StringComparison.Ordinal);
        Assert.Contains("e.ClickCount != 1", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EditNoteCommand.Execute(card)", code, StringComparison.Ordinal);

        Assert.Contains("CardGestureCopy.ListHelp", help, StringComparison.Ordinal);
        Assert.Contains("Enter в списке открывает выбранную заметку, Ctrl+E — в отдельном окне", shortcuts, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAncestor_FromRun_DoesNotThrow_AndDetectsButtonOrigin()
    {
        StaTestHarness.Run(() =>
        {
            var cardRun = new Run("Карточка для разворота UX-C14");
            var cardText = new TextBlock();
            cardText.Inlines.Add(cardRun);

            Assert.Null(Record.Exception(() => MainWindow.FindAncestor<Button>(cardRun)));
            Assert.Null(MainWindow.FindAncestor<Button>(cardRun));
            Assert.Same(cardText, MainWindow.FindAncestor<TextBlock>(cardRun));

            var buttonRun = new Run("Развернуть");
            var buttonText = new TextBlock();
            buttonText.Inlines.Add(buttonRun);
            var button = new Button { Content = buttonText };

            Assert.Null(Record.Exception(() => MainWindow.FindAncestor<Button>(buttonRun)));
            Assert.Same(button, MainWindow.FindAncestor<Button>(buttonRun));
        });
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }
}
