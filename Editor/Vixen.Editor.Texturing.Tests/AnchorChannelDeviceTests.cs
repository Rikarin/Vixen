// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #111: one anchor mask is one picture, whichever channel is being built.</summary>
public class AnchorChannelDeviceTests(ITestOutputHelper output) {
    /// <summary>
    ///     The anchored layer is 0.2 in base colour and 0.9 in roughness. A white layer over black, masked by that anchor
    ///     reading roughness, must come out 0.9 in both channels: the mask is roughness, and nothing else.
    /// </summary>
    [Fact]
    public void An_anchor_mask_reads_its_chosen_channel_in_every_channel() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            new LayerAsset {
                Id = "a",
                AnchorPoint = "Wear",
                Values = { ["baseColor"] = [0.2f, 0.2f, 0.2f, 1f], ["roughness"] = [0.9f, 0.9f, 0.9f, 1f] }
            },
            new LayerAsset {
                Id = "black", Values = { ["baseColor"] = [0f, 0f, 0f, 1f], ["roughness"] = [0f, 0f, 0f, 1f] }
            },
            new LayerAsset {
                Id = "b",
                Values = { ["baseColor"] = [1f, 1f, 1f, 1f], ["roughness"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = "Wear", Channel = "roughness" }
                }
            }
        );

        var colour = BakeRed(device, stack, "baseColor");
        var rough = BakeRed(device, stack, "roughness");

        output.WriteLine($"{TexturingDevice.Adapter(device)}: baseColor {colour}, roughness {rough}");

        Assert.InRange(rough, 228, 232);
        Assert.InRange(colour, 228, 232);
    }

    /// <summary>A channel the anchored layer leaves alone still has a picture at that point: the stack beneath it.</summary>
    [Fact]
    public void An_anchor_onto_a_layer_that_skips_the_channel_reads_the_stack_at_that_point() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            new LayerAsset { Id = "under", Values = { ["roughness"] = [0.75f, 0.75f, 0.75f, 1f] } },
            new LayerAsset {
                Id = "a",
                AnchorPoint = "Colour only",
                Channels = ["baseColor"],
                Values = { ["baseColor"] = [0.2f, 0.2f, 0.2f, 1f] }
            },
            new LayerAsset {
                Id = "black", Values = { ["baseColor"] = [0f, 0f, 0f, 1f], ["roughness"] = [0f, 0f, 0f, 1f] }
            },
            new LayerAsset {
                Id = "b",
                Values = { ["baseColor"] = [1f, 1f, 1f, 1f], ["roughness"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = "Colour only", Channel = "roughness" }
                }
            }
        );

        var colour = BakeRed(device, stack, "baseColor");

        output.WriteLine($"{TexturingDevice.Adapter(device)}: baseColor {colour}");

        Assert.InRange(colour, 189, 193);
    }

    /// <summary>A fill reads an anchor point in the channel it is writing, or in the one it names.</summary>
    [Theory]
    [InlineData("", 51, 229)]
    [InlineData("roughness", 229, 229)]
    public void A_fill_reads_an_anchor_point(string channel, int colour, int rough) {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            Worn(),
            Black(),
            new LayerAsset {
                Id = "clone", Fill = LayerFillSource.Anchor, Anchor = new() { Anchor = "Wear", Channel = channel }
            }
        );

        Assert.InRange(BakeRed(device, stack, "baseColor"), colour - 2, colour + 2);
        Assert.InRange(BakeRed(device, stack, "roughness"), rough - 2, rough + 2);
    }

    /// <summary>A filter's second image input is fed by an anchor point, and its first reads the stack beneath.</summary>
    [Fact]
    public void A_filter_input_reads_an_anchor_point() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            Worn(),
            Black(),
            new LayerAsset {
                Id = "over",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Blend",
                Settings = { ["Opacity"] = [0.5f] },
                Inputs = { ["Foreground"] = LayerInputAsset.Of(new() { Anchor = "Wear", Channel = "roughness" }) }
            }
        );

        // Half of 0.9 over black, in both channels: the input reads roughness whichever channel is built.
        Assert.InRange(BakeRed(device, stack, "baseColor"), 113, 117);
        Assert.InRange(BakeRed(device, stack, "roughness"), 113, 117);
    }

    /// <summary>A generator's image input is fed by an anchor point, so any node with one becomes a mask.</summary>
    [Fact]
    public void A_generator_input_reads_an_anchor_point() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            Worn(),
            Black(),
            new LayerAsset {
                Id = "b",
                Values = { ["baseColor"] = [1f, 1f, 1f, 1f], ["roughness"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Generator,
                    Generator = "Filters/Blur",
                    GeneratorValues = { ["Radius"] = [2f] },
                    GeneratorInputs = { ["Input"] = new() { Anchor = "Wear", Channel = "roughness" } }
                }
            }
        );

        Assert.InRange(BakeRed(device, stack, "baseColor"), 227, 231);
        Assert.InRange(BakeRed(device, stack, "roughness"), 227, 231);
    }

    /// <summary>What a reference to an anchor point is refused for, and what a draft only warns of.</summary>
    [Theory]
    [InlineData("Wear", "", "names no channel")]
    [InlineData("Wear", "metalness", "not a channel")]
    [InlineData("Tear", "roughness", "publishes an anchor point named 'Tear'")]
    public void A_mask_reference_says_what_it_lacks(string anchor, string channel, string said) {
        var stack = Stack(
            Worn(),
            new LayerAsset {
                Id = "b",
                Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = anchor, Channel = channel } }
            }
        );

        var refused = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(
            refused.Problems,
            problem => problem.Severity == Vixen.NodeGraph.NodeSeverity.Error
                && problem.Message.Contains(said, StringComparison.Ordinal)
        );

        var drafted = LayerStackGraph.Build(stack, stack.Sets[0], draft: true);

        Assert.False(drafted.HasErrors, string.Join("; ", drafted.Problems));
    }

    /// <summary>Two layers may not publish one name: a reference could not say which it meant.</summary>
    [Fact]
    public void Two_anchor_points_of_one_name_are_refused() {
        var stack = Stack(Worn(), Worn() with { Id = "again" });

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(build.Problems, static problem => problem.Message.Contains("publish the anchor point 'Wear'"));
    }

    static LayerAsset Worn() =>
        new() {
            Id = "a",
            AnchorPoint = "Wear",
            Values = { ["baseColor"] = [0.2f, 0.2f, 0.2f, 1f], ["roughness"] = [0.9f, 0.9f, 0.9f, 1f] }
        };

    static LayerAsset Black() =>
        new() { Id = "black", Values = { ["baseColor"] = [0f, 0f, 0f, 1f], ["roughness"] = [0f, 0f, 0f, 1f] } };

    static LayerStackAsset Stack(params LayerAsset[] layers) =>
        new() {
            Name = "Anchors",
            BaseWidth = 16,
            BaseHeight = 16,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] },
                        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [..layers]
                }
            ]
        };

    static byte BakeRed(VulkanDevice device, LayerStackAsset stack, string usage) {
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, usage)).Pixels[0];
    }
}
