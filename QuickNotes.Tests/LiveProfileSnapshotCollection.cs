using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// Definition for tests that inspect or snapshot the production live profile directory (%LOCALAPPDATA%\QuickNotes).
/// Parallelization is disabled across this collection to prevent races between profile snapshot tests.
/// </summary>
[CollectionDefinition(LiveProfileSnapshotCollection.Name, DisableParallelization = true)]
public class LiveProfileSnapshotCollection
{
    public const string Name = "LiveProfileSnapshot";
}
