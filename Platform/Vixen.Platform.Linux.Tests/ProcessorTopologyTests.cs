// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.Linux.Tests;

/// <summary>
///     The sysfs parsing, and — where the suite is running on Linux — the affinity calls
///     themselves.
/// </summary>
public class ProcessorTopologyTests {
    /// <summary>
    ///     sysfs's processor-list syntax, which mixes single indices and inclusive ranges in one
    ///     comma-separated string. Reading a range as exclusive loses the last performance core on
    ///     every Intel hybrid machine.
    /// </summary>
    [Theory]
    [InlineData("0-7", new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData("0", new[] { 0 })]
    [InlineData("0,2,4", new[] { 0, 2, 4 })]
    [InlineData("0-1,4-5", new[] { 0, 1, 4, 5 })]
    [InlineData("0-3,8", new[] { 0, 1, 2, 3, 8 })]
    public void AProcessorListIsInclusiveAtBothEnds(string text, int[] expected) =>
        Assert.Equal(expected, Sysfs.ParseCpuList(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void NothingUsableIsAnEmptyList(string? text) =>
        Assert.Empty(Sysfs.ParseCpuList(text));

    /// <summary>An absent sysfs file is the normal case in a container and is not an exception.</summary>
    [Fact]
    public void AMissingSysfsFileReadsAsNothing() {
        Assert.Null(Sysfs.ReadText("/sys/devices/system/cpu/cpu9999/cpu_capacity"));
        Assert.Null(Sysfs.ReadText("/proc/there-is-no-such-file"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void TheTopologyDescribesAPlausibleMachine() {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads /sys and calls sched_getaffinity.");

        var topology = new LinuxProcessorTopology();

        Assert.Equal(Environment.ProcessorCount, topology.AvailableProcessors);
        Assert.InRange(topology.PhysicalCores, 1, topology.AvailableProcessors);
        Assert.InRange(topology.PerformanceCores, 0, topology.AvailableProcessors);
    }

    /// <summary>
    ///     Pinning and unpinning on the thread that asked, which is the whole of what the deferred
    ///     thread-affinity work owed. Restoring matters more than setting: a test framework's worker
    ///     thread that is left pinned to processor 0 makes everything after it slow in a way nothing
    ///     reports.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void AThreadCanBePinnedAndReleased() {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Calls sched_setaffinity.");

        var topology = new LinuxProcessorTopology();
        Assert.SkipUnless(topology.SupportsAffinity, "The kernel would not report this thread's affinity.");

        Assert.True(topology.TrySetAffinity(0));
        topology.ClearAffinity();

        // Out of range is refused rather than clamped: pinning to a processor that is not there
        // would silently be pinning to a different one.
        Assert.False(topology.TrySetAffinity(topology.AvailableProcessors));
        Assert.False(topology.TrySetAffinity(-1));
    }
}
