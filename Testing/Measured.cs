// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.Testing;

/// <summary>
///     Counts the bytes a piece of work asks for, once it has stopped growing whatever it grows.
/// </summary>
/// <remarks>
///     <para>
///         Per-thread allocation rather than <c>GC.GetTotalMemory</c>, because the latter measures the
///         heap after whatever the collector has done to it and is not a count of what this code asked
///         for. A single-digit byte count here is not noise to be tolerated — the paths these tests
///         guard own every buffer they touch, so the answer is zero or something is wrong.
///     </para>
///     <para>
///         <b>Which is why the collector has to be kept out of the measurement.</b>
///         <c>GC.GetAllocatedBytesForCurrentThread</c> is the thread's allocated total less the unused
///         remainder of the allocation context it is currently holding, and a collection retires that
///         context — so the counter steps up by as much as a whole context, some eight kilobytes, at
///         whichever collection happens to land inside the measured window, whether or not the measured
///         code allocated a byte of it. Whether one lands depends on what else ran in the process, so
///         the artefact moves with test ordering and machine load rather than with the code under
///         measurement. The same artefact is described at <c>FuzzSession.Weigh</c>, where a window long
///         enough to absorb it was the right answer. It is not the right answer here: the claim is
///         exactly zero, and a tolerance wide enough to hide a collection is wide enough to hide the
///         per-frame allocation these tests exist to catch.
///     </para>
///     <para>
///         Collecting first is most of the defence. It hands the loop an <i>empty</i> allocation
///         context, and work that allocates nothing never asks for another one — so a collection
///         landing mid-window, including one provoked by whichever other test class is running
///         alongside this one, finds nothing to settle up and the reading stays at zero. The artefact
///         can therefore only ever inflate a reading that was already non-zero, which is what
///         <see cref="GC.CollectionCount" /> either side is for: a non-zero reading with a collection
///         in it is thrown away and measured again rather than reported. A number that survives
///         <see cref="Attempts" /> of that is a number the collector cannot explain.
///     </para>
///     <para>
///         <b>A caller whose work has a side effect must count it rather than predict it.</b> Because a
///         non-zero reading is measured again, <paramref name="work" /> runs <i>at least</i>
///         <c>warmUp + passes</c> times and possibly several times that. Assert that a counter the work
///         advances is non-zero, or read it after the measurement — never against an arithmetic
///         expectation of how many times the work should have run.
///     </para>
/// </remarks>
static class Measured {
    /// <summary>How many times a non-zero reading with a collection in it is measured again.</summary>
    const int Attempts = 4;

    /// <summary>
    ///     Runs <paramref name="work" /> <paramref name="warmUp" /> times, then counts the bytes the
    ///     next <paramref name="passes" /> of it allocate on this thread.
    /// </summary>
    public static long Bytes(Action work, int warmUp = 200, int passes = 1_000) {
        for (var index = 0; index < warmUp; index++) {
            work();
        }

        for (var attempt = 1; ; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var collections = GC.CollectionCount(0);
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var index = 0; index < passes; index++) {
                work();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated == 0 || GC.CollectionCount(0) == collections) {
                return allocated;
            }

            if (attempt == Attempts) {
                Assert.Fail(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"All {Attempts} measurements read non-zero with a collection inside them, the last of them {allocated:N0} B. The loop starts on an empty allocation context, so work that allocates nothing reads zero straight through a collection — this work is allocating, and the collection only means that number is an upper bound rather than the amount."
                    )
                );
            }
        }
    }
}
