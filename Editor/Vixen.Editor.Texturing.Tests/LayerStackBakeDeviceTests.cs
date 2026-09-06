// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 exit criterion 6: a stack and its exploded graph bake byte-identical outputs.</summary>
/// <remarks>
///     <para>
///         <b>The one test that proves § D1's "one evaluator" rather than asserting it.</b> A stack
///         compiles to a <c>TexturePlan</c>; its explosion is written as a <c>.vxtexgraph</c>, read
///         back off the file, and compiled to another. Both plans go to the same
///         <c>TexturePlanEvaluator</c>, and every map either produces is compared byte for byte.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Without one a headless run falls back to the Null
///         device on every platform and exits 0 — and this comparison in particular would then be
///         the claim that a black image equals a black image, which is the exact failure doc 48 § D3
///         names. The adapter is in every message, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip
///         into a failure.
///     </para>
///     <para>
///         ⚠ <b>And the pictures are checked for <em>variation</em> before they are compared.</b>
///         Every source a stack can express today except a texture fill is a constant, so a
///         differential run on a stack of constants would compare two flat images and pass whatever
///         either plan had done. <see cref="LayerStackDifferential.CheckerAsset" /> is uploaded as a
///         checkerboard for exactly that reason, and
///         <c>The_baked_maps_are_not_flat</c> is what says the variation survived to the output.
///     </para>
/// </remarks>
public class LayerStackBakeDeviceTests(ITestOutputHelper output) {
    /// <summary>Criterion 6.</summary>
    [Fact]
    public void A_stack_and_its_explosion_bake_byte_identical_outputs() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        var stack = LayerStackDifferential.Stack();
        var (direct, exploded) = LayerStackDifferential.Both(stack);

        LayerStackDifferential.AssertSamePlan(direct.Plan!, exploded.Plan!);

        using var evaluator = new TexturePlanEvaluator(device);

        foreach (var wanted in direct.Outputs) {
            var first = Bake(device, evaluator, direct, wanted.Usage);
            var second = Bake(device, evaluator, exploded, wanted.Usage);

            output.WriteLine(
                $"{adapter}: '{wanted.Usage}' is {first.Width}×{first.Height}, "
                + $"{first.Pixels.Length} bytes, first texel "
                + $"({first.Pixels[0]}, {first.Pixels[1]}, {first.Pixels[2]}, {first.Pixels[3]})"
            );

            Assert.Equal(first.Width, second.Width);
            Assert.Equal(first.Height, second.Height);
            Assert.True(
                first.Pixels.AsSpan().SequenceEqual(second.Pixels),
                $"{adapter}: the '{wanted.Usage}' map baked from the stack and the one baked from its "
                + $"exploded graph differ at texel {FirstDifference(first, second)}. Doc 48 exit criterion 6 "
                + "is that they cannot: the explosion is the graph the stack compiled, written out and read "
                + "back, so a difference is something the file lost or something the decoration added."
            );
        }
    }

    /// <summary>The instrument: the maps being compared are pictures, not flat colours.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked on the baked output rather than on the uploaded checkerboard.</b> A texture
    ///     that reached the plan and was then blended away — an opacity folded to zero, a mask that
    ///     replaced it, a bitmap node reading the wrong image — would leave the comparison above
    ///     green and meaningless. Base colour is the channel the checker layer writes; roughness and
    ///     height are constants by construction and are not asked.
    /// </remarks>
    [Fact]
    public void The_baked_base_colour_is_not_flat() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        var stack = LayerStackDifferential.Stack();
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        using var evaluator = new TexturePlanEvaluator(device);

        var picture = Bake(device, evaluator, compilation, "baseColor");
        byte low = 255;
        byte high = 0;

        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            low = Math.Min(low, picture.Pixels[index]);
            high = Math.Max(high, picture.Pixels[index]);
        }

        output.WriteLine($"{adapter}: baseColor red runs {low}…{high}");

        // ⚠ A *spread* and not a count of distinct values, and the difference is a defect this test
        // had for one run. `Colour/Levels` dithers by default, so a stack whose checkerboard had been
        // replaced by a flat grey still produced 147 distinct texels — the instrument was measuring
        // the dither. The checker is 32 against 255 before anything composites it; anything under a
        // quarter of that range is not a checkerboard.
        Assert.True(
            high - low >= 32,
            $"{adapter}: the baked base colour runs only {low}…{high}, so the byte-identical differential "
            + "beside this test is comparing two near-constants. The checkerboard uploaded for "
            + $"'{LayerStackDifferential.CheckerAsset}' is not reaching the output."
        );
    }

    /// <summary>Both plans ask for the same external, under the same reference.</summary>
    /// <remarks>
    ///     ⚠ <b>Device-free in spirit and kept here because it is the upload's precondition.</b> If
    ///     the explosion lost the bitmap node's <c>Source</c> text, the second plan would want an
    ///     external the test fills with the same checkerboard anyway — and the two bakes would
    ///     agree while the file had lost the reference. The asset string is what the round trip has
    ///     to carry, so it is compared rather than assumed.
    /// </remarks>
    [Fact]
    public void The_explosion_carries_the_texture_reference_through_the_file() {
        var stack = LayerStackDifferential.Stack();
        var (direct, exploded) = LayerStackDifferential.Both(stack);

        var wanted = Assert.Single(direct.Externals);
        var got = Assert.Single(exploded.Externals);

        Assert.Equal(LayerStackDifferential.CheckerAsset, wanted.Asset);
        Assert.Equal(wanted.Asset, got.Asset);
        Assert.Equal(wanted.Image, got.Image);
    }

    static Bitmap Bake(
        VulkanDevice device,
        TexturePlanEvaluator evaluator,
        LayerStackCompilation compilation,
        string usage
    ) {
        var plan = compilation.Plan!;

        using TextureUploads uploads = new(device);

        foreach (var external in compilation.Externals) {
            uploads.Add(plan, external.Image, Side, Side, Checker());
        }

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, usage));
    }

    /// <summary>How many texels across the uploaded checkerboard is.</summary>
    /// <remarks>
    ///     Eight over a 64-texel bake, so one cell is eight texels and the blur three texels wide
    ///     leaves plenty of both. A checker at the bake's own resolution would be one texel per cell
    ///     and would look like noise after the blur.
    /// </remarks>
    const int Side = 8;

    /// <summary>An eight-square checkerboard, RGBA8.</summary>
    static byte[] Checker() {
        var texels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var value = (byte)(((x + y) & 1) == 0 ? 255 : 32);
                var offset = ((y * Side) + x) * 4;

                texels[offset] = value;
                texels[offset + 1] = value;
                texels[offset + 2] = value;
                texels[offset + 3] = 255;
            }
        }

        return texels;
    }

    static string FirstDifference(Bitmap first, Bitmap second) {
        for (var index = 0; index < first.Pixels.Length && index < second.Pixels.Length; index++) {
            if (first.Pixels[index] != second.Pixels[index]) {
                var texel = index / 4;

                return $"({texel % first.Width}, {texel / first.Width}) channel {index % 4}: "
                    + $"{first.Pixels[index]} against {second.Pixels[index]}";
            }
        }

        return "nowhere — the two differ only in length";
    }
}
