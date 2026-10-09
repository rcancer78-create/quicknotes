using System;
using System.Security.Cryptography;
using System.Text;

namespace QuickNotes.App.Models.Sync;

/// <summary>
/// In-memory holder for S3 credentials (Access Key ID and Secret Access Key).
/// Overrides ToString() to guarantee that secret values are NEVER leaked to logs,
/// string formatters, or exceptions.
/// </summary>
public sealed class S3Credentials : IEquatable<S3Credentials>
{
    public string AccessKeyId { get; }
    public string SecretAccessKey { get; }

    public S3Credentials(string accessKeyId, string secretAccessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId))
            throw new ArgumentException("Access Key ID не может быть пустым.", nameof(accessKeyId));
        if (string.IsNullOrWhiteSpace(secretAccessKey))
            throw new ArgumentException("Secret Access Key не может быть пустым.", nameof(secretAccessKey));

        AccessKeyId = accessKeyId.Trim();
        SecretAccessKey = secretAccessKey.Trim();
    }

    /// <summary>
    /// Explicitly redact secrets in any string conversion.
    /// </summary>
    public override string ToString() => "S3Credentials [PROTECTED]";

    public bool Equals(S3Credentials? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        byte[] keyA = Encoding.UTF8.GetBytes(AccessKeyId);
        byte[] keyB = Encoding.UTF8.GetBytes(other.AccessKeyId);
        byte[] secA = Encoding.UTF8.GetBytes(SecretAccessKey);
        byte[] secB = Encoding.UTF8.GetBytes(other.SecretAccessKey);

        return CryptographicOperations.FixedTimeEquals(keyA, keyB) &&
               CryptographicOperations.FixedTimeEquals(secA, secB);
    }

    public override bool Equals(object? obj) => Equals(obj as S3Credentials);

    public override int GetHashCode()
    {
        return HashCode.Combine(AccessKeyId.Length, SecretAccessKey.Length);
    }
}
