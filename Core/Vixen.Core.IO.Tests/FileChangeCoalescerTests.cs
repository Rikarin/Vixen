// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.IO.Watch;
using Xunit;

namespace Vixen.Core.IO.Tests;

/// <summary>
///     The coalescer, driven by synthetic events at exact timestamps rather than by a real
///     filesystem at whatever moment it feels like.
/// </summary>
/// <remarks>
///     Every one of these sequences is something a real editor really does. They are asserted here,
///     against a clock the test controls, because the same assertions against a real filesystem
///     would be a set of sleeps — and a test that sleeps for the debounce window is a test that fails
///     on a loaded CI runner for reasons that have nothing to do with the code.
/// </remarks>
public class FileChangeCoalescerTests {
    static readonly long Millisecond = Stopwatch.Frequency / 1000;

    [Fact]
    public void NothingIsReportedUntilThePathHasBeenQuiet() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 0);

        Assert.Equal(0, coalescer.Drain(30 * Millisecond, changes));
        Assert.Empty(changes);

        Assert.Equal(1, coalescer.Drain(60 * Millisecond, changes));
        Assert.Equal(new VirtualPath("/a.txt"), changes[0].Path);
    }

    [Fact]
    public void WriteTruncateWriteIsOneChange() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        // What a text editor that saves in place actually produces.
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 5 * Millisecond);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 10 * Millisecond);

        Assert.Equal(1, coalescer.Drain(70 * Millisecond, changes));
        Assert.Equal(FileChangeKind.Changed, changes[0].Kind);
    }

    [Fact]
    public void EachWriteExtendsTheWindowRatherThanRunningItOut() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        // A large file being written in chunks. Reporting it after the first quiet 50 ms measured
        // from the *first* event would hand a consumer a half-written file.
        for (var tick = 0; tick <= 200; tick += 20) {
            coalescer.Record(new(new("/big.bin"), FileChangeKind.Changed), tick * Millisecond);
        }

        Assert.Equal(0, coalescer.Drain(230 * Millisecond, changes));
        Assert.Equal(1, coalescer.Drain(260 * Millisecond, changes));
    }

    [Fact]
    public void CreatedThenChangedIsStillCreated() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt"), FileChangeKind.Created), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 10 * Millisecond);

        coalescer.Drain(70 * Millisecond, changes);
        Assert.Equal(FileChangeKind.Created, Assert.Single(changes).Kind);
    }

    [Fact]
    public void CreatedThenDeletedIsNothingAtAll() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/tmp.txt"), FileChangeKind.Created), 0);
        coalescer.Record(new(new("/tmp.txt"), FileChangeKind.Deleted), 10 * Millisecond);

        Assert.Equal(0, coalescer.Drain(70 * Millisecond, changes));
        Assert.Empty(changes);
    }

    [Fact]
    public void DeletedThenCreatedIsAChangeRatherThanADeletion() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        // Replace-in-place. Reporting the deletion would have a consumer drop its cache for a file
        // that is sitting right there, and then reload it a moment later anyway.
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Deleted), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Created), 10 * Millisecond);

        coalescer.Drain(70 * Millisecond, changes);
        Assert.Equal(FileChangeKind.Changed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void ChangedThenDeletedIsADeletion() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Deleted), 10 * Millisecond);

        coalescer.Drain(70 * Millisecond, changes);
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(changes).Kind);
    }

    /// <summary>
    ///     The pattern vim, VS Code and every careful writer uses: write a new file, then rename it
    ///     over the old one. Raw, it is three events about two paths; what happened is that one file
    ///     changed.
    /// </summary>
    [Fact]
    public void AnAtomicSaveIsOneChangeToTheDestination() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt.tmp"), FileChangeKind.Created), 0);
        coalescer.Record(new(new("/a.txt.tmp"), FileChangeKind.Changed), 5 * Millisecond);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Renamed, new("/a.txt.tmp")), 10 * Millisecond);

        Assert.Equal(1, coalescer.Drain(70 * Millisecond, changes));
        Assert.Equal(new VirtualPath("/a.txt"), changes[0].Path);
        Assert.Equal(FileChangeKind.Changed, changes[0].Kind);
    }

    [Fact]
    public void AnOrdinaryRenameIsStillARename() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        // The source was not created inside this window, so it is a user renaming a file and the
        // provenance is worth keeping.
        coalescer.Record(new(new("/new.txt"), FileChangeKind.Renamed, new("/old.txt")), 0);

        coalescer.Drain(70 * Millisecond, changes);
        var change = Assert.Single(changes);
        Assert.Equal(FileChangeKind.Renamed, change.Kind);
        Assert.Equal(new VirtualPath("/old.txt"), change.OldPath);
    }

    [Fact]
    public void OurOwnWritesAreNotReportedBackToUs() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Suppress(new("/artefact.bin"), 0);
        coalescer.Record(new(new("/artefact.bin"), FileChangeKind.Created), 5 * Millisecond);
        coalescer.Record(new(new("/artefact.bin"), FileChangeKind.Changed), 10 * Millisecond);

        Assert.Equal(0, coalescer.Drain(70 * Millisecond, changes));
        Assert.Empty(changes);
    }

    [Fact]
    public void SuppressionExpiresRatherThanBeingPermanent() {
        var coalescer = Create();
        coalescer.SuppressionWindow = TimeSpan.FromMilliseconds(100);
        var changes = new List<FileChange>();

        coalescer.Suppress(new("/a.txt"), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 200 * Millisecond);

        Assert.Equal(1, coalescer.Drain(300 * Millisecond, changes));
    }

    [Fact]
    public void SuppressingADestinationAlsoSuppressesTheRenameOntoIt() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        // The asset pipeline writes atomically too, so suppressing only the plain events would let
        // its own rename through.
        coalescer.Suppress(new("/artefact.bin"), 0);
        coalescer.Record(new(new("/artefact.bin"), FileChangeKind.Renamed, new("/artefact.tmp")), 5 * Millisecond);

        Assert.Equal(0, coalescer.Drain(70 * Millisecond, changes));
    }

    [Fact]
    public void ChangesComeOutInTheOrderTheyWereFirstSeen() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/z.txt"), FileChangeKind.Changed), 0);
        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), Millisecond);
        coalescer.Record(new(new("/m.txt"), FileChangeKind.Changed), 2 * Millisecond);

        coalescer.Drain(70 * Millisecond, changes);

        Assert.Equal(
            [new VirtualPath("/z.txt"), new VirtualPath("/a.txt"), new VirtualPath("/m.txt")],
            changes.Select(change => change.Path)
        );
    }

    [Fact]
    public void OverflowingTheBufferIsCountedRatherThanHidden() {
        var coalescer = new FileChangeCoalescer(capacity: 4);
        var changes = new List<FileChange>();

        for (var index = 0; index < 10; index++) {
            coalescer.Record(new(new($"/file{index}.txt"), FileChangeKind.Changed), 0);
        }

        Assert.Equal(4, coalescer.PendingCount);
        Assert.Equal(6, coalescer.DroppedCount);

        // A consumer that sees a non-zero drop count knows its picture is incomplete, which is a
        // rescan. Silently reporting four of ten changes is the version of this that ships a stale
        // asset.
        Assert.Equal(4, coalescer.Drain(100 * Millisecond, changes));
    }

    [Fact]
    public void DrainingTwiceDoesNotReportTheSameChangeTwice() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 0);

        Assert.Equal(1, coalescer.Drain(70 * Millisecond, changes));
        Assert.Equal(0, coalescer.Drain(140 * Millisecond, changes));
        Assert.Single(changes);
    }

    [Fact]
    public void ClearingForgetsEverythingPending() {
        var coalescer = Create();
        var changes = new List<FileChange>();

        coalescer.Record(new(new("/a.txt"), FileChangeKind.Changed), 0);
        coalescer.Clear();

        Assert.Equal(0, coalescer.Drain(70 * Millisecond, changes));
    }

    static FileChangeCoalescer Create() => new() { Debounce = TimeSpan.FromMilliseconds(50) };
}
