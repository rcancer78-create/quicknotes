using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QuickNotes.App.Services.Sync;
using QuickNotes.Tools;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class EncryptCredentialsToolTests
{
    [Fact]
    public async Task Run_ValidInput_WritesCiphertextWithoutPlaintext_AndDpapiStorageRecoversOriginalValues()
    {
        string dir = CreateTempDir();
        try
        {
            string input = Path.Combine(dir, "creds.json");
            string output = Path.Combine(dir, "creds.dat");
            const string access = "YCA_ISOLATED_ACCESS_KEY_7f3c";
            const string secret = "YCP_ISOLATED_SECRET_KEY_never-in-ciphertext_9a2b";
            File.WriteAllText(input, $"{{\"AccessKeyId\":\"{access}\",\"SecretAccessKey\":\"{secret}\"}}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            int exit = EncryptCredentialsTool.Run(new[] { input, output });
            Assert.Equal(0, exit);
            Assert.True(File.Exists(output));

            byte[] cipher = File.ReadAllBytes(output);
            string cipherText = Encoding.UTF8.GetString(cipher);
            Assert.DoesNotContain(access, cipherText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, cipherText, StringComparison.Ordinal);
            Assert.DoesNotContain("AccessKeyId", cipherText, StringComparison.Ordinal);

            var storage = new DpapiS3CredentialsStorage(output);
            var loaded = await storage.LoadCredentialsAsync();
            Assert.NotNull(loaded);
            Assert.Equal(access, loaded.AccessKeyId);
            Assert.Equal(secret, loaded.SecretAccessKey);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Run_MissingInput_DoesNotCreateOutput()
    {
        string dir = CreateTempDir();
        try
        {
            string missing = Path.Combine(dir, "missing.json");
            string output = Path.Combine(dir, "creds.dat");
            int exit = EncryptCredentialsTool.Run(new[] { missing, output });
            Assert.Equal(1, exit);
            Assert.False(File.Exists(output));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Theory]
    [InlineData("{}", 1)]
    [InlineData("{\"AccessKeyId\":\"only\"}", 1)]
    [InlineData("{\"AccessKeyId\":\"\",\"SecretAccessKey\":\"secret\"}", 1)]
    [InlineData("{\"AccessKeyId\":\"key\",\"SecretAccessKey\":\"\"}", 1)]
    [InlineData("{\"AccessKeyId\":\"   \",\"SecretAccessKey\":\"secret\"}", 1)]
    [InlineData("{\"AccessKeyId\":1,\"SecretAccessKey\":\"secret\"}", 1)]
    [InlineData("{", 1)]
    public void Run_InvalidOrEmptyInput_PreservesPriorOutput(string json, int expectedExit)
    {
        string dir = CreateTempDir();
        try
        {
            string input = Path.Combine(dir, "creds.json");
            string output = Path.Combine(dir, "creds.dat");
            byte[] prior = Encoding.UTF8.GetBytes("PRIOR_VALID_OUTPUT_SENTINEL_BYTES_aa11");
            File.WriteAllBytes(output, prior);
            File.WriteAllText(input, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            int exit = EncryptCredentialsTool.Run(new[] { input, output });
            Assert.Equal(expectedExit, exit);
            Assert.Equal(prior, File.ReadAllBytes(output));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Run_Usage_WhenArgumentCountWrong()
    {
        Assert.Equal(1, EncryptCredentialsTool.Run(Array.Empty<string>()));
        Assert.Equal(1, EncryptCredentialsTool.Run(new[] { "only-one" }));
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn-encrypt-creds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
