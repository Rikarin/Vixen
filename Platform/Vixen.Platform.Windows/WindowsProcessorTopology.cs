// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>Processor counts, performance classes and thread affinity, from the Windows scheduler.</summary>
/// <remarks>
///     <para>
///         This is the Windows half of what <c>docs/plan/03</c> deferred out of
///         <c>Vixen.Core.Threading</c>: the job scheduler wanted to pin its workers and there was no
///         platform layer to ask. <see cref="TrySetAffinity" /> is that answer, and it is a
///         <c>Try</c> because it stays one on the platforms — a browser, a container under a CPU
///         quota, macOS — that will not do it.
///     </para>
///     <para>
///         <b>Groups, not a mask.</b> Windows numbers logical processors within groups of at most
///         64, because an affinity mask is one machine word. Below 64 processors there is one group
///         and the distinction is invisible; above it, <c>SetThreadAffinityMask</c> can only address
///         the group the thread already happens to be in, which on a dual-socket machine is half of
///         it. <c>SetThreadGroupAffinity</c> addresses all of them, so that is what is used, and a
///         flat processor index is mapped onto (group, bit) here.
///     </para>
///     <para>
///         <b>Efficiency classes are relative, not absolute.</b> Windows reports a number per core
///         where higher is faster and the scale means nothing between machines. A homogeneous
///         machine reports the same number for every core, which is why this reports
///         <see cref="ProcessorClass.Unknown" /> and <c>0</c> performance cores there rather than
///         calling every core a performance core — the contract's question is whether the platform
///         distinguishes, and on that machine it does not.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsProcessorTopology : IProcessorTopology {
    [ThreadStatic]
    static GroupAffinity previousAffinity;

    [ThreadStatic]
    static bool hasPreviousAffinity;

    readonly ProcessorClass[] classes;
    readonly ushort[] groupOf;
    readonly byte[] bitOf;

    /// <summary>Reads the machine's topology, once.</summary>
    /// <remarks>
    ///     Cached rather than re-read. Unlike the display list, which changes when somebody closes a
    ///     lid, the processor topology is fixed for the life of the process — hot-plugged CPUs are a
    ///     virtualisation feature that Windows does not surface to a user-mode process mid-run — and
    ///     the enumeration allocates and walks a variable-length buffer, which is not something to
    ///     do from a scheduler.
    /// </remarks>
    public WindowsProcessorTopology() {
        var groups = Math.Max((ushort)1, Win32.GetActiveProcessorGroupCount());
        var offsets = new int[groups];
        var total = 0;

        for (ushort group = 0; group < groups; group++) {
            offsets[group] = total;
            total += (int)Win32.GetActiveProcessorCount(group);
        }

        if (total <= 0) {
            total = Environment.ProcessorCount;
            offsets = [0];
        }

        classes = new ProcessorClass[total];
        groupOf = new ushort[total];
        bitOf = new byte[total];

        for (var index = 0; index < total; index++) {
            var group = 0;

            while (group + 1 < offsets.Length && offsets[group + 1] <= index) {
                group++;
            }

            groupOf[index] = (ushort)group;
            bitOf[index] = (byte)(index - offsets[group]);
        }

        PhysicalCores = Describe(offsets, out var performanceCores);
        PerformanceCores = performanceCores;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Environment.ProcessorCount" /> rather than the sum of the groups: the
    ///     runtime's number accounts for a job object's affinity limit and for the CPU rate control
    ///     a container puts the process under, and the scheduler's does not. Sizing a worker pool
    ///     from the machine's count inside a two-processor quota produces a job system that spends
    ///     its time being descheduled.
    /// </remarks>
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores { get; }

    /// <inheritdoc />
    public int PerformanceCores { get; }

    /// <summary>Always <see langword="true" />: Windows has always allowed it.</summary>
    /// <remarks>
    ///     Allowing it and being right to use it are different questions. Pinning every worker
    ///     removes the scheduler's ability to move one off a core the OS wants for something else,
    ///     which is a win for a frame-critical worker and a loss for a pool that also runs asset
    ///     decode.
    /// </remarks>
    public bool SupportsAffinity => true;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) =>
        (uint)processor < (uint)classes.Length ? classes[processor] : ProcessorClass.Unknown;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) {
        if ((uint)processor >= (uint)groupOf.Length) {
            return false;
        }

        var affinity = new GroupAffinity { Group = groupOf[processor], Mask = (nuint)1 << bitOf[processor] };

        if (!Win32.SetThreadGroupAffinity(Win32.GetCurrentThread(), affinity, out var previous)) {
            return false;
        }

        // The first pin is the one worth remembering: it is the only mask this thread had before
        // anything of ours touched it, and a second pin's "previous" is the first pin.
        if (!hasPreviousAffinity) {
            previousAffinity = previous;
            hasPreviousAffinity = true;
        }

        return true;
    }

    /// <inheritdoc />
    public void ClearAffinity() {
        if (!hasPreviousAffinity) {
            return;
        }

        Win32.SetThreadGroupAffinity(Win32.GetCurrentThread(), previousAffinity, out _);
        hasPreviousAffinity = false;
    }

    /// <summary>Walks the core relationships, filling <see cref="classes" />.</summary>
    /// <returns>The number of physical cores.</returns>
    int Describe(int[] offsets, out int performanceCores) {
        performanceCores = 0;

        uint length = 0;
        Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, null, &length);

        if (length == 0) {
            return AvailableProcessors;
        }

        var buffer = new byte[length];
        var cores = 0;
        var raw = new byte[classes.Length];
        byte highest = 0;
        byte lowest = byte.MaxValue;

        fixed (byte* start = buffer) {
            if (!Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, start, &length)) {
                return AvailableProcessors;
            }

            for (var offset = 0u; offset + (uint)sizeof(LogicalProcessorInformation) <= length;) {
                var entry = (LogicalProcessorInformation*)(start + offset);

                // Size, not sizeof: the entries are variable-length, because a core that spans two
                // groups carries two masks inline. Stepping by the managed size walks into the
                // middle of the next entry on exactly the machines this code exists for.
                if (entry->Size <= 0) {
                    break;
                }

                offset += (uint)entry->Size;
                cores++;

                var efficiency = entry->Processor.EfficiencyClass;
                highest = Math.Max(highest, efficiency);
                lowest = Math.Min(lowest, efficiency);

                var masks = &entry->Processor.FirstGroupMask;

                for (var index = 0; index < entry->Processor.GroupCount; index++) {
                    var mask = masks[index];

                    if (mask.Group >= offsets.Length) {
                        continue;
                    }

                    for (var bit = 0; bit < sizeof(nuint) * 8; bit++) {
                        if ((mask.Mask & ((nuint)1 << bit)) == 0) {
                            continue;
                        }

                        var flat = offsets[mask.Group] + bit;

                        if (flat < raw.Length) {
                            raw[flat] = efficiency;
                        }
                    }
                }
            }
        }

        if (cores == 0 || highest == lowest) {
            return cores == 0 ? AvailableProcessors : cores;
        }

        for (var index = 0; index < classes.Length; index++) {
            classes[index] = raw[index] == highest ? ProcessorClass.Performance : ProcessorClass.Efficiency;
        }

        // Counted a second time over the entries rather than derived from `classes`, because the
        // question is how many *cores* are fast and a hyper-threaded core is two entries in that
        // array and one core here.
        fixed (byte* start = buffer) {
            for (var offset = 0u; offset + (uint)sizeof(LogicalProcessorInformation) <= length;) {
                var entry = (LogicalProcessorInformation*)(start + offset);

                if (entry->Size <= 0) {
                    break;
                }

                offset += (uint)entry->Size;

                if (entry->Processor.EfficiencyClass == highest) {
                    performanceCores++;
                }
            }
        }

        return cores;
    }
}
