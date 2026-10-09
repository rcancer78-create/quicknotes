using System.Drawing;

namespace QuickNotes.App.Services;

/// <summary>
/// Modal screen-area picker used by OCR capture. Implementations must hide and
/// close themselves on cancel so the desktop is never left covered.
/// </summary>
public interface IScreenCropOverlay
{
    Rectangle? SelectedScreenRect { get; }

    bool? ShowModal();

    void RequestCancel();

    void ForceClose();
}
