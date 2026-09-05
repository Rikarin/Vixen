// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What #779 actually looked like, on a device: the picture a node downstream of a resample draws.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>"Tests pass" is not evidence for a defect whose symptom is a picture, and this one's
///         is.</b> Every compiled plan validated, every op dispatched, every counter read healthy —
///         the image table simply said 256 where the source said 128, and a pointwise kernel reads
///         its input at the coordinate it is writing and clamps. What that produces is the source's
///         top-left quarter over the whole target with its right and bottom edge rows smeared across
///         the rest, which is a perfectly plausible-looking image of the wrong thing.
///     </para>
///     <para>
///         <b>The oracle is a closed form rather than an eye.</b> <c>Invert</c> is <c>1 − v</c>
///         exactly, so the assertion is texel-for-texel arithmetic between two images this bake
///         produced — and it holds only if the invert read the same texel it wrote. Under the defect
///         the two images are not even the same size.
///     </para>
///     <para>
///         ⚠ Opened through <see cref="TextureKernelHarness.Open" />, which names the adapter and
///         skips loudly without one: on the Null device a comparison of two empty images would pass.
///     </para>
/// </remarks>
public class TextureLevelDeviceTests(ITestOutputHelper output) {
    const int Side = 128;

    /// <summary>A node downstream of a half resample reads the texel it writes, on a real adapter.</summary>
    [Fact]
    public void An_invert_downstream_of_a_resample_draws_the_half_sized_image_it_read() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new() { Name = "Half" };
        var noise = graph.Add("Source/Noise");
        var resample = graph.Add("Space/Resample");
        var invert = graph.Add("Colour/Invert");
        var kept = graph.Add("Output/Output");

        noise.SetValue("Scale", 6f);
        noise.SetValue("Octaves", 3f);
        kept.SetText("Usage", "height");

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(invert.Id, "Input"));
        graph.Connect(new(invert.Id, "Out"), new(kept.Id, "Input"));

        // ⚠ Every node's image kept, because the source of the comparison is an intermediate — and an
        // image a plan does not keep is pooled over, so reading one back after the bake gives
        // whichever op wrote that texture last.
        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            PreviewEveryNode = true
        };

        var compilation = compiler.Compile(graph);

        // ⚠ The terminus warning is expected and named, rather than the emptiness this asserted
        // before #805: the Output here is fed by a half-resolution chain, so the compiler resamples
        // the *kept* image back to the graph's size and says so. What this test measures is the
        // intermediate — `Image(compiler, invert.Id)`, below — which is untouched by that.
        Assert.Equal("TG0022", Assert.Single(compilation.Diagnostics).Id);

        var plan = compilation.Value;
        var source = Image(compiler, resample.Id);
        var target = Image(compiler, invert.Id);

        Assert.Equal(new(Side / 2, Side / 2), plan.SizeOf(source));
        Assert.Equal(plan.SizeOf(source), plan.SizeOf(target));

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var read = bake.Read(source);
        var inverted = bake.Read(target);

        output.WriteLine($"{adapter}: {read.Width}×{read.Height} source, {Distinct(read)} distinct values");

        // The instrument: a flat image would satisfy the arithmetic below however the invert read it,
        // because 1 − k is the same wherever it is sampled from.
        Assert.True(
            Distinct(read) > 16,
            $"{adapter}: the resampled noise holds {Distinct(read)} distinct values, so it is not a "
            + "picture and nothing below would mean anything."
        );

        Assert.Equal(read.Width, inverted.Width);
        Assert.Equal(read.Height, inverted.Height);

        var worst = 0;

        for (var y = 0; y < read.Height; y++) {
            for (var x = 0; x < read.Width; x++) {
                var expected = 255 - TextureKernelHarness.At(read, x, y, 0);

                worst = Math.Max(worst, Math.Abs(expected - TextureKernelHarness.At(inverted, x, y, 0)));
            }
        }

        // One level, because both images are read back through an eight-bit quantiser and 1 − v is
        // exact only before it. A corner crop is not off by one anywhere except in its own quarter.
        Assert.True(
            worst <= 1,
            $"{adapter}: the invert differs from 1 − source by up to {worst}/255, so it did not read "
            + "the texel it wrote."
        );
    }

    /// <summary>The image one node's <c>Out</c> port wrote.</summary>
    static int Image(TextureGraphCompiler compiler, NodeId node) =>
        Assert.Single(compiler.NodeImages.Where(written => written.Node == node)).Image;

    /// <summary>How many different values the red channel holds, so "this is a picture" is a claim.</summary>
    static int Distinct(Bitmap picture) {
        HashSet<byte> values = [];

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                values.Add(TextureKernelHarness.At(picture, x, y, 0));
            }
        }

        return values.Count;
    }
}
