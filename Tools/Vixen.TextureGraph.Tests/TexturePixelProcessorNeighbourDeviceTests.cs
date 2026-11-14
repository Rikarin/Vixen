// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>A Pixel Processor reads its inputs' neighbours — Patina #42.</summary>
/// <remarks>
///     The oracle is a shift: <c>ReadA(coord + int2(1, 0))</c> is the picture one column to the left, and at the right
///     edge it reads column nought when the graph tiles and the edge itself when it does not.
/// </remarks>
public class TexturePixelProcessorNeighbourDeviceTests(ITestOutputHelper output) {
    const int Wide = 40;

    static float Column(int x) => Unit((x + 0.5f) / Wide);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_neighbour_is_read_round_the_edge_when_the_graph_tiles_and_held_there_when_it_does_not(bool tiling) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var columns = Picture(Wide, 8, static (x, _) => ((x + 0.5f) / Wide, 0.5f, 0.5f, 1f));
        var shifted = Bake(
            device,
            "Filters/Pixel Processor",
            Wide,
            8,
            [new("A", columns)],
            static node => node.SetText("Expression", "ReadA(coord + int2(1, 0))"),
            tiling: tiling
        );

        for (var x = 0; x < Wide - 1; x++) {
            Assert.Equal(Column(x + 1), shifted.At(x, 3, 0), 5);
        }

        Assert.Equal(tiling ? Column(0) : Column(Wide - 1), shifted.At(Wide - 1, 3, 0), 5);
    }
}
