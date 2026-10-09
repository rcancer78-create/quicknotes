using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface IScreenCaptureService
{
    Task<ScreenCaptureResult> CaptureAreaAsync(CancellationToken cancellationToken = default);
}
