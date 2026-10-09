using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Headless restore entry for <c>QuickNotes.App.exe</c>. Secrets are read from stdin only.
/// </summary>
public static class EncryptedArchiveRestoreExitCodes
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int ArchiveFormat = 2;
    public const int Security = 3;
    public const int Validation = 4;
    public const int IoFailure = 5;
    public const int Unexpected = 6;
}

public enum EncryptedArchiveRestoreSecretSource
{
    None = 0,
    PasswordStdin = 1,
    RecoveryKeyStdin = 2
}

public sealed class EncryptedArchiveRestoreCliParseResult
{
    public bool IsRestoreCommand { get; init; }
    public bool ParseError { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public string ArchivePath { get; init; } = string.Empty;
    public string DestinationDirectory { get; init; } = string.Empty;
    public EncryptedArchiveRestoreSecretSource SecretSource { get; init; }
    public bool DryRun { get; init; }
}

public static class EncryptedArchiveRestoreCli
{
    public static readonly string UsageText =
        "Usage: QuickNotes.App.exe --restore-archive <file> --destination <dir> (--password-stdin | --recovery-key-stdin) [--dry-run]"
        + Environment.NewLine
        + "The archive password or recovery key is read from stdin (not argv) and is not logged."
        + Environment.NewLine
        + "Exit codes: 0 success, 1 usage, 2 archive format/missing, 3 wrong secret or tamper, 4 validation, 5 I/O, 6 unexpected.";

    public static bool TryRun(string[]? args, out int exitCode)
    {
        EncryptedArchiveRestoreCliParseResult parsed = Parse(args);
        if (!parsed.IsRestoreCommand)
        {
            exitCode = EncryptedArchiveRestoreExitCodes.Success;
            return false;
        }

        using Stream stdin = Console.OpenStandardInput();
        exitCode = Execute(parsed, stdin, Console.Out, Console.Error, new EncryptedArchiveService());
        return true;
    }

    public static EncryptedArchiveRestoreCliParseResult Parse(string[]? args)
    {
        string[] list = args ?? Array.Empty<string>();
        bool sawRestore = false;
        bool sawDestination = false;
        bool sawPasswordStdin = false;
        bool sawRecoveryStdin = false;
        bool sawDryRun = false;
        bool sawAnyCli = false;
        string archivePath = string.Empty;
        string destination = string.Empty;

        for (int i = 0; i < list.Length; i++)
        {
            string arg = list[i];
            if (arg == "--restore-archive")
            {
                sawAnyCli = true;
                sawRestore = true;
                if (!TryReadValue(list, ref i, out archivePath))
                {
                    return Fail("Ожидался путь после --restore-archive.");
                }
            }
            else if (arg == "--destination")
            {
                sawAnyCli = true;
                sawDestination = true;
                if (!TryReadValue(list, ref i, out destination))
                {
                    return Fail("Ожидался путь после --destination.");
                }
            }
            else if (arg == "--password-stdin")
            {
                sawAnyCli = true;
                sawPasswordStdin = true;
            }
            else if (arg == "--recovery-key-stdin")
            {
                sawAnyCli = true;
                sawRecoveryStdin = true;
            }
            else if (arg == "--dry-run")
            {
                sawAnyCli = true;
                sawDryRun = true;
            }
            else if (IsForbiddenSecretArgvSwitch(arg))
            {
                return Fail("Секрет нельзя передавать в аргументах командной строки.");
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (sawRestore || sawDestination || sawPasswordStdin || sawRecoveryStdin || sawDryRun)
                {
                    return Fail("Неизвестный параметр командной строки.");
                }

                // Unrelated process flags (isolated-profile, output-dir, perf, etc.)
                // are not a restore invocation.
                continue;
            }
            else if (sawAnyCli)
            {
                return Fail("Неизвестный параметр командной строки.");
            }
        }

        if (!sawAnyCli)
        {
            return new EncryptedArchiveRestoreCliParseResult { IsRestoreCommand = false };
        }

        if (!sawRestore || !sawDestination || string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(destination))
        {
            return Fail("Нужны --restore-archive <file> и --destination <dir>.");
        }

        if (sawPasswordStdin == sawRecoveryStdin)
        {
            return Fail("Нужен ровно один из флагов --password-stdin или --recovery-key-stdin.");
        }

        return new EncryptedArchiveRestoreCliParseResult
        {
            IsRestoreCommand = true,
            ArchivePath = archivePath,
            DestinationDirectory = destination,
            SecretSource = sawPasswordStdin
                ? EncryptedArchiveRestoreSecretSource.PasswordStdin
                : EncryptedArchiveRestoreSecretSource.RecoveryKeyStdin,
            DryRun = sawDryRun
        };
    }

