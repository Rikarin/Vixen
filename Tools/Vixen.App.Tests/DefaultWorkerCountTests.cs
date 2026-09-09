// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     ⚠ That the one worker count the browser can accept is a count this builder can produce.
/// </summary>
/// <remarks>
///     <para>
///         <c>AppBuilder</c> read <c>Math.Max(1, AvailableProcessors - 1)</c>, whose floor is one, and
///         handed the result to <c>new JobScheduler(workers)</c>
///         (<a href="https://github.com/Rikarin/Vixen/issues/486">#486</a>). On <c>browser-wasm</c>
///         without a threaded runtime pack, <c>Thread.Start()</c> throws
///         <see cref="PlatformNotSupportedException" />, so every browser head this builder produced
///         asked for a thread it could not have — <b>including the non-isolated page</b>, where
///         <c>WebProcessors</c> already answered one and <c>Math.Max(1, 0)</c> turned that into a
///         request for one worker.
///     </para>
///     <para>
///         ⚠ <b>So the bug was not only the number <c>WebServices</c> reported.</b> #486 asks whether a
///         count derived from cross-origin isolation can be honoured, and the answer is that it made
///         no difference at this call site: one available processor and eight came out of that
///         expression as one worker and seven, and both are more than the runtime has.
///     </para>
///     <para>
///         ⚠ <b>The browser branch is asserted by passing the flag, not by running in a browser.</b>
///         This suite runs on desktop, so <c>OperatingSystem.IsBrowser()</c> is false here and a test
///         that called the real overload would exercise one half. Naming the flag as a parameter is
///         what makes both halves visible; what it cannot prove is that <c>AppBuilder</c> passes
///         <c>OperatingSystem.IsBrowser()</c> into it, which is one token at the call site and is
///         read there.
///     </para>
/// </remarks>
public sealed class DefaultWorkerCountTests {
    /// <summary>
    ///     ⚠ The case that crashed: a browser head asks for no workers, whatever the topology says.
    /// </summary>
    /// <remarks>
    ///     Both arguments, because the two were reached differently and both were wrong. Eight is the
    ///     cross-origin-isolated page reporting <c>navigator.hardwareConcurrency</c>; one is the
    ///     ordinary page, and it is the one that shows the floor rather than the topology was the
    ///     defect.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void ABrowserHeadAsksForNoWorkers(int availableProcessors) =>
        Assert.Equal(0, AppBuilder.DefaultWorkerCount(availableProcessors, isBrowser: true));

    /// <summary>Everywhere else, one worker per available processor beyond the calling thread.</summary>
    /// <remarks>
    ///     The main thread participates in the scheduler's work, so a four-processor machine wants
    ///     three workers and not four. This half is unchanged and is asserted so that a future edit to
    ///     the browser branch cannot quietly take the desktop one with it.
    /// </remarks>
    [Theory]
    [InlineData(4, 3)]
    [InlineData(2, 1)]
    public void ADesktopHeadLeavesOneProcessorForTheCallingThread(int availableProcessors, int expected) =>
        Assert.Equal(expected, AppBuilder.DefaultWorkerCount(availableProcessors, isBrowser: false));

    /// <summary>
    ///     ⚠ And the floor stays where it is off the browser, which is why it cannot simply be
    ///     deleted.
    /// </summary>
    /// <remarks>
    ///     A one-processor container reports one, and <c>1 - 1</c> is zero. Zero workers is a
    ///     supported mode — the calling thread runs the work — but it is not what a desktop head that
    ///     merely landed on a small quota should silently become, and changing that is a different
    ///     decision from #486's. The floor is kept deliberately, and named here so that the next
    ///     reader knows it was looked at.
    /// </remarks>
    [Fact]
    public void ASingleProcessorMachineStillGetsOneWorker() =>
        Assert.Equal(1, AppBuilder.DefaultWorkerCount(1, isBrowser: false));
}
