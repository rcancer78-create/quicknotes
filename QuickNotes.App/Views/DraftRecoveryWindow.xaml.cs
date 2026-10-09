using System;
using System.Windows;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models.DraftJournal;

namespace QuickNotes.App.Views;

public partial class DraftRecoveryWindow : Window
{
    public DraftRecoveryChoice Choice { get; private set; } = DraftRecoveryChoice.KeepCommitted;

    public DraftRecoveryWindow(DraftRecoveryPromptArgs args)
    {
        InitializeComponent();

        string source = string.IsNullOrWhiteSpace(args.Source) ? "Неизвестный источник" : args.Source;
        string date = args.JournalTimestamp.ToString("dd.MM.yyyy HH:mm:ss");
        SourceAndDateText.Text = $"Источник: {source} | Сохранено: {date}";

        DiffSummaryText.Text = string.IsNullOrWhiteSpace(args.DiffSummary)
            ? "Различий не обнаружено."
            : args.DiffSummary;

        string deferExplanation;
        if (args.NoteId == null)
        {
            CommittedHeaderBlock.Text = "Сохранённая версия (отсутствует)";
            CommittedTitleBox.Text = "(Новая заметка)";
            CommittedTextBox.Text = "(Заметка не была сохранена в базу данных до сбоя)";
            deferExplanation = "Заметка не будет создана. Черновик останется на этом компьютере и будет предложен позже.";
        }
        else
        {
            CommittedHeaderBlock.Text = "Сохранённая версия";
            CommittedTitleBox.Text = string.IsNullOrWhiteSpace(args.CommittedTitle) ? "(без заголовка)" : args.CommittedTitle;
            CommittedTextBox.Text = args.CommittedText;
            deferExplanation = "Сохранённая версия не изменится. Черновик останется на этом компьютере и будет предложен позже.";
        }

        KeepCommittedButton.Content = "Отложить";
        KeepCommittedButton.ToolTip = deferExplanation;
        KeepCommittedExplanationText.Text = deferExplanation;

        DraftTitleBox.Text = string.IsNullOrWhiteSpace(args.DraftTitle) ? "(без заголовка)" : args.DraftTitle;
        DraftTextBox.Text = args.DraftText;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        Choice = DraftRecoveryChoice.Restore;
        DialogResult = true;
        Close();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (!DraftDiscardPrompt.Show(this))
        {
            return;
        }

        Choice = DraftRecoveryChoice.Discard;
        DialogResult = true;
        Close();
    }

    private void OnKeepCommittedClick(object sender, RoutedEventArgs e)
    {
        Choice = DraftRecoveryChoice.KeepCommitted;
        DialogResult = false;
        Close();
    }
}
