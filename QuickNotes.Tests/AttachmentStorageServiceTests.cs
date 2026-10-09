using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class AttachmentStorageServiceTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly AttachmentStorageService _service;

    public AttachmentStorageServiceTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), $"QuickNotes_AttTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testBaseDir);
        _service = new AttachmentStorageService(_testBaseDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private static (QuickNotesDbContext Context, SqliteConnection Connection) CreateInMemoryDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);

        return (context, connection);
    }

    [Fact]
    public void ComputeSha256_ValidContent_CalculatesCorrectLowercaseHex()
    {
        var testFilePath = Path.Combine(_testBaseDir, "test_sha.txt");
        var content = "QuickNotes attachment checksum test 2026";
        File.WriteAllBytes(testFilePath, Encoding.UTF8.GetBytes(content));

        string expectedHash;
        using (var sha = SHA256.Create())
        {
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
            expectedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var calculated = AttachmentFileHelper.ComputeSha256(testFilePath);

        Assert.Equal(expectedHash, calculated);
        Assert.Equal(64, calculated.Length);
    }

    [Fact]
    public void GetSafeStoredFileName_SanitizesExtensionAndPreventsDirectoryTraversal()
    {
        var sha = new string('a', 64);

        // Standard extension
        var name1 = AttachmentFileHelper.GetSafeStoredFileName(sha, "my_document.pdf");
        Assert.Equal($"{sha}.pdf", name1);

        // Path traversal attempt in file name
        var name2 = AttachmentFileHelper.GetSafeStoredFileName(sha, "../../../secret.png");
        Assert.Equal($"{sha}.png", name2);

        // Multiple dots and traversal in extension
        var name3 = AttachmentFileHelper.GetSafeStoredFileName(sha, "file.tar.gz");
        Assert.Equal($"{sha}.gz", name3);

        // Invalid characters in extension
        var name4 = AttachmentFileHelper.GetSafeStoredFileName(sha, "file.p/d\\f");
        Assert.DoesNotContain("/", name4);
        Assert.DoesNotContain("\\", name4);
        Assert.DoesNotContain("..", name4);
    }

    [Theory]
    [InlineData("..\\secret.txt")]
    [InlineData("../secret.txt")]
    [InlineData("Attachments/../../secret.txt")]
    [InlineData("Attachments\\..\\..\\secret.txt")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
    public void GetFullPath_DirectoryTraversal_ThrowsInvalidOperationException(string maliciousRelativePath)
    {
        Assert.Throws<InvalidOperationException>(() => _service.GetFullPath(maliciousRelativePath));
    }

    [Fact]
    public void SaveAttachment_AtomicCopy_CreatesDestinationFileAndCleansUpTemp()
    {
        var sourceFile = Path.Combine(_testBaseDir, "sample.txt");
        File.WriteAllText(sourceFile, "Hello QuickNotes Attachments!");

        var result = _service.SaveAttachment(sourceFile, maxSizeBytes: 1024 * 1024);

        Assert.NotNull(result);
        Assert.Equal("sample.txt", result.OriginalFileName);
        Assert.True(File.Exists(result.FullPath));
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal(new FileInfo(sourceFile).Length, result.Size);

        // Check that no .tmp files remain in attachments directory
        var tmpFiles = Directory.GetFiles(_service.AttachmentsDirectory, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void SaveAttachment_LimitExceeded_ThrowsAndDoesNotCreateDestination()
    {
        var sourceFile = Path.Combine(_testBaseDir, "large.dat");
        var data = new byte[1000];
        File.WriteAllBytes(sourceFile, data);

        // Limit to 500 bytes
        var ex = Assert.Throws<InvalidOperationException>(() => _service.SaveAttachment(sourceFile, maxSizeBytes: 500));
        Assert.Contains("превышает", ex.Message);

        var storedFiles = Directory.GetFiles(_service.AttachmentsDirectory);
        Assert.Empty(storedFiles);
    }

    [Fact]
    public void SaveAttachment_SameFileTwice_ReusesExistingManagedFile()
    {
        var sourceFile1 = Path.Combine(_testBaseDir, "file1.png");
        var sourceFile2 = Path.Combine(_testBaseDir, "file2.png");
        var content = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        File.WriteAllBytes(sourceFile1, content);
        File.WriteAllBytes(sourceFile2, content);

        var result1 = _service.SaveAttachment(sourceFile1, maxSizeBytes: 1024 * 1024);
        var result2 = _service.SaveAttachment(sourceFile2, maxSizeBytes: 1024 * 1024);

        Assert.Equal(result1.StoredFileName, result2.StoredFileName);
        Assert.Equal(result1.FullPath, result2.FullPath);
        Assert.Equal(result1.Sha256, result2.Sha256);
        Assert.True(result1.WasCreated);
        Assert.False(result2.WasCreated);

        // Exactly one physical file exists in Attachments directory
        var allFiles = Directory.GetFiles(_service.AttachmentsDirectory);
        Assert.Single(allFiles);
    }

    [Fact]
    public void FileExists_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var relativePath = "Attachments\\non_existent_file.png";
        var exists = _service.FileExists(relativePath);
        Assert.False(exists);
    }

    [Fact]
    public void DeleteManagedFileIfUnreferenced_SharedFile_DoesNotDeleteWhileReferenced()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var sourceFile = Path.Combine(_testBaseDir, "shared.pdf");
        File.WriteAllText(sourceFile, "Shared PDF document content");
        var saveResult = _service.SaveAttachment(sourceFile, maxSizeBytes: 1024 * 1024);

        // Note 1 and Note 2 in DB
        var note1 = new Note { Text = "Note 1" };
        var note2 = new Note { Text = "Note 2" };
        db.Notes.AddRange(note1, note2);
        db.SaveChanges();

        // Both notes reference the same stored file
        var att1 = new NoteAttachment
        {
            NoteId = note1.Id,
            OriginalFileName = "doc1.pdf",
            StoredFileName = saveResult.StoredFileName,
            RelativePath = saveResult.RelativePath,
            ContentType = saveResult.ContentType,
            Size = saveResult.Size,
            Sha256 = saveResult.Sha256
        };

        var att2 = new NoteAttachment
        {
            NoteId = note2.Id,
            OriginalFileName = "doc2.pdf",
            StoredFileName = saveResult.StoredFileName,
            RelativePath = saveResult.RelativePath,
            ContentType = saveResult.ContentType,
            Size = saveResult.Size,
            Sha256 = saveResult.Sha256
        };

        db.NoteAttachments.AddRange(att1, att2);
        db.SaveChanges();

        // Delete attachment from Note 1 in DB
        db.NoteAttachments.Remove(att1);
        db.SaveChanges();

        // Attempt to delete physical file: should return false because Note 2 still references it!
        var deleted = _service.DeleteManagedFileIfUnreferenced(db, saveResult.StoredFileName);
        Assert.False(deleted);
        Assert.True(File.Exists(saveResult.FullPath));

        // Now delete attachment from Note 2 in DB
        db.NoteAttachments.Remove(att2);
        db.SaveChanges();

        // Now attempt to delete physical file: should succeed because 0 notes reference it!
        var deletedSecond = _service.DeleteManagedFileIfUnreferenced(db, saveResult.StoredFileName);
        Assert.True(deletedSecond);
        Assert.False(File.Exists(saveResult.FullPath));
    }

    [Fact]
    public void CleanupUnreferencedFiles_RemovesOnlyOrphanedPhysicalFiles()
    {
        var (db, conn) = CreateInMemoryDb();
        using var _conn = conn;
        using var _db = db;

        var file1 = Path.Combine(_testBaseDir, "keep.txt");
        var file2 = Path.Combine(_testBaseDir, "delete.txt");
        File.WriteAllText(file1, "Keep me");
        File.WriteAllText(file2, "Delete me");

        var save1 = _service.SaveAttachment(file1, maxSizeBytes: 1024 * 1024);
        var save2 = _service.SaveAttachment(file2, maxSizeBytes: 1024 * 1024);

        var note = new Note { Text = "Note" };
        db.Notes.Add(note);
        db.SaveChanges();

        var att1 = new NoteAttachment
        {
            NoteId = note.Id,
            OriginalFileName = "keep.txt",
            StoredFileName = save1.StoredFileName,
            RelativePath = save1.RelativePath,
            ContentType = save1.ContentType,
            Size = save1.Size,
            Sha256 = save1.Sha256
        };
        db.NoteAttachments.Add(att1);
        db.SaveChanges();

        // save2 is NOT in database
        _service.CleanupUnreferencedFiles(db, new[] { save1.StoredFileName, save2.StoredFileName });

        Assert.True(File.Exists(save1.FullPath));
        Assert.False(File.Exists(save2.FullPath));
    }
}
