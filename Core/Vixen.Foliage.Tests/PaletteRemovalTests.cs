// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>
///     Removing a palette entry — the item [docs/plan/31 § T5] registered as unavailable with a
///     reason, and the reason is exactly what these tests are about.
/// </summary>
public sealed class PaletteRemovalTests {
    static FoliageVolume Stocked(out int pine, out int oak, out int rock) {
        var volume = new FoliageVolume(new(32f));

        pine = volume.AddType(FoliageType.Of("Pine") with { Radius = 2f });
        oak = volume.AddType(FoliageType.Of("Oak") with { Radius = 4f });
        rock = volume.AddType(FoliageType.Of("Rock") with { Radius = 1f });

        for (var i = 0; i < 12; i++) {
            volume.Add(pine, At(i * 5f, 0f));
            volume.Add(oak, At(i * 5f, 40f));
            volume.Add(rock, At(i * 5f, 80f));
        }

        return volume;
    }

    static FoliageInstance At(float x, float z) => new(new(x, 0f, z), Quaternion.Identity, 1f);

    [Fact]
    public void RemovingAnEntryTakesItsInstancesWithIt() {
        var volume = Stocked(out _, out var oak, out _);

        var removed = volume.RemoveType(oak);

        Assert.Equal(12, removed);
        Assert.Equal(2, volume.Palette.Count);
        Assert.Equal(24, volume.InstanceCount);
    }

    /// <summary>And renumbers everything above it.</summary>
    /// <remarks>
    ///     ⚠ <b>A palette index is a position, not a name.</b> Removing the second of three shifts
    ///     the third down by one, and every chunk is filed under one — so a volume that removed the
    ///     entry and left the chunks alone would draw its rocks with the oaks' mesh.
    /// </remarks>
    [Fact]
    public void EverythingAboveTheRemovalIsRenumbered() {
        var volume = Stocked(out var pine, out var oak, out var rock);

        Assert.Equal(0, pine);
        Assert.Equal(1, oak);
        Assert.Equal(2, rock);

        volume.RemoveType(oak);

        Assert.Equal("Pine", volume.Palette[0].Name);
        Assert.Equal("Rock", volume.Palette[1].Name);

        Assert.Equal(12, volume.CountOf(0));
        Assert.Equal(12, volume.CountOf(1));

        // The rocks are where the rocks were, filed under their new index.
        foreach (var chunk in volume.Chunks.Where(chunk => chunk.Type == 1)) {
            Assert.All(chunk.Instances, instance => Assert.Equal(80f, instance.Position.Z));
        }
    }

    /// <summary>Removing the last entry renumbers nothing and still works.</summary>
    [Fact]
    public void RemovingTheLastEntryIsNotASpecialCase() {
        var volume = Stocked(out var pine, out var oak, out var rock);

        Assert.Equal(12, volume.RemoveType(rock));
        Assert.Equal(2, volume.Palette.Count);
        Assert.Equal(12, volume.CountOf(pine));
        Assert.Equal(12, volume.CountOf(oak));
    }

    [Fact]
    public void RemovingTheFirstEntryRenumbersBothOthers() {
        var volume = Stocked(out var pine, out _, out _);

        volume.RemoveType(pine);

        Assert.Equal(["Oak", "Rock"], volume.Palette.Select(type => type.Name));
        Assert.Equal(12, volume.CountOf(0));
        Assert.Equal(12, volume.CountOf(1));
    }

    /// <summary>No chunk is lost or doubled in the renumbering.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no safe in-place order.</b> Shifting a key from 3 to 2 while 2 exists
    ///     collides; ascending collides with the entry being moved next and descending with the one
    ///     just moved. Re-filing into a fresh dictionary is what removes the question.
    /// </remarks>
    [Fact]
    public void EveryChunkSurvivesExactlyOnce() {
        var volume = new FoliageVolume(new(16f));

        for (var type = 0; type < 5; type++) {
            volume.AddType(FoliageType.Of($"Type{type}") with { Radius = 1f });
        }

        // One chunk per type per cell across several cells, so the renumbering has plenty to collide
        // with if it is going to.
        for (var type = 0; type < 5; type++) {
            for (var cell = 0; cell < 6; cell++) {
                volume.Add(type, At(cell * 16f, type * 16f));
            }
        }

        Assert.Equal(30, volume.CellCount);

        volume.RemoveType(2);

        Assert.Equal(24, volume.CellCount);
        Assert.Equal(24, volume.InstanceCount);
        Assert.Equal(4, volume.Palette.Count);
        Assert.All(volume.Chunks, chunk => Assert.InRange(chunk.Type, 0, 3));

        var keys = volume.Chunks.Select(chunk => (chunk.Type, chunk.Cell)).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void TheBoundsSurviveTheMove() {
        var volume = Stocked(out _, out var oak, out _);

        var before = volume.Chunks.First(chunk => chunk.Type == 2).Bounds;

        volume.RemoveType(oak);

        var after = volume.Chunks.First(chunk => chunk.Type == 1).Bounds;

        Assert.Equal(before.Minimum, after.Minimum);
        Assert.Equal(before.Maximum, after.Maximum);
    }

    [Fact]
    public void AnIndexPastThePaletteIsRefused() {
        var volume = Stocked(out _, out _, out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => volume.RemoveType(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => volume.RemoveType(-1));
    }

    /// <summary>What it cannot renumber is what it cannot see.</summary>
    /// <remarks>
    ///     ⚠ <b>An address a caller is holding names a type by index and is not here.</b> This is the
    ///     reason the operation was registered as unavailable rather than left absent, and it is why
    ///     the editor's version of it clears the selection and never merges with anything.
    /// </remarks>
    [Fact]
    public void AHeldAddressDoesNotSurviveARemoval() {
        var volume = Stocked(out _, out var oak, out var rock);

        var held = new FoliageAddress(rock, volume.Grid.CellOf(new(0f, 0f, 80f)), 0);

        Assert.NotNull(volume.At(held));

        volume.RemoveType(oak);

        // The index it names is now the last one past the palette, so it resolves to nothing rather
        // than to the wrong tree — which is the direction this is allowed to be wrong in.
        Assert.Null(volume.At(held));
    }
}
