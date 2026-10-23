// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Fuzz.Tests;

/// <summary>
///     Two writers of one finding — the watchdog thread and the session that gets the abandoned case
///     back — land the same bytes under the same name, and neither may fail for having lost the race.
/// </summary>
/// <remarks>
///     ⚠ <b>The race is real and was observed, not imagined:</b> on Windows the second
///     <c>File.WriteAllBytes</c> to a path the first still has open fails with "being used by another
///     process", and the session recorded that <c>IOException</c> as the case having thrown, which
///     made one runaway into two findings (2026-09-22, whole-assembly run). This drives the same
///     path from four threads at once, many rounds, which is the shape that lost before the writer
///     was taught that a file named by its content's fingerprint is already the file it wanted.
/// </remarks>
public sealed class CorpusWriteTests {
    [Fact]
    public void ConcurrentWritersOfOneFindingNeverFail() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-fuzz-corpus", nameof(ConcurrentWritersOfOneFindingNeverFail));

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, true);
        }

        try {
            for (var round = 0; round < 200; round++) {
                var input = BitConverter.GetBytes(round);
                var paths = new string[4];
                var gate = new Barrier(paths.Length);

                Parallel.For(
                    0,
                    paths.Length,
                    writer => {
                        gate.SignalAndWait();
                        paths[writer] = Corpus.WriteRegression(directory, "race", input);
                    }
                );

                Assert.All(paths, path => Assert.Equal(paths[0], path));
                Assert.Equal(input, File.ReadAllBytes(paths[0]));
            }
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     ⚠ A finding is on disk whole or not at all: while its bytes are being written, no
    ///     <c>.bin</c> exists for a reader to open (#1431).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>CaseGuardTests.ACaseOverTheAllocationCeilingIsCaughtInFlight</c> polled the folder
    ///         from inside the running case and read the <c>.bin</c> the watchdog was still writing;
    ///         on Windows that read throws "being used by another process" (reproduced against a
    ///         <c>FileStream</c> held the way <c>File.WriteAllBytes</c> holds it), the case recorded a
    ///         second finding, and <c>Assert.Single</c> saw two. The window is a few microseconds and
    ///         a loaded machine opened it.
    ///     </para>
    ///     <para>
    ///         ⚠ So this does not race anything: the staging hook holds the writer in that window
    ///         and the folder is looked at from inside it, deterministically. Red against a writer
    ///         that writes the final name and then calls the hook.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFindingIsPublishedWholeOrNotAtAll() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-fuzz-corpus", nameof(AFindingIsPublishedWholeOrNotAtAll));

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, true);
        }

        try {
            byte[] input = [0xBE, 0x7A, 0x5E, 0x77];
            var looked = false;

            var path = Corpus.WriteRegression(
                directory,
                "window",
                input,
                staged => {
                    looked = true;

                    // The bytes are all there, under a name no reader enumerates...
                    Assert.Equal(input, File.ReadAllBytes(staged));
                    Assert.DoesNotContain(".bin", Path.GetFileName(staged), StringComparison.Ordinal);

                    // ...and the finding is not, by either of the ways it is looked for.
                    Assert.Empty(Directory.GetFiles(Path.Combine(directory, "window"), "*.bin"));
                    Assert.Empty(Corpus.ReadRegressions(directory, "window"));
                }
            );

            Assert.True(looked, "the staging hook never ran, so the window was never looked at");

            Assert.Equal(input, File.ReadAllBytes(path));
            Assert.Equal([input], Corpus.ReadRegressions(directory, "window"));

            // Published by a rename, so nothing is left behind it.
            Assert.Equal([path], Directory.GetFiles(Path.Combine(directory, "window")));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>A finding already on disk is kept rather than rewritten, and its path comes back.</summary>
    [Fact]
    public void AnExistingFindingIsKept() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-fuzz-corpus", nameof(AnExistingFindingIsKept));

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, true);
        }

        try {
            byte[] input = [1, 2, 3, 4];
            var first = Corpus.WriteRegression(directory, "kept", input);
            var stamp = File.GetLastWriteTimeUtc(first);
            var again = Corpus.WriteRegression(directory, "kept", input);

            Assert.Equal(first, again);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(again));
        } finally {
            Directory.Delete(directory, true);
        }
    }
}
