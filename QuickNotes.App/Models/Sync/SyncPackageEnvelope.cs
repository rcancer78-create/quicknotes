using System;
using System.Text.Json.Serialization;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Outer technical envelope for a portable QuickNotes sync package.
/// Exposes ONLY minimal technical metadata required for envelope routing and AEAD decryption.
/// Never contains note text, filenames, tags, or plaintext keys.
///
/// KDF descriptor versioning note: <see cref="SyncPackageEnvelope.GetAssociatedData"/> is
/// unchanged by this stage. It already binds the work factor through
/// <c>Crypto.KdfIterations</c>, and it deliberately does not include the new descriptor text,
/// so legacy packages keep a byte-for-byte identical AAD and stay readable.
/// </summary>
public class SyncPackageEnvelope
{
    public string Magic { get; set; } = "QNSP";
    public int FormatVersion { get; set; } = 1;
    public Guid PackageId { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public SyncCryptoHeader Crypto { get; set; } = new();
    public string EncryptedPayloadBase64 { get; set; } = string.Empty;

    public byte[] GetAssociatedData()
    {
        string aad = $"{Magic}|{FormatVersion}|{PackageId:D}|{DeviceId:D}|{Crypto.Algorithm}|{Crypto.KdfAlgorithm}|{Crypto.KdfVersion}|{Crypto.KdfIterations}";
        return System.Text.Encoding.UTF8.GetBytes(aad);
    }
}

public class SyncCryptoHeader
{
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string KdfAlgorithm { get; set; } = KdfAlgorithmIds.Pbkdf2HmacSha256;
    public int KdfVersion { get; set; } = KdfDescriptorConstants.CurrentDescriptorVersion;
    public int KdfIterations { get; set; } = 100_000;

    /// <summary>
    /// Explicit versioned KDF descriptor (<c>PBKDF2-HMAC-SHA256|1|iterations|32</c>).
    /// Null/empty means a legacy package: readers use <see cref="KdfAlgorithm"/>,
    /// <see cref="KdfVersion"/> and <see cref="KdfIterations"/> exactly and report the package
    /// as needing migration. Non-secret metadata only; never part of the AAD.
    ///
    /// Legacy serialized shape is preserved: a null descriptor is omitted from the JSON instead
    /// of being written as <c>"kdfDescriptor":null</c>, so old readers see the same envelope.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KdfDescriptor { get; set; }

    public string SaltBase64 { get; set; } = string.Empty;
    public string NonceBase64 { get; set; } = string.Empty;
    public string TagBase64 { get; set; } = string.Empty;
}
