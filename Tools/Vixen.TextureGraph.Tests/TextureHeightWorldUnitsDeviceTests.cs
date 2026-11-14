// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <c>Surface/Height to Normal</c> measured in the world: a height of one rises <c>Depth</c> centimetres over the
///     graph's physical size, so the same ramp leans as far as a real surface of that size would.
/// </summary>
/// <remarks>
///     Measured as a ratio against the same ramp in the picture's units, so the test asks only what World changes
///     and not what a gradient's slope is: <c>Depth / width</c> across, <c>Depth / height</c> down.
/// </remarks>
public class TextureHeightWorldUnitsDeviceTests(ITestOutputHelper output) {
    const int Width = 256;
    const int Height = 128;
    const float Depth = 50f;
    static readonly Vector2 Tile = new(200f, 100f);

    [Fact]
    public void A_height_in_world_units_leans_as_its_depth_over_the_tile_across_and_down() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        using var evaluator = new TexturePlanEvaluator(device);

        var across = Slope(evaluator, false, "World", Tile).X / Slope(evaluator, false, "Picture", Tile).X;
        var down = Slope(evaluator, true, "World", Tile).Y / Slope(evaluator, true, "Picture", Tile).Y;

        output.WriteLine($"across {across:F4} (want {Depth / Tile.X:F4}), down {down:F4} (want {Depth / Tile.Y:F4})");

        Assert.Equal(Depth / Tile.X, across, 0.01f);
        Assert.Equal(Depth / Tile.Y, down, 0.01f);
    }

    [Fact]
    public void World_units_in_a_graph_with_no_physical_size_measure_in_the_picture_and_say_so() {
        var (graph, normal) = Ramp(false, "World", default);
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = Width, BaseHeight = Height };

        var compilation = compiler.Compile(graph);

        var warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal(("TG0027", normal, NodeSeverity.Warning), (warning.Id, warning.Node, warning.Severity));
    }

    [Fact]
    public void A_new_physical_size_compiles_again_rather_than_refreshing_the_old_plan() {
        var (graph, _) = Ramp(false, "World", Tile);
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = Width, BaseHeight = Height };
        var first = compiler.Compile(graph).Value;

        graph.Info = graph.Info with { PhysicalSize = new(400f, 200f) };

        Assert.Null(compiler.Refresh(graph));
        var again = compiler.Compile(graph).Value;
        Assert.NotEqual(Intensity(first), Intensity(again));
    }

    static float Intensity(TexturePlan plan) =>
        plan.Ops.Single(static op => op.Kernel == "HeightToNormal")
            .Parameters.Single(static parameter => parameter.Name == "intensity")
            .Value;

    /// <summary>The slope a normal stands for in the middle of the picture: <c>−n.xy / n.z</c>.</summary>
    static Vector2 Slope(TexturePlanEvaluator evaluator, bool down, string units, Vector2 tile) {
        var (graph, _) = Ramp(down, units, tile);
        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = Width, BaseHeight = Height, Seed = 7 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        using var bake = evaluator.Evaluate(compilation.Value);
        var raw = bake.ReadRaw(compilation.Value.Outputs[0]);
        var at = (Height / 2 * Width + Width / 2) * 8;
        var x = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at, 2)) * 2f - 1f;
        var y = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at + 2, 2)) * 2f - 1f;
        var z = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at + 4, 2)) * 2f - 1f;

        return new(-x / z, -y / z);
    }

    /// <summary>A ramp across or down, made grey, into a Height to Normal measuring in the units asked for.</summary>
    static (NodeGraphModel Graph, NodeId Normal) Ramp(bool down, string units, Vector2 tile) {
        NodeGraphModel graph = new() { Info = GraphInfo.None with { PhysicalSize = tile } };
        var ramp = graph.Add("Filters/Pixel Processor");
        var grey = graph.Add("Colour/Grayscale");
        var normal = graph.Add("Surface/Height to Normal");
        var sink = graph.Add("Output/Output");

        ramp.SetText("Expression", down ? "float4(uv.y, 0f, 0f, 1f)" : "float4(uv.x, 0f, 0f, 1f)");
        grey.SetValue("Weight R", 1f);
        grey.SetValue("Weight G", 0f);
        grey.SetValue("Weight B", 0f);
        normal.SetText("Units", units);
        normal.SetValue("Depth", Depth);
        sink.SetText("Usage", "normal");
        graph.Connect(new(ramp.Id, "Out"), new(grey.Id, "Input"));
        graph.Connect(new(grey.Id, "Out"), new(normal.Id, "Height"));
        graph.Connect(new(normal.Id, "Out"), new(sink.Id, "Input"));

        return (graph, normal.Id);
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }
}
