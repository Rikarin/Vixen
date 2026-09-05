// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Threading;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>That <see cref="AppBuilder" /> actually hands the placement to the scheduler.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one assertion the placement's own tests cannot make.</b>
///         <c>ProcessorAffinityPlacementTests</c> proves the policy hands out performance cores
///         first, and <c>WorkerPlacementTests</c> proves a scheduler <em>given</em> a placement asks
///         each worker to place itself. Neither says the two are ever introduced. Deleting the line
///         in <see cref="AppBuilder" /> that builds the placement leaves both of those suites green
///         and every worker unpinned — which is this repository's commonest defect, a finished thing
///         nothing calls, one level up from the thing itself.
///     </para>
///     <para>
///         ⚠ <b>It needs a topology that says yes, which this machine is not.</b> macOS reports
///         <see cref="IProcessorTopology.SupportsAffinity" /> as <see langword="false" />, so a test
///         run against the real platform would read <c>WorkersPlaced == 0</c> whether the wiring is
///         there or not — a green that means nothing. <see cref="PinningPlatform" /> substitutes a
///         topology that pins, so zero and non-zero say different things.
///     </para>
/// </remarks>
public class WorkerPinningWiringTests {
    // A hang check, not a bound. Workers place themselves as they start, so the count either
    // reaches the worker count promptly or the wiring is absent; this bounds the second case
    // instead of spinning forever.
    const int HangCheckMilliseconds = 30_000;

    /// <summary>Asking for pinning on the command line reaches the workers.</summary>
    [Fact]
    public void TheFlagReachesTheSchedulerAndNotJustTheConfig() {
        using var platform = new PinningPlatform(4);

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless", "--vixen-workers", "3", "--vixen-pin-workers"]))
            .WithPlatform(platform)
            .Build(new Quiet());

        Assert.True(
            SpinUntilPlaced(application.Services.Jobs, 3),
            $"Only {application.Services.Jobs.WorkersPlaced} of 3 workers were placed — the placement never reached the scheduler."
        );

        // The pins are the platform's own record, so this is the topology saying it was called
        // rather than the scheduler saying it called something.
        Assert.Equal(3, platform.Topology.Pins);
    }

    /// <summary>And the same run without the flag pins nothing, so the assertion above can fail.</summary>
    /// <remarks>
    ///     Without this half the test above is satisfied by an <see cref="AppBuilder" /> that pinned
    ///     unconditionally, which would be a different defect with the same green.
    /// </remarks>
    [Fact]
    public void WithoutTheFlagNothingIsPinnedOnAMachineThatWouldAllowIt() {
        using var platform = new PinningPlatform(4);

        using var application = new AppBuilder(AppArguments.Parse(["--vixen-headless", "--vixen-workers", "3"]))
            .WithPlatform(platform)
            .Build(new Quiet());

        Assert.Equal(0, application.Services.Jobs.WorkersPlaced);
        Assert.Equal(0, platform.Topology.Pins);
    }

    static bool SpinUntilPlaced(JobScheduler jobs, int wanted) =>
        SpinWait.SpinUntil(() => jobs.WorkersPlaced >= wanted, HangCheckMilliseconds);

    /// <summary>A game with no device, because the workers are the whole of what is under test.</summary>
    sealed class Quiet : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Graphics.Enabled = false;
    }

    /// <summary>A headless platform whose processors accept affinity, which this machine's do not.</summary>
    [SuppressMessage("Design", "CA1001", Justification = "Disposed through IPlatform, which the tests own.")]
    sealed class PinningPlatform(int processors) : IPlatform {
        readonly HeadlessPlatform inner = new(new() { Organisation = "Vixen", Application = "Test" });

        public PinningTopology Topology { get; } = new(processors);

        public string Name => inner.Name;
        public PlatformCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyList<IWindow> Windows => inner.Windows;
        public IDisplayInfo Displays => inner.Displays;
        public SystemColorScheme ColorScheme => inner.ColorScheme;
        public SystemAccessibility Accessibility => inner.Accessibility;
        public IFileSystemHost FileSystem => inner.FileSystem;
        public IClipboard Clipboard => inner.Clipboard;
        public INativeDialogs Dialogs => inner.Dialogs;
        public ILifecycle Lifecycle => inner.Lifecycle;
        public IInputSource Input => inner.Input;
        public ITextInput TextInput => inner.TextInput;
        public IPowerInfo Power => inner.Power;

        public IProcessorTopology Processors => Topology;

        public IWindow CreateWindow(in WindowOptions options) => inner.CreateWindow(options);

        public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) => inner.TryGetWindow(id, out window);

        public ReadOnlySpan<PlatformEvent> PumpEvents() => inner.PumpEvents();

        public bool TryOpenUrl(string url) => inner.TryOpenUrl(url);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Counts what it was asked to pin, from however many threads ask at once.</summary>
    sealed class PinningTopology(int processors) : IProcessorTopology {
        int pins;

        public int Pins => Volatile.Read(ref pins);

        public int AvailableProcessors => processors;
        public int PhysicalCores => processors;
        public int PerformanceCores => processors / 2;
        public bool SupportsAffinity => true;

        public ProcessorClass ClassOf(int processor) =>
            processor < PerformanceCores ? ProcessorClass.Performance : ProcessorClass.Efficiency;

        public bool TrySetAffinity(int processor) {
            Interlocked.Increment(ref pins);

            return true;
        }

        public void ClearAffinity() => Interlocked.Decrement(ref pins);
    }
}
