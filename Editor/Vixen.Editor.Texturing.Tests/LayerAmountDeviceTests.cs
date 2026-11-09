// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>How much one layer covers in the picture it is part of, read off the device.</summary>
/// <remarks>
///     <para>
///         <c>LayerStackGraph.Amount</c> answers <c>Weights</c>' question for a layer whose mask reads the picture. Patina's
///         Find Layer weighed each layer alone, so a mask step reading the layers beneath (Patina #113) read nothing, and
///         the layer found under a click was not the one the picture shows there.
///     </para>
///     <para>
///         ⚠ <b>Grey beneath, at a half, so every wrong reading is a different number.</b> The rock beneath is 0.5 grey;
///         a mask step taking its red reads a half, and the moss covers half the texel. Weighed alone the step reads the
///         set's black default and the moss covers nothing; weighed over the picture rather than over black, the answer
///         would be the picture's colour rather than the amount.
///     </para>
/// </remarks>
/// <param name="output">Where the measured texel goes, so a failure names it.</param>
public class LayerAmountDeviceTests(ITestOutputHelper output) {
    /// <summary>A mask step that reads the layers beneath reads the picture's, and the amount is what it masks.</summary>
    [Fact]
    public void A_mask_that_reads_the_layers_beneath_weighs_what_the_picture_masks() {
        using var device = TexturingDevice.Open();
        var stack = Stack(Rock(), Moss());

        var alone = Weigh(device, stack, LayerStackGraph.Weights(stack, stack.Sets[0] with { Layers = [Moss()] }));
        var amount = Weigh(device, stack, LayerStackGraph.Amount(stack, stack.Sets[0], "moss"));

        output.WriteLine($"{TexturingDevice.Adapter(device)}: alone {alone}, in the picture {amount}");

        // The instrument: weighed alone the step reads nothing, which is the defect this answers.
        Assert.True(alone <= 3, $"alone, the moss's mask reads no picture and must weigh ~0; it weighed {alone}");
        Assert.InRange(amount, 125, 131);
    }

    /// <summary>⚠ A folder's own amount multiplies the layer's, whatever the folder's operator.</summary>
    /// <remarks>
    ///     A Multiply folder over a black backdrop would mix to black and weigh nothing, which is <c>Coverage</c>'s
    ///     reason for compositing with Copy; the folder here is Multiply at half opacity over a layer at half, so the
    ///     layer weighs a quarter. The layer above the folder covers everything and must not reach the answer.
    /// </remarks>
    [Fact]
    public void A_folder_around_the_layer_multiplies_its_amount_and_the_layers_above_feed_nothing() {
        using var device = TexturingDevice.Open();
        LayerAsset half = new() { Id = "half", Kind = LayerKind.Fill, Opacity = 0.5f, Values = { ["baseColor"] = [0.2f, 0.6f, 0.2f, 1f] } };
        LayerAsset folder = new() {
            Id = "folder",
            Kind = LayerKind.Group,
            Blend = LayerBlendMode.Multiply,
            Opacity = 0.5f,
            Children = [half]
        };
        LayerAsset above = new() { Id = "snow", Kind = LayerKind.Fill, Values = { ["baseColor"] = [1f, 1f, 1f, 1f] } };
        var stack = Stack(Rock(), folder, above);

        var amount = Weigh(device, stack, LayerStackGraph.Amount(stack, stack.Sets[0], "half"));

        output.WriteLine($"{TexturingDevice.Adapter(device)}: inside a half Multiply folder {amount}");

        Assert.InRange(amount, 61, 67);
    }

