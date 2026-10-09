using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public sealed class GenerationResolveResult
{
    public bool Success { get; init; }
    public bool PointerMissing { get; init; }
    public string? Error { get; init; }
    public SyncGenerationPointer Pointer { get; init; } = new();
    public string? ETag { get; init; }

    public static GenerationResolveResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}

/// <summary>
/// Loads generation.json and applies ActiveGenerationId to the shared key helper.
/// Missing pointer = legacy layout (empty generation). Corrupt pointer is fail-closed.
/// </summary>
public static class SyncGenerationResolver
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<GenerationResolveResult> ResolveAndApplyAsync(
        ICloudObjectStoreTransport transport,
        SyncObjectKeyHelper keyHelper,
        CancellationToken ct = default)
    {
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        if (keyHelper == null) throw new ArgumentNullException(nameof(keyHelper));

        string pointerKey = keyHelper.GetGenerationPointerKey();
        StorageObjectResult? obj;
        try
        {
            obj = await transport.GetObjectAsync(pointerKey, ct).ConfigureAwait(false);
        }
        catch (CloudOfflineException)
        {
            throw;
        }
        catch (CloudAuthException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GenerationResolveResult.Fail(
                $"Не удалось прочитать указатель поколения: {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
        }

        if (obj == null || obj.Content == null || obj.Content.Length == 0)
        {
            keyHelper.ApplyGeneration(Guid.Empty);
            return new GenerationResolveResult
            {
                Success = true,
                PointerMissing = true,
                Pointer = new SyncGenerationPointer { FormatVersion = FormatVersion, ActiveGenerationId = Guid.Empty },
                ETag = null
            };
        }

        SyncGenerationPointer? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SyncGenerationPointer>(obj.Content, JsonOptions);
        }
        catch (Exception ex)
        {
            return GenerationResolveResult.Fail(
                $"Указатель поколения повреждён (невалидный JSON): {CloudErrorSanitizer.SanitizeDiagnosticDetail(ex.Message)}");
        }

        if (parsed == null)
            return GenerationResolveResult.Fail("Указатель поколения пуст.");

        if (parsed.FormatVersion != FormatVersion)
            return GenerationResolveResult.Fail($"Неподдерживаемая версия указателя поколения ({parsed.FormatVersion}).");

        if (parsed.PendingGenerationId.HasValue && parsed.PendingGenerationId.Value == Guid.Empty)
            parsed.PendingGenerationId = null;
        if (parsed.PreviousGenerationId.HasValue && parsed.PreviousGenerationId.Value == Guid.Empty)
            parsed.PreviousGenerationId = null;

        keyHelper.ApplyGeneration(parsed.ActiveGenerationId);
        return new GenerationResolveResult
        {
            Success = true,
            Pointer = parsed,
            ETag = StorageObjectMetadata.NormalizeETag(obj.Metadata?.ETag)
        };
    }
}
