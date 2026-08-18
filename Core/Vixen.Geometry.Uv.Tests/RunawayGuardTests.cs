// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The guard, shown failing — because a guard that has never fired is not a guard.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every property test in both geometry suites runs inside
///         <see cref="RunawayGuard.Run{T}(string, Func{T}, TimeSpan?, long?)" />, and if the watchdog
///         were wired up wrongly every one of them would still be green.</b> That is the same argument
///         <c>IsotropicRemesh.Run</c>'s <c>reproject</c> parameter exists for: "a bound that passes
///         whether or not the code under it runs is not a test". So the cap is a parameter, and here
///         it is set to a fifth of a second and breached deliberately.
///     </para>
///     <para>
///         ⚠ <b>The heap ceiling is a parameter now for exactly the same reason, and until it was one
///         the half <c>RemeshPipelinePropertyTests</c> calls "the one that matters" had never fired
///         anywhere.</b> Sabotaging it used to mean really retaining a gigabyte, which is not a
///         thing to do on a shared runner, so nobody did — and the sixteen-sample grace, the message
///         and the subtraction were all carried by inspection. Sixty-four megabytes against four
///         proves the same code.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately <i>not</i> here is the converse — a case that churns without
///         retaining, asserted not to fire.</b> It is a true claim about the guard and it cannot be
///         tested without a threshold: the assertion would be "the process did not grow by N bytes
///         for sixteen consecutive samples", and <see cref="GC.GetTotalMemory(bool)" /> is
///         process-wide, so a neighbouring class deciding to allocate is enough to fail it. That is
///         the shape this whole file was rewritten to stop shipping.
///     </para>
/// </remarks>
public class RunawayGuardTests {
    [Fact]
    public void A_case_that_returns_returns_what_it_produced() =>
        Assert.Equal(41, RunawayGuard.Run("adding up", () => 41));

    /// <summary>An ordinary failure stays an ordinary failure, with its own stack.</summary>
    /// <remarks>
    ///     ⚠ Without this the guard would turn every assertion failure inside a property into a
    ///     <see cref="RunawayException" /> with no line number in it, which is worse than no guard.
    /// </remarks>
    [Fact]
    public void An_exception_from_the_case_arrives_unchanged() {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => RunawayGuard.Run<int>("throwing", () => throw new InvalidOperationException("the case said so"))
        );

        Assert.Equal("the case said so", thrown.Message);
        Assert.Contains(nameof(An_exception_from_the_case_arrives_unchanged), thrown.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>A case that will not stop is named, and the run carries on without it.</summary>
    /// <remarks>
    ///     ⚠ <b>The wedged thread is not taken back and cannot be.</b> It spins for a second and then
    ///     ends on its own, so this test leaves nothing behind; a real runaway would not, which is why
    ///     the worker is a background thread and why the message says the thread was abandoned rather
    ///     than stopped.
    /// </remarks>
    [Fact]
    public void A_case_that_will_not_stop_is_a_named_finding() {
        var released = new ManualResetEventSlim(false);

        try {
            var thrown = Assert.Throws<RunawayException>(
                () => RunawayGuard.Run("a loop with no exit", () => released.Wait(), TimeSpan.FromMilliseconds(200))
            );

            Assert.Contains("a loop with no exit", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("abandoned", thrown.Message, StringComparison.Ordinal);
        } finally {
            released.Set();
        }
    }

    /// <summary>A case that keeps what it allocates is named, and the message says what it kept.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half the guard exists for and it had no test.</b> The runaways actually
    ///         measured in this code are growth failures, not hangs — a pre-remesh that quadruples its
    ///         triangle count every round allocates 763 MB in one, and a mirrored remesh 8.7 GB in
    ///         42 s — and both of those <i>return</i>. The clock never sees them.
    ///     </para>
    ///     <para>
    ///         The case retains sixteen times the ceiling it is given rather than a byte over it,
    ///         because <see cref="GC.GetTotalMemory(bool)" /> is process-wide and a margin the
    ///         neighbours cannot supply is what makes this a test of the guard rather than of the
    ///         suite it is running in. It holds the list until the guard has abandoned it, which is
    ///         what <see cref="RunawayGuard.RetentionSamples" /> consecutive samples requires.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_case_that_keeps_what_it_allocates_is_a_named_finding() {
        var released = new ManualResetEventSlim(false);

        try {
            var thrown = Assert.Throws<RunawayException>(
                () => RunawayGuard.Run(
                    "a loop that keeps everything",
                    () => {
                        var kept = new List<byte[]>();

                        // ⚠ Before the first byte, because the watchdog takes its baseline reading
                        // after starting this thread rather than before it. Sixty-four megabytes of
                        // page mappings can land inside that window, and then the growth is under
                        // the baseline instead of over it and this test measures nothing.
                        Thread.Sleep(100);

                        for (var block = 0; block < 64; block++) {
                            // GC.AllocateUninitializedArray, so the megabyte costs a page mapping
                            // rather than a megabyte of zeroing on every one of sixty-four blocks.
                            kept.Add(GC.AllocateUninitializedArray<byte>(1 << 20));
                        }

                        released.Wait();

                        // Read after the wait, or a release build is free to collect the list while
                        // the guard is still sampling and the case becomes a hang instead.
                        return kept.Count;
                    },
                    // A minute it will never reach, so a breach here can only be the heap. Without
                    // this the clock would be the twenty-minute ceiling and the test would take it.
                    TimeSpan.FromMinutes(1),
                    4L << 20
                )
            );

            Assert.Contains("a loop that keeps everything", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("consecutive samples", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("4,194,304 B", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("abandoned", thrown.Message, StringComparison.Ordinal);
        } finally {
            released.Set();
        }
    }

    /// <summary>A case slow enough to be sampled many times over is not a finding.</summary>
    /// <remarks>
    ///     ⚠ <b>The one test here that exercises the <i>defaults</i>, and therefore the only one that
    ///     would notice <see cref="RunawayGuard.Cap" /> being tightened back into a stopwatch.</b>
    ///     Half a second is some thirty polls — every branch in the watchdog runs — and against a
    ///     twenty-minute ceiling it is not a finding, which is the whole claim: the guard reports
    ///     cases that do not return, not cases that take a while. A cap sized against a healthy case
    ///     would eventually fail this on a loaded machine, and that is the point of it being here.
    /// </remarks>
    [Fact]
    public void A_case_that_is_merely_slow_is_not_a_finding() =>
        Assert.Equal(
            41,
            RunawayGuard.Run(
                "a case that takes its time",
                () => {
                    Thread.Sleep(500);

                    return 41;
                }
            )
        );
}
