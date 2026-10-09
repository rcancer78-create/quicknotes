using System;
using System.IO;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// File-system abstraction for the two-phase attachment file transitions performed by
/// <see cref="NoteProtectionService"/>. Enables failure injection in tests: every file
/// create/read/write/delete during protect / password-rotation / unprotect / attachment
/// add-remove flows goes through this adapter.
/// </summary>
public interface IProtectionFileAdapter
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] content);
    void Delete(string path);
    string ComputeSha256(string path);
}

/// <summary>
/// Default physical implementation. Writes are atomic per file (temp file + move)
/// inside the same directory.
/// </summary>
public sealed class PhysicalProtectionFileAdapter : IProtectionFileAdapter
{
    public bool Exists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] content)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(dir ?? ".", $"{Guid.NewGuid():N}.tmpwrite");
        try
        {
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string ComputeSha256(string path)
    {
        return AttachmentFileHelper.ComputeSha256(path);
    }
}
