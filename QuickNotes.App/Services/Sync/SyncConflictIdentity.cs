using System;
using System.Security.Cryptography;
using System.Text;

namespace QuickNotes.App.Services.Sync;

internal static class SyncConflictIdentity
{
    public static Guid DeriveKeepBothSyncId(Guid originalSyncId, Guid remoteRevisionId)
        => Derive("keep-both-entity", originalSyncId, remoteRevisionId);

    public static Guid DeriveRevisionId(Guid syncId, Guid remoteRevisionId, string action)
        => Derive(action, syncId, remoteRevisionId);

    public static Guid Derive(string action, Guid a, Guid b)
    {
        Span<byte> dst = stackalloc byte[16];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(action ?? string.Empty));
        sha.AppendData(a.ToByteArray());
        sha.AppendData(b.ToByteArray());
        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset(hash);
        hash[..16].CopyTo(dst);
        dst[6] = (byte)((dst[6] & 0x0F) | 0x40);
        dst[8] = (byte)((dst[8] & 0x3F) | 0x80);
        return new Guid(dst);
    }
}
