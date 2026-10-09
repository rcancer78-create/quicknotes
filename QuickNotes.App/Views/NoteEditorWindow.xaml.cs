using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace QuickNotes.App.Views;

public partial class NoteEditorWindow : Window
{
    private readonly NoteEditorViewModel _viewModel;

    public NoteEditorWindow(NoteEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.RequestUnsavedEditorDecision ??= () => UnsavedEditorPrompt.Show(this);

        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        _viewModel.GetSelection = () => (NoteTextBox.SelectionStart, NoteTextBox.SelectionLength);
        _viewModel.SetSelection = (start, len) =>
        {
            if (!_viewModel.IsEditorVisible)
            {
                _viewModel.ViewMode = MarkdownViewMode.Edit;
            }
            NoteTextBox.Focus();
            NoteTextBox.Select(Math.Clamp(start, 0, NoteTextBox.Text.Length), Math.Clamp(len, 0, Math.Max(0, NoteTextBox.Text.Length - start)));
        };

        _viewModel.RequestNavigateToOutline += item =>
        {
            if (_viewModel.IsEditorVisible)
            {
                NoteTextBox.Focus();
                if (item.CharacterIndex >= 0 && item.CharacterIndex <= NoteTextBox.Text.Length)
                {
                    NoteTextBox.Select(item.CharacterIndex, 0);
                    int line = NoteTextBox.GetLineIndexFromCharacterIndex(item.CharacterIndex);
                    if (line >= 0)
                    {
                        NoteTextBox.ScrollToLine(line);
                    }
                }
            }
            if (_viewModel.IsPreviewVisible && PreviewViewer?.Document != null)
            {
                foreach (var block in PreviewViewer.Document.Blocks)
                {
                    if (block.Tag is string tag && string.Equals(tag, item.AnchorId, StringComparison.OrdinalIgnoreCase))
                    {
                        block.BringIntoView();
                        break;
                    }
                }
            }
        };

        _viewModel.InsertTextAtCaret = marker =>
        {
            if (!_viewModel.IsEditorVisible)
            {
                _viewModel.ViewMode = MarkdownViewMode.Edit;
            }
            NoteTextBox.Focus();
            int start = NoteTextBox.SelectionStart;
            NoteTextBox.SelectedText = marker;
            NoteTextBox.SelectionStart = start + marker.Length;
            NoteTextBox.SelectionLength = 0;
        };

        _viewModel.PickLinkedNote = () =>
        {
            var pickerVm = new NoteLinkPickerViewModel(_viewModel.ContextFactory, _viewModel.NoteId);
            var dlg = new NoteLinkPickerDialog(pickerVm) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                return pickerVm.SelectedNote;
            }
            return null;
        };
        _viewModel.PickLinkNoteId = () => _viewModel.PickLinkedNote?.Invoke()?.Id;