    /// <summary>
    ///     ⚠ Inside an isolated folder, "beneath" is the folder's own canvas, as the picture has it: the step reads
    ///     nothing there and the layer covers nothing, however grey the rock outside the folder is.
    /// </summary>
    [Fact]
    public void Inside_an_isolated_folder_a_mask_reads_what_the_folder_holds_beneath_it() {
        using var device = TexturingDevice.Open();
        LayerAsset folder = new() { Id = "folder", Kind = LayerKind.Group, Blend = LayerBlendMode.Multiply, Children = [Moss()] };
        LayerAsset passing = folder with { Id = "passing", Blend = LayerBlendMode.Copy };

        var isolated = Weigh(device, Stack(Rock(), folder), LayerStackGraph.Amount(Stack(Rock(), folder), Stack(Rock(), folder).Sets[0], "moss"));
        var passed = Weigh(device, Stack(Rock(), passing), LayerStackGraph.Amount(Stack(Rock(), passing), Stack(Rock(), passing).Sets[0], "moss"));

        output.WriteLine($"{TexturingDevice.Adapter(device)}: isolated {isolated}, passing through {passed}");

        Assert.True(isolated <= 3, $"an isolated folder holds nothing beneath the moss; it weighed {isolated}");
        Assert.InRange(passed, 125, 131);
    }

    /// <summary>⚠ In a channel the layer leaves alone it covers nothing, rather than handing on the picture beneath.</summary>
    /// <remarks>
    ///     A layer that does not write a channel composites nothing and its walk hands back what lies beneath it, which
    ///     here is the grey rock: an amount of a half where the layer is not at all.
    /// </remarks>
    [Fact]
    public void A_channel_the_layer_leaves_alone_weighs_nothing() {
        using var device = TexturingDevice.Open();
        LayerAsset rough = new() { Id = "rough", Kind = LayerKind.Fill, Channels = ["roughness"], Values = { ["roughness"] = [1f, 1f, 1f, 1f] } };
        LayerAsset folder = new() { Id = "folder", Kind = LayerKind.Group, Children = [rough] };
        var stack = Stack(Rock(), folder);

        var amount = Weigh(device, stack, LayerStackGraph.Amount(stack, stack.Sets[0], "rough"));

        output.WriteLine($"{TexturingDevice.Adapter(device)}: base colour of a roughness layer {amount}");

        Assert.True(amount <= 3, $"the layer writes no base colour and must weigh ~0 there; it weighed {amount}");
    }

    /// <summary>A layer no set holds is refused by its id.</summary>
    [Fact]
    public void A_layer_the_set_does_not_hold_is_refused() {
        var stack = Stack(Rock());

        var refused = Assert.Throws<ArgumentException>(() => LayerStackGraph.Amount(stack, stack.Sets[0], "moss"));

        Assert.Contains("'moss'", refused.Message, StringComparison.Ordinal);
    }

    static LayerAsset Rock() =>
        new() { Id = "rock", Kind = LayerKind.Fill, Values = { ["baseColor"] = [0.5f, 0.5f, 0.5f, 1f] } };

    /// <summary>A green fill whose mask is the red of the picture beneath it.</summary>
    static LayerAsset Moss() =>
        new() {
            Id = "moss",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [0.2f, 0.6f, 0.2f, 1f] },
            Mask = new() {
                Source = LayerMaskSource.Constant,
                Value = 1f,
                Effects = [
                    new() {
                        Node = "Colour/Channel Shuffle",
                        Texts = { ["Red From"] = "SecondRed" },
                        Inputs = { ["Second"] = LayerInputAsset.BeneathIn("baseColor") }
                    }
                ]
            }
        };

    static LayerStackAsset Stack(params LayerAsset[] layers) =>
        new() {
            Name = "Amount",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 3u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [.. layers]
                }
            ]
        };

    /// <summary>The red of the first texel of the build's first output, 0..255.</summary>
    static byte Weigh(VulkanDevice device, LayerStackAsset stack, LayerStackBuild build) {
        Assert.DoesNotContain(build.Problems, problem => problem.Severity == NodeSeverity.Error);

        var compilation = LayerStackCompiler.Compile(stack, build);

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);
        Assert.NotEmpty(compilation.Outputs);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        return bake.Read(compilation.Outputs[0].Image).Pixels[0];
    }
}
