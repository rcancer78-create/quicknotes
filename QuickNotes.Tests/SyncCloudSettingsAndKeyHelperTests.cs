using System;
using System.Text.Json;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

public class SyncCloudSettingsAndKeyHelperTests
{
    [Fact]
    public void Settings_DefaultValues_AreSafeAndYandexCompatible()
    {
        var settings = new SyncCloudSettings();

        Assert.False(settings.Enabled);
        Assert.Equal("https://s3.yandexcloud.net", settings.Endpoint);
        Assert.Equal("ru-central1", settings.Region);
        Assert.Equal(string.Empty, settings.Bucket);
        Assert.Null(settings.Prefix);
        Assert.Equal(30, settings.RequestTimeoutSeconds);
        Assert.Equal(3, settings.MaxRetryAttempts);
        Assert.True(settings.SyncAttachments);
        Assert.Equal(SyncCloudSettings.DefaultMaxAttachmentSyncBytes, settings.MaxAttachmentSyncBytes);
    }

    [Theory]
    [InlineData("my-bucket")]
    [InlineData("quicknotes-2026")]
    [InlineData("backup.bucket.1")]
    [InlineData("qn-storage")]
    public void BucketValidation_ValidNames_PassValidation(string validBucket)
    {
        SyncCloudSettingsValidator.ValidateBucket(validBucket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")] // Too short (< 3)
    [InlineData("MyBucket")] // Uppercase forbidden
    [InlineData("my_bucket")] // Underscore not DNS compliant in standard S3
    [InlineData("my/bucket")] // Slashes forbidden
    [InlineData("my\\bucket")] // Backslashes forbidden
    [InlineData("my..bucket")] // Double dots forbidden
    [InlineData(".startwithdot")]
    [InlineData("endwithdot.")]
    [InlineData("-startwithhyphen")]
    [InlineData("endwithhyphen-")]
    [InlineData("192.168.1.1")] // IP address format forbidden
    public void BucketValidation_InvalidNames_ThrowsSyncValidationException(string invalidBucket)
    {
        Assert.Throws<SyncValidationException>(() => SyncCloudSettingsValidator.ValidateBucket(invalidBucket));
    }

    [Fact]
    public void BucketValidation_TooLong_ThrowsSyncValidationException()
    {
        string tooLong = new string('a', 64);
        Assert.Throws<SyncValidationException>(() => SyncCloudSettingsValidator.ValidateBucket(tooLong));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("quicknotes", "quicknotes/")]
    [InlineData("quicknotes/", "quicknotes/")]
    [InlineData("/quicknotes/", "quicknotes/")]
    [InlineData("user/notes", "user/notes/")]
    [InlineData("//user///notes//", "user/notes/")]
    [InlineData("user\\notes", "user/notes/")]
    public void PrefixNormalization_CleansAndFormatsPrefix(string? input, string expected)
    {
        string normalized = SyncCloudSettingsValidator.NormalizePrefix(input);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("foo/../bar")]
    [InlineData("..")]
    [InlineData("foo:bar")]
    [InlineData("foo*bar")]
    [InlineData("foo?bar")]
    [InlineData("foo<bar")]
    public void PrefixValidation_PathTraversalOrInvalidChars_ThrowsSyncValidationException(string dangerousPrefix)
    {
        Assert.Throws<SyncValidationException>(() => SyncCloudSettingsValidator.ValidatePrefix(dangerousPrefix));
    }

    [Fact]
    public void ObjectKeyHelper_GeneratesDeterministicIsolatedKeys()
    {
        var settings = new SyncCloudSettings { Prefix = "quicknotes" };
        var helper = new SyncObjectKeyHelper(settings, "v1");

        var deviceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var deviceB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var packageId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        string keyA = helper.GetPackageKey(deviceA, packageId);
        string keyB = helper.GetPackageKey(deviceB, packageId);
        string pointerA = helper.GetDevicePointerKey(deviceA);
        string pointerB = helper.GetDevicePointerKey(deviceB);

        Assert.Equal("quicknotes/v1/devices/11111111-1111-1111-1111-111111111111/packages/33333333-3333-3333-3333-333333333333.json", keyA);
        Assert.Equal("quicknotes/v1/devices/22222222-2222-2222-2222-222222222222/packages/33333333-3333-3333-3333-333333333333.json", keyB);
        Assert.Equal("quicknotes/v1/devices/11111111-1111-1111-1111-111111111111/pointer.json", pointerA);
        Assert.Equal("quicknotes/v1/devices/22222222-2222-2222-2222-222222222222/pointer.json", pointerB);

        // Strict isolation: device A's package key is NOT in device B's namespace
        Assert.NotEqual(keyA, keyB);
        Assert.StartsWith("quicknotes/v1/devices/11111111-1111-1111-1111-111111111111/", keyA);
        Assert.StartsWith("quicknotes/v1/devices/22222222-2222-2222-2222-222222222222/", keyB);
    }

    [Fact]
    public void ObjectKeyHelper_EmptyPrefix_DefaultsToV1Root()
    {
        var helper = new SyncObjectKeyHelper();
        var deviceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var packageId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        string key = helper.GetPackageKey(deviceId, packageId);
        string pointer = helper.GetDevicePointerKey(deviceId);

        Assert.Equal("v1/devices/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/packages/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.json", key);
        Assert.Equal("v1/devices/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/pointer.json", pointer);
    }

    [Fact]
    public void ObjectKeyHelper_Parsing_RoundtripsSuccessfully()
    {
        var helper = new SyncObjectKeyHelper(new SyncCloudSettings { Prefix = "my-app" });
        var deviceId = Guid.NewGuid();
        var packageId = Guid.NewGuid();

        string pkgKey = helper.GetPackageKey(deviceId, packageId);
        bool pkgParsed = helper.TryParsePackageKey(pkgKey, out var parsedDev, out var parsedPkg);

        Assert.True(pkgParsed);
        Assert.Equal(deviceId, parsedDev);
        Assert.Equal(packageId, parsedPkg);

        string ptrKey = helper.GetDevicePointerKey(deviceId);
        bool ptrParsed = helper.TryParsePointerKey(ptrKey, out var ptrDev);

        Assert.True(ptrParsed);
        Assert.Equal(deviceId, ptrDev);

        // Invalid keys fail parsing gracefully
        Assert.False(helper.TryParsePackageKey("other/prefix/foo.json", out _, out _));
        Assert.False(helper.TryParsePackageKey("my-app/v1/devices/not-a-guid/packages/foo.json", out _, out _));
        Assert.False(helper.TryParsePointerKey("my-app/v1/devices/not-a-guid/pointer.json", out _));
    }

    [Theory]
    [InlineData("/leading-slash")]
    [InlineData("\\leading-backslash")]
    [InlineData("path/../traversal")]
    [InlineData("path\\with\\backslash")]
    [InlineData("")]
    public void ValidateObjectKey_RejectsDangerousOrMalformedKeys(string dangerousKey)
    {
        Assert.Throws<SyncValidationException>(() => SyncObjectKeyHelper.ValidateObjectKey(dangerousKey));
    }

    [Fact]
    public void AppSettings_JsonSerialization_PersistsCloudSettingsWithoutSecrets()
    {
        var appSettings = new AppSettings
        {
            CloudSync = new SyncCloudSettings
            {
                Enabled = true,
                Endpoint = "https://custom.s3.endpoint.local",
                Region = "custom-region",
                Bucket = "test-bucket",
                Prefix = "notes"
            }
        };

        string json = JsonSerializer.Serialize(appSettings, new JsonSerializerOptions { WriteIndented = true });

        // Verify public settings are serialized
        Assert.Contains("\"Enabled\": true", json);
        Assert.Contains("\"Endpoint\": \"https://custom.s3.endpoint.local\"", json);
        Assert.Contains("\"Region\": \"custom-region\"", json);
        Assert.Contains("\"Bucket\": \"test-bucket\"", json);
        Assert.Contains("\"Prefix\": \"notes\"", json);

        // Verify no secrets exist in JSON
        Assert.DoesNotContain("AccessKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);

        // Roundtrip deserialization
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.CloudSync.Enabled);
        Assert.Equal("test-bucket", deserialized.CloudSync.Bucket);
        Assert.Equal("notes", deserialized.CloudSync.Prefix);
    }

    [Fact]
    public void KeyHelper_EmptyGeneration_UsesLegacyLayoutWithoutGSegment()
    {
        var helper = new SyncObjectKeyHelper(new SyncCloudSettings { Prefix = "sync-root" });
        Assert.True(helper.IsLegacyGeneration);
        Assert.Equal("sync-root/v1/", helper.VersionedPrefix);
        Assert.Equal("sync-root/v1/generation.json", helper.GetGenerationPointerKey());
        Assert.StartsWith("sync-root/v1/devices/", helper.GetDevicesPrefix());
        Assert.DoesNotContain("/g/", helper.GetBlobKey(new string('a', 64)));
        Assert.DoesNotContain("/g/", helper.GetGenerationPointerKey());
    }

    [Fact]
    public void KeyHelper_NonEmptyGeneration_IsolatesDevicesAndBlobs_PointerStaysAtRoot()
    {
        var gen = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var helper = new SyncObjectKeyHelper(new SyncCloudSettings { Prefix = "sync-root" }, generationId: gen);
        Assert.False(helper.IsLegacyGeneration);
        Assert.Equal($"sync-root/v1/g/{gen:D}/", helper.VersionedPrefix);
        Assert.Equal("sync-root/v1/generation.json", helper.GetGenerationPointerKey());
        Assert.Contains($"/g/{gen:D}/devices/", helper.GetDevicesPrefix());
        Assert.Contains($"/g/{gen:D}/blobs/", helper.GetBlobsPrefix());
        Assert.DoesNotContain("/g/", helper.GetGenerationPointerKey());
    }
}
