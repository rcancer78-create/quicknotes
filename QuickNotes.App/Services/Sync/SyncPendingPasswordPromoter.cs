using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// If the cloud generation already accepts the DPAPI pending password, promote it to active.
/// Used after a crash between generation switch and local promote.
/// </summary>
public static class SyncPendingPasswordPromoter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<string> TryPromoteAsync(
        ISyncPasswordStorage storage,
        ICloudObjectStoreTransport transport,
        SyncObjectKeyHelper keyHelper,
        ISyncCryptoService crypto,
        string currentPassword,
        CancellationToken ct)
    {
        if (storage == null || !storage.HasPendingPassword())
            return currentPassword;

        string? pending;
        try
        {
            pending = await storage.LoadPendingPasswordAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return currentPassword;
        }

        if (string.IsNullOrEmpty(pending))
            return currentPassword;

        bool pendingReads = await CurrentGenerationAcceptsPasswordAsync(
            transport, keyHelper, crypto, pending, ct).ConfigureAwait(false);
        if (!pendingReads)
            return currentPassword;

        try
        {
            await storage.PromotePendingPasswordAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return pending;
        }

        return pending;
    }

    public static async Task<bool> CurrentGenerationAcceptsPasswordAsync(
        ICloudObjectStoreTransport transport,
        SyncObjectKeyHelper keyHelper,
        ISyncCryptoService crypto,
        string password,
        CancellationToken ct)
    {
        string devicesPrefix = keyHelper.GetDevicesPrefix();
        StorageListResult page;
        try
        {
            page = await transport.ListObjectsV2Async(new StorageListRequest
            {
                Prefix = devicesPrefix,
                MaxKeys = 200
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (page.Objects == null || page.Objects.Count == 0)
            return false;

        foreach (var obj in page.Objects)
        {
            if (!keyHelper.TryParsePackageKey(obj.Key, out _, out _))
                continue;

            StorageObjectResult? pkg = await transport.GetObjectAsync(obj.Key, ct).ConfigureAwait(false);
            if (pkg?.Content == null || pkg.Content.Length == 0)
                continue;

            try
            {
                DecryptPackage(pkg.Content, password, crypto);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    internal static byte[] DecryptPackage(byte[] packageBytes, string password, ISyncCryptoService crypto)
    {
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageBytes, JsonOptions)
            ?? throw new SyncSecurityException("Пустой envelope пакета.");
        if (!string.Equals(envelope.Magic, "QNSP", StringComparison.Ordinal))
            throw new SyncSecurityException("Неверная сигнатура пакета.");
        if (envelope.Crypto == null ||
            !string.Equals(envelope.Crypto.Algorithm, SyncCryptoService.AlgorithmName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncSecurityException("Неподдерживаемые параметры криптографического конверта.");
        }

        // KDF descriptor is validated before derivation; legacy packages keep working.
        KdfEnvelopeReading reading = crypto.ResolveDescriptor(
            envelope.Crypto.KdfDescriptor,
            envelope.Crypto.KdfAlgorithm,
            envelope.Crypto.KdfVersion,
            envelope.Crypto.KdfIterations,
            SyncCryptoService.SaltByteSize);

        byte[] ciphertext = Convert.FromBase64String(envelope.EncryptedPayloadBase64);
        byte[] salt = Convert.FromBase64String(envelope.Crypto.SaltBase64);
        byte[] nonce = Convert.FromBase64String(envelope.Crypto.NonceBase64);
        byte[] tag = Convert.FromBase64String(envelope.Crypto.TagBase64);
        return crypto.DecryptPayload(
            ciphertext, salt, nonce, tag, password, envelope.GetAssociatedData(), reading.Descriptor.Iterations);
    }
}
