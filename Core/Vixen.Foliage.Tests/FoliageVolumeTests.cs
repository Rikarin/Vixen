// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>The cell grid, the volume and what is stored beside the scene — [docs/plan/31 § D9].</summary>
public sealed class FoliageVolumeTests {
    static FoliageInstance At(float x, float z, float scale = 1f) =>
        new(new(x, 0f, z), Quaternion.Identity, scale);

    static (FoliageVolume Volume, int Tree, int Rock) Built() {
        var volume = new FoliageVolume(new(32f));

        return (volume, volume.AddType(Types.Tree), volume.AddType(Types.Rock));
    }

    // --- The grid -----------------------------------------------------------

    /// <summary>A negative coordinate is not folded into a positive one.</summary>
    /// <remarks>
    ///     ⚠ <b>Truncation folds −0.5 and +0.5 into the same cell</b>, so the four cells around the
    ///     origin become two — a seam through the middle of every level built around zero.
    /// </remarks>
    [Fact]
    public void The_four_cells_around_the_origin_are_four_cells() {
        var grid = new FoliageCellGrid(32f);

        Assert.Equal(new(0, 0), grid.CellOf(new(1f, 0f, 1f)));
        Assert.Equal(new(-1, 0), grid.CellOf(new(-1f, 0f, 1f)));
        Assert.Equal(new(0, -1), grid.CellOf(new(1f, 0f, -1f)));
        Assert.Equal(new(-1, -1), grid.CellOf(new(-1f, 0f, -1f)));
    }

    [Fact]
    public void A_cells_origin_is_its_low_corner() {
        var grid = new FoliageCellGrid(32f);

        Assert.Equal(new(64f, 0f, -32f), grid.OriginOf(new(2, -1)));
        Assert.Equal(new(2, -1), grid.CellOf(grid.OriginOf(new(2, -1))));
    }

    [Fact]
    public void A_circle_touches_every_cell_its_square_reaches() {
        var grid = new FoliageCellGrid(32f);
        var cells = grid.Touching(new(16f, 16f), 4f).ToArray();

        Assert.Equal([new FoliageCellKey(0, 0)], cells);

        // Straddling a boundary reaches two.
        Assert.Equal(2, grid.Touching(new(31f, 16f), 4f).Count());
    }

    [Fact]
    public void A_cell_smaller_than_a_metre_is_lifted_off_zero() {
        Assert.Equal(FoliageCellGrid.MinimumSize, new FoliageCellGrid(0f).CellSize, 4);
    }

    // --- The volume ---------------------------------------------------------

    [Fact]
    public void An_instance_goes_into_the_cell_its_position_falls_in() {
        var (volume, tree, _) = Built();

        var address = volume.Add(tree, At(70f, 40f));

        Assert.Equal(new(2, 1), address.Cell);
        Assert.Equal(1, volume.InstanceCount);
        Assert.Equal(1, volume.CellCount);
        Assert.Equal(At(70f, 40f), volume.At(address));
    }

    [Fact]
    public void Two_types_in_one_cell_are_two_chunks() {
        var (volume, tree, rock) = Built();

        volume.Add(tree, At(10f, 10f));
        volume.Add(rock, At(12f, 12f));

        Assert.Equal(2, volume.CellCount);
        Assert.Equal(1, volume.CountOf(tree));
        Assert.Equal(1, volume.CountOf(rock));
    }

