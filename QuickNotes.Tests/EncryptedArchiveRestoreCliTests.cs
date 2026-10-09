using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class EncryptedArchiveRestoreCliTests
{
    private const int TestKdfN = 10;

    [Fact]
    public void Parse_RequiresArchiveDestinationAndExactlyOneStdinSecret()
    {
        Assert.False(EncryptedArchiveRestoreCli.Parse(Array.Empty<string>()).IsRestoreCommand);
        Assert.False(EncryptedArchiveRestoreCli.Parse(new[] { "--port", "123", "--blame-hang" }).IsRestoreCommand);
        Assert.False(EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--isolated-profile", @"C:\temp\qn",
            "--three-pane-smoke",
            "--output-dir", @"C:\temp\out"
        }).IsRestoreCommand);

        EncryptedArchiveRestoreCliParseResult both = EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--restore-archive", "a.qnar",
            "--destination", "d",
            "--password-stdin",
            "--recovery-key-stdin"
        });
        Assert.True(both.ParseError);

        EncryptedArchiveRestoreCliParseResult neither = EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--restore-archive", "a.qnar",
            "--destination", "d"
        });
        Assert.True(neither.ParseError);

        EncryptedArchiveRestoreCliParseResult argvSecret = EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--restore-archive", "a.qnar",
            "--destination", "d",
            "--password", "leaked-secret"
        });
        Assert.True(argvSecret.ParseError);
        Assert.Contains("аргументах", argvSecret.ErrorMessage, StringComparison.Ordinal);

        EncryptedArchiveRestoreCliParseResult ok = EncryptedArchiveRestoreCli.Parse(new[]
        {
            "--restore-archive", "a.qnar",
            "--destination", "d",
            "--password-stdin",
            "--dry-run"
        });
        Assert.False(ok.ParseError);
        Assert.True(ok.DryRun);
        Assert.Equal(EncryptedArchiveRestoreSecretSource.PasswordStdin, ok.SecretSource);
    }

    [Fact]
    public void Execute_MapsExitCodes_AndDoesNotEchoSecret()
    {
        string root = Path.Combine(Path.GetTempPath(), "qn_cli_restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dbPath = Path.Combine(root, "data", "quicknotes.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var storage = new AttachmentStorageService(Path.Combine(root, "data"));
            using (var db = SqliteTestUtil.CreateContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(new QuickNotes.App.Models.Note
                {
                    Text = "cli-note",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
                db.SaveChanges();
            }

            string archivePath = Path.Combine(root, "out", "a.qnar");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            const string password = "CLI_SECRET_TOKEN_DO_NOT_LOG_9f2c";
            var service = new EncryptedArchiveService(EncryptedArchiveKdfLimits.ForTests());
            service.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = dbPath,
                AttachmentsDirectory = storage.AttachmentsDirectory,
                DestinationArchivePath = archivePath,
                ArchivePassword = password,
                Pbkdf2Iterations = TestKdfN
            });

            string dest = Path.Combine(root, "restored");
            var parsed = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", archivePath,
                "--destination", dest,
                "--password-stdin"
            });
            int ok = Run(parsed, password, service, out string stdoutOk, out string stderrOk);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, ok);
            Assert.True(File.Exists(Path.Combine(dest, "quicknotes.db")));
            Assert.DoesNotContain(password, stdoutOk, StringComparison.Ordinal);
            Assert.DoesNotContain(password, stderrOk, StringComparison.Ordinal);

            var wrong = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", archivePath,
                "--destination", Path.Combine(root, "restored-wrong"),
                "--password-stdin"
            });
            int security = Run(wrong, password + "-nope", service, out string stdoutSec, out string stderrSec);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Security, security);
            Assert.DoesNotContain(password, stdoutSec, StringComparison.Ordinal);
            Assert.DoesNotContain(password, stderrSec, StringComparison.Ordinal);
            Assert.DoesNotContain(password + "-nope", stderrSec, StringComparison.Ordinal);

            var missing = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", Path.Combine(root, "missing.qnar"),
                "--destination", Path.Combine(root, "missing-dest"),
                "--password-stdin"
            });
            int format = Run(missing, password, service, out _, out _);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.ArchiveFormat, format);

            string nonempty = Path.Combine(root, "nonempty");
            Directory.CreateDirectory(nonempty);
            File.WriteAllText(Path.Combine(nonempty, "x.txt"), "x");
            var blocked = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", archivePath,
                "--destination", nonempty,
                "--password-stdin"
            });
            int validation = Run(blocked, password, service, out _, out _);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Validation, validation);

            var usage = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", archivePath,
                "--destination", dest,
                "--password", password
            });
            int usageCode = EncryptedArchiveRestoreCli.Execute(
                usage,
                new MemoryStream(Encoding.UTF8.GetBytes(password)),
                new StringWriter(),
                new StringWriter(),
                service);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, usageCode);

            string overflow = "OVERFLOW_SECRET_TOKEN_CLI_NO_ECHO_" + new string('X', EncryptedArchiveConstants.MaxStdinSecretUtf8Bytes);
            var overflowParsed = EncryptedArchiveRestoreCli.Parse(new[]
            {
                "--restore-archive", archivePath,
                "--destination", Path.Combine(root, "overflow-dest"),
                "--password-stdin"
            });
            var overflowOut = new StringWriter();
            var overflowErr = new StringWriter();
            int overflowCode = EncryptedArchiveRestoreCli.Execute(
                overflowParsed,
                new MemoryStream(Encoding.UTF8.GetBytes(overflow)),
                overflowOut,
                overflowErr,
                service);
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, overflowCode);
            Assert.DoesNotContain(overflow, overflowOut.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(overflow, overflowErr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("OVERFLOW_SECRET_TOKEN", overflowErr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("OVERFLOW_SECRET_TOKEN", overflowOut.ToString(), StringComparison.Ordinal);

            if (File.Exists(ErrorLogService.LogFilePath))
            {
                string log = File.ReadAllText(ErrorLogService.LogFilePath);
                Assert.DoesNotContain(password, log, StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Process_HeadlessRestore_ExitsWithoutUi_AndDoesNotLogSecret()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickNotes.App.exe");
        Assert.True(File.Exists(exe), "QuickNotes.App.exe should be copied next to testhost.");

        string missingArchive = Path.Combine(Path.GetTempPath(), "qn-missing-" + Guid.NewGuid().ToString("N") + ".qnar");
        string dest = Path.Combine(Path.GetTempPath(), "qn-cli-dest-" + Guid.NewGuid().ToString("N"));
        const string secret = "PROCESS_SECRET_TOKEN_CLI_NO_LOG_c81a";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "--restore-archive",
                missingArchive,
                "--destination",
                dest,
                "--password-stdin"
            }
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.Write(secret);
        process.StandardInput.Close();
        bool exited = process.WaitForExit(20_000);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Test failure path already records hang.
            }
        }

        Assert.True(exited, "Headless restore must exit without WPF UI.");
        Assert.True(process.HasExited);
        Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
        Assert.Equal(EncryptedArchiveRestoreExitCodes.ArchiveFormat, process.ExitCode);
        Assert.DoesNotContain("MainWindow", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, psi.ArgumentList);
        if (File.Exists(ErrorLogService.LogFilePath))
        {
            Assert.DoesNotContain(secret, File.ReadAllText(ErrorLogService.LogFilePath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadSecret_PreservesSpaces_StripsOnlyTrailingCrLf()
    {
        const string secret = "  pass phrase  ";
        using (var crlf = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\r\n")))
        {
            Assert.Equal(secret, EncryptedArchiveRestoreCli.ReadSecret(crlf));
        }

        using (var lf = new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n")))
        {
            Assert.Equal(secret, EncryptedArchiveRestoreCli.ReadSecret(lf));
        }

        using (var raw = new MemoryStream(Encoding.UTF8.GetBytes(secret)))
        {
            Assert.Equal(secret, EncryptedArchiveRestoreCli.ReadSecret(raw));
        }

        byte[] bomAndSecret = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(secret + "\r\n"))
            .ToArray();
        using var bom = new MemoryStream(bomAndSecret);
        Assert.Equal(secret, EncryptedArchiveRestoreCli.ReadSecret(bom));
    }

    [Fact]
    public void ReadSecret_AcceptsMaxUtf8Length_AndRejectsOverflowWithoutEcho()
    {
        string max = new string('A', EncryptedArchiveConstants.MaxStdinSecretUtf8Bytes);
        using (var ok = new MemoryStream(Encoding.UTF8.GetBytes(max + "\r\n")))
        {
            Assert.Equal(max, EncryptedArchiveRestoreCli.ReadSecret(ok));
        }

        string overflow = "OVERFLOW_SECRET_TOKEN_LEN_" + new string('B', EncryptedArchiveConstants.MaxStdinSecretUtf8Bytes);
        using var tooBig = new MemoryStream(Encoding.UTF8.GetBytes(overflow));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => EncryptedArchiveRestoreCli.ReadSecret(tooBig));
        Assert.DoesNotContain(overflow, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW_SECRET_TOKEN", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", ex.Message, StringComparison.Ordinal);
    }

    private static int Run(
        EncryptedArchiveRestoreCliParseResult parsed,
        string secret,
        IEncryptedArchiveService service,
        out string stdoutText,
        out string stderrText)
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(secret + Environment.NewLine));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = EncryptedArchiveRestoreCli.Execute(parsed, stdin, stdout, stderr, service);
        stdoutText = stdout.ToString();
        stderrText = stderr.ToString();
        return code;
    }
}
