using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class CloudRunProtocolTests
{
    private static string RepoRoot => CiReportingConfigTests.FindRepoRoot();

    private static string ProtocolScript => Path.Combine(RepoRoot, "scripts", "CloudRunProtocol.ps1");

    private static string SmokeRunner => Path.Combine(RepoRoot, "scripts", "Run-YandexCloudLiveSmoke.ps1");

    private static string AcceptanceRunner => Path.Combine(RepoRoot, "scripts", "Run-YandexCloudLiveAcceptance.ps1");

    [Fact]
    public void BucketFingerprint_IsStable_AndOmitsRawBucket()
    {
        const string bucket = "qn-raw-bucket-must-never-appear";
        string first = RunProtocol($"-Action Fingerprint -Bucket \"{bucket}\"").Stdout.Trim();
        string second = RunProtocol($"-Action Fingerprint -Bucket \"{bucket}\"").Stdout.Trim();
        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal(71, first.Length);
        Assert.DoesNotContain(bucket, first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(bucket, first);
    }

    [Fact]
    public void EndpointHost_DropsUserInfoQueryAndPath()
    {
        const string endpoint = "https://user:s3secret@storage.example.test:443/path?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE&Signature=sig";
        CiReportingConfigTests.PwshResult result = RunProtocol($"-Action SanitizeEndpoint -Endpoint \"{endpoint}\"");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("storage.example.test", result.Stdout.Trim());
        Assert.DoesNotContain("user", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("s3secret", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Signature", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TransitionsPlannedRunningPassedCleanup_WithoutForbiddenMarkers()
    {
        string dir = CreateTempResults();
        try
        {
            const string bucket = "unique-bucket-xyz-should-not-leak";
            const string endpoint = "https://alice:pw@storage.example.test/bucket?token=abc";
            string common =
                $"-ResultsDirectory \"{dir}\" -SanitizedReportFileName smoke.md -Scenario Smoke -RunId 11111111-1111-1111-1111-111111111111 -RepoRoot \"{RepoRoot}\" -Endpoint \"{endpoint}\" -Bucket \"{bucket}\" -TrxLogFileName cloud-smoke.trx -StartedUtc 2026-09-14T00:00:00Z";

            Assert.Equal(0, RunProtocol($"-Action Write -State planned {common}").ExitCode);
            AssertField(File.ReadAllText(Path.Combine(dir, "smoke.md")), "Status", "planned");

            Assert.Equal(0, RunProtocol($"-Action Write -State running {common}").ExitCode);
            AssertField(File.ReadAllText(Path.Combine(dir, "smoke.md")), "Status", "running");

            Assert.Equal(0, RunProtocol($"-Action Write -State passed -ExitCode 0 -DurationMs 12 {common}").ExitCode);
            AssertField(File.ReadAllText(Path.Combine(dir, "smoke.md")), "Status", "passed");

            Assert.Equal(0, RunProtocol($"-Action Write -State cleanup-attempted -Outcome passed -ExitCode 0 -DurationMs 12 -CleanupAttempted yes -CleanupResult process-env-cleared {common}").ExitCode);
            string text = File.ReadAllText(Path.Combine(dir, "smoke.md"));
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "passed");
            AssertField(text, "Exit code", "0");
            Assert.Contains("cloud-run-protocol/v1", text, StringComparison.Ordinal);
            Assert.Contains("storage.example.test", text, StringComparison.Ordinal);
            Assert.Contains("sha256:", text, StringComparison.Ordinal);
            Assert.Contains("quicknotes-live-smoke/<yyyyMMdd>/<guid>/", text, StringComparison.Ordinal);
            Assert.Contains("Hard cancellation or runner loss", text, StringComparison.Ordinal);
            Assert.Contains("cloud-smoke.trx, smoke.md", text, StringComparison.Ordinal);
            AssertNoSecrets(text, bucket, endpoint);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_FailureLifecycle_RecordsFailedCleanupAttempted()
    {
        string dir = CreateTempResults();
        try
        {
            string common =
                $"-ResultsDirectory \"{dir}\" -SanitizedReportFileName fail.md -Scenario Acceptance -RunId 22222222-2222-2222-2222-222222222222 -RepoRoot \"{RepoRoot}\" -StartedUtc 2026-09-14T01:00:00Z";
            Assert.Equal(0, RunProtocol($"-Action Write -State planned {common}").ExitCode);
            Assert.Equal(0, RunProtocol($"-Action Write -State running {common}").ExitCode);
            Assert.Equal(0, RunProtocol($"-Action Write -State failed -ExitCode 1 -ActualResult \"setup-failed: credentials-or-connection-metadata-missing\" {common}").ExitCode);
            Assert.Equal(0, RunProtocol($"-Action Write -State cleanup-attempted -Outcome failed -ExitCode 1 -CleanupAttempted yes -CleanupResult not-applicable -ActualResult \"setup-failed: credentials-or-connection-metadata-missing\" {common}").ExitCode);
            string text = File.ReadAllText(Path.Combine(dir, "fail.md"));
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "failed");
            AssertField(text, "Exit code", "1");
            Assert.Contains("setup-failed: credentials-or-connection-metadata-missing", text, StringComparison.Ordinal);
            Assert.Contains("quicknotes-live-acceptance/<yyyyMMdd>/<guid>/", text, StringComparison.Ordinal);
            Assert.Contains("does not prove a GitHub run", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_PreservesPriorReport_WhenReplaceIsLocked()
    {
        string dir = CreateTempResults();
        try
        {
            string report = Path.Combine(dir, "locked.md");
            string common =
                $"-ResultsDirectory \"{dir}\" -SanitizedReportFileName locked.md -Scenario Smoke -RunId 33333333-3333-3333-3333-333333333333 -RepoRoot \"{RepoRoot}\"";
            Assert.Equal(0, RunProtocol($"-Action Write -State planned {common}").ExitCode);
            string before = File.ReadAllText(report);
            Assert.Contains("| Status | planned |", before, StringComparison.Ordinal);

            using (var lockStream = new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                CiReportingConfigTests.PwshResult failed = RunProtocol($"-Action Write -State running {common}");
                Assert.NotEqual(0, failed.ExitCode);
                lockStream.Position = 0;
                using var reader = new StreamReader(lockStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                string lockedText = reader.ReadToEnd();
                Assert.Contains("| Status | planned |", lockedText, StringComparison.Ordinal);
                Assert.DoesNotContain("| Status | running |", lockedText, StringComparison.Ordinal);
            }

            Assert.Equal(before, File.ReadAllText(report));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Theory]
    [InlineData("..\\escape.md")]
    [InlineData("sub/report.md")]
    [InlineData("report.txt")]
    [InlineData("*.md")]
    public void Write_RejectsUnsafeReportFileName(string fileName)
    {
        string dir = CreateTempResults();
        try
        {
            CiReportingConfigTests.PwshResult result = RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName \"{fileName}\" -Scenario Smoke");
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Any());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_RejectsLiveProfileResultsDirectory()
    {
        string live = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickNotes");
        CiReportingConfigTests.PwshResult result = RunProtocol(
            $"-Action Write -State planned -ResultsDirectory \"{live}\" -SanitizedReportFileName protocol.md -Scenario Smoke");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("live profile", result.Stderr + result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_RejectsContentThatWouldIncludeRawBucketOrEndpoint()
    {
        string dir = CreateTempResults();
        try
        {
            const string bucket = "must-not-appear-as-plain-bucket";
            CiReportingConfigTests.PwshResult ok = RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName ok.md -Scenario Smoke -Bucket \"{bucket}\" -Endpoint \"https://host.example.test/?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE\"");
            Assert.Equal(0, ok.ExitCode);
            string text = File.ReadAllText(Path.Combine(dir, "ok.md"));
            AssertNoSecrets(text, bucket, "https://host.example.test/?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE");
            Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_ProtocolSelfTest_WritesSanitizedSuccessReport_WhenOptedIn()
    {
        string dir = CreateTempResults();
        try
        {
            const string bucket = "selftest-bucket-raw";
            CiReportingConfigTests.PwshResult result = RunSmoke(
                $"-ProtocolSelfTest -SelfTestExitCode 0 -Scenario Smoke -ResultsDirectory \"{dir}\" -SanitizedReportFileName cloud-smoke.md -TrxLogFileName cloud-smoke.trx -Endpoint \"https://user:secret@storage.example.test/path\" -Bucket \"{bucket}\"");
            Assert.Equal(0, result.ExitCode);
            string report = Path.Combine(dir, "cloud-smoke.md");
            Assert.True(File.Exists(report), result.Stdout + result.Stderr);
            string text = File.ReadAllText(report);
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "passed");
            AssertField(text, "Exit code", "0");
            Assert.Contains("storage.example.test", text, StringComparison.Ordinal);
            AssertNoSecrets(text, bucket, "https://user:secret@storage.example.test/path");
            Assert.DoesNotContain("user:secret", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_FailureBeforeTests_StillWritesReport_WhenResultsAndReportSupplied()
    {
        string dir = CreateTempResults();
        try
        {
            string missing = Path.Combine(dir, "missing-creds.dat");
            const string bucket = "failure-raw-bucket";
            CiReportingConfigTests.PwshResult result = RunSmoke(
                $"-CredentialsPath \"{missing}\" -Endpoint \"https://user:pw@storage.example.test/?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE\" -Bucket \"{bucket}\" -ResultsDirectory \"{dir}\" -SanitizedReportFileName cloud-smoke.md -TrxLogFileName cloud-smoke.trx");
            Assert.NotEqual(0, result.ExitCode);
            string report = Path.Combine(dir, "cloud-smoke.md");
            Assert.True(File.Exists(report), result.Stdout + result.Stderr);
            string text = File.ReadAllText(report);
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "failed");
            Assert.Contains("setup-failed: credentials-or-connection-metadata-missing", text, StringComparison.Ordinal);
            Assert.Contains("Hard cancellation or runner loss", text, StringComparison.Ordinal);
            AssertNoSecrets(text, bucket, "https://user:pw@storage.example.test/?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE");
            Assert.DoesNotContain(missing, text, StringComparison.Ordinal);
            Assert.DoesNotContain("missing-creds.dat", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_DoesNotWriteReport_WithoutSanitizedReportFileName()
    {
        string dir = CreateTempResults();
        try
        {
            CiReportingConfigTests.PwshResult result = RunSmoke(
                $"-ProtocolSelfTest -SelfTestExitCode 0 -Scenario Smoke -ResultsDirectory \"{dir}\" -TrxLogFileName cloud-smoke.trx");
            Assert.Equal(0, result.ExitCode);
            Assert.False(Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Any(), "ordinary opted-out run must not write a protocol report");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_PlannedAndRunningWrites_SurviveLegacyArgumentPassing_WithoutExitCode()
    {
        string dir = CreateTempResults();
        try
        {
            const string bucket = "legacy-raw-bucket-must-not-leak";
            const string endpoint = "https://user:secret@storage.example.test/path?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE";
            CiReportingConfigTests.PwshResult result = RunSmokeInLegacyMode(dir, "cloud-smoke.md", endpoint, bucket);

            Assert.DoesNotContain("Missing an argument", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, result.ExitCode);

            string report = Path.Combine(dir, "cloud-smoke.md");
            Assert.True(File.Exists(report), result.Stdout + result.Stderr);
            string text = File.ReadAllText(report);
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "passed");
            AssertField(text, "Exit code", "0");
            Assert.Contains("YandexObjectStorageLiveSmokeTests", text, StringComparison.Ordinal);
            Assert.Contains("quicknotes-live-smoke/<yyyyMMdd>/<guid>/", text, StringComparison.Ordinal);
            AssertNoSecrets(text, bucket, endpoint);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_LegacyArgumentPassing_StillRecordsActualNonZeroExitCode()
    {
        string dir = CreateTempResults();
        try
        {
            const string bucket = "legacy-failure-raw-bucket";
            CiReportingConfigTests.PwshResult result =
                RunSmokeInLegacyMode(dir, "cloud-fail.md", "https://storage.example.test", bucket, selfTestExitCode: 7);
            Assert.Equal(7, result.ExitCode);

            string text = File.ReadAllText(Path.Combine(dir, "cloud-fail.md"));
            AssertField(text, "Status", "cleanup-attempted");
            AssertField(text, "Outcome", "failed");
            AssertField(text, "Exit code", "7");
            Assert.DoesNotContain(bucket, text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void AcceptanceRunner_ForwardsSanitizedReport_AndSelfTestFailure()
    {
        string dir = CreateTempResults();
        try
        {
            CiReportingConfigTests.PwshResult result = CiReportingConfigTests.RunPwshDetailed(
                AcceptanceRunner,
                $"-ProtocolSelfTest -SelfTestExitCode 7 -ResultsDirectory \"{dir}\" -SanitizedReportFileName cloud-acceptance.md -TrxLogFileName cloud-acceptance.trx -Endpoint https://storage.example.test -Bucket acc-bucket",
                RepoRoot);
            Assert.Equal(7, result.ExitCode);
            string text = File.ReadAllText(Path.Combine(dir, "cloud-acceptance.md"));
            AssertField(text, "Scenario", "Acceptance");
            AssertField(text, "Outcome", "failed");
            AssertField(text, "Exit code", "7");
            Assert.Contains("YandexObjectStorageLiveAcceptanceTests", text, StringComparison.Ordinal);
            Assert.DoesNotContain("acc-bucket", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ManualNoVpnRunner_DoesNotEnableProtocolReportByDefault()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "Run-YandexCloudManualNoVpn.ps1"));
        Assert.DoesNotContain("SanitizedReportFileName", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_OmittingIdentity_ReusesPriorRunIdAndStartedUtc()
    {
        string dir = CreateTempResults();
        try
        {
            string common =
                $"-ResultsDirectory \"{dir}\" -SanitizedReportFileName identity.md -Scenario Smoke -RunId 44444444-4444-4444-4444-444444444444 -StartedUtc 2026-09-14T02:00:00Z -RepoRoot \"{RepoRoot}\"";
            Assert.Equal(0, RunProtocol($"-Action Write -State planned {common}").ExitCode);
            CiReportingConfigTests.PwshResult later = RunProtocol(
                $"-Action Write -State running -ResultsDirectory \"{dir}\" -SanitizedReportFileName identity.md -Scenario Smoke -RepoRoot \"{RepoRoot}\"");
            Assert.Equal(0, later.ExitCode);
            string text = File.ReadAllText(Path.Combine(dir, "identity.md"));
            AssertField(text, "Status", "running");
            AssertField(text, "Run id", "44444444-4444-4444-4444-444444444444");
            AssertField(text, "Started (UTC)", "2026-09-14T02:00:00Z");
            Assert.DoesNotContain("| Finished (UTC) | 20", text, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_ConflictingRunId_LeavesPriorReportUnchanged()
    {
        string dir = CreateTempResults();
        try
        {
            string report = Path.Combine(dir, "identity.md");
            Assert.Equal(0, RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName identity.md -Scenario Smoke -RunId 55555555-5555-5555-5555-555555555555 -StartedUtc 2026-09-14T03:00:00Z -RepoRoot \"{RepoRoot}\"").ExitCode);
            string before = File.ReadAllText(report);
            CiReportingConfigTests.PwshResult conflict = RunProtocol(
                $"-Action Write -State running -ResultsDirectory \"{dir}\" -SanitizedReportFileName identity.md -Scenario Smoke -RunId 66666666-6666-6666-6666-666666666666 -StartedUtc 2026-09-14T03:00:00Z -RepoRoot \"{RepoRoot}\"");
            Assert.NotEqual(0, conflict.ExitCode);
            Assert.Contains("different run id", conflict.Stderr + conflict.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllText(report));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_RejectsForbiddenCallerField_LeavesPriorReport()
    {
        string dir = CreateTempResults();
        try
        {
            string report = Path.Combine(dir, "safe.md");
            Assert.Equal(0, RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName safe.md -Scenario Smoke -RunId 77777777-7777-7777-7777-777777777777 -StartedUtc 2026-09-14T04:00:00Z -RepoRoot \"{RepoRoot}\"").ExitCode);
            string before = File.ReadAllText(report);
            CiReportingConfigTests.PwshResult rejected = RunProtocol(
                $"-Action Write -State failed -ResultsDirectory \"{dir}\" -SanitizedReportFileName safe.md -Scenario Smoke -RunId 77777777-7777-7777-7777-777777777777 -ActualResult \"leaked AWS_SECRET_ACCESS_KEY=example\" -RepoRoot \"{RepoRoot}\"");
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("forbidden", rejected.Stderr + rejected.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllText(report));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Theory]
    [InlineData("..\\escape.trx")]
    [InlineData("sub/log.trx")]
    public void Write_RejectsUnsafeTrxLogFileName(string trxName)
    {
        string dir = CreateTempResults();
        try
        {
            CiReportingConfigTests.PwshResult result = RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName ok.md -Scenario Smoke -TrxLogFileName \"{trxName}\"");
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(dir, "ok.md")));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Write_RejectsExistingNonProtocolFile()
    {
        string dir = CreateTempResults();
        try
        {
            string report = Path.Combine(dir, "occupied.md");
            File.WriteAllText(report, "not a protocol");
            CiReportingConfigTests.PwshResult result = RunProtocol(
                $"-Action Write -State planned -ResultsDirectory \"{dir}\" -SanitizedReportFileName occupied.md -Scenario Smoke");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("not a protocol", File.ReadAllText(report));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Runner_Source_KeepsStableIdentityAndWritesTerminalThenCleanup()
    {
        string smoke = File.ReadAllText(SmokeRunner);
        Assert.Contains("$protocolRunId = [guid]::NewGuid().ToString('D')", smoke, StringComparison.Ordinal);
        Assert.Contains("'-StartedUtc', $protocolStartedUtc", smoke, StringComparison.Ordinal);
        Assert.Contains("'-RunId', $protocolRunId", smoke, StringComparison.Ordinal);
        Assert.Contains("WriteState $state", smoke, StringComparison.Ordinal);
        Assert.Contains("WriteState 'cleanup-attempted'", smoke, StringComparison.Ordinal);
        string acceptance = File.ReadAllText(AcceptanceRunner);
        Assert.Contains("SanitizedReportFileName", acceptance, StringComparison.Ordinal);
        Assert.Contains("Run-YandexCloudLiveSmoke.ps1", acceptance, StringComparison.Ordinal);
    }

    private static CiReportingConfigTests.PwshResult RunProtocol(string args) =>
        CiReportingConfigTests.RunPwshDetailed(ProtocolScript, args, RepoRoot);

    private static CiReportingConfigTests.PwshResult RunSmoke(string args) =>
        CiReportingConfigTests.RunPwshDetailed(SmokeRunner, args, RepoRoot);

    // Windows PowerShell 5.1 drops empty-string native arguments, so the runner
    // must not pass an empty -ExitCode. Legacy argument-passing mode reproduces
    // that behavior deterministically on pwsh.
    private static CiReportingConfigTests.PwshResult RunSmokeInLegacyMode(
        string resultsDirectory,
        string reportFileName,
        string endpoint,
        string bucket,
        int selfTestExitCode = 0)
    {
        string command =
            "$PSNativeCommandArgumentPassing = 'Legacy'; & '" + QuoteLiteral(SmokeRunner) + "'" +
            " -ProtocolSelfTest -SelfTestExitCode " + selfTestExitCode + " -Scenario Smoke" +
            " -ResultsDirectory '" + QuoteLiteral(resultsDirectory) + "'" +
            " -SanitizedReportFileName '" + QuoteLiteral(reportFileName) + "'" +
            " -Endpoint '" + QuoteLiteral(endpoint) + "'" +
            " -Bucket '" + QuoteLiteral(bucket) + "'" +
            "; exit $LASTEXITCODE";

        PowerShellHost.PowerShellResult result = PowerShellHost.RunCommand(command, RepoRoot);
        return new CiReportingConfigTests.PwshResult(result.ExitCode, result.Stdout, result.Stderr);
    }

    private static string QuoteLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateTempResults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn-cloud-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void AssertField(string markdown, string field, string expected)
    {
        string? line = markdown.Split('\n').FirstOrDefault(l => l.StartsWith("| " + field + " |", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(line), "missing field " + field + Environment.NewLine + markdown);
        Assert.Contains("| " + expected + " |", line, StringComparison.Ordinal);
    }

    private static void AssertNoSecrets(string text, params string[] rawValues)
    {
        foreach (string raw in rawValues.Where(static v => !string.IsNullOrWhiteSpace(v)))
            Assert.DoesNotContain(raw, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretAccessKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QUICKNOTES_LIVE_S3_CREDENTIALS", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qn-cloud-creds", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("note=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at QuickNotes.", text, StringComparison.Ordinal);
    }
}