    internal static int Execute(
        EncryptedArchiveRestoreCliParseResult parsed,
        Stream stdin,
        TextWriter stdout,
        TextWriter stderr,
        IEncryptedArchiveService service)
    {
        if (!parsed.IsRestoreCommand)
        {
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        if (parsed.ParseError)
        {
            stderr.WriteLine(parsed.ErrorMessage);
            stderr.WriteLine(UsageText);
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        string secret;
        try
        {
            secret = ReadSecret(stdin);
        }
        catch (Exception)
        {
            stderr.WriteLine("Не удалось прочитать секрет из stdin.");
            stderr.WriteLine(UsageText);
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        if (string.IsNullOrEmpty(secret))
        {
            stderr.WriteLine("Секрет не прочитан из stdin.");
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        try
        {
            if (parsed.DryRun)
            {
                EncryptedArchiveDryRunResult dry = service.DryRun(new EncryptedArchiveDryRunRequest
                {
                    ArchivePath = parsed.ArchivePath,
                    ArchivePassword = parsed.SecretSource == EncryptedArchiveRestoreSecretSource.PasswordStdin ? secret : null,
                    RecoveryKeyFormatted = parsed.SecretSource == EncryptedArchiveRestoreSecretSource.RecoveryKeyStdin ? secret : null,
                    RestoreDestinationDirectory = parsed.DestinationDirectory
                });
                stdout.WriteLine(
                    "OK dry-run archiveId=" + dry.ArchiveId.ToString("D")
                    + " notes=" + dry.NoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return EncryptedArchiveRestoreExitCodes.Success;
            }

            EncryptedArchiveRestoreResult restored = service.Restore(new EncryptedArchiveRestoreRequest
            {
                ArchivePath = parsed.ArchivePath,
                ArchivePassword = parsed.SecretSource == EncryptedArchiveRestoreSecretSource.PasswordStdin ? secret : null,
                RecoveryKeyFormatted = parsed.SecretSource == EncryptedArchiveRestoreSecretSource.RecoveryKeyStdin ? secret : null,
                DestinationDirectory = parsed.DestinationDirectory
            });
            stdout.WriteLine(
                "OK restore destination=" + restored.DestinationDirectory
                + " notes=" + restored.NoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return EncryptedArchiveRestoreExitCodes.Success;
        }
        catch (EncryptedArchiveSecurityException ex)
        {
            stderr.WriteLine(ex.Message);
            return EncryptedArchiveRestoreExitCodes.Security;
        }
        catch (EncryptedArchiveFormatException ex)
        {
            stderr.WriteLine(ex.Message);
            return EncryptedArchiveRestoreExitCodes.ArchiveFormat;
        }
        catch (EncryptedArchiveValidationException ex)
        {
            stderr.WriteLine(ex.Message);
            return EncryptedArchiveRestoreExitCodes.Validation;
        }
        catch (EncryptedArchiveException ex)
        {
            stderr.WriteLine(ex.Message);
            return EncryptedArchiveRestoreExitCodes.Validation;
        }
        catch (IOException ex)
        {
            stderr.WriteLine(ex.GetType().Name);
            return EncryptedArchiveRestoreExitCodes.IoFailure;
        }
        catch (UnauthorizedAccessException)
        {
            stderr.WriteLine("Access denied.");
            return EncryptedArchiveRestoreExitCodes.IoFailure;
        }
        catch (Exception)
        {
            stderr.WriteLine("Restore failed.");
            return EncryptedArchiveRestoreExitCodes.Unexpected;
        }
    }

    private static EncryptedArchiveRestoreCliParseResult Fail(string message)
        => new()
        {
            IsRestoreCommand = true,
            ParseError = true,
            ErrorMessage = message
        };

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        string next = args[index + 1];
        if (next.StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = next;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsForbiddenSecretArgvSwitch(string arg)
        => (arg.StartsWith("--password", StringComparison.Ordinal)
            && !string.Equals(arg, "--password-stdin", StringComparison.Ordinal))
           || (arg.StartsWith("--recovery-key", StringComparison.Ordinal)
               && !string.Equals(arg, "--recovery-key-stdin", StringComparison.Ordinal));

    internal static string ReadSecret(Stream stdin)
    {
        int maxSecret = EncryptedArchiveConstants.MaxStdinSecretUtf8Bytes;
        int cap = maxSecret + 3 + 2 + 1; // UTF-8 BOM + trailing CR/LF + overflow probe
        byte[] buffer = new byte[cap];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stdin.Read(buffer.AsSpan(total, buffer.Length - total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total >= buffer.Length)
        {
            throw new InvalidDataException("Secret exceeds the stdin limit.");
        }

        int start = 0;
        if (total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            start = 3;
        }

        int end = total;
        while (end > start && (buffer[end - 1] == (byte)'\n' || buffer[end - 1] == (byte)'\r'))
        {
            end--;
        }

        int length = end - start;
        if (length > maxSecret)
        {
            throw new InvalidDataException("Secret exceeds the stdin limit.");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return utf8.GetString(buffer, start, length);
    }
}
