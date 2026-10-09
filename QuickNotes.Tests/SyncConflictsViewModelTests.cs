using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public class SyncConflictsViewModelTests
{
    private class FakeConflictService : ISyncConflictService
    {
        public List<SyncConflictRecord> Conflicts { get; } = new();
        public Dictionary<int, SyncConflictDetail> Details { get; } = new();
        public string? LastActionExecuted { get; private set; }

        public Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Conflicts.Where(c => !c.IsResolved).ToList());
        }

        public Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Conflicts.Count(c => !c.IsResolved));
        }

        public Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default)
        {
            Details.TryGetValue(conflictId, out var detail);
            return Task.FromResult(detail);
        }

        public Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default)
        {
            LastActionExecuted = "KeepBoth";
            var c = Conflicts.FirstOrDefault(x => x.Id == conflictId);
            if (c != null) c.IsResolved = true;
            return Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth"));
        }

        public Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default)
        {
            LastActionExecuted = "KeepLocal";
            var c = Conflicts.FirstOrDefault(x => x.Id == conflictId);
            if (c != null) c.IsResolved = true;
            return Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal"));
        }

        public Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default)
        {
            LastActionExecuted = "AcceptRemote";
            var c = Conflicts.FirstOrDefault(x => x.Id == conflictId);
            if (c != null) c.IsResolved = true;
            return Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote"));
        }

        public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default)
        {
            LastActionExecuted = "Merge";
            LastMergedText = mergedText;
            var c = Conflicts.FirstOrDefault(x => x.Id == conflictId);
            if (c != null) c.IsResolved = true;
            return Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "Merge"));
        }

        public string? LastMergedText { get; private set; }

        public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, SyncConflictMergeChoices choices, CancellationToken ct = default)
        {
            LastChoices = choices;
            return ResolveMergeNoteAsync(conflictId, mergedText, ct);
        }

        public SyncConflictMergeChoices? LastChoices { get; private set; }
    }

    [Fact]
    public async Task LoadConflicts_PopulatesList_AndSelectsFirstItem()
    {
        var fakeService = new FakeConflictService();
        var rec1 = new SyncConflictRecord { Id = 1, SyncId = Guid.NewGuid(), EntityType = "Note", Reason = "Conflict 1" };
        var rec2 = new SyncConflictRecord { Id = 2, SyncId = Guid.NewGuid(), EntityType = "Tag", Reason = "Conflict 2" };
        fakeService.Conflicts.Add(rec1);
        fakeService.Conflicts.Add(rec2);

        fakeService.Details[1] = new SyncConflictDetail
        {
            ConflictId = 1,
            EntityType = "Note",
            LocalTitle = "Title 1",
            LocalText = "Text 1",
            RemoteTitle = "Cloud Title 1",
            RemoteText = "Cloud Text 1",
            InitialMergedText = "Merged Draft 1"
        };

        var vm = new SyncConflictsViewModel(fakeService);
        await vm.LoadConflictsAsync();

        Assert.Equal(2, vm.Conflicts.Count);
        Assert.True(vm.HasConflicts);
        Assert.False(vm.HasNoConflicts);
        Assert.NotNull(vm.SelectedConflict);
        Assert.Equal(1, vm.SelectedConflict.ConflictId);
        Assert.Equal("Merged Draft 1", vm.MergedText);
    }

    [Fact]
    public async Task ResolveKeepLocalCommand_ResolvesAndRemovesItem_AndFiresEvent()
    {
        var fakeService = new FakeConflictService();
        fakeService.Conflicts.Add(new SyncConflictRecord { Id = 10, SyncId = Guid.NewGuid(), EntityType = "Note", Reason = "Test" });
        fakeService.Details[10] = new SyncConflictDetail { ConflictId = 10, EntityType = "Note" };

        var vm = new SyncConflictsViewModel(fakeService);
        await vm.LoadConflictsAsync();

        bool eventFired = false;
        vm.ConflictsResolved += () => eventFired = true;

        Assert.True(vm.KeepLocalCommand.CanExecute(null));
        await vm.ResolveKeepLocalAsync();

        Assert.True(eventFired);
        Assert.Equal("KeepLocal", fakeService.LastActionExecuted);
        Assert.Empty(vm.Conflicts);
        Assert.False(vm.HasConflicts);
        Assert.True(vm.HasNoConflicts);
        Assert.Null(vm.SelectedConflict);
    }

    [Fact]
    public async Task ResolveMergeCommand_ResolvesAndPassesMergedText()
    {
        var fakeService = new FakeConflictService();
        fakeService.Conflicts.Add(new SyncConflictRecord { Id = 20, SyncId = Guid.NewGuid(), EntityType = "Note", Reason = "Merge test" });
        fakeService.Details[20] = new SyncConflictDetail { ConflictId = 20, EntityType = "Note", InitialMergedText = "Initial" };

        var vm = new SyncConflictsViewModel(fakeService);
        await vm.LoadConflictsAsync();

        vm.MergedText = "User edited merged text";
        vm.UseLocalTitle = true;
        vm.UseLocalTags = true;
        await vm.ResolveMergeAsync();

        Assert.Equal("Merge", fakeService.LastActionExecuted);
        Assert.Equal("User edited merged text", fakeService.LastMergedText);
        Assert.Equal(SyncConflictFieldChoice.Local, fakeService.LastChoices!.Title);
        Assert.Empty(vm.Conflicts);
    }

    [Fact]
    public async Task Detail_ExposesDeviceTimeReason_AndDoesNotSelectDestructiveDefault()
    {
        var fakeService = new FakeConflictService();
        fakeService.Conflicts.Add(new SyncConflictRecord { Id = 7, SyncId = Guid.NewGuid(), EntityType = "Note", Reason = "Точная причина" });
        fakeService.Details[7] = new SyncConflictDetail
        {
            ConflictId = 7,
            EntityType = "Note",
            Reason = "Точная причина",
            ReasonDisplay = "Точная причина",
            LocalDeviceDisplay = "Это устройство (abcd1234)",
            RemoteDeviceDisplay = "Устройство deadbeef",
            LocalUpdatedDisplay = "01.01.2026 12:00",
            RemoteUpdatedDisplay = "01.01.2026 13:00",
            LocalTitle = "L",
            RemoteTitle = "R",
            LocalText = "local md",
            RemoteText = "remote md",
            InitialMergedText = "draft"
        };

        var vm = new SyncConflictsViewModel(fakeService);
        await vm.LoadConflictsAsync();
        Assert.Equal("Точная причина", vm.SelectedConflict!.ReasonDisplay);
        Assert.Contains("устройство", vm.SelectedConflict.LocalDeviceDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasDefaultDestructiveChoice);
        Assert.False(vm.CanCommitMerge);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.True(vm.KeepLocalCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetailLoad_StaleSuccess_AfterAThenB_DoesNotOverwriteB_WhenBCompletesFirst()
    {
        var gated = CreateGatedPair(honorCancellation: false);
        using var vm = new SyncConflictsViewModel(gated.Service, loadOnStart: false);
        await vm.LoadConflictsAsync();

        var itemA = vm.Conflicts[0];
        var itemB = vm.Conflicts[1];
        Task loadA = vm.PendingDetailLoad;
        vm.SelectedConflict = itemB;
        Task loadB = vm.PendingDetailLoad;

        gated.GateB.SetResult(gated.DetailB);
        await loadB;
        AssertSelectedIsB(vm, itemA, itemB, gated);

        vm.UseLocalTitle = true;
        vm.UseLocalTags = true;
        vm.MergedText = "user-merge-B";
        string? status = vm.StatusMessage;
        bool canMerge = vm.MergeCommand.CanExecute(null);
        bool canKeepLocal = vm.KeepLocalCommand.CanExecute(null);
        bool canKeepBoth = vm.KeepBothCommand.CanExecute(null);

        gated.GateA.SetResult(gated.DetailA);
        await loadA;

        Assert.Same(itemB, vm.SelectedConflict);
        Assert.Null(itemA.Detail);
        Assert.Same(gated.DetailB, itemB.Detail);
        Assert.Equal("user-merge-B", vm.MergedText);
        Assert.True(vm.UseLocalTitle);
        Assert.True(vm.UseLocalTags);
        Assert.Equal(status, vm.StatusMessage);
        Assert.Equal(canMerge, vm.MergeCommand.CanExecute(null));
        Assert.Equal(canKeepLocal, vm.KeepLocalCommand.CanExecute(null));
        Assert.Equal(canKeepBoth, vm.KeepBothCommand.CanExecute(null));
        Assert.True(vm.CanCommitMerge);
        Assert.False(vm.HasDefaultDestructiveChoice);
    }

    [Fact]
    public async Task DetailLoad_StaleSuccess_AfterAThenB_DoesNotOverwriteB_WhenACompletesFirst()
    {
        var gated = CreateGatedPair(honorCancellation: false);
        using var vm = new SyncConflictsViewModel(gated.Service, loadOnStart: false);
        await vm.LoadConflictsAsync();

        var itemA = vm.Conflicts[0];
        var itemB = vm.Conflicts[1];
        Task loadA = vm.PendingDetailLoad;
        vm.SelectedConflict = itemB;
        Task loadB = vm.PendingDetailLoad;

        gated.GateA.SetResult(gated.DetailA);
        await loadA;

        Assert.Same(itemB, vm.SelectedConflict);
        Assert.Null(itemA.Detail);
        Assert.Null(itemB.Detail);
        Assert.Equal(string.Empty, vm.MergedText);
        Assert.False(vm.UseLocalTitle);
        Assert.False(vm.CanCommitMerge);
        Assert.False(vm.MergeCommand.CanExecute(null));

        gated.GateB.SetResult(gated.DetailB);
        await loadB;
        AssertSelectedIsB(vm, itemA, itemB, gated);
    }

    [Fact]
    public async Task DetailLoad_StaleException_DoesNotChangeCurrentB()
    {
        var gated = CreateGatedPair(honorCancellation: false);
        using var vm = new SyncConflictsViewModel(gated.Service, loadOnStart: false);
        await vm.LoadConflictsAsync();

        var itemA = vm.Conflicts[0];
        var itemB = vm.Conflicts[1];
        Task loadA = vm.PendingDetailLoad;
        vm.SelectedConflict = itemB;
        Task loadB = vm.PendingDetailLoad;

        gated.GateB.SetResult(gated.DetailB);
        await loadB;
        AssertSelectedIsB(vm, itemA, itemB, gated);
        vm.UseRemoteTitle = true;
        vm.UseRemoteTags = true;
        vm.StatusMessage = "B-status";
        vm.MergedText = "merge-B-edit";
        bool canMerge = vm.MergeCommand.CanExecute(null);

        gated.GateA.SetException(new InvalidOperationException("stale-A-failure"));
        await loadA;

        Assert.Null(itemA.Detail);
        Assert.Same(gated.DetailB, itemB.Detail);
        Assert.Equal("B-status", vm.StatusMessage);
        Assert.Equal("merge-B-edit", vm.MergedText);
        Assert.True(vm.UseRemoteTitle);
        Assert.True(vm.UseRemoteTags);
        Assert.Equal(canMerge, vm.MergeCommand.CanExecute(null));
        Assert.DoesNotContain("stale-A-failure", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(loadA.IsFaulted);
    }

    [Fact]
    public async Task DetailLoad_StaleCancellation_DoesNotChangeCurrentB()
    {
        var gated = CreateGatedPair(honorCancellation: true);
        using var vm = new SyncConflictsViewModel(gated.Service, loadOnStart: false);
        await vm.LoadConflictsAsync();

        var itemA = vm.Conflicts[0];
        var itemB = vm.Conflicts[1];
        Task loadA = vm.PendingDetailLoad;
        vm.SelectedConflict = itemB;
        Task loadB = vm.PendingDetailLoad;

        await loadA;
        Assert.False(loadA.IsFaulted);
        Assert.Same(itemB, vm.SelectedConflict);
        Assert.Null(itemA.Detail);
        Assert.Null(itemB.Detail);
        Assert.Equal(string.Empty, vm.MergedText);
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.MergeCommand.CanExecute(null));

        gated.GateB.SetResult(gated.DetailB);
        await loadB;
        AssertSelectedIsB(vm, itemA, itemB, gated);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task DetailLoad_DisposeDuringInFlight_DoesNotThrowOrApplyStaleException()
    {
        var gated = CreateGatedPair(honorCancellation: false);
        var vm = new SyncConflictsViewModel(gated.Service, loadOnStart: false);
        await vm.LoadConflictsAsync();
        Task loadA = vm.PendingDetailLoad;
        var itemA = vm.Conflicts[0];

        vm.Dispose();
        gated.GateA.SetException(new InvalidOperationException("disposed-load"));
        await loadA;

        Assert.False(loadA.IsFaulted);
        Assert.Null(itemA.Detail);
        Assert.DoesNotContain("disposed-load", vm.StatusMessage ?? string.Empty, StringComparison.Ordinal);
    }

    private static GatedConflictPair CreateGatedPair(bool honorCancellation)
    {
        var recA = new SyncConflictRecord { Id = 1, SyncId = Guid.NewGuid(), EntityType = "Tag", Reason = "A" };
        var recB = new SyncConflictRecord { Id = 2, SyncId = Guid.NewGuid(), EntityType = "Note", Reason = "B" };
        var detailA = new SyncConflictDetail
        {
            ConflictId = 1,
            EntityType = "Tag",
            LocalTitle = "Tag A",
            LocalText = "plaintext-A-must-not-apply-to-B",
            RemoteText = "remote-A",
            InitialMergedText = "merge-A"
        };
        var detailB = new SyncConflictDetail
        {
            ConflictId = 2,
            EntityType = "Note",
            LocalTitle = "Note B",
            LocalText = "local-B",
            RemoteText = "remote-B",
            InitialMergedText = "merge-B"
        };

        var service = new GatedConflictService { HonorCancellation = honorCancellation };
        service.Conflicts.Add(recA);
        service.Conflicts.Add(recB);
        return new GatedConflictPair(service, detailA, detailB);
    }

    private static void AssertSelectedIsB(
        SyncConflictsViewModel vm,
        SyncConflictItemViewModel itemA,
        SyncConflictItemViewModel itemB,
        GatedConflictPair gated)
    {
        Assert.Same(itemB, vm.SelectedConflict);
        Assert.Null(itemA.Detail);
        Assert.Same(gated.DetailB, itemB.Detail);
        Assert.Equal("merge-B", vm.MergedText);
        Assert.Equal("Note B", itemB.Title);
        Assert.True(itemB.CanMerge);
        Assert.False(itemA.CanMerge);
        Assert.False(vm.HasDefaultDestructiveChoice);
        Assert.False(vm.CanCommitMerge);
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.True(vm.KeepLocalCommand.CanExecute(null));
        Assert.True(vm.KeepBothCommand.CanExecute(null));
    }

    private sealed class GatedConflictPair
    {
        public GatedConflictPair(GatedConflictService service, SyncConflictDetail detailA, SyncConflictDetail detailB)
        {
            Service = service;
            DetailA = detailA;
            DetailB = detailB;
        }

        public GatedConflictService Service { get; }
        public SyncConflictDetail DetailA { get; }
        public SyncConflictDetail DetailB { get; }
        public TaskCompletionSource<SyncConflictDetail?> GateA => Service.Gates[1];
        public TaskCompletionSource<SyncConflictDetail?> GateB => Service.Gates[2];
    }

    private sealed class GatedConflictService : ISyncConflictService
    {
        public List<SyncConflictRecord> Conflicts { get; } = new();
        public Dictionary<int, TaskCompletionSource<SyncConflictDetail?>> Gates { get; } = new()
        {
            [1] = NewGate(),
            [2] = NewGate()
        };
        public bool HonorCancellation { get; set; }

        public Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default)
            => Task.FromResult(Conflicts.Where(c => !c.IsResolved).ToList());

        public Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default)
            => Task.FromResult(Conflicts.Count(c => !c.IsResolved));

        public async Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default)
        {
            if (!Gates.TryGetValue(conflictId, out var gate))
            {
                return null;
            }

            if (HonorCancellation)
            {
                using (ct.Register(() => gate.TrySetCanceled(ct)))
                {
                    return await gate.Task;
                }
            }

            return await gate.Task;
        }

        public Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default)
            => Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth"));

        public Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default)
            => Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal"));

        public Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default)
            => Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote"));

        public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default)
            => Task.FromResult(SyncConflictResolutionResult.Succeeded(conflictId, "Merge"));

        public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(
            int conflictId,
            string mergedText,
            SyncConflictMergeChoices choices,
            CancellationToken ct = default)
            => ResolveMergeNoteAsync(conflictId, mergedText, ct);

        private static TaskCompletionSource<SyncConflictDetail?> NewGate()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
