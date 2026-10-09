using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace QuickNotes.Tests;

public sealed class LiveCloudPrefixGuardTests
{
    private const string AcceptancePrefix = "quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/";
    private const string SmokePrefixD = "quicknotes-live-smoke/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/";
    private const string SmokePrefixN = "quicknotes-live-smoke/20260913/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/";

    [Fact]
    public void RegisterCreatedKey_AcceptsOnlyExactRunPrefix()
    {
        var registry = new HashSet<string>(StringComparer.Ordinal);
        string ok = AcceptancePrefix + "v1/devices/11111111-1111-1111-1111-111111111111/pointer.json";

        LiveCloudPrefixGuard.RegisterCreatedKey(registry, AcceptancePrefix, ok);
        Assert.Single(registry);
        Assert.Contains(ok, registry);
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/other-run/v1/x")]
    [InlineData("other-prefix/v1/x")]
    [InlineData("quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/../escape")]
    [InlineData("")]
    public void RegisterCreatedKey_RejectsForeignOrUnsafeKeys(string key)
    {
        var registry = new HashSet<string>(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(
            () => LiveCloudPrefixGuard.RegisterCreatedKey(registry, AcceptancePrefix, key));
        Assert.Empty(registry);
    }

    [Fact]
    public void FilterDeletableKeys_DropsKeysOutsidePrefix()
    {
        string inside = AcceptancePrefix + "v1/generation.json";
        string[] mixed =
        {
            inside,
            "quicknotes-live-acceptance/other/v1/x",
            "unrelated",
            AcceptancePrefix + "../outside"
        };

        string[] deletable = LiveCloudPrefixGuard.FilterDeletableKeys(AcceptancePrefix, mixed).ToArray();
        Assert.Equal(new[] { inside }, deletable);
    }

    [Theory]
    [InlineData(AcceptancePrefix)]
    [InlineData(SmokePrefixD)]
    [InlineData(SmokePrefixN)]
    public void IsSafeLivePrefix_AcceptsStrictAcceptanceAndSmokeFormats(string prefix)
    {
        Assert.True(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/")]
    [InlineData("quicknotes-live-smoke/")]
    public void IsSafeLivePrefix_RejectsRootOnly(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    [InlineData("quicknotes-live-smoke/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("quicknotes-live-acceptance//aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    public void IsSafeLivePrefix_RejectsMissingDate(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/2026-09-13/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    [InlineData("quicknotes-live-acceptance/20261340/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    [InlineData("quicknotes-live-acceptance/20260230/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    [InlineData("quicknotes-live-smoke/YYYYMMDD/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    public void IsSafeLivePrefix_RejectsBadDate(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/20260913/")]
    [InlineData("quicknotes-live-smoke/20260913/")]
    public void IsSafeLivePrefix_RejectsMissingGuid(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/20260913/g/")]
    [InlineData("quicknotes-live-smoke/20260913/g/")]
    [InlineData("quicknotes-live-acceptance/20260913/not-a-guid/")]
    [InlineData("quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee/")]
    [InlineData("quicknotes-live-acceptance/20260913/00000000-0000-0000-0000-000000000000/")]
    [InlineData("quicknotes-live-acceptance/20260913/{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}/")]
    public void IsSafeLivePrefix_RejectsBadGuid(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Theory]
    [InlineData("quicknotes-live-acceptance/20260913/extra/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/")]
    [InlineData("quicknotes-live-smoke/20260913/run/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    public void IsSafeLivePrefix_RejectsExtraSegmentBeforeGuid(string prefix)
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix(prefix));
    }

    [Fact]
    public void ClassifyObjectKind_UsesRoleTokensWithoutExposingFullKey()
    {
        const string prefix = "quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/";
        Assert.Equal("generation", LiveCloudPrefixGuard.ClassifyObjectKind(prefix + "v1/generation.json"));
        Assert.Equal("package", LiveCloudPrefixGuard.ClassifyObjectKind(prefix + "v1/devices/11111111-1111-1111-1111-111111111111/packages/22222222-2222-2222-2222-222222222222.json"));
        Assert.Equal("pointer", LiveCloudPrefixGuard.ClassifyObjectKind(prefix + "v1/devices/11111111-1111-1111-1111-111111111111/pointer.json"));
        Assert.Equal("blob", LiveCloudPrefixGuard.ClassifyObjectKind(prefix + "v1/blobs/" + new string('a', 64) + ".bin"));
        Assert.Equal("none", LiveCloudPrefixGuard.ClassifyObjectKind(null));
    }

    [Fact]
    public void RedactLiveDiagnostics_RemovesRunPrefixAndIds()
    {
        string raw = "phase=tombstone-create; key=quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/v1/generation.json";
        string redacted = LiveCloudPrefixGuard.RedactLiveDiagnostics(raw);
        Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("quicknotes-live-acceptance/20260913/", redacted, StringComparison.Ordinal);
        Assert.Contains("live-prefix/", redacted, StringComparison.Ordinal);
        Assert.Contains("generation.json", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void IsSafeLivePrefix_RejectsMissingTrailingSlashAndUnknownRoot()
    {
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix("quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix("notes/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/"));
        Assert.False(LiveCloudPrefixGuard.IsSafeLivePrefix("quicknotes-live-acceptance/20260913/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/v1/"));
    }
}
