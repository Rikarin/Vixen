// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Xunit;

namespace Vixen.Core.Diagnostics.Tests;

/// <summary>
///     The type a deliberately-allocating fixture is meant to be named by. Top level rather than
///     nested, so that the name the runtime's sampler reports is the one <see cref="Type.FullName" />
///     gives with no <c>+</c> in it.
/// </summary>
sealed class DeliberateAllocation {
    public long A = 1, B = 2, C = 3, D = 4;
}

/// <summary>
///     What the allocation gate says when it fails — which is the part of it that has never been
///     tested, and the part docs/plan/12 § allocation gates actually specifies.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A non-zero count is not the property under test here.</b> An instrument that fails to
///         attach produces a failure message with a byte count and no names, which is exactly what the
///         gate printed before any of this existed — so a test that only asserted the failure would
///         pass on the day the listener stopped working. Every assertion below is therefore about a
///         <i>name</i> appearing in the message, or about the message saying in so many words that the
///         instrument did not run.
///     </para>
///     <para>
///         <b>The sabotage that proves it.</b> Changing <c>AllocationNames.AllocationSamplingKeyword</c>
///         to any other bit turns <see cref="TheGuiltyTypeIsNamed" /> red with "no names, though the
///         instrument did run" — the sampler is armed on a keyword that carries nothing. Removing the
///         <c>OSThreadId</c> filter leaves it green but lets a parallel collection's types into the
///         list, which is why <see cref="TheReportIsNotJustEveryTypeInTheProcess" /> exists.
///     </para>
/// </remarks>
public class MeasuredTests {
    /// <summary>Static so the JIT cannot delete the allocation as non-escaping. See AllocationTests.</summary>
    static DeliberateAllocation? sink;

    [Fact]
    public void WorkThatAllocatesNothingPassesQuietly() {
        var counter = 0;

        Measured.NothingAllocated(() => counter++, warmUp: 8, passes: 200);

        Assert.True(counter > 0);
    }

    /// <summary>
    ///     The whole point: the message names the type, not only the size. Asserting on the name is
    ///     what makes this test fail on the day the sampler stops being delivered — a count-only
    ///     assertion would not.
    /// </summary>
    [Fact]
    public void TheGuiltyTypeIsNamed() {
        var failure = Record.Exception(() => Measured.NothingAllocated(Allocate, warmUp: 8, passes: 200));

        Assert.NotNull(failure);
        Assert.Contains(
            typeof(DeliberateAllocation).FullName!,
            failure.Message,
            StringComparison.Ordinal
        );

        // ⚠ Found the hard way: with the drain sentinel unrecognised, every explanatory run sat on the
        // patience ceiling for twenty seconds and still produced the right names — so the naming
        // assertion above was green while the mechanism underneath it was broken.
        Assert.DoesNotContain("The drain gave up waiting", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The count is still reported, and still first — the name is an addition, not a swap.</summary>
    [Fact]
    public void TheByteCountSurvivesAlongsideTheName() {
        var failure = Record.Exception(() => Measured.NothingAllocated(Allocate, warmUp: 8, passes: 200));

        Assert.NotNull(failure);
        Assert.Contains("Expected 0 B, measured", failure.Message, StringComparison.Ordinal);
        Assert.Contains("B/pass", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Samples arrive from every thread in the process, so without the <c>OSThreadId</c> filter a
    ///     parallel test collection's garbage would be listed as this test's. A thread allocating a
    ///     different type throughout must not appear.
    /// </summary>
    [Fact]
    public void TheReportIsNotJustEveryTypeInTheProcess() {
        using var stop = new CancellationTokenSource();

        var noise = new Thread(
            () => {
                var bucket = new List<byte[]>(64);

                while (!stop.IsCancellationRequested) {
                    if (bucket.Count == 64) {
                        bucket.Clear();
                    }

                    bucket.Add(new byte[512]);
                }
            }
        ) { IsBackground = true };

        noise.Start();

        try {
            var failure = Record.Exception(() => Measured.NothingAllocated(Allocate, warmUp: 8, passes: 200));

            Assert.NotNull(failure);
            Assert.Contains(typeof(DeliberateAllocation).FullName!, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Byte[]", failure.Message, StringComparison.Ordinal);
        } finally {
            stop.Cancel();
            noise.Join();
        }
    }

    static void Allocate() => sink = new();
}
