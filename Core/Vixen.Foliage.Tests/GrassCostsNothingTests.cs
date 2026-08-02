// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>
///     [docs/plan/31 § T6]'s fourth exit criterion: grass costs nothing in any file.
/// </summary>
/// <remarks>
///     <b>The criterion is structural before it is behavioural.</b> A <see cref="GrassType" /> is not
///     a <see cref="FoliageType" /> and never enters a <see cref="FoliageVolume" />, so the ordinary
///     way to fail this is not to save grass — it is to mark a <em>foliage</em> type derived, paint
///     with it, and have the store write it anyway. [§ D8] says the flag decides, and
///     <see cref="FoliageStore.Persisted" /> is the one place that reads it.
/// </remarks>
public sealed class GrassCostsNothingTests {
    static FoliageVolume Mixed(out int trees, out int carpet) {
        var volume = new FoliageVolume(new(32f));

        trees = volume.AddType(Types.Tree);
        carpet = volume.AddType(
            FoliageType.Of("Carpet") with { Storage = FoliageStorage.Derived, Radius = 0.25f }
        );

        for (var i = 0; i < 40; i++) {
            volume.Add(trees, new(new(i * 4f, 0f, 8f), Quaternion.Identity, 1f));
            volume.Add(carpet, new(new(i * 0.5f, 0f, 3f), Quaternion.Identity, 1f));
        }

        return volume;
    }

    [Fact]
    public void ADerivedTypeIsNotWritten() {
        var volume = Mixed(out var trees, out _);

        var bytes = new byte[FoliageStore.ByteCount(volume)];
        var written = FoliageStore.Write(volume, bytes);

        Assert.Equal(bytes.Length, written);

        var read = new FoliageVolume(new(32f));

        read.AddType(Types.Tree);
        read.AddType(FoliageType.Of("Carpet") with { Storage = FoliageStorage.Derived });

        var count = FoliageStore.Read(read, bytes.AsSpan(0, written));

        Assert.Equal(40, count);
        Assert.Equal(40, read.CountOf(trees));
        Assert.Equal(0, read.CountOf(1));
    }

    /// <summary>And the file is the size of the trees alone.</summary>
    [Fact]
    public void ByteCountIgnoresWhatItWillNotWrite() {
        var volume = Mixed(out var trees, out var carpet);

        var withGrass = FoliageStore.ByteCount(volume);

        volume.ClearType(carpet);

        Assert.Equal(FoliageStore.ByteCount(volume), withGrass);
        Assert.Equal(40, volume.CountOf(trees));
    }

    /// <summary>A volume of nothing but derived types is a header and no more.</summary>
    [Fact]
    public void AFieldOfGrassIsAnEmptyFile() {
        var volume = new FoliageVolume(new(32f));
        var carpet = volume.AddType(FoliageType.Of("Carpet") with { Storage = FoliageStorage.Derived });

        for (var i = 0; i < 5000; i++) {
            volume.Add(carpet, new(new(i * 0.1f, 0f, 0f), Quaternion.Identity, 1f));
        }

        var bytes = new byte[FoliageStore.ByteCount(volume)];

        FoliageStore.Write(volume, bytes);

        Assert.Equal(5000, volume.CountOf(carpet));
        Assert.Empty(FoliageStore.Persisted(volume));
        Assert.True(
            bytes.Length < 32,
            $"five thousand derived instances wrote {bytes.Length} bytes; grass is meant to cost "
            + "nothing in any file."
        );
    }
}
