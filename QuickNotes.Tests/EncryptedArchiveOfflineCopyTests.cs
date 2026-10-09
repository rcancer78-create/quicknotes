using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Services.EncryptedArchive;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class EncryptedArchiveOfflineCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qn_rk_copy_" + Guid.NewGuid().ToString("N"));

    public EncryptedArchiveOfflineCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_root);

    [Fact]
    public void Document_ContainsGroupedKey_AndChecksum_AndDoesNotReplaceNotePassword()
    {
        byte[] material = RandomNumberGenerator.GetBytes(32);
        string formatted = RecoveryKeyEncoding.Format(material);
        Guid archiveId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string document = RecoveryKeyOfflineCopy.BuildDocument(formatted, archiveId);

        Assert.Contains(RecoveryKeyOfflineCopy.FileTitle, document, StringComparison.Ordinal);
        Assert.Contains(formatted, document, StringComparison.Ordinal);
        Assert.Contains(RecoveryKeyEncoding.ChecksumGroups(formatted), document, StringComparison.Ordinal);
        Assert.Contains(archiveId.ToString("D"), document, StringComparison.Ordinal);
        Assert.Contains("does not replace a protected note password", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not copy it to the clipboard", document, StringComparison.OrdinalIgnoreCase);
        Assert.True(material.AsSpan().SequenceEqual(RecoveryKeyEncoding.Parse(formatted)));
    }

    [Fact]
    public void WriteNewFile_IsAtomic_RejectsExisting_AndRejectsSameDirectoryAsArchive()
    {
        string archiveDir = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archiveDir);
        string archivePath = Path.Combine(archiveDir, "notes.qnar");
        File.WriteAllBytes(archivePath, new byte[] { 1 });
        string formatted = RecoveryKeyEncoding.Format(RandomNumberGenerator.GetBytes(32));

        string beside = Path.Combine(archiveDir, "rk.txt");
        EncryptedArchiveValidationException besideEx = Assert.Throws<EncryptedArchiveValidationException>(
            () => RecoveryKeyOfflineCopy.WriteNewFile(beside, formatted, archivePath));
        Assert.Contains(".qnar", besideEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(beside));

        string destDir = Path.Combine(_root, "docs");
        string dest = Path.Combine(destDir, "rk.txt");
        RecoveryKeyOfflineCopy.WriteNewFile(dest, formatted, archivePath);
        Assert.True(File.Exists(dest));
        Assert.Contains(formatted, File.ReadAllText(dest, Encoding.UTF8), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(destDir, "*.tmp"));

        Assert.Throws<EncryptedArchiveValidationException>(
            () => RecoveryKeyOfflineCopy.WriteNewFile(dest, formatted, archivePath));
        string extracted = ExtractKey(File.ReadAllText(dest));
        Assert.Equal(formatted, extracted);
        RecoveryKeyEncoding.Parse(extracted);
    }

    [Fact]
    public void SuggestedPath_DefaultsToDocuments_NotArchiveFolder()
    {
        string suggested = RecoveryKeyOfflineCopy.SuggestedNewFilePath();
        string documents = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Assert.Equal(documents, Path.GetDirectoryName(suggested));
        Assert.Equal(RecoveryKeyOfflineCopy.DefaultFileName, Path.GetFileName(suggested));
    }

    private static string ExtractKey(string document)
    {
        const string marker = "Recovery key:";
        int i = document.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0);
        string rest = document[(i + marker.Length)..].TrimStart();
        string line = rest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return line.Trim();
    }
}
