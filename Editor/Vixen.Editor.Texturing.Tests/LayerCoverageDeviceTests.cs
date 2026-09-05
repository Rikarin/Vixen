// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Issue <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a>'s first two defects, as
///     pictures with a closed-form answer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both are about <em>coverage</em>, and neither can be seen in the plan.</b> A stack
///         composites through a cursor image, so "what this layer wrote" and "what is on the canvas"
///         are the same value — a group blends the whole canvas rather than what its children
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
/// </remarks>
public class LayerCoverageDeviceTests(ITestOutputHelper output) {
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
