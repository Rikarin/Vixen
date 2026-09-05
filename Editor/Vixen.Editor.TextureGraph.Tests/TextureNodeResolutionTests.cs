// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The two things a node could not ask a plan for until #732 and #733: an image the caller
///     supplies, and an image at a resolution of its own.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both gaps were invisible from inside the compiler and visible only as an absence.</b>
///         Nothing failed: <c>TextureEmitter</c> had two allocation calls, both of them produced a
///         pooled image at the plan's base level, and the six kernels that needed anything else
///         simply had no node — which is this repository's commonest defect wearing its other face,
///         a finished thing nothing <em>can</em> call.
///     </para>
///     <para>
///         ⚠ <b>So every assertion here is over a compiled plan rather than over an emitter.</b> An
///         emitter that returned an index and allocated nothing would satisfy any test of its return
///         value; what says the feature exists is the image table, the level arithmetic and the op
///         list the walk actually produced.
///     </para>
/// </remarks>
public class TextureNodeResolutionTests {
    const int Side = 256;

    /// <summary>A gradient's ramp is an external image, and the compiler carries its bytes.</summary>
    /// <remarks>
    ///     ⚠ <b>The bytes are checked and not merely counted.</b> A strip of 1 024 zero bytes is the
    ///     right length, uploads without complaint and draws a black gradient — which is a picture,
    ///     of nothing, with no error anywhere. The default ramp is baked by
    ///     <c>TextureRamp.FromRamp</c> over an 8-bit quantiser, so entry <c>k</c> holds exactly
    ///     <c>k</c> and the whole strip is one closed form.
    /// </remarks>
    [Fact]
    public void A_gradients_ramp_is_an_external_image_whose_texels_the_compiler_carries() {
        var compiler = Compiler();
        var plan = Compile(compiler, Graph("Source/Gradient", out _));
        var ramp = Assert.Single(compiler.Externals);

        Assert.True(plan.Images[ramp.Image].External);
        Assert.Equal(TextureFormat.Rgba8, plan.Images[ramp.Image].Format);
        Assert.Equal("", ramp.Asset);
        Assert.Equal(256, ramp.Width);
        Assert.Equal(1, ramp.Height);
        Assert.Equal(256 * 4, ramp.Texels.Length);

        for (var entry = 0; entry < 256; entry++) {
            Assert.Equal((byte)entry, ramp.Texels[entry * 4]);
            Assert.Equal((byte)entry, ramp.Texels[(entry * 4) + 1]);
            Assert.Equal((byte)entry, ramp.Texels[(entry * 4) + 2]);
            Assert.Equal((byte)255, ramp.Texels[(entry * 4) + 3]);
        }

        // And the op reads it: an external nothing binds would be an entry in the table and a plan
        // that draws the same picture without it.
        Assert.Equal([ramp.Image], Assert.Single(plan.Ops, op => op.Kernel == "Gradient").Inputs);
    }

    /// <summary>An external image is written by nothing and pooled by nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a "does it compile" test would miss.</b> An image the caller supplies that
    ///     was also given a pool slot would be handed to the next op that wanted a texture of that
    ///     shape, and the ramp would be overwritten by whatever ran next — a gradient of somebody
    ///     else's picture, at the right size, with no error.
    /// </remarks>
    [Fact]
    public void An_external_image_is_never_written_and_never_pooled() {
        var compiler = Compiler();
        var plan = Compile(compiler, Graph("Source/Gradient", out _));
        var ramp = Assert.Single(compiler.Externals).Image;

        Assert.Empty(plan.Validate());
        Assert.DoesNotContain(plan.Ops, op => op.Output == ramp);
        Assert.Equal(-1, TexturePoolSchedule.For(plan).SlotOf[ramp]);

        // The instrument: every other image in this plan does have a slot, so the −1 above is the
        // external's own answer rather than a schedule that pooled nothing.
        for (var image = 0; image < plan.Images.Length; image++) {
            if (image != ramp) {
                Assert.True(TexturePoolSchedule.For(plan).SlotOf[image] >= 0);
            }
        }
    }

    /// <summary>A bitmap carries the asset's reference and none of its pixels.</summary>
    [Fact]
    public void A_bitmaps_external_names_the_asset_and_carries_no_texels() {
        var compiler = Compiler();
        var graph = Graph("Source/Bitmap", out var bitmap);

        bitmap.SetText("Source", "Assets/Textures/rust.png");

        var plan = Compile(compiler, graph);
        var picture = Assert.Single(compiler.Externals);

        Assert.True(plan.Images[picture.Image].External);
        Assert.Equal("Assets/Textures/rust.png", picture.Asset);
        Assert.Empty(picture.Texels);
    }

