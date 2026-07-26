// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.IO.Watch;
using Xunit;

namespace Vixen.Core.IO.Tests;

/// <summary>The thin layer between a real filesystem and the coalescer.</summary>
/// <remarks>
///     Deliberately few, and deliberately patient. The behaviour worth testing lives in
///     <see cref="FileChangeCoalescer" />, where it can be driven at exact timestamps; what is left
///     here is "does the platform's watcher reach it, and does an OS path come out as the right
///     virtual path". Those cannot be tested without a real filesystem and a real wait, so they are
///     two tests with a generous budget rather than twenty with a tight one.
/// </remarks>
public sealed class FileWatcherTests : IDisposable {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-watch-" + Guid.NewGuid().ToString("N"));

    public FileWatcherTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AWrittenFileArrivesAsAVirtualPathUnderTheMount() {
        using var watcher = new FileWatcher(directory, MountPoints.Project);
        watcher.Debounce = TimeSpan.FromMilliseconds(20);

        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        File.WriteAllText(Path.Combine(directory, "Assets", "a.txt"), "hello");

        var changes = WaitForChanges(watcher, change => change.Path == new VirtualPath("/project/Assets/a.txt"));

        Assert.Contains(changes, change => change.Path == new VirtualPath("/project/Assets/a.txt"));
    }

    [Fact]
    public void ASuppressedPathIsNotReportedBack() {
        using var watcher = new FileWatcher(directory, MountPoints.Project);
        watcher.Debounce = TimeSpan.FromMilliseconds(20);

        // What the asset pipeline does: announce the write, then make it. Without this the importer
        // wakes the watcher, which reimports, which writes an artefact.
        watcher.Suppress(new("/project/artefact.bin"));
        File.WriteAllText(Path.Combine(directory, "artefact.bin"), "generated");

        // Something else changes too, so the test can tell "suppressed" from "the watcher is asleep".
        File.WriteAllText(Path.Combine(directory, "other.txt"), "real");

        var changes = WaitForChanges(watcher, change => change.Path == new VirtualPath("/project/other.txt"));

        Assert.Contains(changes, change => change.Path == new VirtualPath("/project/other.txt"));
        Assert.DoesNotContain(changes, change => change.Path == new VirtualPath("/project/artefact.bin"));
    }

    static List<FileChange> WaitForChanges(FileWatcher watcher, Func<FileChange, bool> until) {
        var collected = new List<FileChange>();
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < Budget) {
            watcher.Drain(collected);

            if (collected.Any(until)) {
                // One more drain after a beat, so anything the platform reported alongside the
                // change being waited for is in the list too — which is what lets a test assert
                // that something was *not* reported.
                Thread.Sleep(100);
                watcher.Drain(collected);
                return collected;
            }

            Thread.Sleep(20);
        }

        return collected;
    }
}
