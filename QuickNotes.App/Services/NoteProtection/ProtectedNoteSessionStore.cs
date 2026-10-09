using System;
using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// Process-local store of unlocked protected notes.
/// Holds the derived key and decrypted payload ONLY in memory; never persisted.
/// All sessions are wiped on application exit (see App.OnExit).
/// </summary>
public interface IProtectedNoteSessionStore
{
    bool IsUnlocked(int noteId);
    ProtectedNoteSession? Get(int noteId);
    void Add(ProtectedNoteSession session);
    void Remove(int noteId);
    void WipeAll();
    IReadOnlyList<int> UnlockedNoteIds { get; }
}

public sealed class ProtectedNoteSessionStore : IProtectedNoteSessionStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ProtectedNoteSession> _sessions = new();

    public IReadOnlyList<int> UnlockedNoteIds
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Keys.ToList();
            }
        }
    }

    public bool IsUnlocked(int noteId)
    {
        lock (_lock)
        {
            return _sessions.ContainsKey(noteId);
        }
    }

    public ProtectedNoteSession? Get(int noteId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(noteId, out var session) ? session : null;
        }
    }

    public void Add(ProtectedNoteSession session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.NoteId, out var existing))
            {
                existing.Wipe();
            }
            _sessions[session.NoteId] = session;
        }
    }

    public void Remove(int noteId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(noteId, out var session))
            {
                session.Wipe();
            }
        }
    }

    public void WipeAll()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Wipe();
            }
            _sessions.Clear();
        }
    }
}
