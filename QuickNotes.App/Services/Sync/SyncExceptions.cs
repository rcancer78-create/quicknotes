using System;

namespace QuickNotes.App.Services.Sync;

public class SyncSecurityException : Exception
{
    public SyncSecurityException(string message) : base(message) { }
    public SyncSecurityException(string message, Exception inner) : base(message, inner) { }
}

public class SyncValidationException : Exception
{
    public SyncValidationException(string message) : base(message) { }
    public SyncValidationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Fail-closed stop of a sync cycle because untrusted inbound KDF work would exceed
/// the per-cycle budget. Distinct from offline, auth, cancel and timeout.
/// The message is non-secret (no password, salt, key, ciphertext or object key).
/// </summary>
public sealed class SyncKdfWorkBudgetExceededException : Exception
{
    public SyncKdfWorkBudgetExceededException(string message) : base(message) { }
}
