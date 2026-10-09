using System;

namespace QuickNotes.App.Services.Sync;

public enum CloudErrorCode
{
    Offline,
    Timeout,
    Authentication,
    NotFound,
    Conflict,
    QuotaExceeded,
    Corruption,
    General
}

/// <summary>
/// Base exception for cloud storage transport and synchronization operations.
/// Sanitizes error messages to ensure that access keys, tokens, and note contents
/// are never captured in the exception message or stack trace.
/// </summary>
public class CloudStorageException : Exception
{
    public CloudErrorCode ErrorCode { get; }
    public int? StatusCode { get; }
    public string? ObjectKey { get; }

    public CloudStorageException(
        CloudErrorCode errorCode,
        string message,
        int? statusCode = null,
        string? objectKey = null,
        Exception? innerException = null)
        : base(Sanitize(message), innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        ObjectKey = objectKey;
    }

    public override string ToString()
        => CloudErrorSanitizer.RedactSecrets(base.ToString());

    protected static string Sanitize(string message)
        => CloudErrorSanitizer.RedactSecrets(message);
}

public class CloudOfflineException : CloudStorageException
{
    public CloudOfflineException(string message, Exception? inner = null)
        : base(CloudErrorCode.Offline, message, null, null, inner) { }
}

public class CloudAuthException : CloudStorageException
{
    public CloudAuthException(string message, int? statusCode = null, Exception? inner = null)
        : base(CloudErrorCode.Authentication, message, statusCode, null, inner) { }
}

public class CloudConflictException : CloudStorageException
{
    public string? ExpectedETag { get; }
    public string? ActualETag { get; }

    public CloudConflictException(
        string message,
        string? objectKey = null,
        string? expectedETag = null,
        string? actualETag = null,
        int? statusCode = null,
        Exception? inner = null)
        : base(CloudErrorCode.Conflict, message, statusCode, objectKey, inner)
    {
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }
}

public class CloudQuotaException : CloudStorageException
{
    public CloudQuotaException(string message, int? statusCode = null, Exception? inner = null)
        : base(CloudErrorCode.QuotaExceeded, message, statusCode, null, inner) { }
}

public class CloudCorruptionException : CloudStorageException
{
    public CloudCorruptionException(string message, string? objectKey = null, Exception? inner = null)
        : base(CloudErrorCode.Corruption, message, null, objectKey, inner) { }
}
