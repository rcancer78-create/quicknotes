using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class IsolatedProfilePathTests
{
    [Fact]
    public void JunctionToLiveProfile_IsRejected_BeforeAnyCreate()
    {
        string live = QuickNotesDbContext.LiveProfileDirectory;
        if (!Directory.Exists(live))
        {
            return;
        }

        string[] liveBefore = Snapshot(live);

        string root = Path.Combine(Path.GetTempPath(), "qn_junc_live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string junction = Path.Combine(root, "as-live");
        try
        {
            if (!TryCreateJunction(junction, live))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() => QuickNotesDbContext.ValidateIsolatedProfilePath(junction));
            Assert.Throws<InvalidOperationException>(
                () => QuickNotesDbContext.ValidateIsolatedProfilePath(Path.Combine(junction, "child")));

            var prepared = StartupProfilePreparation.Prepare(new IsolatedProfileAppOptions
            {
                IsIsolated = true,
                ProfileDirectory = junction
            });
            Assert.False(prepared.Succeeded);
            Assert.Equal(StartupProfilePreparationKind.InvalidIsolatedPath, prepared.Kind);

            var childPrepared = StartupProfilePreparation.Prepare(new IsolatedProfileAppOptions
            {
                IsIsolated = true,
                ProfileDirectory = Path.Combine(junction, "child")
            });
            Assert.False(childPrepared.Succeeded);
            Assert.Equal(StartupProfilePreparationKind.InvalidIsolatedPath, childPrepared.Kind);
            Assert.False(Directory.Exists(Path.Combine(live, "child")));
            Assert.Equal(liveBefore, Snapshot(live));
        }
        finally
        {
            TryDeleteJunction(junction);
            SqliteTestUtil.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void JunctionToUnrelatedDirectory_IsAllowed()
    {
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = Directory.Exists(live) ? Snapshot(live) : Array.Empty<string>();

        string root = Path.Combine(Path.GetTempPath(), "qn_junc_ok_" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(root, "real");
        string junction = Path.Combine(root, "link");
        Directory.CreateDirectory(real);
        try
        {
            if (!TryCreateJunction(junction, real))
            {
                return;
            }

            QuickNotesDbContext.ValidateIsolatedProfilePath(junction);
            var prepared = StartupProfilePreparation.Prepare(new IsolatedProfileAppOptions
            {
                IsIsolated = true,
                ProfileDirectory = junction
            });
            Assert.True(prepared.Succeeded, prepared.UserMessage);
            Assert.True(Directory.Exists(real));
            Assert.Equal(liveBefore, Directory.Exists(live) ? Snapshot(live) : Array.Empty<string>());
        }
        finally
        {
            TryDeleteJunction(junction);
            SqliteTestUtil.TryDeleteDirectory(root);
        }
    }

    private static string[] Snapshot(string liveRoot)
    {
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return Directory.Exists(linkPath)
                   && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteJunction(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkPath);
            }
        }
        catch
        {
            // Best-effort: never recurse through a junction.
        }
    }
}
