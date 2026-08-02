// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The cell streamer — the foliage half of [docs/plan/31 § D13].</summary>
public sealed class FoliageStreamingTests {
    static FoliageVolume Forest(int cells, int perCell = 4) {
        var volume = new FoliageVolume(new(Size: 32f));
        var type = volume.AddType(FoliageType.Of("Pine") with { Radius = 2f });

        for (var z = 0; z < cells; z++) {
            for (var x = 0; x < cells; x++) {
                for (var index = 0; index < perCell; index++) {
                    volume.Add(type, new(new((x * 32f) + 4f + index, 0f, (z * 32f) + 4f), Quaternion.Identity, 1f));
                }
            }
        }

        return volume;
    }

    /// <summary>A cell nobody is near is not uploaded, and one under a source is.</summary>
    [Fact]
    public void ASourceMakesTheCellsAroundItResidentAndNotTheRest() {
        var volume = Forest(cells: 8);
        using var streamer = new FoliageStreamer(volume);

        for (var frame = 0; frame < 8; frame++) {
            Span<StreamingSource> sources = [new(new(16f, 0f, 16f), 20f)];

            streamer.Update(sources);
            Thread.Sleep(2);
        }

        Assert.True(streamer.IsResident(new(0, 0)), "the cell the source is standing in was never uploaded.");
        Assert.False(streamer.IsResident(new(7, 7)), "a cell two hundred metres away was uploaded anyway.");
    }

    /// <summary>A cell outside the window is uploaded rather than skipped.</summary>
    /// <remarks>
    ///     ⚠ <b>The safe direction, and the reason it is worth a test of its own.</b> A stroke beyond
    ///     the window is the frame after which <see cref="FoliageStreamer.Rebuild" /> is due; refusing
    ///     the cell in the meantime is a tree an artist has just placed and cannot see, which reads as
    ///     the brush not working.
    /// </remarks>
    [Fact]
    public void ACellBeyondTheWindowIsUploaded() {
        var volume = Forest(cells: 2);
        using var streamer = new FoliageStreamer(volume);

        Assert.True(streamer.IsResident(new(400, 400)));
    }

    /// <summary>The resident set changing is visible, and accepting it clears the flag.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this a host has no way to tell an ordinary frame from one that must
    ///     re-upload</b>, so it re-uploads every frame — which is the cost the streamer was added to
    ///     remove, arriving through the other door.
    /// </remarks>
    [Fact]
    public void TheResidentSetSaysWhenItHasMoved() {
        var volume = Forest(cells: 4);
        using var streamer = new FoliageStreamer(volume);

        Assert.False(streamer.Changed);

        for (var frame = 0; frame < 8 && !streamer.Changed; frame++) {
            Span<StreamingSource> sources = [new(new(16f, 0f, 16f), 20f)];

            streamer.Update(sources);
            Thread.Sleep(2);
        }

        Assert.True(streamer.Changed);

        streamer.Accept();

        Assert.False(streamer.Changed);
    }

    /// <summary>An empty volume is a streamer, not a refusal.</summary>
    [Fact]
    public void AVolumeWithNothingInItStreams() {
        var volume = new FoliageVolume(new(Size: 32f));
        using var streamer = new FoliageStreamer(volume);

        Assert.Equal(1, streamer.Grid.CellCount);
        Assert.True(streamer.IsResident(new(9, 9)));
    }

    /// <summary>A rebuilt window follows a volume that grew past it.</summary>
    [Fact]
    public void RebuildingFollowsAVolumeThatGrew() {
        var volume = Forest(cells: 2);
        using var streamer = new FoliageStreamer(volume);

        Assert.Equal(4, streamer.Grid.CellCount);

        volume.Add(0, new(new(320f, 0f, 320f), Quaternion.Identity, 1f));
        streamer.Rebuild(volume);

        Assert.Equal(11 * 11, streamer.Grid.CellCount);
    }
}
