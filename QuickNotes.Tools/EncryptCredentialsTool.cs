using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickNotes.Tools;

/// <summary>
/// DPAPI-encrypts a plaintext JSON credentials file for use by
/// <c>DpapiS3CredentialsStorage</c> on the same Windows user account.
/// Intended for CI runners that receive secrets via environment variables
/// and need to produce a DPAPI-encrypted file that the existing live
/// smoke/acceptance tests can consume.
///
/// Usage: QuickNotes.Tools.exe encrypt-credentials &lt;input.json&gt; &lt;output.dat&gt;
///
/// Exit codes: 0 success, 1 usage/validation, 6 unexpected.
/// The input file must contain JSON with non-empty string "AccessKeyId" and "SecretAccessKey".
/// Invalid input never replaces an existing output file. Buffers are zeroed before return.
/// </summary>
public static class EncryptCredentialsTool
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickNotes.S3.DPAPI.Entropy.v1");

    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: QuickNotes.Tools.exe encrypt-credentials <input.json> <output.dat>");
            return 1;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine("Input file not found: " + inputPath);
            return 1;
        }

        byte[]? plaintext = null;
        byte[]? encrypted = null;
        try
        {
            plaintext = File.ReadAllBytes(inputPath);
            if (!TryValidateCredentialJson(plaintext, out string error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            string fullOutput = Path.GetFullPath(outputPath);
            string? dir = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tempFile = fullOutput + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempFile, encrypted);
                File.Move(tempFile, fullOutput, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("encrypt-credentials failed: " + ex.GetType().Name);
            return 6;
        }
        finally
        {
            if (plaintext != null)
                Array.Clear(plaintext, 0, plaintext.Length);
            if (encrypted != null)
                Array.Clear(encrypted, 0, encrypted.Length);
        }
    }

    private static bool TryValidateCredentialJson(byte[] plaintext, out string error)
    {
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Input JSON must be an object with AccessKeyId and SecretAccessKey.";
                return false;
            }

            if (!TryReadNonEmptyString(root, "AccessKeyId", out error))
                return false;
            if (!TryReadNonEmptyString(root, "SecretAccessKey", out error))
                return false;

            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Input is not valid JSON.";
            return false;
        }
    }

    private static bool TryReadNonEmptyString(JsonElement root, string name, out string error)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            error = "Input JSON must contain AccessKeyId and SecretAccessKey properties.";
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = name + " must be a string.";
            return false;
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = name + " must be a non-empty string.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
