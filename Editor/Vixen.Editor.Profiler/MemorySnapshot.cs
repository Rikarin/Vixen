// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Profiler;

/// <summary>Which of the four arenas doc 13 names a row belongs to.</summary>
public enum MemoryArena : byte {
    /// <summary>The garbage collector's heap.</summary>
    Managed,

    /// <summary>Native allocations, from <see cref="LeakTracker" />.</summary>
    Native,

    /// <summary>Device memory.</summary>
    Gpu,

    /// <summary>Loaded assets, and what they are holding alive.</summary>
    Assets
}

/// <summary>One line of the memory view.</summary>
/// <param name="Arena">Which arena.</param>
/// <param name="Label">What it is.</param>
/// <param name="Bytes">How much, or a count where <paramref name="IsCount" /> says so.</param>
/// <param name="Detail">A sentence about it, or <see langword="null" />.</param>
/// <param name="IsCount">
///     Whether <paramref name="Bytes" /> is a number of things rather than a number of bytes. A
///     view formats the two differently, and "1.2 KB of collections" is the mistake this avoids.
/// </param>
public readonly record struct MemoryRow(
    MemoryArena Arena,
    string Label,
    long Bytes,
    string? Detail = null,
    bool IsCount = false
);

/// <summary>
///     Where the memory view's numbers come from, for the arenas this assembly cannot see into.
/// </summary>
/// <remarks>
///     ⚠ <b>Two delegates rather than two references, and the reason is layering.</b> GPU heap usage
///     is the graphics backend's — <c>VK_EXT_memory_budget</c> on Vulkan, residency on D3D12 — and
///     asset residency is the editor's asset database. A profiler assembly that referenced both
///     would be a panel that cannot be tested without a device and a project on disk, which is the
///     bargain every other model here refuses to make.
/// </remarks>
public sealed class MemoryProviders {
    /// <summary>What the GPU's heaps report, or <see langword="null" /> where nothing can say.</summary>
    public Func<IEnumerable<MemoryRow>>? Gpu { get; set; }

    /// <summary>What is resident, or <see langword="null" />.</summary>
    public Func<IEnumerable<MemoryRow>>? Assets { get; set; }
}

/// <summary>A reading of every arena at one moment.</summary>
/// <remarks>
///     <para>
///         <b>A snapshot rather than a live view, and doc 13's four arenas rather than one number.</b>
///         "The process is using 3 GB" is not a fact anybody can act on; "the managed heap is 400 MB,
///         native allocations are 2.1 GB across 14 000 tracked resources, and the largest category is
///         VkImage" is.
///     </para>
///     <para>
///         ⚠ <b>The native arena is only populated in a build that tracks.</b>
///         <see cref="LeakTracker.IsSupported" /> is a compile-time constant that is false in
///         release, so the rows say so rather than reading zero — a memory panel claiming no native
///         allocations on a build that cannot see them is the most misleading thing it could do.
///     </para>
/// </remarks>
public sealed class MemorySnapshot {
    MemorySnapshot(IReadOnlyList<MemoryRow> rows, DateTimeOffset taken) {
        Rows = rows;
        Taken = taken;
    }

    /// <summary>The rows, grouped by arena in the order the enum declares.</summary>
    public IReadOnlyList<MemoryRow> Rows { get; }

    /// <summary>When it was taken.</summary>
    public DateTimeOffset Taken { get; }

    /// <summary>The rows of one arena.</summary>
    /// <param name="arena">Which one.</param>
    /// <returns>Its rows, in the order they were produced.</returns>
    public IEnumerable<MemoryRow> Of(MemoryArena arena) {
        foreach (var row in Rows) {
            if (row.Arena == arena) {
                yield return row;
            }
        }
    }

    /// <summary>How many bytes one arena's byte-valued rows add up to.</summary>
    /// <param name="arena">Which one.</param>
    /// <returns>The total, ignoring rows that are counts.</returns>
    public long BytesOf(MemoryArena arena) {
        var total = 0L;

        foreach (var row in Rows) {
            if (row.Arena == arena && !row.IsCount) {
                total += row.Bytes;
            }
        }

        return total;
    }

    /// <summary>Takes a reading.</summary>
    /// <param name="providers">Where the arenas this assembly cannot see come from.</param>
    /// <param name="time">The clock, for a test that wants a fixed timestamp.</param>
    /// <returns>The snapshot.</returns>
    public static MemorySnapshot Take(MemoryProviders? providers = null, TimeProvider? time = null) {
        List<MemoryRow> rows = [];

        Managed(rows);
        Native(rows);

        if (providers?.Gpu?.Invoke() is { } gpu) {
            rows.AddRange(gpu);
        }

        if (providers?.Assets?.Invoke() is { } assets) {
            rows.AddRange(assets);
        }

        return new(rows, (time ?? TimeProvider.System).GetUtcNow());
    }

    static void Managed(List<MemoryRow> rows) {
        // ⚠ `false` — a snapshot must not collect. Passing `true` runs a blocking gen-2 collection,
        // which changes the very number being read and stalls the editor for however long the heap
        // takes; a memory panel that compacted the heap every time it refreshed would be a
        // diagnostic that fixes its own symptom.
        var total = GC.GetTotalMemory(false);
        var info = GC.GetGCMemoryInfo();

        rows.Add(new(MemoryArena.Managed, "Heap", total, "live objects, without forcing a collection"));

        if (info.HeapSizeBytes > 0) {
            rows.Add(new(MemoryArena.Managed, "Committed", info.HeapSizeBytes, "as of the last collection"));
            rows.Add(new(MemoryArena.Managed, "Fragmented", info.FragmentedBytes, "free space inside used segments"));
        }

        rows.Add(new(MemoryArena.Managed, "Allocated since start", GC.GetTotalAllocatedBytes(false)));

        for (var generation = 0; generation <= GC.MaxGeneration; generation++) {
            rows.Add(
                new(MemoryArena.Managed, $"Gen {generation} collections", GC.CollectionCount(generation), IsCount: true)
            );
        }
    }

    static void Native(List<MemoryRow> rows) {
        // ⚠ Through a local, and it is not a style preference. `LeakTracker.IsSupported` is a
        // `const bool` whose value is `#if DEBUG`, so testing it directly makes one of the two
        // branches below unreachable at compile time — which is CS0162, and this tree treats
        // warnings as errors. The local is what keeps both branches compiled in a build where only
        // one of them can run.
        var tracked = LeakTracker.IsSupported;

        if (!tracked) {
            rows.Add(
                new(
                    MemoryArena.Native,
                    "Not tracked in this build",
                    0,
                    "LeakTracker compiles out without DEBUG or VIXEN_MEMORY_DEBUG",
                    IsCount: true
                )
            );

            return;
        }

        var live = LeakTracker.Snapshot();

        if (live.Length == 0) {
            rows.Add(new(MemoryArena.Native, "Nothing live", 0, IsCount: true));
            return;
        }

        Dictionary<string, int> byCategory = [];

        foreach (var report in live) {
            byCategory[report.Category] = byCategory.GetValueOrDefault(report.Category) + 1;
        }

        // Biggest category first, because the one holding ten thousand handles is the answer and the
        // one holding three is noise underneath it.
        foreach (var (category, count) in byCategory.OrderByDescending(entry => entry.Value)) {
            rows.Add(new(MemoryArena.Native, category, count, IsCount: true));
        }

        rows.Add(new(MemoryArena.Native, "Tracked resources", live.Length, IsCount: true));
    }
}
