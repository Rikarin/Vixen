// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A mask switched off (Patina #109): its layer composites unmasked, but for the entries a host keeps.</summary>
/// <remarks>
///     A white fill over black reads its amount in red: a quarter mask is 64, no mask 255, and a kept entry at a half,
///     multiplied over the white an unmasked layer has, is 128. Every wrong reading is another of those numbers.
/// </remarks>
/// <param name="output">Where the measured texel goes, so a failure names it.</param>
public class MaskSwitchDeviceTests(ITestOutputHelper output) {
    /// <summary>The switch takes the base, the entries and the effects away, and keeps a kept entry.</summary>
    [Fact]
    public void A_mask_switched_off_masks_nothing_but_its_kept_entries() {
        using var device = TexturingDevice.Open();
        MaskAsset mask = new() {
            Source = LayerMaskSource.Constant, Value = 0.25f, Effects = [new() { Node = "Colour/Invert" }]
        };
        MaskLayerAsset kept = new() {
            Source = LayerMaskSource.Constant, Value = 0.5f, Blend = LayerBlendMode.Multiply, Kept = true
        };
        MaskLayerAsset artists =
            new() { Source = LayerMaskSource.Constant, Value = 0f, Blend = LayerBlendMode.Multiply };

        var on = Red(device, mask with { Effects = [] });
        var off = Red(device, mask with { Enabled = false });
        var limited = Red(device, mask with { Enabled = false, Layers = [artists, kept] });
        var limitedOn = Red(device, mask with { Effects = [], Layers = [kept] });

        output.WriteLine(
            $"{TexturingDevice.Adapter(device)}: on {on}, off {off}, off with a kept entry {limited}, on with it {limitedOn}"
        );

        Assert.InRange(on, 61, 67);
        Assert.True(off >= 252, $"a mask switched off masks nothing; the layer weighed {off}");
        Assert.InRange(limited, 125, 131);
        Assert.InRange(limitedOn, 29, 35);
    }

    /// <summary>The switch and the kept mark are saved, and a file that says neither reads as on and not kept.</summary>
    [Fact]
    public void The_switch_and_a_kept_entry_round_trip() {
        var stack = Stack(
            new() { Enabled = false, Source = LayerMaskSource.Constant, Layers = [new() { Kept = true }, new()] }
        );

        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[0].Mask;

        Assert.False(read.Enabled);
        Assert.Equal([true, false], read.Layers.Select(static entry => entry.Kept));
        Assert.True(
            LayerStackYaml.Read(LayerStackYaml.Write(Stack(new() { Source = LayerMaskSource.Constant })))
                .Sets[0]
                .Layers[0]
                .Mask.Enabled
        );
    }

    static LayerStackAsset Stack(MaskAsset mask) =>
        new() {
            Name = "Switch",
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