        _viewModel.PickAttachmentFile = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите файл для прикрепления",
                Filter = "Все файлы (*.*)|*.*|Изображения (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Документы (*.pdf;*.txt;*.md;*.docx;*.xlsx)|*.pdf;*.txt;*.md;*.docx;*.xlsx"
            };
            if (dlg.ShowDialog(this) == true)
            {
                return dlg.FileName;
            }
            return null;
        };

        _viewModel.RequestOpenLinkedNote += targetId =>
        {
            try
            {
                using var db = _viewModel.ContextFactory();
                var targetNote = db.Notes
                    .Include(n => n.NoteTags)
                    .ThenInclude(nt => nt.Tag)
                    .FirstOrDefault(n => n.Id == targetId && n.DeletedAt == null);

                if (targetNote == null)
                {
                    MessageBox.Show(
                        "Заметка недоступна или перемещена в корзину.",
                        "Информация",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var childVm = new NoteEditorViewModel(
                    _viewModel.TagDetectionService,
                    _viewModel.AllTags,
                    existingNote: targetNote,
                    rules: _viewModel.Rules,
                    noteHistoryService: _viewModel.NoteHistoryService,
                    contextFactory: _viewModel.ContextFactory,
                    noteLinkService: _viewModel.NoteLinkService,
                    attachmentStorageService: _viewModel.AttachmentStorageService,
                    settingsService: _viewModel.SettingsService,
                    noteProtectionService: _viewModel.NoteProtectionService,
                    draftJournalService: _viewModel.DraftJournalService);

                childVm.Commit = () =>
                {
                    using var writeDb = _viewModel.ContextFactory();
                    using var tx = writeDb.Database.BeginTransaction();
                    try
                    {
                        var current = writeDb.Notes.Include(n => n.NoteTags).Single(n => n.Id == targetId);
                        if (childVm.IsPermanentlyDeleted)
                        {
                            writeDb.Notes.Remove(current);
                        }
                        else
                        {
                            childVm.ApplyToNote(current);
                            _viewModel.NoteHistoryService.SaveSnapshot(writeDb, current);
                        }
                        writeDb.SaveChanges();
                        tx.Commit();
                        _viewModel.DraftJournalService.DeleteJournal(childVm.DraftId);
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }

                    childVm.NotifyNoteSaved();
                };

                childVm.NoteSaved += () => _viewModel.NotifyNoteSaved();
                childVm.SetOcrHotkeySuspended = _viewModel.SetOcrHotkeySuspended;

                var childWin = new NoteEditorWindow(childVm);
                QuickNotes.App.Helpers.WindowActivationHelper.PrepareAndShowEditor(childWin, this);
                _viewModel.RefreshLinks();
            }
            catch (Exception ex)
            {
                QuickNotes.App.Services.ErrorLogService.Write("NoteEditor.OpenLinkedNote", ex);
            }
        };

        _viewModel.RequestSelectMatch = (start, len) =>
        {
            if (!_viewModel.IsPreviewMode)
            {
                NoteTextBox.Focus();
                NoteTextBox.Select(start, len);
                int lineIndex = NoteTextBox.GetLineIndexFromCharacterIndex(start);
                if (lineIndex >= 0)
                {
                    NoteTextBox.ScrollToLine(lineIndex);
                }
            }
        };

        _viewModel.RequestFocusFind = () =>
        {
            FindInputBox.Focus();
            FindInputBox.SelectAll();
        };

        _viewModel.RequestFocusEditor = () =>
        {
            if (!_viewModel.IsPreviewMode)
            {
                NoteTextBox.Focus();
            }
        };

        FindInputBox.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    _viewModel.FindPrevious();
                }
                else
                {
                    _viewModel.FindNext();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _viewModel.CloseFind();
                e.Handled = true;
            }
        };

        PreviewKeyDown += NoteEditorWindow_PreviewKeyDown;
        NoteTextBox.GotKeyboardFocus += (_, _) => _viewModel.SetOcrHotkeySuspended?.Invoke(true);
        NoteTextBox.LostKeyboardFocus += (_, _) => _viewModel.SetOcrHotkeySuspended?.Invoke(false);

        Loaded += (s, e) =>
        {
            QuickNotes.App.Helpers.WindowActivationHelper.ActivateToForeground(this);
            _viewModel.CheckAndPromptDraftRecovery(forcePrompt: true);
            if (string.IsNullOrWhiteSpace(TitleTextBox.Text))
            {
                TitleTextBox.Focus();
            }
            else
            {
                NoteTextBox.Focus();
                NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
            }
        };

        Closed += (s, e) =>
        {
            _viewModel.SetOcrHotkeySuspended?.Invoke(false);
            _viewModel.Dispose();
        };

        TitleTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                NoteTextBox.Focus();
                NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
                e.Handled = true;
            }
        };

        PreviewViewer.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnHyperlinkRequestNavigate));

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(NoteEditorViewModel.IsPreviewMode) && !_viewModel.IsPreviewMode)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NoteTextBox.Focus();
                }));
            }
        };
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri != null)
        {
            if (e.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                e.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.Uri.AbsoluteUri,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    QuickNotes.App.Services.ErrorLogService.Write("OpenBrowser", ex);
                }
            }
            else if (e.Uri.Scheme.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = !string.IsNullOrEmpty(e.Uri.Host) ? e.Uri.Host : e.Uri.AbsolutePath.Trim('/');
                if (int.TryParse(idStr, out int targetNoteId))
                {
                    _viewModel.OpenLinkedNote(targetNoteId);
                }
            }
        }
        e.Handled = true;
    }

    private void NoteEditorWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (NoteTextBox.IsKeyboardFocused)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            {
                if (e.Key != Key.ImeProcessed)
                {
                    var enterRes = MarkdownEditorOperations.HandleEnter(NoteTextBox.Text, NoteTextBox.SelectionStart, NoteTextBox.SelectionLength);
                    if (enterRes.Handled)
                    {
                        e.Handled = true;
                        if (enterRes.Action == ListContinuationAction.Terminate)
                        {
                            int lineStart = NoteTextBox.Text.LastIndexOf('\n', Math.Max(0, NoteTextBox.SelectionStart - 1)) + 1;
                            int lineEnd = NoteTextBox.Text.IndexOf('\n', NoteTextBox.SelectionStart);
                            if (lineEnd < 0) lineEnd = NoteTextBox.Text.Length;
                            NoteTextBox.Select(lineStart, lineEnd - lineStart);
                            NoteTextBox.SelectedText = string.Empty;
                        }
                        else
                        {
                            int selStart = NoteTextBox.SelectionStart;
                            string cont = enterRes.Text.Substring(selStart, enterRes.SelectionStart - selStart);
                            NoteTextBox.SelectedText = cont;
                        }
                        return;
                    }
                }
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                if (e.Key == Key.B && !isShift)
                {
                    _viewModel.ToggleBoldCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.I && !isShift)
                {
                    _viewModel.ToggleItalicCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (ShortcutCatalog.IsHeading(e))
                {
                    _viewModel.ToggleHeadingCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (isShift && e.Key == Key.C)
                {
                    _viewModel.ToggleCodeCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (isShift && e.Key == Key.U)
                {
                    _viewModel.ToggleBulletListCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (ShortcutCatalog.IsNumberedList(e))
                {
                    _viewModel.ToggleNumberedListCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (isShift && e.Key == Key.X)
                {
                    _viewModel.ToggleCheckboxCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.K && !isShift)
                {
                    if (NoteTextBox.SelectionLength > 0)
                    {
                        _viewModel.InsertMarkdownLinkCommand.Execute(null);
                    }
                    else if (_viewModel.InsertLinkCommand.CanExecute(null))
                    {
                        _viewModel.InsertLinkCommand.Execute(null);
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && System.Windows.Clipboard.ContainsImage())
        {
            int caret = NoteTextBox.IsKeyboardFocused ? NoteTextBox.SelectionStart : (NoteTextBox.Text?.Length ?? 0);
            if (_viewModel.TryPasteImageFromClipboard(caret, out var pasteMessage))
            {
                e.Handled = true;
            }
            else if (!string.IsNullOrEmpty(pasteMessage))
            {
                MessageBox.Show(pasteMessage, "Вложение", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
            }
            return;
        }

        if ((e.Key == Key.Enter || e.Key == Key.S) && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.Enter && _viewModel.IsMarkProcessedVisible && _viewModel.MarkProcessedAndNextCommand.CanExecute(null))
            {
                _viewModel.MarkProcessedAndNextCommand.Execute(null);
                e.Handled = true;
            }
            else if (_viewModel.SaveCommand.CanExecute(null))
            {
                _viewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_viewModel.TogglePreviewModeCommand.CanExecute(null))
            {
                _viewModel.TogglePreviewModeCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (ShortcutCatalog.IsHistory(e))
        {
            if (_viewModel.ToggleHistoryCommand.CanExecute(null))
            {
                _viewModel.ToggleHistoryCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _viewModel.OpenFind();
            FindInputBox.Focus();
            FindInputBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                _viewModel.FindPrevious();
            }
            else
            {
                _viewModel.FindNext();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_viewModel.IsFindOpen)
            {
                _viewModel.CloseFind();
                e.Handled = true;
            }
            else if (_viewModel.CancelCommand.CanExecute(null))
            {
                _viewModel.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult == true)
        {
            base.OnClosing(e);
            return;
        }

        if (DialogResult == false)
        {
            base.OnClosing(e);
            return;
        }

        if (!_viewModel.TryHandleUnsavedClose(out var shouldCloseWithoutSave, closeWindowOnSave: false))
        {
            e.Cancel = true;
            return;
        }

        if (!shouldCloseWithoutSave && _viewModel.HasRestoredVersion)
        {
            DialogResult = true;
        }

        base.OnClosing(e);
    }
}
