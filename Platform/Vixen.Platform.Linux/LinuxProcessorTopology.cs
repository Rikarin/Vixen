// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.Linux;

/// <summary>Processor counts, performance classes and thread affinity, from the kernel and sysfs.</summary>
/// <remarks>
///     <para>
///         This is the Linux half of what <c>docs/plan/03</c> deferred out of
///         <c>Vixen.Core.Threading</c>. <c>sched_setaffinity</c> is the call; everything else here is
///         <c>/sys/devices/system/cpu</c>, which is where Linux says what the machine is made of.
///     </para>
///     <para>
///         <b><c>pid 0</c> means the calling thread, not the process.</b> Linux threads are
///         processes with a shared address space, and the affinity calls take a thread id — passing
///         zero means "me", which is the whole of what
///         <see cref="IProcessorTopology.TrySetAffinity" /> promises. There is no version of this
///         that pins somebody else's thread, and that is deliberate: a job scheduler pins its own
///         workers from inside them.
///     </para>
///     <para>
///         <b>Two different vendors say "this core is faster" in two different places.</b> ARM
///         big.LITTLE publishes <c>cpu_capacity</c> per processor, a number derived from the device
///         tree. Intel's hybrid parts publish two PMU devices, <c>cpu_core</c> and <c>cpu_atom</c>,
///         each listing the processors it covers. Both are read; a machine with neither is
///         homogeneous as far as anything here can tell, and says so by reporting
///         <see cref="ProcessorClass.Unknown" /> rather than by guessing.
///     </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class LinuxProcessorTopology : IProcessorTopology {
    /// <summary>Enough <c>cpu_set_t</c> for 1024 processors, which is the kernel's own default.</summary>
    const int SetBytes = 128;

    [ThreadStatic]
    static byte[]? previousMask;

    readonly ProcessorClass[] classes;

    /// <summary>Reads the machine's topology, once.</summary>
    /// <remarks>
    ///     Cached, unlike the display list. A processor can in principle be hot-plugged offline
    ///     through sysfs, and a job scheduler asking how many cores there are on every job is a
    ///     scheduler that spends its time in the virtual file system.
    /// </remarks>
    public LinuxProcessorTopology() {
        var count = Environment.ProcessorCount;
        classes = new ProcessorClass[count];

        PhysicalCores = CountPhysicalCores(count);
        PerformanceCores = Classify(classes);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Environment.ProcessorCount" />, which on Linux is already the intersection of
    ///     the affinity mask the process was started with and the CPU quota of its cgroup. Counting
    ///     the directories under <c>/sys/devices/system/cpu</c> would give the machine's number and
    ///     size a worker pool for a machine the process cannot use all of.
    /// </remarks>
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores { get; }

    /// <inheritdoc />
    public int PerformanceCores { get; }

    /// <summary>Whether the kernel let us read this thread's affinity mask.</summary>
    /// <remarks>
    ///     Read rather than assumed. A seccomp filter, a sufficiently restrictive container runtime
    ///     or an unusual libc can all make the call fail, and reporting <see langword="true" /> for
    ///     something that will not work is exactly what the capability model exists to prevent.
    /// </remarks>
    public bool SupportsAffinity { get; } = Probe();

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) =>
        (uint)processor < (uint)classes.Length ? classes[processor] : ProcessorClass.Unknown;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) {
        if ((uint)processor >= (uint)classes.Length || !SupportsAffinity) {
            return false;
        }

        // The first pin is the one worth remembering: it is the only mask this thread had before
        // anything of ours touched it.
        if (previousMask is null) {
            var current = new byte[SetBytes];

            fixed (byte* mask = current) {
                if (Libc.SchedGetAffinity(0, SetBytes, mask) == 0) {
                    previousMask = current;
                }
            }
        }

        var wanted = new byte[SetBytes];
        wanted[processor / 8] = (byte)(1 << (processor % 8));

        fixed (byte* mask = wanted) {
            return Libc.SchedSetAffinity(0, SetBytes, mask) == 0;
        }
    }

    /// <inheritdoc />
    public void ClearAffinity() {
        if (previousMask is not { } restore) {
            return;
        }

        fixed (byte* mask = restore) {
            Libc.SchedSetAffinity(0, SetBytes, mask);
        }

        previousMask = null;
    }

    static bool Probe() {
        var buffer = stackalloc byte[SetBytes];
        return Libc.SchedGetAffinity(0, SetBytes, buffer) == 0;
    }

    /// <summary>
    ///     Counts distinct (package, core) pairs, which is what a physical core is on a machine with
    ///     more than one socket.
    /// </summary>
    static int CountPhysicalCores(int logical) {
        var cores = new HashSet<(int Package, int Core)>();

        for (var index = 0; index < logical; index++) {
            var directory = $"/sys/devices/system/cpu/cpu{index}/topology";
            var package = Sysfs.ReadInteger(Path.Combine(directory, "physical_package_id"));
            var core = Sysfs.ReadInteger(Path.Combine(directory, "core_id"));

            if (package is { } socket && core is { } identifier) {
                cores.Add((socket, identifier));
            }
        }

        // A container may not have sysfs mounted at all, and a virtual machine may report no
        // topology. One core per logical processor is the honest answer there rather than zero.
        return cores.Count > 0 ? cores.Count : logical;
    }

    /// <summary>Fills in the per-processor classes, and returns how many performance cores there are.</summary>
    static int Classify(ProcessorClass[] classes) {
        var capacities = new int[classes.Length];
        var distinct = false;

        for (var index = 0; index < classes.Length; index++) {
            capacities[index] = Sysfs.ReadInteger($"/sys/devices/system/cpu/cpu{index}/cpu_capacity") ?? 0;
            distinct |= capacities[index] != capacities[0];
        }

        if (distinct) {
            var highest = 0;

            foreach (var capacity in capacities) {
                highest = Math.Max(highest, capacity);
            }

            var fast = 0;

            for (var index = 0; index < classes.Length; index++) {
                classes[index] = capacities[index] == highest
                    ? ProcessorClass.Performance
                    : ProcessorClass.Efficiency;

                fast += capacities[index] == highest ? 1 : 0;
            }

            return fast;
        }

        // Intel's hybrid parts: two PMU devices, each naming the processors it covers. The presence
        // of `cpu_atom` at all is the statement that the machine is heterogeneous.
        var performance = Sysfs.ParseCpuList(Sysfs.ReadText("/sys/devices/cpu_core/cpus"));
        var efficiency = Sysfs.ParseCpuList(Sysfs.ReadText("/sys/devices/cpu_atom/cpus"));

        if (performance.Count == 0 || efficiency.Count == 0) {
            return 0;
        }

        foreach (var index in performance) {
            if (index < classes.Length) {
                classes[index] = ProcessorClass.Performance;
            }
        }

        foreach (var index in efficiency) {
            if (index < classes.Length) {
                classes[index] = ProcessorClass.Efficiency;
            }
        }

        return performance.Count;
    }
}

/// <summary>The two libc calls this assembly needs.</summary>
[SupportedOSPlatform("linux")]
static unsafe partial class Libc {
    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    public static partial int SchedSetAffinity(int pid, nuint size, byte* mask);

    [LibraryImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    public static partial int SchedGetAffinity(int pid, nuint size, byte* mask);
}
