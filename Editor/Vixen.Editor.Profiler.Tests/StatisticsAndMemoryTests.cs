// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>The two panels that read the process rather than a capture.</summary>
public sealed class StatisticsAndMemoryTests {
    /// <summary>A three-field component, so a chunk's column has a size worth counting.</summary>
    /// <remarks>
    ///     A record struct rather than a struct with public fields: the fields are never read here —
    ///     what is being counted is storage, not values — and unread public fields are CS0649, which
    ///     this tree treats as an error.
    /// </remarks>
    readonly record struct Position(float X, float Y, float Z);

    /// <summary>A second component, so the world has two archetypes rather than one.</summary>
    readonly record struct Velocity(float X);

    [Fact]
    public void CountingWalksTheWorldAndFindsWhatIsInIt() {
        using World world = new("Test");

        for (var index = 0; index < 20; index++) {
            world.Create(new Position());
        }

        for (var index = 0; index < 5; index++) {
            world.Create(new Position(), new Velocity());
        }

        var statistics = SceneStatistics.Collect(world);

        Assert.Equal(25, Row(statistics, "Entities").Value);
        Assert.Equal(2, Row(statistics, "Archetypes").Value);

        // 20 entities × 1 component + 5 × 2.
        Assert.Equal(30, Row(statistics, "Component instances").Value);
        Assert.True(Row(statistics, "Chunk memory").Value > 0);
    }

    [Fact]
    public void ABudgetCrossedIsAWarningAndOneApproachedIsToo() {
        using World world = new("Test");

        for (var index = 0; index < 10; index++) {
            world.Create(new Position());
        }

        var over = SceneStatistics.Collect(world, new() { Entities = 5 });
        Assert.Equal(BudgetState.Over, Row(over, "Entities").State);
        Assert.Contains(over.Warnings, warning => warning.Contains("over budget", StringComparison.Ordinal));

        var near = SceneStatistics.Collect(world, new() { Entities = 10 });
        Assert.Equal(BudgetState.Near, Row(near, "Entities").State);
        Assert.True(near.HasWarnings);

        var fine = SceneStatistics.Collect(world, new() { Entities = 1000 });
        Assert.Equal(BudgetState.Fine, Row(fine, "Entities").State);
    }

    [Fact]
    public void ARowWithNoBudgetIsNeverAWarningAndHasNoFill() {
        StatisticRow row = new("Chunks", 9_000_000);

        Assert.Equal(BudgetState.Fine, row.State);
        Assert.Equal(0f, row.Fill);
    }

    [Fact]
    public void FillIsClampedRatherThanRunningPastTheTrack() {
        Assert.Equal(1f, new StatisticRow("Entities", 500, 100).Fill);
        Assert.Equal(0.5f, new StatisticRow("Entities", 50, 100).Fill, 3);
    }

    /// <summary>
    ///     ⚠ Depth is the caller's, because the parent relation is not the ECS's — and a row that is
    ///     absent is different from a row reading zero, which would say the scene is flat.
    /// </summary>
    [Fact]
    public void HierarchyDepthIsOnlyShownWhenSomebodySaysWhatItIs() {
        using World world = new("Test");
        world.Create(new Position());

        Assert.DoesNotContain(SceneStatistics.Collect(world).Rows, row => row.Label == "Hierarchy depth");
        Assert.Equal(4, Row(SceneStatistics.Collect(world, depth: 4), "Hierarchy depth").Value);
    }

    [Fact]
    public void AnEmptyWorldCountsToZeroWithoutWarning() {
        using World world = new("Test");
        var statistics = SceneStatistics.Collect(world);

        Assert.Equal(0, Row(statistics, "Entities").Value);
        Assert.False(statistics.HasWarnings);
    }

    [Fact]
    public void AMemorySnapshotHasTheManagedArenaInIt() {
        var snapshot = MemorySnapshot.Take();

        Assert.Contains(snapshot.Of(MemoryArena.Managed), row => row.Label == "Heap" && row.Bytes > 0);
        Assert.True(snapshot.BytesOf(MemoryArena.Managed) > 0);
    }

    /// <summary>
    ///     ⚠ Counts are excluded from the arena's byte total. "1.2 KB of collections" is the mistake
    ///     the flag exists to prevent.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The collection is what makes the count non-zero, and without it this asserted a
    ///     property of the machine.</b> The count rows are <see cref="GC.CollectionCount" /> per
    ///     generation, which in a process that has not collected yet is zero three times over — so
    ///     "counts exist and are excluded" degenerated into "zero is excluded", which every
    ///     implementation satisfies. A test host with enough memory and few enough tests reaches this
    ///     before the first gen-0 collection; the Linux CI leg did, and nothing else did.
    /// </remarks>
    [Fact]
    public void CountRowsAreNotAddedIntoTheByteTotal() {
        GC.Collect();

        var snapshot = MemorySnapshot.Take();
        var counted = 0L;

        foreach (var row in snapshot.Of(MemoryArena.Managed)) {
            if (row.IsCount) {
                counted += row.Bytes;
            }
        }

        Assert.True(counted > 0);
        Assert.True(snapshot.BytesOf(MemoryArena.Managed) > counted);
    }

    /// <summary>
    ///     ⚠ A build that cannot see native allocations says so rather than reading zero, which is
    ///     the most misleading thing a memory panel could do.
    /// </summary>
    [Fact]
    public void TheNativeArenaSaysWhetherItIsTrackedAtAll() {
        var rows = MemorySnapshot.Take().Of(MemoryArena.Native).ToArray();

        Assert.NotEmpty(rows);

        // Through a local, because `IsSupported` is a `const bool` whose value is `#if DEBUG` — the
        // same reason `MemorySnapshot.Native` reads it into one.
        var tracked = LeakTracker.IsSupported;

        if (!tracked) {
            Assert.Contains(rows, row => row.Label.Contains("Not tracked", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ProvidersFillTheArenasThisAssemblyCannotSee() {
        MemoryProviders providers = new() {
            Gpu = () => [new(MemoryArena.Gpu, "Device-local", 512 * 1024 * 1024)],
            Assets = () => [new(MemoryArena.Assets, "Textures", 12, IsCount: true)]
        };

        var snapshot = MemorySnapshot.Take(providers);

        Assert.Equal(512L * 1024 * 1024, snapshot.BytesOf(MemoryArena.Gpu));
        Assert.Single(snapshot.Of(MemoryArena.Assets));
    }

    [Fact]
    public void ByteCountsAreFormattedInBinaryUnits() {
        Assert.Equal("0 B", MemoryView.Bytes(0));
        Assert.Equal("512 B", MemoryView.Bytes(512));
        Assert.Equal("1 KiB", MemoryView.Bytes(1024));
        Assert.Equal("1.5 MiB", MemoryView.Bytes(1024 * 1536));
        Assert.Equal("-1 KiB", MemoryView.Bytes(-1024));
    }

    static StatisticRow Row(SceneStatistics statistics, string label) =>
        Assert.Single(statistics.Rows, row => row.Label == label);
}
