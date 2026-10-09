using System;
using System.Globalization;
using System.Threading;

namespace QuickNotes.App.Services.Crypto;

/// <summary>
/// Neutral, non-secret validation failure for a KDF descriptor.
/// Callers translate it into their envelope-specific exception
/// (<c>NoteProtectionSecurityException</c>, <c>SyncSecurityException</c>,
/// <c>EncryptedArchiveFormatException</c>). Never contains key material, salt or plaintext.
/// </summary>
public sealed class KdfDescriptorValidationException : Exception
{
    public KdfDescriptorValidationException(string message) : base(message) { }

    public KdfDescriptorValidationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Single source of truth for KDF algorithm identifiers used by every encrypted envelope.
/// </summary>
public static class KdfAlgorithmIds
{
    /// <summary>Only algorithm accepted by readers in this stage. Argon2id is an isolated Tools spike (ADR-016), not a production reader.</summary>
    public const string Pbkdf2HmacSha256 = "PBKDF2-HMAC-SHA256";

    /// <summary>Compact id for binary envelopes (QNAT) that cannot carry a string safely.</summary>
    public const byte Pbkdf2HmacSha256Id = 0x01;
}

/// <summary>
/// Shared descriptor constants.
///
/// The iteration ceiling is a resource-exhaustion bound, not a benchmarked cost target.
/// This stage does not change any effective PBKDF2 cost. Argon2id is not a production algorithm.
/// </summary>
public static class KdfDescriptorConstants
{
    /// <summary>Descriptor contract version written by new envelopes.</summary>
    public const int CurrentDescriptorVersion = 1;

    /// <summary>Salt length used by every supported v1 envelope.</summary>
    public const int SaltByteSize = 32;

    /// <summary>
    /// Ceiling accepted from any envelope before derivation starts. Matches the archive
    /// reader bound; protects note/sync/blob readers from a hostile work factor.
    /// </summary>
    public const int MaxPbkdf2Iterations = 5_000_000;

    /// <summary>
    /// Floor for envelopes that may come from another device. Values below it are degenerate
    /// descriptors, not weak-but-real ones. The archive reader keeps its stricter 10_000 floor.
    /// </summary>
    public const int MinPbkdf2IterationsUntrusted = 1;

