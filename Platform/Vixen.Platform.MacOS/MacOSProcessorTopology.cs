// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>Processor counts and performance classes, from <c>sysctl</c>.</summary>
/// <remarks>
///     <para>
///         <b>Affinity is not supported here, and that is Apple's decision rather than an omission.</b>
///         macOS has <c>thread_policy_set</c> with <c>THREAD_AFFINITY_POLICY</c>, which was always
///         documented as a hint about which threads share cache rather than a request for a
///         processor, and which is unimplemented on Apple silicon. What the system offers instead is
///         quality-of-service classes: a thread declares whether it is user-interactive or
///         background and the scheduler chooses the core, which on a machine with two kinds of core
///         it is in a much better position to do than we are.
///         <see cref="IProcessorTopology.SupportsAffinity" /> reports <see langword="false" />, and
///         <c>docs/plan/03</c>'s deferred pinning work has its answer on this platform: do not.
///     </para>
///     <para>
///         <b>Performance levels, not core numbers.</b> <c>hw.nperflevels</c> is 2 on Apple silicon
///         and absent on Intel, and level 0 is the fastest. That gives the counts, which is what
///         sizing a worker pool needs. What it does not give is a map from a logical processor index
///         to a level — the kernel does not publish one, because the index is not something a
///         process is meant to act on. <see cref="ClassOf" /> answers from the counts in index
///         order, which is the documented layout of <c>hw.perflevel*</c> and is advisory in the
///         strict sense: nothing here can pin a thread to act on it anyway.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacOSProcessorTopology : IProcessorTopology {
    readonly int performanceLogical;

    /// <summary>Reads the machine's topology, once.</summary>
    public MacOSProcessorTopology() {
        PhysicalCores = (int)(Read("hw.physicalcpu") ?? Environment.ProcessorCount);

        var levels = Read("hw.nperflevels") ?? 1;

        if (levels < 2) {
            return;
        }

        performanceLogical = (int)(Read("hw.perflevel0.logicalcpu") ?? 0);
        PerformanceCores = (int)(Read("hw.perflevel0.physicalcpu") ?? 0);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="Environment.ProcessorCount" />, which on macOS honours the process's
    ///     <c>hw.logicalcpu</c> and the container limits of anything running it under a virtual
    ///     machine.
    /// </remarks>
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores { get; }

    /// <inheritdoc />
    public int PerformanceCores { get; }

    /// <summary>Always <see langword="false" />. See the remarks on this class.</summary>
    public bool SupportsAffinity => false;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) {
        if (performanceLogical <= 0 || (uint)processor >= (uint)AvailableProcessors) {
            return ProcessorClass.Unknown;
        }

        return processor < performanceLogical ? ProcessorClass.Performance : ProcessorClass.Efficiency;
    }

    /// <summary>Always <see langword="false" />. See the remarks on this class.</summary>
    public bool TrySetAffinity(int processor) => false;

    /// <summary>Does nothing: nothing here ever set an affinity to undo.</summary>
    public void ClearAffinity() { }

    /// <summary>Reads an integer <c>sysctl</c>, or nothing where the key does not exist.</summary>
    /// <remarks>
    ///     The width varies by key — <c>hw.physicalcpu</c> is 32 bits and several of its neighbours
    ///     are 64 — so the size the kernel reports back is what decides how to read the buffer
    ///     rather than an assumption per key.
    /// </remarks>
    static long? Read(string name) {
        long value = 0;
        var size = (nuint)sizeof(long);

        if (Sysctl.ByName(name, &value, &size, null, 0) != 0) {
            return null;
        }

        return size switch {
            8 => value,
            4 => (int)value,
            _ => null
        };
    }
}

/// <summary>The one libc call this assembly needs.</summary>
[SupportedOSPlatform("macos")]
static unsafe partial class Sysctl {
    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ByName(string name, void* output, nuint* size, void* input, nuint inputSize);
}
