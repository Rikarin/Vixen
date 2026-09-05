// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A graph whose source is a picture the caller supplies, baked — the whole of #732's path.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four things had to meet for this to draw, and each of them existed alone.</b> The plan
///         could hold an external image, <c>TextureUploads</c> could fill one and
///         <c>TexturePlanEvaluator</c> could bind one — and no node could <em>ask</em> for one, so
///         nothing in the tree ever put the three together. That is this repository's commonest
///         defect from the far side: not a finished thing nothing calls, but three finished things
///         with no caller between them.
///     </para>
///     <para>
///         <b>The oracle is exact and is a property of the whole chain.</b> The default ramp is the
///         identity strip — entry <c>k</c> holds <c>k</c>, baked by <c>TextureRamp.FromRamp</c> — and
///         a linear sweep over it at rotation zero is <c>(x + 0.5) / width</c>, which
///         <c>Gradient.rvn</c>'s own remarks name as the one closed form in the file that has to be
///         believed. A ramp that uploaded as zeros, one bound to the wrong image, or a sweep reading
///         the wrong axis fails it.
///     </para>
///     <para>
///         ⚠ Names its adapter and skips loudly without one: without a real device a headless run
///         falls back to the Null device, and every byte of this comparison would be zero.
///     </para>
/// </remarks>
public class TextureExternalGraphDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>A Gradient node's ramp is uploaded and swept, and the picture is the closed form.</summary>
    [Fact]
    public void A_gradient_over_its_own_baked_ramp_draws_the_closed_form() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeGraphModel graph = new();
        var gradient = graph.Add("Source/Gradient");
        var target = graph.Add("Output/Output");

        graph.Connect(new(gradient.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compiler = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 11 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;

        using var uploads = new TextureUploads(device);

        // Everything the compilation carried bytes for, which for a graph with no asset reference in
        // it is everything: nothing is owed back.
        Assert.Empty(TextureGraphExternals.Upload(uploads, plan, compiler.Externals));
        Assert.Equal(1, uploads.Count);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(plan.Outputs[0]);

        output.WriteLine($"{adapter}: {picture.Width}×{picture.Height}, first row "
            + $"{TextureKernelHarness.At(picture, 0, 0, 0)}…{TextureKernelHarness.At(picture, Side - 1, 0, 0)}");

        // (x + 0.5) / width, to within a byte of rounding on either side.
        for (var x = 0; x < Side; x++) {
            var expected = 255f * ((x + 0.5f) / Side);

            Assert.InRange(TextureKernelHarness.At(picture, x, 0, 0), expected - 2f, expected + 2f);

            // And flat down the column, because a linear sweep at rotation zero has no y in it.
            Assert.Equal(TextureKernelHarness.At(picture, x, 0, 0), TextureKernelHarness.At(picture, x, Side - 1, 0));
        }
    }

    /// <summary>An external nothing filled is a refusal, not a picture with a hole in it.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument for the test above.</b> If the evaluator drew something plausible for
    ///     an image nobody supplied, the closed form there would be evidence about the kernel and not
    ///     about the upload — so this is what says the ramp really is where the picture comes from.
    /// </remarks>
    [Fact]
    public void A_graph_whose_ramp_was_not_uploaded_is_refused() {
        using var device = TextureKernelHarness.Open();

        NodeGraphModel graph = new();
        var gradient = graph.Add("Source/Gradient");
        var target = graph.Add("Output/Output");

        graph.Connect(new(gradient.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var plan = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side }.Compile(graph).Value;

        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(() => evaluator.Evaluate(plan));

        Assert.Contains("external", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A bitmap's asset is handed back for a host to resolve, and nothing is uploaded for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Handed back rather than skipped.</b> Skipping would leave a plan missing exactly one
    ///     texture and an exception at <c>Evaluate</c> about an image index — while what the caller
    ///     needs to know is which asset it has to load, before it starts.
    /// </remarks>
    [Fact]
    public void A_bitmaps_asset_is_owed_back_rather_than_uploaded() {
        using var device = TextureKernelHarness.Open();

        NodeGraphModel graph = new();
        var bitmap = graph.Add("Source/Bitmap");
        var gradient = graph.Add("Source/Gradient");
        var blend = graph.Add("Colour/Blend");
        var target = graph.Add("Output/Output");

        bitmap.SetText("Source", "Assets/Textures/rust.png");
        graph.Connect(new(bitmap.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(gradient.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compiler = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side };
        var plan = compiler.Compile(graph).Value;

        using var uploads = new TextureUploads(device);

        var owed = Assert.Single(TextureGraphExternals.Upload(uploads, plan, compiler.Externals));

        Assert.Equal("Assets/Textures/rust.png", owed.Asset);
        Assert.Equal(bitmap.Id, owed.Node);

        // The gradient's ramp went up; the bitmap's picture did not, and the count is what says the
        // walk did not simply stop at the first entry it could not fill.
        Assert.Equal(2, compiler.Externals.Length);
        Assert.Equal(1, uploads.Count);
    }
}