    /// <summary>Strict archive floor (ADR-002 / ADR-004), kept byte-for-byte identical.</summary>
    public const int MinPbkdf2IterationsArchiveUntrusted = 10_000;
}

/// <summary>
/// Accepted iteration and salt bounds for one envelope family.
/// The lower bound guards degenerate descriptors, the upper bound guards resource exhaustion.
/// </summary>
public readonly struct KdfDescriptorLimits
{
    public int MinIterations { get; }

    public int MaxIterations { get; }

    public int SaltByteSize { get; }

    public KdfDescriptorLimits(int minIterations, int maxIterations, int saltByteSize)
    {
        if (minIterations < 1 || maxIterations < minIterations)
        {
            throw new KdfDescriptorValidationException("Некорректные границы KDF.");
        }

        if (saltByteSize != KdfDescriptorConstants.SaltByteSize)
        {
            throw new KdfDescriptorValidationException("Некорректная длина соли KDF.");
        }

        MinIterations = minIterations;
        MaxIterations = maxIterations;
        SaltByteSize = saltByteSize;
    }

    /// <summary>Archive files from disk or cloud: strict historical floor (10_000).</summary>
    public static KdfDescriptorLimits ArchiveUntrusted { get; } = new(
        KdfDescriptorConstants.MinPbkdf2IterationsArchiveUntrusted,
        KdfDescriptorConstants.MaxPbkdf2Iterations,
        KdfDescriptorConstants.SaltByteSize);

    /// <summary>Note envelopes in the local SQLite profile (note/revision/attachment rows, QNAT files).</summary>
    public static KdfDescriptorLimits LocalEnvelope { get; } = new(
        KdfDescriptorConstants.MinPbkdf2IterationsUntrusted,
        KdfDescriptorConstants.MaxPbkdf2Iterations,
        KdfDescriptorConstants.SaltByteSize);

    /// <summary>Sync packages and attachment blobs (may arrive from a peer device).</summary>
    public static KdfDescriptorLimits CloudEnvelope { get; } = new(
        KdfDescriptorConstants.MinPbkdf2IterationsUntrusted,
        KdfDescriptorConstants.MaxPbkdf2Iterations,
        KdfDescriptorConstants.SaltByteSize);
}

/// <summary>
/// How a stored envelope's KDF parameters were resolved.
/// <see cref="Current"/> means an explicit descriptor was present and accepted.
/// <see cref="LegacyMissingDescriptor"/> means the envelope predates descriptor versioning and
/// was read with its exact historical defaults; rewriting it is a separate, explicitly
/// triggered migration, never an implicit side effect of reading.
/// </summary>
public enum KdfEnvelopeState
{
    Current = 0,
    LegacyMissingDescriptor = 1
}

/// <summary>Resolved KDF descriptor plus the compatibility state of the stored envelope.</summary>
public readonly struct KdfEnvelopeReading
{
    public KdfDescriptor Descriptor { get; }

    public KdfEnvelopeState State { get; }

    public KdfEnvelopeReading(KdfDescriptor descriptor, KdfEnvelopeState state)
    {
        Descriptor = descriptor;
        State = state;
    }

    /// <summary>True when an explicit, versioned descriptor is already stored.</summary>
    public bool IsCurrent => State == KdfEnvelopeState.Current;

    /// <summary>True when the envelope still relies on historical defaults.</summary>
    public bool NeedsMigration => State == KdfEnvelopeState.LegacyMissingDescriptor;
}

/// <summary>
/// Explicit, versioned KDF parameters carried next to ciphertext:
/// algorithm id, descriptor version, work factor and salt length.
///
/// A descriptor is metadata only. It authenticates nothing by itself and is deliberately
/// NOT part of any AAD in this stage: extending AAD would break byte-for-byte legacy
/// compatibility. Tampering with the descriptor changes the derived key, so AEAD
/// authentication fails closed.
/// </summary>
public readonly struct KdfDescriptor
{
    public string AlgorithmId { get; }

    public int DescriptorVersion { get; }

    public int Iterations { get; }

    public int SaltByteSize { get; }

    public KdfDescriptor(string algorithmId, int descriptorVersion, int iterations, int saltByteSize)
    {
        AlgorithmId = algorithmId ?? string.Empty;
        DescriptorVersion = descriptorVersion;
        Iterations = iterations;
        SaltByteSize = saltByteSize;
    }

    /// <summary>Descriptor for a new PBKDF2 envelope with the given work factor.</summary>
    public static KdfDescriptor Pbkdf2(int iterations)
        => new(
            KdfAlgorithmIds.Pbkdf2HmacSha256,
            KdfDescriptorConstants.CurrentDescriptorVersion,
            iterations,
            KdfDescriptorConstants.SaltByteSize);

    /// <summary>
    /// Validates algorithm id, descriptor version, work factor and salt length without
    /// deriving anything. Throws before any PBKDF2 call, which is what bounds
    /// resource-exhaustion inputs.
    /// </summary>
    public void Validate(KdfDescriptorLimits limits)
    {
        if (!string.Equals(AlgorithmId, KdfAlgorithmIds.Pbkdf2HmacSha256, StringComparison.Ordinal))
        {
            throw new KdfDescriptorValidationException("Неизвестный алгоритм KDF.");
        }

        if (DescriptorVersion != KdfDescriptorConstants.CurrentDescriptorVersion)
        {
            throw new KdfDescriptorValidationException("Неподдерживаемая версия дескриптора KDF.");
        }

        ValidateIterations(Iterations, limits);

        if (SaltByteSize != limits.SaltByteSize)
        {
            throw new KdfDescriptorValidationException("Недопустимая длина соли KDF.");
        }
    }

    /// <summary>Work-factor bounds check usable without a full descriptor (legacy envelopes).</summary>
    public static void ValidateIterations(int iterations, KdfDescriptorLimits limits)
    {
        if (iterations < limits.MinIterations || iterations > limits.MaxIterations)
        {
            throw new KdfDescriptorValidationException("Недопустимый work factor KDF.");
        }
    }

    public bool Matches(KdfDescriptor other)
        => string.Equals(AlgorithmId, other.AlgorithmId, StringComparison.Ordinal)
            && DescriptorVersion == other.DescriptorVersion
            && Iterations == other.Iterations
            && SaltByteSize == other.SaltByteSize;

    /// <summary>Non-secret, log-safe rendering. Contains no salt, key or plaintext.</summary>
    public override string ToString()
    {
        return AlgorithmId
            + "/v" + DescriptorVersion.ToString(CultureInfo.InvariantCulture)
            + "/n=" + Iterations.ToString(CultureInfo.InvariantCulture)
            + "/salt=" + SaltByteSize.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Canonical wire form: <c>ALG|descriptorVersion|iterations|saltByteSize</c>,
    /// for example <c>PBKDF2-HMAC-SHA256|1|120000|32</c>.
    /// </summary>
    public string ToCanonicalText()
    {
        return AlgorithmId
            + "|" + DescriptorVersion.ToString(CultureInfo.InvariantCulture)
            + "|" + Iterations.ToString(CultureInfo.InvariantCulture)
            + "|" + SaltByteSize.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Strict parse of a canonical descriptor. Unknown algorithm ids, unknown descriptor
    /// versions, non-canonical integers, out-of-range work factors and unexpected salt
    /// lengths are rejected. The error code is fixed and non-secret.
    /// </summary>
    public static bool TryParse(string? text, KdfDescriptorLimits limits, out KdfDescriptor descriptor, out string error)
    {
        descriptor = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty descriptor";
            return false;
        }

        string[] parts = text.Split('|');
        if (parts.Length != 4)
        {
            error = "malformed descriptor";
            return false;
        }

        if (!string.Equals(parts[0], KdfAlgorithmIds.Pbkdf2HmacSha256, StringComparison.Ordinal))
        {
            error = "unsupported kdf algorithm";
            return false;
        }

        if (!TryParseCanonicalInt(parts[1], out int descriptorVersion)
            || descriptorVersion != KdfDescriptorConstants.CurrentDescriptorVersion)
        {
            error = "unsupported descriptor version";
            return false;
        }

        if (!TryParseCanonicalInt(parts[2], out int iterations))
        {
            error = "malformed work factor";
            return false;
        }

        if (iterations < limits.MinIterations || iterations > limits.MaxIterations)
        {
            error = "work factor out of bounds";
            return false;
        }

        if (!TryParseCanonicalInt(parts[3], out int saltByteSize)
            || saltByteSize != limits.SaltByteSize)
        {
            error = "unexpected salt length";
            return false;
        }

        descriptor = new KdfDescriptor(
            KdfAlgorithmIds.Pbkdf2HmacSha256,
            descriptorVersion,
            iterations,
            saltByteSize);
        return true;
    }

    private static bool TryParseCanonicalInt(string raw, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return false;
        }

        // Reject "0123", "+5", leading whitespace and other non-canonical spellings.
        if (!string.Equals(raw, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public override bool Equals(object? obj) => obj is KdfDescriptor other && Matches(other);

    public override int GetHashCode() => HashCode.Combine(AlgorithmId, DescriptorVersion, Iterations, SaltByteSize);

    public static bool operator ==(KdfDescriptor left, KdfDescriptor right) => left.Matches(right);

    public static bool operator !=(KdfDescriptor left, KdfDescriptor right) => !left.Matches(right);
}

/// <summary>
/// Process-wide PBKDF2 invocation counter. Used by tests to prove that malformed or hostile
/// descriptors are rejected <em>before</em> any expensive derivation. Carries no secret.
/// </summary>
public static class KdfDiagnostics
{
    private static long _pbkdf2Invocations;

    public static long Pbkdf2Invocations => Interlocked.Read(ref _pbkdf2Invocations);

    public static void ResetPbkdf2Invocations() => Interlocked.Exchange(ref _pbkdf2Invocations, 0);

    internal static void CountPbkdf2Invocation() => Interlocked.Increment(ref _pbkdf2Invocations);
}
