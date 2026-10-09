using System;
using System.Collections.Generic;
using System.IO;
using QuickNotes.App.Services.NoteProtection;

namespace QuickNotes.Tests;

/// <summary>
/// Test file adapter that records all operations and allows injecting failures
/// during write, read, or delete phases to verify atomic two-phase rollback semantics.
/// </summary>
public sealed class FaultInjectingProtectionFileAdapter : IProtectionFileAdapter
{
    private readonly PhysicalProtectionFileAdapter _inner = new();

    public readonly List<string> CreatedFiles = new();
    public readonly List<string> DeletedFiles = new();
    public readonly List<string> ReadFiles = new();

    public Func<string, byte[], bool>? ShouldFailWrite { get; set; }
    public Func<string, bool>? ShouldFailRead { get; set; }
    public Func<string, bool>? ShouldFailDelete { get; set; }

    public bool Exists(string path) => _inner.Exists(path);

    public byte[] ReadAllBytes(string path)
    {
        ReadFiles.Add(path);
        if (ShouldFailRead != null && ShouldFailRead(path))
        {
            throw new IOException($"[Injected] Simulated read failure for {path}");
        }
        return _inner.ReadAllBytes(path);
    }

    public void WriteAllBytes(string path, byte[] content)
    {
        if (ShouldFailWrite != null && ShouldFailWrite(path, content))
        {
            throw new IOException($"[Injected] Simulated write failure for {path}");
        }
        _inner.WriteAllBytes(path, content);
        CreatedFiles.Add(path);
    }

    public void Delete(string path)
    {
        DeletedFiles.Add(path);
        if (ShouldFailDelete != null && ShouldFailDelete(path))
        {
            throw new IOException($"[Injected] Simulated delete failure for {path}");
        }
        _inner.Delete(path);
    }

    public string ComputeSha256(string path) => _inner.ComputeSha256(path);
}
