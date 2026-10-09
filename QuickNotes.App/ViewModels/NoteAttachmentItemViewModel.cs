using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace QuickNotes.App.ViewModels;

public class NoteAttachmentItemViewModel : ViewModelBase
{
    private bool _isAvailable;

    public int Id { get; set; }
    public int NoteId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool IsNew { get; set; }

    public string FullPath { get; }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasStatusText));
                OnPropertyChanged(nameof(FullFileNameTooltip));
            }
        }
    }

    public bool CanOpen => IsAvailable;

    public string FormattedSize => AttachmentFileHelper.FormatFileSize(Size);

    public bool IsImage => AttachmentFileHelper.IsImage(ContentType, OriginalFileName);

    public string DisplayType => AttachmentFileHelper.GetDisplayTypeName(ContentType, OriginalFileName);

    public string StatusText => IsAvailable ? string.Empty : "Файл недоступен";

    public bool HasStatusText => !IsAvailable;

    public string TypeBadgeText
    {
        get
        {
            var ext = Path.GetExtension(OriginalFileName).TrimStart('.').ToUpperInvariant();
            if (IsImage) return "IMG";
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 4) return ext;
            return "📎";
        }
    }

    public string FullFileNameTooltip
    {
        get
        {
            var baseTooltip = $"{OriginalFileName}\nТип: {DisplayType} ({ContentType})\nРазмер: {FormattedSize}\nSHA-256: {Sha256}\nДобавлено: {CreatedAt:dd.MM.yyyy HH:mm}";
            return IsAvailable ? baseTooltip : $"{baseTooltip}\n\n⚠️ Файл недоступен на диске";
        }
    }

    public BitmapImage? Thumbnail { get; }

    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }

    public Action? RequestOpenExternal { get; set; }

    public NoteAttachmentItemViewModel(
        NoteAttachment attachment,
        string fullPath,
        Action<NoteAttachmentItemViewModel>? onDelete = null)
    {
        Id = attachment.Id;
        NoteId = attachment.NoteId;
        OriginalFileName = attachment.OriginalFileName;
        StoredFileName = attachment.StoredFileName;
        RelativePath = attachment.RelativePath;
        ContentType = attachment.ContentType;
        Size = attachment.Size;
        Sha256 = attachment.Sha256;
        CreatedAt = attachment.CreatedAt;
        FullPath = fullPath;

        _isAvailable = !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);

        if (_isAvailable && IsImage)
        {
            Thumbnail = LoadSafeThumbnail(fullPath);
        }

        OpenCommand = new RelayCommand(Open, () => CanOpen);
        DeleteCommand = new RelayCommand(() => onDelete?.Invoke(this));
    }

    public NoteAttachmentItemViewModel(
        AttachmentSaveResult saveResult,
        int noteId,
        Action<NoteAttachmentItemViewModel>? onDelete = null)
    {
        Id = 0;
        NoteId = noteId;
        OriginalFileName = saveResult.OriginalFileName;
        StoredFileName = saveResult.StoredFileName;
        RelativePath = saveResult.RelativePath;
        ContentType = saveResult.ContentType;
        Size = saveResult.Size;
        Sha256 = saveResult.Sha256;
        CreatedAt = DateTime.Now;
        FullPath = saveResult.FullPath;
        IsNew = true;

        _isAvailable = !string.IsNullOrWhiteSpace(saveResult.FullPath) && File.Exists(saveResult.FullPath);

        if (_isAvailable && IsImage)
        {
            Thumbnail = LoadSafeThumbnail(saveResult.FullPath);
        }

        OpenCommand = new RelayCommand(Open, () => CanOpen);
        DeleteCommand = new RelayCommand(() => onDelete?.Invoke(this));
    }

    private static BitmapImage? LoadSafeThumbnail(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void Open()
    {
        if (RequestOpenExternal != null)
        {
            RequestOpenExternal();
            return;
        }

        if (!IsAvailable || string.IsNullOrWhiteSpace(FullPath))
        {
            MessageBox.Show("Файл недоступен на диске.", "Вложение недоступно", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FullPath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("OpenAttachment", ex);
            MessageBox.Show($"Не удалось открыть файл:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public NoteAttachment ToEntity()
    {
        return new NoteAttachment
        {
            Id = Id,
            NoteId = NoteId,
            OriginalFileName = OriginalFileName,
            StoredFileName = StoredFileName,
            RelativePath = RelativePath,
            ContentType = ContentType,
            Size = Size,
            Sha256 = Sha256,
            CreatedAt = CreatedAt
        };
    }
}