    /// <summary>A bitmap with no asset is refused rather than filled with black.</summary>
    /// <remarks>
    ///     A black stand-in would be a graph that compiles, bakes and draws nothing anybody asked
    ///     for; the refusal names the node and the setting, which is what an author can act on.
    /// </remarks>
    [Fact]
    public void A_bitmap_with_no_asset_is_refused() {
        var graph = Graph("Source/Bitmap", out var bitmap);
        var compilation = Compiler().Compile(graph);
        var refusal = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0002");

        Assert.Equal(bitmap.Id, refusal.Node);
        Assert.Equal("Source", refusal.Port);
        Assert.Null(compilation.Artefact);

        // The instrument: the same graph with a reference compiles, so the refusal is about the
        // empty setting and not about a Bitmap node that cannot be compiled at all.
        bitmap.SetText("Source", "Assets/Textures/rust.png");

        Assert.Empty(Compiler().Compile(graph).Diagnostics);
    }

    /// <summary>Auto Levels' reduction ladder settles at one texel, at descending level offsets.</summary>
    /// <remarks>
    ///     ⚠ <b>The rungs' <em>sizes</em> rather than their count, because the count is the thing
    ///     that looks right when the levels are wrong.</b> A ladder of three images all at level 0 is
    ///     three dispatches, validates, evaluates, and reduces nothing: <c>MinMaxReduce</c>'s block
    ///     is <c>parent / target</c>, so a reduction onto an image of its own size reads one texel
    ///     per invocation and copies the picture down the chain. What the node is for is the last
    ///     image being 1×1.
    /// </remarks>
    [Fact]
    public void Auto_levels_allocates_a_ladder_that_reaches_one_texel() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var levels = graph.Add("Colour/Auto Levels");

        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));
        Rewire(graph, levels);

        var plan = Compile(compiler, graph);
        var rungs = plan.Ops.Where(op => op.Kernel == "MinMaxReduce").ToArray();

        Assert.Equal(TextureAdjust.ReductionDispatches(Side, Side), rungs.Length);
        Assert.NotEmpty(rungs);

        var sizes = rungs.Select(op => plan.SizeOf(op.Output)).ToArray();

        Assert.Equal([32, 4, 1], sizes.Select(size => size.X).ToArray());
        Assert.Equal([32, 4, 1], sizes.Select(size => size.Y).ToArray());

        // And the map reads the last rung, which is the 1×1: an AutoLevels reading any other image
        // stretches by the extremes of a block.
        var map = Assert.Single(plan.Ops, op => op.Kernel == "AutoLevels");

        Assert.Equal(rungs[^1].Output, map.Inputs[1]);
    }

    /// <summary>Auto Levels' ladder starts at the image it reads, not at the graph's base.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap #733 sets, and it is silent.</b> A ladder counted from the base and applied
    ///     to a half-resolution source starts one level below where its source sits: the first rung
    ///     reads a 16×16 block of a 128-texel image into 32 texels, which <c>MinMaxReduce</c> clamps
    ///     to its own 8×8 <c>MaxBlock</c> — so three quarters of every block is never read and the
    ///     extremes are a corner's. A slightly flat picture, and nothing says so.
    /// </remarks>
    [Fact]
    public void Auto_levels_measures_its_ladder_from_the_image_it_reads() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var levels = graph.Add("Colour/Auto Levels");

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(levels.Id, "Input"));

        Rewire(graph, levels);

        var plan = Compile(compiler, graph);
        var source = Assert.Single(plan.Ops, op => op.Kernel == "Resample").Output;
        var rungs = plan.Ops.Where(op => op.Kernel == "MinMaxReduce").ToArray();

        // Half of 256 is 128, and the ladder is measured from there rather than from 256.
        Assert.Equal(128, plan.SizeOf(source).X);
        Assert.Equal([16, 2, 1], rungs.Select(op => plan.SizeOf(op.Output).X).ToArray());
        Assert.Equal(source, rungs[0].Inputs[0]);

        // ⚠ And the map's *target*, which this test asserted nothing about until #779 — the ladder
        // was relative and the image it stretched into was not, so the rungs above were all correct
        // over a 128² source while the answer was written into a 256² image. `AutoLevels.rvn` reads
        // its source at the coordinate it is writing and clamps, so three quarters of that output was
        // the source's right-hand and bottom edge stretched over the rest: a corner crop with a
        // smear, from a node that emitted exactly the right number of exactly the right dispatches.
        var map = Assert.Single(plan.Ops, op => op.Kernel == "AutoLevels");

        Assert.Equal(plan.SizeOf(source), plan.SizeOf(map.Output));
        Assert.Equal(source, map.Inputs[0]);
    }

    /// <summary>Every node in the library writes at the size of what it reads, not the graph's.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>'s roll call, and
    ///         the reason it is a roll call is that the defect was never in one node.</b> The
    ///         relative-level API #733 added was consulted at two call sites in the whole library;
    ///         every other node allocated at the graph's base, so <em>each</em> of them wrote a 256²
    ///         image over a 128² source and read it coordinate-for-coordinate. A test per node would
    ///         have been forty tests and the forty-first node would still have been wrong.
    ///     </para>
    ///     <para>
    ///         <b>The library comes out of the registry rather than a list here</b>, so a node added
    ///         by a later slice is covered the day it is registered. Every image input is fed from
    ///         the same half-resolution image, which is what makes the expected answer unambiguous
    ///         for the multi-input nodes as well.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A floor rather than an exact count, deliberately.</b> Some types cannot compile
    ///         from a bare grey input — a <c>Bitmap</c> needs an asset, an <c>Output</c> writes no
    ///         image of its own — and pinning the exact set that can would be a list that goes red on
    ///         the merge that adds a node rather than on the change that breaks one.
    ///         <see cref="Covered" /> is the guard that keeps this from passing vacuously.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_node_downstream_of_a_half_resample_writes_a_half_sized_image() {
        List<string> wrong = [];
        var covered = 0;

        foreach (var definition in Registry().Types.OrderBy(type => type.Path, StringComparer.Ordinal)) {
            // The one node whose answer is deliberately not the size of what it reads: a resample
            // fed by a resample halves twice, which is `Two_resamples_in_a_row_each_halve`.
            if (string.Equals(definition.Path, "Space/Resample", StringComparison.Ordinal)) {
                continue;
            }

            if (!Downstream(definition, out var graph, out var node)) {
                continue;
            }

            var compiler = Compiler();
            var compilation = compiler.Compile(graph);

            if (compilation.Artefact is not { } plan
                || compilation.Diagnostics.Any(one => one.Severity == NodeSeverity.Error)) {
                continue;
            }

            covered++;

            foreach (var written in compiler.NodeImages) {
                if (written.Node != node.Id) {
                    continue;
                }

                if (plan.SizeOf(written.Image).X != Side / 2) {
                    wrong.Add($"{definition.Path} wrote {plan.SizeOf(written.Image)} over a {Side / 2}² source");
                }
            }
        }

        Assert.Equal([], wrong);
        Assert.True(covered >= 30, $"only {covered} node types reached the assertion, so it proves little");
    }

    /// <summary>Two images of different sizes meeting at one node are brought to the larger.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of #779 that deriving alone cannot answer.</b> A node reads at the
    ///     coordinate it writes, so a blend of a 256² and a 128² image is wrong whichever size the
    ///     output takes — one of the two inputs is always mismatched. The compiler inserts a resample
    ///     exactly as it inserts a <c>ChannelShuffle</c> for a grey image arriving at a colour port,
    ///     and it goes <em>up</em>, because a promotion that threw detail away would be a silent
    ///     downgrade of the branch the author did not touch.
    /// </remarks>
    [Fact]
    public void Two_inputs_at_different_levels_meet_at_one_size() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var checker = graph.Add("Source/Checker");
        var resample = graph.Add("Space/Resample");
        var blend = graph.Add("Colour/Blend");

        graph.Connect(new(checker.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(noise.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(resample.Id, "Out"), new(blend.Id, "Foreground"));

        Rewire(graph, blend);

        var plan = Compile(compiler, graph);
        var mix = Assert.Single(plan.Ops, op => op.Kernel == "Blend");

        // The output is the larger of the two, and *both* of the images it reads are that size —
        // which is the assertion, because the un-resampled input satisfies the first half on its own.
        Assert.Equal(Side, plan.SizeOf(mix.Output).X);

        foreach (var input in mix.Inputs) {
            Assert.Equal(plan.SizeOf(mix.Output), plan.SizeOf(input));
        }

        // And it is a resample that made it so, rather than the half-resolution branch having been
        // quietly dropped: the 128² image the resample node wrote is read by an op in this plan.
        var half = Assert.Single(plan.Ops, op => op.Kernel == "Resample" && plan.SizeOf(op.Output).X == 128);

        Assert.Contains(plan.Ops, op => op.Inputs.Contains(half.Output) && plan.SizeOf(op.Output).X == Side);
    }

    /// <summary>The splat inserted for a grey image is allocated at that image's size.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>#779 inside the compiler rather than inside a node.</b> The promotion op is a
    ///         <c>ChannelShuffle</c>, which reads coordinate-for-coordinate like every other pointwise
    ///         kernel — so a base-sized splat of a half-resolution mask is the same corner crop,
    ///         emitted by the one piece of code no node-library change would ever have fixed.
    ///     </para>
    ///     <para>
    ///         <b>Both branches are resampled first, so the level is settled before the blend reads
    ///         either</b> — which is what isolates the splat: nothing here depends on the rescale
    ///         <see cref="Two_inputs_at_different_levels_meet_at_one_size" /> is about.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_promotion_of_a_half_resolution_grey_image_is_half_sized() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var colour = graph.Add("Source/Uniform");
        var greyHalf = graph.Add("Space/Resample");
        var colourHalf = graph.Add("Space/Resample");
        var blend = graph.Add("Colour/Blend");

        graph.Connect(new(noise.Id, "Out"), new(greyHalf.Id, "Input"));
        graph.Connect(new(colour.Id, "Out"), new(colourHalf.Id, "Input"));
        graph.Connect(new(colourHalf.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(greyHalf.Id, "Out"), new(blend.Id, "Foreground"));

        Rewire(graph, blend);

        var plan = Compile(compiler, graph);
        var splat = Assert.Single(plan.Ops, op => op.Kernel == "ChannelShuffle");

        Assert.Equal(128, plan.SizeOf(splat.Output).X);
        Assert.Equal(128, plan.SizeOf(splat.Inputs[0]).X);

        // The instrument: the splat is what the blend reads, so the size above is the size of an
        // image in the chain rather than of one allocated and abandoned.
        Assert.Contains(splat.Output, Assert.Single(plan.Ops, op => op.Kernel == "Blend").Inputs);
    }

    /// <summary>A graph with one node of a type fed by a half-resolution source, and that node.</summary>
    /// <returns>Whether the type is one this test can say anything about.</returns>
    static bool Downstream(NodeTypeDefinition definition, out NodeGraphModel graph, out GraphNode node) {
        graph = new();
        node = null!;

        var inputs = definition.Ports.Where(port => port is { Direction: PortDirection.Input, Kind: PortKind.Image });
        var output = definition.Ports.FirstOrDefault(port =>
            port is { Direction: PortDirection.Output, Kind: PortKind.Image }
        );

        if (output is null || !inputs.Any()) {
            return false;
        }

        var noise = graph.Add("Source/Noise");
        var resample = graph.Add("Space/Resample");

        node = graph.Add(definition.Path);

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));

        foreach (var port in inputs) {
            graph.Connect(new(resample.Id, "Out"), new(node.Id, port.Name));
        }

        Rewire(graph, node, output.Name);

        return true;
    }

    /// <summary>A resample writes an image at a level of its own, relative to what arrives.</summary>
    /// <remarks>
    ///     ⚠ <b>Two in a row is the assertion that matters.</b> An offset applied to the graph's base
    ///     rather than to the input makes the second resample write an image the size of the first's
    ///     — an identity copy, which is exactly the defect the node exists to make impossible.
    /// </remarks>
    [Fact]
    public void Two_resamples_in_a_row_each_halve() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var first = graph.Add("Space/Resample");
        var second = graph.Add("Space/Resample");

        graph.Connect(new(noise.Id, "Out"), new(first.Id, "Input"));
        graph.Connect(new(first.Id, "Out"), new(second.Id, "Input"));

        Rewire(graph, second);

        var plan = Compile(compiler, graph);
        var sizes = plan.Ops
            .Where(op => op.Kernel == "Resample")
            .Select(op => plan.SizeOf(op.Output).X)
            .ToArray();

        Assert.Equal([128, 64], sizes);
    }

    /// <summary>Every size a resample may ask for is the size the plan reports.</summary>
    /// <remarks>
    ///     The doubling direction as well as the halving one, because they are different arithmetic:
    ///     a level offset is a shift left below zero and a shift right above it, and only one of the
    ///     two clamps.
    /// </remarks>
    [Theory]
    [InlineData("Quarter", 64)]
    [InlineData("Half", 128)]
    [InlineData("Same", 256)]
    [InlineData("Double", 512)]
    [InlineData("Quadruple", 1024)]
    public void A_resamples_size_setting_is_the_size_the_plan_reports(string size, int expected) {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");

        resample.SetText("Size", size);
        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        Rewire(graph, resample);

        var plan = Compile(compiler, graph, allowWarnings: true);

        Assert.Equal(expected, plan.SizeOf(Assert.Single(plan.Ops, op => op.Kernel == "Resample").Output).X);
    }

    /// <summary>A resample onto its own size says that it is a copy.</summary>
    /// <remarks>
    ///     ⚠ <b>A warning rather than a refusal, and it is the whole of what a node can do about
    ///     #733's original symptom.</b> The plan is sound — it is a copy — so refusing it would be
    ///     refusing a legal graph; what an author cannot see is <em>why</em> nothing changed.
    /// </remarks>
    [Fact]
    public void A_resample_onto_its_own_size_is_a_warning_and_not_a_refusal() {
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");

        resample.SetText("Size", "Same");
        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        Rewire(graph, resample);

        var compilation = Compiler().Compile(graph);
        var caution = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0018");

        Assert.Equal(resample.Id, caution.Node);
        Assert.Equal(NodeSeverity.Warning, caution.Severity);
        Assert.NotNull(compilation.Artefact);

        // The instrument: the same graph at any other size says nothing, so the warning is about the
        // setting rather than about the node.
        resample.SetText("Size", "Half");

        Assert.Empty(Compiler().Compile(graph).Diagnostics);
    }

    /// <summary>A graph whose source, table and ladder all differ still validates as one plan.</summary>
    /// <remarks>
    ///     The three shapes this file adds meeting in one graph: an external image at no level, a
    ///     ladder of scratch at three, and an output at one. ⚠ <c>TexturePlan.Validate</c> is what
    ///     would catch a level that ran away, an image written twice or an input read before it was
    ///     written, and none of the individual tests above would.
    /// </remarks>
    [Fact]
    public void An_external_a_ladder_and_a_halving_hold_together_in_one_plan() {
        var compiler = Compiler();
        var graph = Graph("Source/Gradient", out var gradient);
        var grey = graph.Add("Colour/Grayscale");
        var levels = graph.Add("Colour/Auto Levels");
        var resample = graph.Add("Space/Resample");
        var map = graph.Add("Colour/Gradient Map");

        graph.Connect(new(gradient.Id, "Out"), new(grey.Id, "Input"));
        graph.Connect(new(grey.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(map.Id, "Input"));
        graph.Connect(new(map.Id, "Out"), new(resample.Id, "Input"));

        Rewire(graph, resample);

        var plan = Compile(compiler, graph);

        Assert.Empty(plan.Validate());
        Assert.Equal(2, compiler.Externals.Length);
        Assert.Equal(128, plan.SizeOf(plan.Outputs[0]).X);
    }

    /// <summary>A graph with one node of a type, wired to an Output, and the node it made.</summary>
    static NodeGraphModel Graph(string type, out GraphNode node) {
        NodeGraphModel graph = new();

        node = graph.Add(type);

        Rewire(graph, node);

        return graph;
    }

    /// <summary>Points the graph's single Output node at <paramref name="from" />.</summary>
    static void Rewire(NodeGraphModel graph, GraphNode from, string port = "Out") {
        foreach (var node in graph.Nodes) {
            if (string.Equals(node.Type, "Output/Output", StringComparison.Ordinal)) {
                graph.Disconnect(new(node.Id, "Input"));
                graph.Connect(new(from.Id, port), new(node.Id, "Input"));

                return;
            }
        }

        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(from.Id, port), new(output.Id, "Input"));
    }

    /// <summary>Compiles, and refuses to hand back a plan nobody could have meant.</summary>
    static TexturePlan Compile(TextureGraphCompiler compiler, NodeGraphModel graph, bool allowWarnings = false) {
        var compilation = compiler.Compile(graph);

        Assert.All(
            compilation.Diagnostics,
            diagnostic => Assert.True(
                allowWarnings && diagnostic.Severity == NodeSeverity.Warning,
                $"{diagnostic.Id}: {diagnostic.Message}"
            )
        );

        return compilation.Value;
    }

    static TextureGraphCompiler Compiler() =>
        new(Registry()) { BaseWidth = Side, BaseHeight = Side, Seed = 5150 };

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }
}
