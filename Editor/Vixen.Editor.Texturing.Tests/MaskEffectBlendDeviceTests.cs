// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A mask step's blend and opacity (Patina #109): its result composites over the mask it read.</summary>
/// <remarks>
///     A white fill over black reads its amount in red. The mask is a quarter, and an Invert step makes it three
///     quarters: 191 replaced, 128 at half opacity, 48 multiplied, 64 at no opacity. Every wrong reading is another of
///     those numbers.
/// </remarks>
/// <param name="output">Where the measured texel goes, so a failure names it.</param>
public class MaskEffectBlendDeviceTests(ITestOutputHelper output) {
    static readonly MaskAsset Quarter = new() { Source = LayerMaskSource.Constant, Value = 0.25f };

    /// <summary>Each blend and opacity lands on the number the arithmetic says.</summary>
    [Fact]
    public void A_mask_step_composites_over_the_mask_it_read() {
        using var device = TexturingDevice.Open();

        var replaced = Red(device, Inverted(LayerBlendMode.Copy, 1f));
        var half = Red(device, Inverted(LayerBlendMode.Copy, 0.5f));
        var multiplied = Red(device, Inverted(LayerBlendMode.Multiply, 1f));
        var none = Red(device, Inverted(LayerBlendMode.Copy, 0f));

        output.WriteLine(
            $"{TexturingDevice.Adapter(device)}: replaced {replaced}, half {half}, multiplied {multiplied}, none {none}"
        );

        Assert.InRange(replaced, 188, 194);
        Assert.InRange(half, 125, 131);
        Assert.InRange(multiplied, 45, 51);
        Assert.InRange(none, 61, 67);
    }

    /// <summary>
    ///     Copy at full opacity adds nothing to the graph, so every file that says neither compiles as it did: only the
    ///     faded step has a blend more.
    /// </summary>
    [Fact]
    public void A_step_that_says_neither_compiles_as_it_did() {
        var plain = Stack(Quarter with { Effects = [new() { Node = "Colour/Invert" }] });
        var faded = Stack(Quarter with { Effects = [new() { Node = "Colour/Invert", Opacity = 0.5f }] });

        static int Blends(LayerStackAsset stack) =>
            LayerStackGraph.Build(stack, stack.Sets[0]).Graph.Nodes.Count(static node => node.Type == "Colour/Blend");

        Assert.Equal(Blends(plain) + 1, Blends(faded));
    }

    /// <summary>The blend and opacity are saved, and a file that says neither reads as Copy at full.</summary>
    [Fact]
    public void A_steps_blend_and_opacity_round_trip() {
        var stack = Stack(Inverted(LayerBlendMode.Screen, 0.4f));

        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[0].Mask.Effects[0];

        Assert.Equal(LayerBlendMode.Screen, read.Blend);
        Assert.Equal(0.4f, read.Opacity, 5);

        var plain = LayerStackYaml.Read(
            LayerStackYaml.Write(Stack(Quarter with { Effects = [new() { Node = "Colour/Invert" }] }))
        );

        Assert.Equal(LayerBlendMode.Copy, plain.Sets[0].Layers[0].Mask.Effects[0].Blend);
        Assert.Equal(1f, plain.Sets[0].Layers[0].Mask.Effects[0].Opacity);
    }

    static MaskAsset Inverted(LayerBlendMode blend, float opacity) =>
        Quarter with { Effects = [new() { Node = "Colour/Invert", Blend = blend, Opacity = opacity }] };

    static LayerStackAsset Stack(MaskAsset mask) =>
        new() {
            Name = "Steps",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 3u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "white",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Mask = mask
                        }
                    ]
                }
            ]
        };

    static byte Red(VulkanDevice device, MaskAsset mask) {
        var stack = Stack(mask);
        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.DoesNotContain(build.Problems, static problem => problem.Severity == NodeSeverity.Error);

        var compilation = LayerStackCompiler.Compile(stack, build);

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        return bake.Read(compilation.Outputs[0].Image).Pixels[0];
    }
}
