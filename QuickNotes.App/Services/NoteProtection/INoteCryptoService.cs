using System;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// Result of an authenticated encryption operation for a protected note object.
/// The password is never stored; only the derived envelope parameters are persisted,
/// including an explicit versioned KDF descriptor for new writes.
/// </summary>
public sealed class NoteEncryptionResult
{
    public int FormatVersion { get; init; }
    public int KdfIterations { get; init; }

    /// <summary>Explicit KDF descriptor for new envelopes. Never null for new writes.</summary>
    public KdfDescriptor KdfDescriptor => KdfDescriptor.Pbkdf2(KdfIterations);

    /// <summary>Canonical descriptor text persisted next to the ciphertext (non-secret metadata).</summary>
    public string KdfDescriptorText => KdfDescriptor.ToCanonicalText();

    public byte[] Salt { get; init; } = Array.Empty<byte>();
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Tag { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Cryptography contract for per-note password protection.
/// Uses standard .NET primitives only: AES-256-GCM, PBKDF2-HMAC-SHA256,
/// random per-note salt, unique nonce per encrypted object, authentication tag and
/// AAD bound to the format version, Note.SyncId and the object type.
/// The format and the KDF descriptor are both versioned to allow future changes.
/// </summary>
public interface INoteCryptoService
{
    /// <summary>Current note crypto payload format version.</summary>
    int CurrentFormatVersion { get; }

    /// <summary>Default PBKDF2 iteration count used when protecting a new note.</summary>
    int DefaultIterations { get; }

    /// <summary>Explicit KDF descriptor written next to every new ciphertext.</summary>
    KdfDescriptor CurrentDescriptor { get; }

    /// <summary>
    /// Derives a 256-bit key from a password with PBKDF2-HMAC-SHA256.
    /// The returned key is a plain managed array and must be zeroed by the caller when no longer needed.
    /// Work factor is bounds-checked before derivation.
    /// </summary>
    byte[] DeriveKey(string password, byte[] salt, int iterations);

    /// <summary>
    /// Resolves the KDF descriptor of a stored note envelope. Legacy envelopes without an
    /// explicit descriptor are accepted with their exact historical algorithm and iteration
    /// count and reported as <see cref="KdfEnvelopeState.LegacyMissingDescriptor"/>.
    /// Unknown algorithms, unknown descriptor versions, out-of-range work factors and
    /// unexpected salt lengths are rejected before any derivation.
    /// </summary>
    KdfEnvelopeReading ResolveDescriptor(string? descriptorText, int legacyIterations, int legacySaltByteSize);

    /// <summary>
    /// Encrypts using the provided derived key (for an already-unlocked note).
    /// Generates a fresh random nonce for each call; returns nonce/tag/ciphertext.
    /// The key is NOT zeroed by this method (the caller owns the key lifetime).
    /// </summary>
    NoteCryptoObject EncryptWithKey(byte[] key, byte[] plaintext, int formatVersion, Guid syncId, string objectType);

    /// <summary>
    /// Decrypts using the provided derived key. Throws <see cref="NoteProtectionSecurityException"/>
    /// on wrong key, tamper detection or malformed data - never returns partial plaintext.
    /// </summary>
    byte[] DecryptWithKey(byte[] key, byte[] ciphertext, byte[] nonce, byte[] tag, int formatVersion, Guid syncId, string objectType);

    /// <summary>
    /// One-shot encrypt for a new password: derives the key from the password, generates a fresh salt and nonce.
    /// The derived key is zeroed before returning.
    /// </summary>
    NoteEncryptionResult Encrypt(string password, byte[] plaintext, Guid syncId, string objectType, int? iterations = null);

    /// <summary>
    /// One-shot decrypt with a password; used to verify a password or decrypt historical envelopes
    /// when the key is not already stored in the session. Derived key is zeroed before returning.
    /// </summary>
    byte[] Decrypt(string password, byte[] ciphertext, byte[] salt, byte[] nonce, byte[] tag, Guid syncId, string objectType, int iterations);
}

/// <summary>
/// Raw ciphertext triple for an object encrypted with an already-derived key.
/// </summary>
public sealed class NoteCryptoObject
{
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] Tag { get; init; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
}