    /// <summary>A chunk's bounds reach past the trunks by the instance's own scale.</summary>
    /// <remarks>
    ///     ⚠ A box built from positions alone ends at the trunks, so every tree at the edge of a cell
    ///     pops out of existence while half of it is still on screen.
    /// </remarks>
    [Fact]
    public void A_chunks_bounds_include_what_the_instance_occupies_not_only_where_it_stands() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(10f, 10f, scale: 2f));

        var chunk = volume.ChunkOf(tree, new(0, 0))!;
        var reach = MathF.Max(Types.Tree.Radius, 0.5f) * 2f;

        Assert.Equal(10f - reach, chunk.Bounds.Minimum.X, 3);
        Assert.Equal(10f + reach, chunk.Bounds.Maximum.X, 3);
    }

    /// <summary>Removing shrinks the bounds, because a box cannot be un-grown.</summary>
    [Fact]
    public void Removing_an_instance_shrinks_the_cells_bounds() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(4f, 4f));
        var far = volume.Add(tree, At(28f, 28f));

        var chunk = volume.ChunkOf(tree, new(0, 0))!;
        Assert.True(chunk.Bounds.Maximum.X > 27f);

        volume.Remove([far]);

        Assert.True(volume.ChunkOf(tree, new(0, 0))!.Bounds.Maximum.X < 10f);
    }

    /// <summary>Removing several at once removes the ones asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>Removing index 3 shifts 4 and 5 down</b>, so a caller handing over three ascending
    ///     addresses would delete one of them and two of somebody else's. The volume sorts descending
    ///     within each chunk so the caller cannot get it wrong.
    /// </remarks>
    [Fact]
    public void Removing_several_addresses_removes_exactly_those() {
        var (volume, tree, _) = Built();

        var addresses = new List<FoliageAddress>();

        for (var index = 0; index < 6; index++) {
            addresses.Add(volume.Add(tree, At(index * 2f, 4f)));
        }

        // Ascending, which is the order a naive caller collects them in.
        Assert.Equal(3, volume.Remove([addresses[1], addresses[3], addresses[5]]));

        var left = volume.ChunkOf(tree, new(0, 0))!.Instances.Select(instance => instance.Position.X).ToArray();

        Assert.Equal([0f, 4f, 8f], left);
    }

    [Fact]
    public void A_cell_that_empties_is_dropped() {
        var (volume, tree, _) = Built();

        var address = volume.Add(tree, At(10f, 10f));

        Assert.Equal(1, volume.CellCount);

        volume.Remove([address]);

        Assert.Equal(0, volume.CellCount);
        Assert.Null(volume.ChunkOf(tree, new(0, 0)));
    }

    /// <summary>Moving across a cell boundary re-cells the instance.</summary>
    /// <remarks>
    ///     ⚠ A gizmo drag that left the instance in its old cell would put a tree outside the bounds
    ///     everything culls that cell by — it would vanish when the cell went off screen and reappear
    ///     from nowhere.
    /// </remarks>
    [Fact]
    public void Moving_an_instance_out_of_its_cell_puts_it_in_the_new_one() {
        var (volume, tree, _) = Built();

        var address = volume.Add(tree, At(10f, 10f));
        var moved = volume.Move(address, At(70f, 10f));

        Assert.Equal(new(2, 0), moved.Cell);
        Assert.Null(volume.ChunkOf(tree, new(0, 0)));
        Assert.Equal(1, volume.InstanceCount);
        Assert.Equal(70f, volume.At(moved)!.Value.Position.X, 3);
    }

    [Fact]
    public void Moving_within_a_cell_keeps_the_address() {
        var (volume, tree, _) = Built();

        var address = volume.Add(tree, At(10f, 10f));
        var moved = volume.Move(address, At(12f, 14f));

        Assert.Equal(address, moved);
        Assert.Equal(12f, volume.At(moved)!.Value.Position.X, 3);
    }

    [Fact]
    public void Everything_within_a_radius_is_found_and_nothing_else_is() {
        var (volume, tree, rock) = Built();

        volume.Add(tree, At(10f, 10f));
        volume.Add(tree, At(12f, 10f));
        volume.Add(tree, At(40f, 10f));
        volume.Add(rock, At(11f, 10f));

        Assert.Equal(3, volume.Within(new(11f, 10f), 5f).Count());
        Assert.Equal(2, volume.Within(new(11f, 10f), 5f, new HashSet<int> { tree }).Count());
        Assert.Empty(volume.Within(new(200f, 200f), 5f));
    }

    [Fact]
    public void Changing_a_palette_entry_keeps_every_instance_of_it() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(10f, 10f));
        volume.SetType(tree, Types.Tree with { Name = "Oak", MaxScale = 3f });

        Assert.Equal("Oak", volume.Palette[tree].Name);
        Assert.Equal(1, volume.CountOf(tree));
    }

    [Fact]
    public void Clearing_one_type_leaves_the_others() {
        var (volume, tree, rock) = Built();

        volume.Add(tree, At(10f, 10f));
        volume.Add(rock, At(12f, 12f));

        Assert.Equal(1, volume.ClearType(tree));
        Assert.Equal(0, volume.CountOf(tree));
        Assert.Equal(1, volume.CountOf(rock));
    }

    // --- Beside the scene ---------------------------------------------------

    /// <summary>Instances written and read back are the instances that were written.</summary>
    [Fact]
    public void A_volume_round_trips_through_bytes() {
        var (volume, tree, rock) = Built();
        var random = new Random(0x5EED);

        for (var index = 0; index < 500; index++) {
            volume.Add(
                index % 3 == 0 ? rock : tree,
                new(
                    new((float)random.NextDouble() * 400f, (float)random.NextDouble() * 10f, (float)random.NextDouble() * 400f),
                    Quaternion.Normalize(new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), 1f)),
                    0.5f + (float)random.NextDouble()
                )
            );
        }

        var before = Snapshot(volume);
        var bytes = new byte[FoliageStore.ByteCount(volume)];

        Assert.Equal(bytes.Length, FoliageStore.Write(volume, bytes));

        var reloaded = new FoliageVolume(new(32f));
        reloaded.AddType(Types.Tree);
        reloaded.AddType(Types.Rock);

        Assert.Equal(500, FoliageStore.Read(reloaded, bytes));
        Assert.Equal(before, Snapshot(reloaded));
    }

    /// <summary>A file celled one way is re-celled when the grid has changed.</summary>
    /// <remarks>
    ///     ⚠ <b>Positions are the truth; cells are an index over them.</b> A forest whose cells no
    ///     longer match its grid culls wrongly and cannot be repaired without a rewrite.
    /// </remarks>
    [Fact]
    public void Reading_into_a_volume_with_different_cells_re_cells_the_instances() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(70f, 40f));

        var bytes = new byte[FoliageStore.ByteCount(volume)];
        FoliageStore.Write(volume, bytes);

        var coarse = new FoliageVolume(new(128f));
        coarse.AddType(Types.Tree);

        Assert.Equal(1, FoliageStore.Read(coarse, bytes));

        var chunk = Assert.Single(coarse.Chunks);
        Assert.Equal(new(0, 0), chunk.Cell);
        Assert.Equal(70f, chunk.Instances[0].Position.X, 3);
    }

    /// <summary>An instance of a type the palette does not have is dropped, not clamped.</summary>
    /// <remarks>
    ///     ⚠ Clamping puts somebody's oaks into whatever the last palette entry happens to be,
    ///     silently. Dropping loses them visibly, which is what a person can act on.
    /// </remarks>
    [Fact]
    public void An_instance_of_a_type_the_palette_lost_is_dropped() {
        var (volume, tree, rock) = Built();

        volume.Add(tree, At(10f, 10f));
        volume.Add(rock, At(12f, 12f));

        var bytes = new byte[FoliageStore.ByteCount(volume)];
        FoliageStore.Write(volume, bytes);

        var thinner = new FoliageVolume(new(32f));
        thinner.AddType(Types.Tree);

        Assert.Equal(1, FoliageStore.Read(thinner, bytes));
        Assert.Equal(1, thinner.CountOf(0));
    }

    [Fact]
    public void Bytes_that_are_not_a_foliage_file_are_refused_rather_than_interpreted() {
        var volume = new FoliageVolume();

        var refusal = Assert.Throws<ArgumentException>(() => FoliageStore.Read(volume, new byte[64]));

        Assert.Contains("magic number", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_ends_early_is_refused() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(10f, 10f));

        var bytes = new byte[FoliageStore.ByteCount(volume)];
        FoliageStore.Write(volume, bytes);

        Assert.Throws<ArgumentException>(() => FoliageStore.Read(volume, bytes.AsSpan(0, bytes.Length - 8)));
    }

    [Fact]
    public void Too_little_room_to_write_is_refused() {
        var (volume, tree, _) = Built();

        volume.Add(tree, At(10f, 10f));

        Assert.Throws<ArgumentException>(() => FoliageStore.Write(volume, new byte[8]));
    }

    static (int Type, Vector3 Position, float Scale)[] Snapshot(FoliageVolume volume) =>
        [
            .. volume.Chunks
                .SelectMany(chunk => chunk.Instances.Select(instance => (chunk.Type, instance.Position, instance.Scale)))
                .OrderBy(entry => entry.Type)
                .ThenBy(entry => entry.Position.X)
                .ThenBy(entry => entry.Position.Z)
        ];
}
