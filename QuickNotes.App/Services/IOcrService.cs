using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface IOcrService
{
    bool IsSupported { get; }
    Task<OcrResult> RecognizeTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
