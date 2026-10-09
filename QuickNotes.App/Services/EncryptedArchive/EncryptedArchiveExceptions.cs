using System;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Base type for encrypted-archive failures. Does not include secrets or plaintext.
/// </summary>
public class EncryptedArchiveException : Exception
{
    public EncryptedArchiveException(string message)
        : base(message)
    {
    }

    public EncryptedArchiveException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Wrong secret, tamper, or authenticated-data mismatch. One class for password, recovery key, and AEAD failures.
/// </summary>
public sealed class EncryptedArchiveSecurityException : EncryptedArchiveException
{
    public const string GenericUserMessage = "Неверный пароль/ключ или файл повреждён.";

    public EncryptedArchiveSecurityException()
        : base(GenericUserMessage)
    {
    }

    public EncryptedArchiveSecurityException(Exception inner)
        : base(GenericUserMessage, inner)
    {
    }
}

/// <summary>
/// Magic, framing, version, or KDF-limit failure that is raised before PBKDF2.
/// </summary>
public sealed class EncryptedArchiveFormatException : EncryptedArchiveException
{
    public EncryptedArchiveFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Payload/path/size/destination validation failure after (or instead of) AEAD.
/// </summary>
public sealed class EncryptedArchiveValidationException : EncryptedArchiveException
{
    public EncryptedArchiveValidationException(string message)
        : base(message)
    {
    }
}
