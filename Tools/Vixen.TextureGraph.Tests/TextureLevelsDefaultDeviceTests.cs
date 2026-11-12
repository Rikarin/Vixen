// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A <c>Colour/Levels</c> left at its defaults is an identity, down to the slope of a height.</summary>
/// <remarks>
///     ⚠
///     <b>
///         It dithered by one 8-bit step by default, on the argument that a bake is a file and a
///         banded file is permanent. That argument belongs to an 8-bit encode, and a graph computes in 16
///         or 32-bit float.
///     </b>
///     Noise of 1/255 between neighbouring texels is a slope, and a slope is
///     per texel, so <c>Surface/Height to Normal</c> reads it as a surface that is not flat.
///     27 of the shipped compounds hold a Levels, and <c>Surface/Bevel</c> feeds one straight into a
///     normal.
/// </remarks>
public class TextureLevelsDefaultDeviceTests(ITestOutputHelper output) {
    const int Side = 1024;

    /// <summary>Flat grey through Levels into a normal, and the steepest tilt anywhere in it.</summary>
    /// <remarks>
    ///     ⚠ <b>The dithered half is the instrument.</b> A flat grey that never reached the normal, or
    ///     a normal that ignored its height, would be flat at any dither; this asserts the same chain
    ///     tilts when dither is asked for, so the flat answer is the node's and not the fixture's.
    /// </remarks>
    [Fact]
    public void A_default_levels_leaves_a_flat_height_flat() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var flat = Tilt(evaluator, null, "levels-default");
        var dithered = Tilt(evaluator, 1f, "levels-dither-1");

        output.WriteLine($"{adapter}: worst tilt {flat:F3}° at the default, {dithered:F3}° at a dither of one");

        Assert.True(dithered > 5f, $"{adapter}: a dither of one tilted the surface only {dithered:F3}°.");
        Assert.True(flat < 0.01f, $"{adapter}: a default Levels tilted a flat surface {flat:F3}°.");
    }

    /// <summary>The steepest a normal leans away from straight up, in degrees.</summary>
    static float Tilt(TexturePlanEvaluator evaluator, float? dither, string picture) {
        NodeGraphModel graph = new();
        var fill = graph.Add("Filters/Pixel Processor");
        var grey = graph.Add("Colour/Grayscale");
        var levels = graph.Add("Colour/Levels");
        var normal = graph.Add("Surface/Height to Normal");
        var sink = graph.Add("Output/Output");

        fill.SetText("Expression", "float4(0.5f, 0f, 0f, 1f)");
        grey.SetValue("Weight R", 1f);
        grey.SetValue("Weight G", 0f);
        grey.SetValue("Weight B", 0f);

        if (dither is { } amount) {
            levels.SetValue("Dither", amount);
        }

        sink.SetText("Usage", "normal");
        graph.Connect(new(fill.Id, "Out"), new(grey.Id, "Input"));
        graph.Connect(new(grey.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(normal.Id, "Height"));
        graph.Connect(new(normal.Id, "Out"), new(sink.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphCompiler compiler = new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7717 };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        using var bake = evaluator.Evaluate(compilation.Value);

        var image = compilation.Value.Outputs[0];
        var raw = bake.ReadRaw(image);
        var worst = 0f;

        for (var at = 0; at < Side * Side; at++) {
            var x = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at * 8, 2)) * 2f - 1f;
            var y = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at * 8 + 2, 2)) * 2f - 1f;
            var z = (float)BitConverter.ToHalf(raw.Texels.AsSpan(at * 8 + 4, 2)) * 2f - 1f;

            worst = MathF.Max(worst, MathF.Atan2(MathF.Sqrt(x * x + y * y), z) * 180f / MathF.PI);
        }

        if (Environment.GetEnvironmentVariable("VIXEN_TILING_PICTURES") is { } folder) {
            PngCodec.Save(Path.Combine(folder, $"{picture}.png"), bake.Read(image));
        }

        return worst;
    }
}
