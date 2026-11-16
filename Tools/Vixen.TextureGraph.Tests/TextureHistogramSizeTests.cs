// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What a histogram's counts and table cost, from the pool's own schedule and with no device.</summary>
/// <remarks>
///     ⚠ <b>Vixen #1646 read the ranking's cost off a 128² plan and scaled it by the square of the side</b>, which
///     made two <c>Rgba32Float</c> images at twice the graph's side, 8 GiB at 8K. The counts and the table are sized
///     from the extent the plan is emitted for (<see cref="TextureOp.EmittedForExtent" />), between 256 and 511 rows,
///     so they are twice the side of a 128² bake and a thirty-second of an 8K one: a plan's cost does not scale from
///     a small bake for any node that sizes its middles from the extent.
/// </remarks>
public class TextureHistogramSizeTests {
    /// <summary>
    ///     A Histogram Equalise keeps its counts and table between 256 and 511 rows at any bake, so at 8K they are 2 MiB
    ///     together and not the 8 GiB that twice the side would be.
    /// </summary>
    /// <param name="side">The graph's base side.</param>
    /// <param name="bake">The bake's level offset, so the bake's side is <c>side &gt;&gt; bake</c>.</param>
    [Theory]
    [InlineData(128, 0)]
    [InlineData(128, -3)]
    [InlineData(128, -6)]
    [InlineData(1024, -3)]
    [InlineData(8192, 0)]
    public void The_counts_and_table_stay_a_few_hundred_rows_tall_at_any_bake(int side, int bake) {
        NodeTypeRegistry registry = new();
        NodeTypes.Register(registry);
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var equalise = graph.Add("Colour/Histogram Equalise");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(equalise.Id, "Input"));
        graph.Connect(new(equalise.Id, "Out"), new(output.Id, "Input"));

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = side, BaseHeight = side, BakeLevelOffset = bake, Seed = 1
        }.Compile(graph);

        Assert.True(compilation.Succeeded);

        var schedule = TexturePoolSchedule.For(compilation.Value);
        var baked = bake <= 0 ? side << -bake : side >> bake;
        var tables = schedule.Slots.Where(static slot => slot.Format == TextureFormat.Rgba32Float).ToArray();

        Assert.Equal(2, tables.Length);
        Assert.All(tables, static slot => Assert.InRange(slot.Height, 256, 511));
        Assert.Contains(schedule.Slots, slot => slot.Width == baked && slot.Height == baked);
        Assert.InRange(tables.Sum(static slot => (long)slot.Width * slot.Height * 16), 1, 2L * 512 * 512 * 16);
    }
}
