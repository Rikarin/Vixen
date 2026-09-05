// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Issues <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> and
///     <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a>, as pictures with a closed-form
///     answer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every one of them is about <em>coverage</em>, and none can be seen in the plan.</b> A
///         stack composites through a cursor image, so "what this layer wrote" and "what is on the
///         canvas" are the same value — a group blends the whole canvas rather than what its children
///         covered, and a fill with nothing to say about a channel says the channel's own base
///         default into it. Both produce a well-formed plan with the right op count; the only place
///         they are visible is the texel.
///     </para>
///     <para>
///         <b>So each is a constant whose correct value is arithmetic rather than a reference
///         image.</b> A group over a canvas of ½ that covers nothing must leave ½; the same group
///         multiplying the whole canvas leaves ¼, and the two are 128 and 64 in a byte. There is no
///         tolerance to widen and no golden to re-bless.
///     </para>
///     <para>
///         ⚠ <b>And #832 is the lesson about how such a constant is chosen.</b> The first version of
///         this file asserted one coverage — zero — which is one of exactly two values at which a
///         premultiplied group and a straight one agree, so the arithmetic below was green against a
///         compositor that was wrong everywhere in between. A coverage that appears in a test as a
///         single number should be read as an untested parameter: <see cref="Coverages" /> sweeps it.
///     </para>
/// </remarks>
public class LayerCoverageDeviceTests(ITestOutputHelper output) {
    /// <summary>Coverages a group is swept over, including the two endpoints that hide #832 · 2.</summary>
    /// <remarks>
    ///     ⚠ <b>0 and 1 are in the list on purpose and they are the two that prove nothing.</b> At 0
    ///     the group blends by nothing whatever it holds, and at 1 a premultiplied colour and a
    ///     straight one are the same colour; the three in between are where a compositor that lerped
    ///     a group's colour towards the transparency it was isolated over shows.
    /// </remarks>
    public static TheoryData<float> Coverages => [0f, 0.25f, 0.5f, 0.75f, 1f];

    /// <summary>#807 · 1 — a group's mode must not reach a texel none of its children covered.</summary>
    /// <remarks>
    ///     ⚠ <b>The child covers nothing because its mask is zero, which is the one way a stack can
    ///     express "no coverage" without a paint layer.</b> Its blend amount is therefore zero and
    ///     it changes no texel — so a correct group, whatever its own mode, is the identity here.
    ///     A group that blends the cursor it was handed squares it instead.
    /// </remarks>
    [Fact]
    public void A_group_that_covers_nothing_leaves_the_canvas_alone() {
        using var device = Open();

        var stack = Grey(
            new LayerAsset {
                Id = "g",
                Kind = LayerKind.Group,
                Blend = LayerBlendMode.Multiply,
                Children = [
                    new() {
                        Id = "child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Constant, Value = 0f }
                    }
                ]
            }
        );

        var red = BakeRed(device, stack, "baseColor");

        output.WriteLine($"{Adapter(device)}: a Multiply group covering nothing baked red {red}");

        Assert.True(
            red is >= 126 and <= 130,
            $"{Adapter(device)}: the canvas is ½ and the group's one child is masked to nothing, so the "
            + $"group covers no texel and a correct composite leaves ½ — 128. It baked {red}. A group "
            + "whose children are composited onto the cursor and whose *result* is then blended applies "
            + "its mode to the whole canvas: ½ multiplied by ½ is ¼, which is 64. #807 · 1."
        );
    }

