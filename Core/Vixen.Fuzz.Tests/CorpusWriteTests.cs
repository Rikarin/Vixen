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
