using System;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Result of one authenticated sync encryption operation.
/// Carries the explicit KDF descriptor text that must be written next to the ciphertext.
/// </summary>
public class EncryptedPayloadResult
{
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();
    public string Algorithm { get; set; } = SyncCryptoService.AlgorithmName;
    public string KdfAlgorithm { get; set; } = KdfAlgorithmIds.Pbkdf2HmacSha256;
    public int KdfVersion { get; set; } = KdfDescriptorConstants.CurrentDescriptorVersion;
    public int KdfIterations { get; set; } = SyncCryptoService.DefaultIterations;

    /// <summary>Canonical descriptor text persisted next to the ciphertext (non-secret metadata).</summary>
    public string KdfDescriptorText => KdfDescriptor.Pbkdf2(KdfIterations).ToCanonicalText();
}

public interface ISyncCryptoService
{
    int Iterations { get; }

    /// <summary>Explicit KDF descriptor written next to every new sync ciphertext.</summary>
    KdfDescriptor CurrentDescriptor { get; }

    EncryptedPayloadResult EncryptPayload(byte[] plaintextBytes, string password, byte[] associatedData);

    byte[] DecryptPayload(byte[] ciphertext, byte[] salt, byte[] nonce, byte[] tag, string password, byte[] associatedData, int iterations, UntrustedInboundKdfWorkBudget? inboundKdfBudget = null);

    /// <summary>
    /// Resolves the KDF parameters of a stored sync envelope.
    /// Empty descriptor text means a legacy envelope; it is accepted with the exact historical
    /// iteration count reported by the envelope and flagged as needing migration.
    /// Unknown algorithms/versions, out-of-range work factors and bad salt lengths are rejected
    /// before any derivation.
    /// </summary>
    KdfEnvelopeReading ResolveDescriptor(string? descriptorText, string? kdfAlgorithm, int kdfVersion, int iterations, int saltByteSize);
}