    /// <summary>#807 · 2 — a fill with nothing to say about a channel must not say the default.</summary>
    /// <remarks>
    ///     ⚠ <b>The upper layer authors base colour only and restricts nothing</b>, which by
    ///     <c>LayerAsset.Channels</c>'s empty-means-all rule makes it a layer that writes roughness
    ///     too. What it writes there is the question: it authored no roughness, so the honest answer
    ///     is nothing at all, and the layer beneath it survives.
    /// </remarks>
    [Fact]
    public void A_fill_that_authors_no_colour_for_a_channel_leaves_that_channel_alone() {
        using var device = Open();

        LayerStackAsset stack = new() {
            Name = "Coverage",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 3u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },

                        // ⚠ 0.9 rather than 0, so that "the default got composited" and "nothing
                        // happened" are different numbers. A default of zero would make this defect
                        // invisible in exactly the way zero-means-off defects always are.
                        new() { Usage = "roughness", Default = [0.9f, 0.9f, 0.9f, 1f] }
                    ],
                    Layers = [
                        new() {
                            Id = "rough",
                            Kind = LayerKind.Fill,
                            Channels = ["roughness"],
                            Values = { ["roughness"] = [0.2f, 0.2f, 0.2f, 1f] }
                        },
                        new() {
                            Id = "colour",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] }
                        }
                    ]
                }
            ]
        };

        var red = BakeRed(device, stack, "roughness");

        output.WriteLine($"{Adapter(device)}: roughness baked {red} under a base-colour-only layer");

        Assert.True(
            red is >= 49 and <= 53,
            $"{Adapter(device)}: the layer beneath set roughness to 0.2 — 51 — and the layer above it "
            + $"authors base colour and no roughness at all. Roughness baked {red}. A fill that falls "
            + "back to the channel's own Default composites 0.9 over it, which is 230: the channel's "
            + "base default arriving as though an artist had asked for it. #807 · 2."
        );
    }

    /// <summary>
    ///     #832 · 2 — a white group multiplied into the canvas is the identity at <em>every</em>
    ///     coverage, because white is what Multiply does nothing with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The oracle is the operator's own neutral, which makes the expected value
    ///         independent of the parameter being swept.</b> Multiply's neutral is white; a group
    ///         holding nothing but white therefore leaves a canvas of ½ at ½, whether it covers a
    ///         quarter of the texel or all of it. Nothing has to be recomputed per row and there is
    ///         no reference to re-bless.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The premultiplied composite this replaces bakes <c>½ − ½c(1 − c)</c></b> — 128 at
    ///         c = 0 and c = 1, 104 at a quarter and three quarters, 96 at a half. So the three
    ///         middle rows are the test and the two endpoints are the demonstration that the
    ///         single-coverage version of this file could not have caught it.
    ///     </para>
    /// </remarks>
    /// <param name="coverage">How much of the texel the group's one child covers.</param>
    [Theory]
    [MemberData(nameof(Coverages))]
    public void A_white_group_multiplied_into_the_canvas_is_the_identity_at_every_coverage(float coverage) {
        using var device = Open();

        var stack = Grey(
            new LayerAsset {
                Id = "g",
                Kind = LayerKind.Group,
                Blend = LayerBlendMode.Multiply,
                Children = [
                    new() {
                        Id = "child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Constant, Value = coverage }
                    }
                ]
            }
        );

        var red = BakeRed(device, stack, "baseColor");

        output.WriteLine($"{Adapter(device)}: a white Multiply group at coverage {coverage} baked red {red}");

        Assert.True(
            red is >= 126 and <= 130,
            $"{Adapter(device)}: the canvas is ½ and the group holds white, which is Multiply's own "
            + $"neutral, so a correct composite leaves ½ — 128 — at every coverage. At {coverage} it "
            + $"baked {red}. A group whose children composite onto a transparent constant hand back a "
            + "premultiplied colour, and a blend that consumes it as a straight one darkens it by "
            + "exactly the coverage: ½ − ½c(1 − c), which is 104 at a quarter and 96 at a half and is "
            + "128 at both endpoints. #832 · 2."
        );
    }

    /// <summary>
    ///     #832 · 1 — a group's mask multiplies the coverage its children accumulated rather than
    ///     replacing it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same stack as <see cref="A_group_that_covers_nothing_leaves_the_canvas_alone" />
    ///     with one line added, and that line used to change the picture from grey to black.</b> A
    ///     group's mask is doc 48 § D10's whole reason for a group — "a stack with one mask, which is
    ///     how twenty layers stay legible" — so this is the intended shape rather than an edge case.
    ///     A mask that <em>replaced</em> the alpha overwrote the group's accumulated coverage with the
    ///     mask's own value: the isolation went in and a fully opaque group came out.
    /// </remarks>
    [Fact]
    public void A_masked_group_that_covers_nothing_still_leaves_the_canvas_alone() {
        using var device = Open();

        var stack = Grey(
            new LayerAsset {
                Id = "g",
                Kind = LayerKind.Group,
                Blend = LayerBlendMode.Multiply,
                Mask = new() { Source = LayerMaskSource.Constant, Value = 1f },
                Children = [
                    new() {
                        Id = "child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Constant, Value = 0f }
                    }
                ]
            }
        );

        var red = BakeRed(device, stack, "baseColor");

        output.WriteLine($"{Adapter(device)}: a masked Multiply group covering nothing baked red {red}");

        Assert.True(
            red is >= 126 and <= 130,
            $"{Adapter(device)}: the group's one child is masked to nothing, so the group covers no "
            + $"texel and its own mask of 1 has nothing to reveal — ½ survives, which is 128. It baked "
            + $"{red}. A mask shuffled *over* the foreground's alpha replaces the coverage the group "
            + "accumulated with the mask's own value, so a group covering nothing multiplies at full "
            + "strength: ½ times the group's black is 0. #832 · 1, and #790 one level up."
        );
    }

    /// <summary>
    ///     A group of half coverage multiplies its own colour, not a colour darkened towards the
    ///     transparency it was isolated over.
    /// </summary>
    /// <remarks>
    ///     <b>The companion to the neutral sweep, and the half that would still pass if the group's
    ///     colour were ignored entirely.</b> A white group is the identity under Multiply whatever
    ///     happens to its colour, so on its own that test cannot tell a correct un-premultiplied
    ///     colour from a group that contributed nothing. Here the group is grey: ½ of the canvas
    ///     multiplied by the group's ½ is ¼, half-covered over ½ is ⅜, and ⅜ is 96.
    /// </remarks>
    [Fact]
    public void A_half_covered_grey_group_multiplies_its_own_colour() {
        using var device = Open();

        var stack = Grey(
            new LayerAsset {
                Id = "g",
                Kind = LayerKind.Group,
                Blend = LayerBlendMode.Multiply,
                Children = [
                    new() {
                        Id = "child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [0.5f, 0.5f, 0.5f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Constant, Value = 0.5f }
                    }
                ]
            }
        );

        var red = BakeRed(device, stack, "baseColor");

        output.WriteLine($"{Adapter(device)}: a half-covered grey Multiply group baked red {red}");

        Assert.True(
            red is >= 94 and <= 98,
            $"{Adapter(device)}: the group is grey at half coverage over a canvas of ½, so the "
            + $"composite is lerp(½, ½·½, ½) = ⅜ — 96. It baked {red}. A premultiplied group hands "
            + "back ¼ instead of ½ and the multiply then reads lerp(½, ½·¼, ½) = 0.3125, which is 80."
        );
    }

    /// <summary>A stack of one set whose single channel starts at ½.</summary>
    static LayerStackAsset Grey(LayerAsset layer) =>
        new() {
            Name = "Coverage",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 3u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] }],
                    Layers = [layer]
                }
            ]
        };

    /// <summary>The red of the first texel of one baked channel.</summary>
    static byte BakeRed(VulkanDevice device, LayerStackAsset stack, string usage) {
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        var picture = bake.Read(LayerStackDifferential.ImageOf(compilation, usage));

        return picture.Pixels[0];
    }

    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";
}
