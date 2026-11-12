// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A sixteen-bit mesh map reaches a graph at more than eight bits (#1534).</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The node used to declare its picture <see cref="TextureFormat.Rgba8" />, "because a baked map
///             is a PNG"
///         </b>
///         , and a bake writes position, curvature, thickness and occlusion as sixteen-bit PNG.
///         A host that uploads at the depth the plan declares — which is what <c>TextureUploads.Add</c> asks
///         of it — narrowed a position map to 256 places across the model, and a volume noise finer than
///         that drew stairs.
///     </para>
///     <para>
///         ⚠ <b>So the upload here follows the declaration rather than naming a format</b>, as such a host
///         does. Naming one would prove <see cref="TextureUploads.AddPicture" />, which
///         <c>TextureUploadDeviceTests</c> already does, and nothing about what the node declares.
///     </para>
/// </remarks>
public class TextureMeshMapDepthDeviceTests(ITestOutputHelper output) {
    /// <summary>Wide enough that a ramp across it has four times the levels eight bits hold.</summary>
    const int Width = 1024;

    const int Height = 4;

    [Fact]
    public void A_sixteen_bit_position_map_reaches_the_graph_with_more_than_256_levels() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"mesh map depth on {TextureKernelHarness.Adapter(device)}");

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var map = graph.Add("Source/Mesh Map");
        var target = graph.Add("Output/Output");

        map.SetText("Map", "position");
        graph.Connect(new(map.Id, "Out"), new(target.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) { BaseWidth = Width, BaseHeight = Height, Seed = 7 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;
        var external = Assert.Single(compiler.Externals);
        var declared = plan.Images[external.Image].Format;

        output.WriteLine($"the position map is declared {declared}");

        // A ramp across the model in sixteen-bit words, as the bake's PNG holds it: one level per texel.
        var words = new ushort[Width * Height * 4];

        for (var at = 0; at < Width * Height; at++) {
            var word = (ushort)Math.Round(at % Width * (double)ushort.MaxValue / (Width - 1));

            words[at * 4] = words[at * 4 + 1] = words[at * 4 + 2] = word;
            words[at * 4 + 3] = ushort.MaxValue;
        }

        using TextureUploads uploads = new(device);
        using TexturePlanEvaluator evaluator = new(device);

        uploads.Add(plan, external.Image, Width, Height, AtDepth(words, declared));

        using var bake = evaluator.Evaluate(plan, uploads.Externals);
        var read = bake.ReadRaw(plan.Outputs[0]);

        Assert.Equal(PixelFormat.Rgba16Float, read.Format);

        var reds = MemoryMarshal.Cast<byte, Half>(read.Texels);
        HashSet<Half> levels = [];

        for (var x = 0; x < Width; x++) {
            levels.Add(reds[(Height / 2 * Width + x) * 4]);
        }

        output.WriteLine($"{levels.Count} levels across one row of {Width}");

        Assert.True(
            levels.Count > 256,
            $"A {Width}-level position map reached the graph with {levels.Count} levels, which is eight bits."
        );
    }

    /// <summary>What a host following the plan uploads: the words at the depth the image is declared.</summary>
    static byte[] AtDepth(ushort[] words, TextureFormat format) {
        switch (format) {
            case TextureFormat.Rgba16Float: {
                var halves = new Half[words.Length];

                for (var at = 0; at < words.Length; at++) {
                    halves[at] = (Half)(words[at] / (float)ushort.MaxValue);
                }

                return [..MemoryMarshal.AsBytes(halves.AsSpan())];
            }
            case TextureFormat.Rgba8:
                return [..words.Select(static word => (byte)(word >> 8))];
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "A mesh map is not declared so.");
        }
    }
}
