// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The guard, shown failing — because a guard that has never fired is not a guard.</summary>
/// <remarks>
///     ⚠ <b>Every property test in both geometry suites runs inside
///     <see cref="RunawayGuard.Run{T}(string, Func{T}, TimeSpan?)" />, and if the watchdog were wired
///     up wrongly every one of them would still be green.</b> That is the same argument
///     <c>IsotropicRemesh.Run</c>'s <c>reproject</c> parameter exists for: "a bound that passes whether
///     or not the code under it runs is not a test". So the cap is a parameter, and here it is set to a
///     fifth of a second and breached deliberately.
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
}
