// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What one layer's coverage comes out as, read off the device rather than off the graph.</summary>
/// <remarks>
///     <para>
///         <b>The weights half of the <em>Bake Splat Map</em> verb, at the one input value where the
///         two readings of a fill's alpha disagree.</b>
///         <c>SplatBakeDeviceTests.The_written_weights_are_the_stacks_own_resolution</c> pins the whole
///         verb through a fixture whose every fill is opaque, and ⚠ an opaque fill is exactly the case
///         both readings agree on — so it could not see
///         <a href="https://github.com/Rikarin/Vixen/issues/1138">#1138</a>.
///     </para>
///     <para>
///         These go through <c>LayerStackGraph.Weights</c> and
///         <c>LayerStackSplat.Coverage</c> directly, which is what the verb does per layer, and read a
///         texel back off a real device. A graph-level assertion would read the number this build
///         wrote into a <c>Blend</c> node's opacity, which is the same number twice.
///     </para>
/// </remarks>
/// <param name="output">Where the measured texel goes, so a failure names it.</param>
public class SplatCoverageDeviceTests(ITestOutputHelper output) {
    /// <summary>⚠ A constant fill's own alpha limits its coverage, and it used not to.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>Builder.Opacity</c> folds a constant fill's alpha into the layer's opacity by looking
    ///         the colour up under the channel being compiled — and a coverage build compiles one
    ///         channel called <c>mask</c>, while a fill's values are keyed by <em>real</em> usages. So
    ///         the lookup missed, the alpha was not folded, and a layer that covers half the texel in
    ///         the picture weighed <b>one</b> in the splat map: the flattened bake and the weights
    ///         disagreeing about the same stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Half rather than none or all, because the endpoints prove nothing.</b> At alpha 0
    ///         the layer covers nothing under either reading and at alpha 1 it covers everything under
    ///         either; a test written at either would have been green against the defect. The pair
    ///         below is the instrument: the opaque layer says the path resolves at all, and the
    ///         half-alpha layer is the measurement.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_constant_fills_alpha_limits_its_coverage() {
        using var device = TexturingDevice.Open();

        var opaque = Weight(device, Fill("solid", 1f));
        var half = Weight(device, Fill("faded", 0.5f));

        output.WriteLine($"{TexturingDevice.Adapter(device)}: alpha 1 weighed {opaque}, alpha ½ weighed {half}");

        // The instrument first: a coverage that came back zero for the opaque layer would make the
        // assertion below true by the whole path having stopped working.
        Assert.True(opaque >= 250, $"an opaque fill covers the texel and must weigh ~255; it weighed {opaque}");

        // ±3 of 255 is quantisation, not a tolerance on the rule: the wrong answer is 255.
        Assert.InRange(half, 125, 131);
    }

    /// <summary>⚠ And the opacity still multiplies it, rather than being replaced by it.</summary>
    /// <remarks>
    ///     A fold is a reassociation of one product — <c>amount = opacity · mask · alpha</c> — so a
    ///     layer at half opacity carrying a half-alpha colour covers a quarter. Written because the
    ///     obvious wrong fix returns the alpha instead of multiplying by it, and against a stack of one
    ///     opaque layer that mistake is invisible.
    /// </remarks>
    [Fact]
    public void The_layers_opacity_multiplies_the_alpha_rather_than_being_replaced_by_it() {
        using var device = TexturingDevice.Open();

        var layer = Fill("faded", 0.5f) with { Opacity = 0.5f };
        var quarter = Weight(device, layer);

        output.WriteLine($"{TexturingDevice.Adapter(device)}: ½ opacity over ½ alpha weighed {quarter}");

        Assert.InRange(quarter, 61, 67);
    }

    /// <summary>
    ///     ⚠ An anchor cannot be weighed, and the refusal names the verb rather than the artist's file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1137">#1137</a>. A coverage is compiled
    ///         over a set holding <em>one</em> layer, so an anchor onto another layer resolves nothing
    ///         — and the general sentence for that says the anchored layer is "not in this set", which
    ///         an artist reads against a stack where it plainly is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is on what the message must <em>not</em> say as well as on what it
    ///         must.</b> A message that merely mentions the anchor is satisfied by the old sentence
    ///         too, so the phrase that misdirects is named. No device: the refusal is decided while the
    ///         graph is built and there is nothing to evaluate.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_anchor_that_a_coverage_cannot_resolve_says_which_verb_cannot_resolve_it() {
        var stack = Stack(
            Fill("rock", 1f),
            Fill("moss", 1f) with { Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "rock" } }
        );

        var build = LayerStackGraph.Weights(stack, LayerStackSplat.Coverage(stack.Sets[0], stack.Sets[0].Layers[1]));

        var refusal = Assert.Single(build.Problems, problem => problem.Message.Contains("anchor", StringComparison.OrdinalIgnoreCase));

        output.WriteLine(refusal.Message);

        Assert.Contains("one layer at a time", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not in this set", refusal.Message, StringComparison.Ordinal);

        // The stack's own picture is fine, and saying so is half of why the sentence is worth having.
        Assert.Empty(LayerStackGraph.Build(stack, stack.Sets[0]).Problems);
    }

    /// <summary>One constant fill authoring one colour at the given alpha.</summary>
    static LayerAsset Fill(string id, float alpha) =>
        new() {
            Id = id,
            Name = id,
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [0.2f, 0.6f, 0.2f, alpha] }
        };

    /// <summary>A stack of one set over black, which is what a coverage build starts from.</summary>
    static LayerStackAsset Stack(params LayerAsset[] layers) =>
        new() {
            Name = "Weights",
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

    /// <summary>The red of the first texel of one layer's coverage, 0..255.</summary>
    static byte Weight(VulkanDevice device, LayerAsset layer) {
        var stack = Stack(layer);
        var build = LayerStackGraph.Weights(stack, LayerStackSplat.Coverage(stack.Sets[0], layer));
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
