// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Which filter a <c>Space/Resample</c> hands its kernel, and why it cannot be a constant.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/865">#865</a>: the node magnified by
///         point-sampling and called it a box.</b> <c>Resample.rvn</c>'s box branch takes
///         <c>clamp(ceil(extent / size), 1, MaxSamples)</c> sub-samples per axis, which is one
///         whenever the target is the larger image — so <c>Box</c> going up reads a single texel at
///         the output texel's centre, which is a point sample. The setting's declared default was
///         <c>Box</c> for every <c>Size</c>, so every magnification in the catalogue was blocky.
///     </para>
///     <para>
///         <b>The first two facts are about the number in the plan and the third is about the
///         picture.</b> A plan-level assertion is what says the *node* changed; it would be satisfied
///         by a kernel that ignored the parameter. The device test is the closed form underneath —
///         a one-texel column checkerboard magnified twofold holds only 0 and 255 under any point
///         sample and must hold something between them under a bilinear one, because a texel centre
///         of the larger image lands halfway between two source columns.
///     </para>
///     <para>
///         ⚠ <b>Changing <c>TextureSettings.Enum</c>'s fallback argument would have fixed nothing.</b>
///         <c>NodeGraphCompiler.Bind</c> writes a <c>[Setting]</c>'s declared default into the node's
///         texts before the node is compiled, so a field initialized to a name makes the fallback
///         beside it unreachable in every graph. <see cref="A_named_filter_still_wins" /> is what
///         holds the other half of that: <c>Auto</c> is a default and not an override.
///     </para>
/// </remarks>
public class TextureResampleFilterTests {
    const int Side = 64;

    /// <summary>Going up, the default is the bilinear the header names — not a one-sample box.</summary>
    /// <param name="size">The magnifying sizes, both of which took one sample before #865.</param>
    [Theory]
    [InlineData("Double")]
    [InlineData("Quadruple")]
    public void A_magnifying_resample_defaults_to_bilinear(string size) {
        var resample = Resampled(size, filter: null, out var plan);

        Assert.Equal(1f, resample.Find("filter")!.Value.Value);

        // And it really is the larger image, so the assertion above is about a magnification rather
        // than about a node that quietly ignored `Size`.
        Assert.True(plan.SizeOf(resample.Output).X > Side);
    }

    /// <summary>Going down, it is still the box — the direction #829 fixed one layer below.</summary>
    [Theory]
    [InlineData("Half")]
    [InlineData("Quarter")]
    public void A_minifying_resample_defaults_to_box(string size) {
        var resample = Resampled(size, filter: null, out var plan);

        Assert.Equal(2f, resample.Find("filter")!.Value.Value);
        Assert.True(plan.SizeOf(resample.Output).X < Side);
    }

    /// <summary>A filter the author typed is passed through, magnifying or not.</summary>
    /// <remarks>
    ///     Both directions with the same name, because "the setting is read" and "the derivation is
    ///     skipped" are two claims and only the pair excludes a node that happened to derive the same
    ///     answer.
    /// </remarks>
    [Theory]
    [InlineData("Double", "Point", 0f)]
    [InlineData("Double", "Box", 2f)]
    [InlineData("Half", "Point", 0f)]
    [InlineData("Half", "Bilinear", 1f)]
    public void A_named_filter_still_wins(string size, string filter, float expected) {
        var resample = Resampled(size, filter, out _);

        Assert.Equal(expected, resample.Find("filter")!.Value.Value);
    }

    /// <summary>
    ///     ⚠ Magnified twofold, a column checkerboard has values between its two under the default and
    ///     only its two under a box.
    /// </summary>
    /// <remarks>
    ///     <b>Both halves in one test, because the second is what makes the first mean something.</b>
    ///     "Some texel is neither 0 nor 255" would be satisfied by any blur; the same plan under the
    ///     name <c>Box</c> producing only 0 and 255 is what says the default now selects a different
    ///     filter <em>and</em> that the box really is a point sample going up, which is the whole of
    ///     #865.
    /// </remarks>
    [Fact]
    public void A_magnified_checkerboard_is_interpolated_under_the_default_and_not_under_the_box() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Columns(Side);
        var automatic = Magnified(device, source, Filter(Resampled("Double", filter: null, out _)));
        var boxed = Magnified(device, source, (float)TextureFilter.Box);

        Assert.Equal(Side * 2, automatic.Width);

        Assert.Contains(
            Row(automatic),
            value => value is > 8 and < 247
        );

        Assert.All(
            Row(boxed),
            value => Assert.True(
                value is 0 or 255,
                $"the box produced {value} magnifying a checkerboard, so it is not the single sample "
                + "#865 says it is and this test no longer proves what it claims."
            )
        );
    }

    static float Filter(TextureOp op) => op.Find("filter")!.Value.Value;

    static byte[] Row(Bitmap picture) {
        var values = new byte[picture.Width];

        for (var x = 0; x < picture.Width; x++) {
            values[x] = TextureKernelHarness.At(picture, x, picture.Height / 2, 0);
        }

        return values;
    }

    /// <summary>The <c>Resample</c> op a one-node graph with these settings compiles to.</summary>
    static TextureOp Resampled(string size, string? filter, out TexturePlan plan) {
        NodeGraphCompiler<TexturePlan> compiler = new TextureGraphCompiler(Registry()) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 5150
        };

        NodeGraphModel graph = new();
        var checker = graph.Add("Source/Checker");
        var resample = graph.Add("Space/Resample");
        var output = graph.Add("Output/Output");

        resample.SetText("Size", size);

        if (filter is not null) {
            resample.SetText("Filter", filter);
        }

        graph.Connect(new(checker.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(output.Id, "Input"));

        var compilation = compiler.Compile(graph);

        // TG0022 only: a graph whose only node resamples says its map is computed at another size,
        // which is the caution every one of these shapes earns and none of them is about.
        Assert.All(
            compilation.Diagnostics,
            diagnostic => Assert.Equal("TG0022", diagnostic.Id)
        );

        var compiled = compilation.Value;

        plan = compiled;

        // ⚠ The op the *node* emitted, which is the one writing an image that is not the base size —
        // the terminus rule inserts a second `Resample` to bring the output back to level zero, and
        // that one is the compiler's, with the filter #829 already derives.
        return Assert.Single(
            compiled.Ops,
            op => op.Kernel == "Resample" && compiled.SizeOf(op.Output).X != Side
        );
    }

    /// <summary>One <c>Resample</c> onto an image one level larger, on a device.</summary>
    static Bitmap Magnified(VulkanDevice device, byte[] source, float filter) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            TexturePlan plan = new() {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8, -1)],
                Ops = [
                    new TextureOp {
                        Kernel = "Resample",
                        Output = 1,
                        Inputs = [0],
                        ReadsOtherExtents = true,
                        Parameters = [new("filter", filter)]
                    }
                ],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }
}
