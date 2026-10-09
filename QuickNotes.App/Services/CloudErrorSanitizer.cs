using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickNotes.App.Services;

/// <summary>
/// Redacts provider error text, signed URLs, and credential patterns from
/// transport exceptions, exception logs, and structured sync diagnostics.
/// Does not invent S3 metadata-header APIs.
/// </summary>
public static class CloudErrorSanitizer
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex UrlWithOptionalQuery = CreateRegex(
        @"https?://[^\s""']{1,2048}");

    private static readonly Regex UserInfoUrl = CreateRegex(
        @"https?://[^/\s""']{1,256}:[^@/\s""']{1,512}@");

    private static readonly Regex AuthorizationHeader = CreateRegex(
        @"(Authorization""?\s*[:=]\s*)(?:""[^""]{0,2048}""|AWS4-HMAC-SHA256(?:[ \t]+(?:Credential|SignedHeaders|Signature)=[^\s,;]{1,512})(?:,[ \t]*(?:Credential|SignedHeaders|Signature)=[^\s,;]{1,512})*|[^\s,;""'\r\n}]{0,2048})");

    private static readonly Regex Aws4Credential = CreateRegex(
        @"(AWS4-HMAC-SHA256\s+Credential=)[^\s,]{1,512}");

    private static readonly Regex CredentialEquals = CreateRegex(
        @"(Credential=)[^\s,;&""']{1,512}");

    private static readonly Regex SignatureValue = CreateRegex(
        @"(Signature=)[^\s,;&""']{1,512}");

    private static readonly Regex SecretKey = CreateRegex(
        @"(Secret(Access)?Key\s*[=:]\s*)[^\s,;]{1,512}");

    private static readonly Regex AccessKey = CreateRegex(
        @"((?:AWS)?AccessKey(?:Id)?\s*[=:]\s*)[^\s,;&""']{1,512}");

    private static readonly Regex LabeledPassword = CreateRegex(
        @"((?:password|passwd)\s*[=:]\s*)[^\s,;]{1,512}");

    private static readonly Regex LabeledNoteDump = CreateRegex(
        @"(note=)[^\s,;]{1,512}");

    private static readonly Regex LabeledRecovery = CreateRegex(
        @"(recovery(?:[-_ ]?(?:key|material|wrap))?=)[^\s,;]{1,512}");

    private static readonly Regex AmzQuery = CreateRegex(
        @"[?&](X-Amz-Credential|X-Amz-Signature|X-Amz-Security-Token|AWSAccessKeyId|Signature|Credential)=[^&\s]{1,2048}");

    private static readonly HashSet<string> SecretQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Amz-Credential",
        "X-Amz-Signature",
        "X-Amz-Security-Token",
        "AWSAccessKeyId",
        "Signature",
        "Credential",
        "Security-Token",
        "session",
        "password",
        "passwd",
        "secret",
        "secretAccessKey",
        "accessKey",
        "access_key",
        "token",
        "auth",
        "authorization"
    };

    private static Regex CreateRegex(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);

    public static bool ContainsSigningMaterial(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("AWS4-HMAC-SHA256", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Credential=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SecretAccessKey", StringComparison.OrdinalIgnoreCase)
            || text.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase)
            || text.Contains("X-Amz-Credential", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AWSAccessKeyId", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authorization:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authorization=", StringComparison.OrdinalIgnoreCase);
    }

    public static string RedactSecrets(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        try
        {
            string sanitized = UserInfoUrl.Replace(message, "https://[redacted]@");
            sanitized = UrlWithOptionalQuery.Replace(sanitized, static m => RedactUrlQuery(m.Value));
            sanitized = AmzQuery.Replace(sanitized, static m =>
            {
                int eq = m.Value.IndexOf('=');
                return eq >= 0 ? m.Value[..(eq + 1)] + "[REDACTED]" : "[REDACTED]";
            });
            sanitized = AuthorizationHeader.Replace(sanitized, static m => RedactAuthorization(m));
            sanitized = Aws4Credential.Replace(sanitized, "$1[REDACTED]");
            sanitized = CredentialEquals.Replace(sanitized, "$1[REDACTED]");
            sanitized = SignatureValue.Replace(sanitized, "$1[REDACTED]");
            sanitized = SecretKey.Replace(sanitized, "$1[REDACTED]");
            sanitized = AccessKey.Replace(sanitized, "$1[REDACTED]");
            sanitized = LabeledPassword.Replace(sanitized, "$1[REDACTED]");
            sanitized = LabeledNoteDump.Replace(sanitized, "$1[REDACTED]");
            sanitized = LabeledRecovery.Replace(sanitized, "$1[REDACTED]");
            return sanitized;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted]";
        }
    }

    public static string SanitizeProviderError(string? message, string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"[{errorCode}]";
        }

        string sanitized = RedactSecrets(message);

        if (sanitized.Length > 200)
        {
            sanitized = sanitized.Substring(0, 200) + "...";
        }

        if (!string.IsNullOrWhiteSpace(errorCode) && sanitized.IndexOf(errorCode, StringComparison.Ordinal) < 0)
        {
            sanitized = $"[{errorCode}] {sanitized}";
        }

        return sanitized.Trim();
    }

    public static string SanitizeDiagnosticDetail(string? message)
        => ErrorLogService.Sanitize(RedactSecrets(message));

    public static string FormatExceptionForLog(Exception? ex)
    {
        if (ex == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        AppendException(sb, ex, depth: 0, maxDepth: 8);
        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.Append(" --> ");
        }

        sb.Append(ex.GetType().Name);
        sb.Append(": ");
        sb.Append(ex.Message);

        if (ex is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (depth + 1 >= maxDepth)
                {
                    break;
                }

                AppendException(sb, inner, depth + 1, maxDepth);
            }

            return;
        }

        if (ex.InnerException != null)
        {
            AppendException(sb, ex.InnerException, depth + 1, maxDepth);
        }
    }

    private static string RedactAuthorization(Match match)
    {
        string prefix = match.Groups[1].Value;
        string value = match.Value.Length >= prefix.Length ? match.Value[prefix.Length..] : string.Empty;
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            return prefix + "\"[REDACTED]\"";
        }

        return prefix + "[REDACTED]";
    }

    private static string RedactUrlQuery(string url)
    {
        int q = url.IndexOf('?');
        if (q <= 0 || q >= url.Length - 1)
        {
            return url;
        }

        string path = url[..q];
        string query = url[(q + 1)..];
        string[] parts = query.Split('&');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            int eq = part.IndexOf('=');
            string name = eq >= 0 ? part[..eq] : part;
            if (SecretQueryParameters.Contains(name))
            {
                parts[i] = name + "=[REDACTED]";
            }
        }

        return path + "?" + string.Join("&", parts);
    }
}
