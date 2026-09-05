// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>The graph compiler, with no device anywhere near it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D5 is what makes this file possible and it is worth naming.</b> Compilation is
///         a walk that appends records to two lists, so what a graph <em>means</em> is a value a test
///         reads — the image table, the op order, the parameters, the outputs — and every assertion
///         below is an equality over one. The single test that needs a GPU is
///         <c>TextureGraphDeviceTests</c>'s differential, and it exists to prove that this value is
///         the one the evaluator already draws correctly.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the node library is not registered.</b> Every
///         graph below would be nodes of a type nobody registered, which is <c>NG0001</c> per node and
///         a compilation with no artefact — so <c>Value</c> throws and the tests fail rather than
///         passing over an empty walk. <see cref="Every_node_this_assembly_declares_is_registered" />
///         is the cheaper guard on the same question.
///     </para>
/// </remarks>
public class TextureGraphCompilerTests {
    /// <summary>Every node type the assembly declares, by menu path.</summary>
    /// <remarks>
    ///     ⚠ <b>The eight this file exercises are the eight § M4 built the compiler against; the rest
    ///     are the library § M4's second half added, and they are listed here because the assertion
    ///     below is about the <em>generator</em> rather than about this file's graphs.</b> What
    ///     covers the library's own correspondence with the kernels is
    ///     <c>TextureNodeLibraryTests</c>, which wires all of them into one graph.
    /// </remarks>
    static readonly string[] Paths = [
        "Analysis/Distance",
        "Analysis/Edge Detect",
        "Analysis/Flood Fill",
        "Colour/Blend",
        "Colour/Channel Shuffle",
        "Colour/Grayscale",
        "Colour/HSL",
        "Colour/Invert",
        "Colour/Levels",
        "Filters/Blur",
        "Filters/Blur HQ",
        "Filters/Directional Blur",
        "Filters/Directional Warp",
        "Filters/Emboss",
        "Filters/Non-Uniform Blur",
        "Filters/Radial Blur",
        "Filters/Sharpen",
        "Filters/Slope Blur",
        "Filters/Vector Warp",
        "Filters/Warp",
        "Output/Output",
        "Placement/Splatter",
        "Placement/Tile Sampler",
        "Source/Checker",
        "Source/Noise",
        "Source/Shape",
        "Source/Uniform",
        "Space/Crop",
        "Space/Mirror",
        "Space/Tile",
        "Space/Transform 2D",
        "Surface/Ambient Occlusion",
        "Surface/Curvature",
        "Surface/Height to Normal",
        "Surface/Normal Combine",
        "Surface/Normal Transform"
    ];

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static TextureGraphCompiler Compiler(int baseWidth = 256, int baseHeight = 256, int bake = 0) =>
        new(Registry()) { BaseWidth = baseWidth, BaseHeight = baseHeight, BakeLevelOffset = bake, Seed = 41823 };

    /// <summary>Every node type the generator found, so a ninth reaches this file by existing.</summary>
    [Fact]
    public void Every_node_this_assembly_declares_is_registered() {
        var registry = Registry();

        Assert.Equal(Paths, registry.Types.Select(type => type.Path).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>A graph compiles to a plan a test can read, with no device.</summary>
    [Fact]
    public void A_graph_compiles_to_a_plan_with_no_device() {
        NodeGraphModel graph = new() { Name = "Rust" };
        var noise = graph.Add("Source/Noise");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "roughness");
        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler();
        var compilation = compiler.Compile(graph);
        var plan = compilation.Value;

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(["Noise", "Levels"], plan.Ops.Select(op => op.Kernel).ToArray());
        Assert.Equal(2, plan.Images.Length);
        Assert.Equal([1], plan.Outputs);
        Assert.Empty(plan.Validate());

        // The usage is the compiler's, because the plan has nowhere to put one.
        var kept = Assert.Single(compiler.Outputs);

        Assert.Equal("roughness", kept.Usage);
        Assert.Equal(1, kept.Image);
        Assert.Equal(output.Id, kept.Node);
    }

    /// <summary>The order is the graph's dependency order, not the order nodes were added.</summary>
    /// <remarks>
    ///     ⚠ <b>The chain is built backwards on purpose.</b> <c>NodeGraphModel.Ordered</c> falls back
    ///     to insertion order for nodes of equal depth, so a graph added in the order it runs would
    ///     pass whether or not anything sorted it — the classic predicate that cannot be false.
    /// </remarks>
    [Fact]
    public void The_ops_come_out_in_dependency_order() {
        NodeGraphModel graph = new();
        var output = graph.Add("Output/Output");
        var blur = graph.Add("Filters/Blur");
        var levels = graph.Add("Colour/Levels");
        var noise = graph.Add("Source/Noise");

        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Value;

        Assert.Equal(["Noise", "Levels", "Blur", "Blur"], plan.Ops.Select(op => op.Kernel).ToArray());
    }

    /// <summary>A cycle is refused by the model, so the compiler never has to have an opinion.</summary>
    [Fact]
    public void The_model_refuses_a_cycle() {
        NodeGraphModel graph = new();
        var first = graph.Add("Colour/Levels");
        var second = graph.Add("Filters/Blur");

        graph.Connect(new(first.Id, "Out"), new(second.Id, "Input"));

        Assert.False(graph.TryConnect(new(second.Id, "Out"), new(first.Id, "Input"), out _, out var error));
        Assert.Equal(GraphConnectionError.Cycle, error);

        // And the graph is what it was: the refused edge left nothing behind.
        Assert.Single(graph.Edges);
    }

    /// <summary>An unconnected input takes the number typed on the node.</summary>
    [Fact]
    public void An_unconnected_input_takes_its_inline_value() {
        var authored = Radius(node => node.SetValue("Radius", 3.5f));
        var declared = Radius(_ => { });

        Assert.Equal(3.5f, authored);

        // And the type's own default when nothing was typed, which is the other half of the same
        // question: a node dropped in and not touched has to do something sensible.
        Assert.Equal(8f, declared);
    }

    static float Radius(Action<GraphNode> author) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        author(blur);
        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Value;

        return plan.Ops.First(op => op.Kernel == "Blur").Find("radius")!.Value.Value;
    }

    /// <summary>A blur's radius is a length at the base resolution, and the bake scales it.</summary>
    /// <remarks>
    ///     Doc 48 § D8. The node writes what the author typed; <c>TexturePlan.Resolve</c> is the one
    ///     place it becomes texels of the image being written, so the same graph baked four times
    ///     larger is the same material.
    /// </remarks>
    [Fact]
    public void A_radius_is_a_length_at_the_base_resolution() {
        Assert.Equal(TextureParameterUnit.TexelsAtBase, Blurred(0).Parameter.Unit);
        Assert.Equal(8f, Blurred(0).Resolved);
        Assert.Equal(32f, Blurred(-2).Resolved);
    }

    static (TextureParameter Parameter, float Resolved) Blurred(int bake) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler(bake: bake).Compile(graph).Value;
        var index = 0;

        while (plan.Ops[index].Kernel != "Blur") {
            index++;
        }

        var parameter = plan.Ops[index].Find("radius")!.Value;

        return (parameter, plan.Resolve(index, parameter));
    }

    /// <summary>A diagnostic names the node and the port an author can see.</summary>
    /// <remarks>
    ///     Doc 48 § Part 4's second half — colour into a grey port is a type error <em>naming the
    ///     port</em> — and the whole reason a <c>NodeDiagnostic</c> carries both.
    /// </remarks>
    [Fact]
    public void A_colour_arriving_at_a_measured_port_names_the_node_and_the_port() {
        NodeGraphModel graph = new();
        var uniform = graph.Add("Source/Uniform");
        var distance = graph.Add("Analysis/Distance");
        var output = graph.Add("Output/Output");

        graph.Connect(new(uniform.Id, "Out"), new(distance.Id, "Mask"));
        graph.Connect(new(distance.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);
        var refusal = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0004");

        Assert.Equal(distance.Id, refusal.Node);
        Assert.Equal("Mask", refusal.Port);
        Assert.Equal(NodeSeverity.Error, refusal.Severity);
        Assert.False(compilation.Succeeded);
        Assert.Null(compilation.Artefact);
    }

    /// <summary>Grey into a node that resolved to colour is splatted, once, by an inserted op.</summary>
    /// <remarks>
    ///     ⚠ <b>The selectors are asserted and not just the op's presence.</b> A shuffle that copied
    ///     the grey into red alone — selectors 0, 1, 2 — is a `ChannelShuffle` in exactly the same
    ///     place producing a red image, which is a perfectly plausible picture nobody would call a
    ///     type-system bug.
    /// </remarks>
    [Fact]
    public void Grey_into_a_colour_port_splats() {
        var plan = Blended("Source/Noise", "Source/Uniform");
        var splat = Assert.Single(plan.Ops, op => op.Kernel == "ChannelShuffle");

        Assert.Equal(0f, splat.Find("sourceR")!.Value.Value);
        Assert.Equal(0f, splat.Find("sourceG")!.Value.Value);
        Assert.Equal(0f, splat.Find("sourceB")!.Value.Value);
        Assert.Equal(9f, splat.Find("sourceA")!.Value.Value);

        // Before the blend that wanted it, and reading the grey the noise wrote.
        var blend = plan.Ops.Single(op => op.Kernel == "Blend");

        Assert.True(Array.IndexOf([.. plan.Ops], splat) < Array.IndexOf([.. plan.Ops], blend));
        Assert.Contains(splat.Output, blend.Inputs);
        Assert.Equal(TextureFormat.Rgba16Float, plan.Images[splat.Output].Format);
    }

    /// <summary>Two greys blended stay one channel wide, all the way to the output.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the rule a <em>widening</em> rather than a promotion to colour.</b>
    ///     A compiler that splatted unconditionally would pass the test above and would make every
    ///     mask in every graph four times the memory it needs — silently, and only visible in a pool
    ///     total nobody reads.
    /// </remarks>
    [Fact]
    public void Grey_into_grey_stays_grey() {
        var plan = Blended("Source/Noise", "Source/Noise");

        Assert.DoesNotContain(plan.Ops, op => op.Kernel == "ChannelShuffle");
        Assert.All(plan.Images, image => Assert.Equal(TextureFormat.R16Float, image.Format));
    }

    /// <summary>One grey feeding two colour ports is splatted once.</summary>
    /// <remarks>
    ///     ⚠ <b>The only claim in this file no sabotage of the compiler could make red without also
    ///     making something else red</b>, and it is worth an assertion of its own: a promotion per port
    ///     is a *correct picture* drawn by twice the dispatches and twice the pool, which is the class
    ///     of defect that never gets reported. Both ports must still see the promoted image and not the
    ///     grey one.
    /// </remarks>
    [Fact]
    public void One_grey_feeding_two_colour_ports_is_splatted_once() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var uniform = graph.Add("Source/Uniform");
        var first = graph.Add("Colour/Blend");
        var second = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        graph.Connect(new(uniform.Id, "Out"), new(first.Id, "Background"));
        graph.Connect(new(noise.Id, "Out"), new(first.Id, "Foreground"));
        graph.Connect(new(first.Id, "Out"), new(second.Id, "Background"));
        graph.Connect(new(noise.Id, "Out"), new(second.Id, "Foreground"));
        graph.Connect(new(second.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;
        var splat = Assert.Single(plan.Ops, op => op.Kernel == "ChannelShuffle");
        var blends = plan.Ops.Where(op => op.Kernel == "Blend").ToArray();

        Assert.Equal(2, blends.Length);
        Assert.All(blends, blend => Assert.Contains(splat.Output, blend.Inputs));
        Assert.DoesNotContain(blends, blend => blend.Inputs.Contains(splat.Inputs[0]));
    }

    /// <summary>Colour into a colour port is left alone: nothing to promote.</summary>
    [Fact]
    public void Colour_into_colour_inserts_nothing() {
        var plan = Blended("Source/Uniform", "Source/Uniform");

        Assert.DoesNotContain(plan.Ops, op => op.Kernel == "ChannelShuffle");
        Assert.All(plan.Images, image => Assert.Equal(TextureFormat.Rgba16Float, image.Format));
    }

    static TexturePlan Blended(string background, string foreground) {
        NodeGraphModel graph = new();
        var under = graph.Add(background);
        var over = graph.Add(foreground);
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        blend.SetText("Mode", "Multiply");
        graph.Connect(new(under.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(over.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    /// <summary>A node whose output has a shape of its own does not follow its input.</summary>
    /// <remarks>
    ///     <c>Worley</c> writes F1, F2 and a cell index into three channels, so a Noise node is grey
    ///     for three of its four bases and colour for the fourth. Calling it grey would throw two
    ///     thirds of the answer away at the first thing that read it.
    /// </remarks>
    [Theory]
    [InlineData("Value", TextureFormat.R16Float)]
    [InlineData("Gradient", TextureFormat.R16Float)]
    [InlineData("White", TextureFormat.R16Float)]
    [InlineData("Worley", TextureFormat.Rgba16Float)]
    public void A_noise_basis_decides_the_image_it_writes(string basis, TextureFormat format) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        noise.SetText("Basis", basis);
        graph.Connect(new(noise.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Value;

        Assert.Equal(format, plan.Images[plan.Outputs[0]].Format);
    }

    /// <summary>A setting nothing recognises is refused rather than falling back to the first member.</summary>
    [Fact]
    public void A_setting_no_member_matches_is_refused() {
        NodeGraphModel graph = new();
        var under = graph.Add("Source/Uniform");
        var over = graph.Add("Source/Uniform");
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        blend.SetText("Mode", "mulitply");
        graph.Connect(new(under.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(over.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);
        var refusal = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0010");

        Assert.Equal(blend.Id, refusal.Node);
        Assert.Equal("Mode", refusal.Port);
        Assert.Contains("Multiply", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A graph with no Output node computes nothing anybody can look at, and is told so.</summary>
    [Fact]
    public void A_graph_with_no_output_is_refused() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var levels = graph.Add("Colour/Levels");

        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));

        var compilation = Compiler().Compile(graph);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0005");
        Assert.Null(compilation.Artefact);
    }

    /// <summary>Two Output nodes cannot both be the roughness map.</summary>
    [Fact]
    public void Two_outputs_under_one_usage_is_refused() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var first = graph.Add("Output/Output");
        var second = graph.Add("Output/Output");

        first.SetText("Usage", "height");
        second.SetText("Usage", "height");
        graph.Connect(new(noise.Id, "Out"), new(first.Id, "Input"));
        graph.Connect(new(noise.Id, "Out"), new(second.Id, "Input"));

        var refusal = Assert.Single(Compiler().Compile(graph).Diagnostics, diagnostic => diagnostic.Id == "TG0006");

        Assert.Equal(second.Id, refusal.Node);
    }

    /// <summary>An unwired image input is reported against the port that wanted one.</summary>
    [Fact]
    public void An_unconnected_image_input_is_refused() {
        NodeGraphModel graph = new();
        var blur = graph.Add("Filters/Blur");
        var output = graph.Add("Output/Output");

        graph.Connect(new(blur.Id, "Out"), new(output.Id, "Input"));

        var refusal = Assert.Single(Compiler().Compile(graph).Diagnostics, diagnostic => diagnostic.Id == "TG0002");

        Assert.Equal(blur.Id, refusal.Node);
        Assert.Equal("Input", refusal.Port);
    }

    /// <summary>A distance node is one jump-flood dispatch per halving, plus the read.</summary>
    /// <remarks>
    ///     ⚠ <b>The one node whose op count depends on the resolution the graph is <em>baked</em>
    ///     at</b>, which is why the compiler has to know that number rather than only the plan.
    /// </remarks>
    [Theory]
    [InlineData(256, 0, 8)]
    [InlineData(256, -1, 9)]
    [InlineData(256, 2, 6)]
    public void A_distance_node_is_one_pass_per_halving(int authored, int bake, int passes) {
        var plan = Measured(authored, bake);

        Assert.Equal(passes, plan.Ops.Count(op => op.Kernel == "JumpFlood"));
        Assert.Single(plan.Ops, op => op.Kernel == "Distance");
    }

    /// <summary>The size a node was told is the size the plan reports for a level-0 image.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument for a duplicated piece of arithmetic.</b>
    ///     <c>TextureGraphCompiler.Extent</c> is a second copy of <c>TexturePlan</c>'s own clamping,
    ///     because a node that needs the bake's resolution needs it <em>while</em> the plan is being
    ///     built and there is no plan to ask. This is what stops the two from drifting: the pass count
    ///     above is <c>log2</c> of the number the compiler used, so a compiler that computed a
    ///     different size from the plan would produce a chain of the wrong length here.
    /// </remarks>
    [Theory]
    [InlineData(256, 0)]
    [InlineData(256, -2)]
    [InlineData(256, 3)]
    public void The_size_a_node_was_told_is_the_size_the_plan_reports(int authored, int bake) {
        var plan = Measured(authored, bake);
        var size = plan.SizeOf(plan.Outputs[0]);

        Assert.Equal(TextureAnalysis.FloodDispatches(size.X, size.Y), plan.Ops.Count(op => op.Kernel == "JumpFlood"));
    }

    static TexturePlan Measured(int authored, int bake) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var distance = graph.Add("Analysis/Distance");
        var output = graph.Add("Output/Output");

        output.SetText("Usage", "mask");
        graph.Connect(new(noise.Id, "Out"), new(distance.Id, "Mask"));
        graph.Connect(new(distance.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler(authored, authored, bake).Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    /// <summary>A distance a half-float cannot record exactly is refused, against the node.</summary>
    [Fact]
    public void A_distance_past_what_a_half_float_records_is_refused_against_the_node() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var distance = graph.Add("Analysis/Distance");
        var output = graph.Add("Output/Output");

        distance.SetValue("Max Distance", 1f);
        graph.Connect(new(noise.Id, "Out"), new(distance.Id, "Mask"));
        graph.Connect(new(distance.Id, "Out"), new(output.Id, "Input"));

        // 4096 texels of field, and a jump flood's record is exact on the integers only to 2048.
        var refusal = Assert.Single(
            Compiler(4096, 4096).Compile(graph).Diagnostics,
            diagnostic => diagnostic.Id == "TG0011"
        );

        Assert.Equal(distance.Id, refusal.Node);
        Assert.Equal("Max Distance", refusal.Port);
        Assert.Contains("2048", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The plan a graph compiles to holds together on its own terms.</summary>
    [Fact]
    public void Every_plan_this_file_builds_validates() {
        var plan = Everything();

        Assert.Empty(plan.Validate());
    }

    /// <summary>Compiling the same graph twice produces the same plan.</summary>
    /// <remarks>
    ///     What a golden needs, and what makes a diff between two saved versions of a graph mean
    ///     something. The image indices come from the walk's order rather than from a counter that
    ///     survives a compilation, which is the thing this would catch.
    /// </remarks>
    [Fact]
    public void Compiling_the_same_graph_twice_produces_the_same_plan() {
        NodeGraphModel graph = new();

        Build(graph);

        var compiler = Compiler();
        var first = compiler.Compile(graph).Value;
        var second = compiler.Compile(graph).Value;

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(compiler.Outputs, compiler.Outputs);
    }

    /// <summary>The pool threads a chain through fewer textures than it has images.</summary>
    /// <remarks>
    ///     ⚠ <b>An assertion about the compiler, not about the pool.</b> <c>TexturePoolSchedule</c>
    ///     reads liveness off the op order and can only do that because an image is written exactly
    ///     once — so a compiler that reused an index, or emitted its ops out of dependency order,
    ///     would show up here as a schedule that keeps everything alive.
    /// </remarks>
    [Fact]
    public void A_compiled_chain_pools_down_to_a_few_textures() {
        NodeGraphModel graph = new();
        var previous = graph.Add("Source/Noise");

        for (var stage = 0; stage < 12; stage++) {
            var levels = graph.Add("Colour/Levels");

            graph.Connect(new(previous.Id, "Out"), new(levels.Id, "Input"));
            previous = levels;
        }

        var output = graph.Add("Output/Output");

        graph.Connect(new(previous.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Value;
        var schedule = TexturePoolSchedule.For(plan);

        Assert.Equal(13, plan.Images.Length);
        Assert.Equal(2, schedule.Allocations);
    }

    /// <summary>
    ///     Every op a node emits carries exactly the parameters its kernel declares, and every
    ///     parameter its kernel declares is one the op carries.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both directions, because they fail differently</b> — the rule the kernel suites
    ///         already keep for the hand-written builders. A member the op omits is an exception at
    ///         bake time, in a background task, with a message about a uniform; a parameter the kernel
    ///         does not declare is <em>silently dropped</em> and the picture is drawn with a default.
    ///         The second is the one that produces a plausible picture, and it is exactly what a node
    ///         that spelled <c>maxRadius</c> where the kernel says <c>radius</c> would do.
    ///     </para>
    ///     <para>
    ///         The graph is the one below, which uses all eight node types — so a ninth node with a
    ///         misspelled parameter is caught by being added to <see cref="Build" /> rather than by
    ///         somebody remembering to add a case here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_op_a_node_emits_carries_exactly_its_kernel_parameters() {
        var plan = Everything();

        Assert.NotEmpty(plan.Ops);

        foreach (var kernel in plan.Ops.Select(op => op.Kernel).Distinct(StringComparer.Ordinal)) {
            var data = Compile(kernel);

            var declared = data.Parameters
                .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
                .Select(member => Unqualified(member.Name, data.ShaderName))
                // `seed` is the one member the evaluator fills itself, from `TexturePlan.SeedFor`.
                .Where(name => !string.Equals(name, "seed", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            foreach (var op in plan.Ops.Where(candidate => candidate.Kernel == kernel)) {
                Assert.Equal(declared, op.Parameters.Select(parameter => parameter.Name).Order(StringComparer.Ordinal).ToArray());
            }
        }
    }

    /// <summary>Every op reads as many images as its kernel declares textures.</summary>
    /// <remarks>
    ///     The other half of the binding contract: the evaluator binds an op's inputs positionally
    ///     over the kernel's sampled textures, so a node that named one input where the kernel has two
    ///     leaves a descriptor unwritten — which is a validation error at best and whatever was in the
    ///     set at worst.
    /// </remarks>
    [Fact]
    public void Every_op_a_node_emits_reads_as_many_images_as_its_kernel_binds() {
        var plan = Everything();

        foreach (var kernel in plan.Ops.Select(op => op.Kernel).Distinct(StringComparer.Ordinal)) {
            var textures = Compile(kernel).Bindings
                .Count(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture });

            foreach (var op in plan.Ops.Where(candidate => candidate.Kernel == kernel)) {
                Assert.Equal(textures, op.Inputs.Length);
            }
        }
    }

    /// <summary>One graph using every node this batch declares.</summary>
    static TexturePlan Everything() {
        NodeGraphModel graph = new();

        Build(graph);

        var compilation = Compiler().Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    /// <summary>Wires all eight node types into one graph.</summary>
    static void Build(NodeGraphModel graph) {
        var noise = graph.Add("Source/Noise");
        var uniform = graph.Add("Source/Uniform");
        var levels = graph.Add("Colour/Levels");
        var blur = graph.Add("Filters/Blur");
        var transform = graph.Add("Space/Transform 2D");
        var blend = graph.Add("Colour/Blend");
        var distance = graph.Add("Analysis/Distance");
        var colour = graph.Add("Output/Output");
        var mask = graph.Add("Output/Output");

        blend.SetText("Mode", "Overlay");
        distance.SetText("Mode", "Both");
        colour.SetText("Usage", "baseColor");
        mask.SetText("Usage", "mask");

        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(distance.Id, "Mask"));
        graph.Connect(new(uniform.Id, "Out"), new(transform.Id, "Input"));
        graph.Connect(new(transform.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(blur.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(colour.Id, "Input"));
        graph.Connect(new(distance.Id, "Out"), new(mask.Id, "Input"));
    }

    /// <summary>A plan as text, so two of them compare as one value.</summary>
    static string Describe(TexturePlan plan) =>
        string.Join(
            "\n",
            plan.Images.Select((image, index) => $"image {index}: {image.Format} {image.LevelOffset}")
                .Concat(
                    plan.Ops.Select((op, index) =>
                        $"op {index}: {op.Kernel} -> {op.Output} <- [{string.Join(", ", op.Inputs)}] "
                        + $"{{{string.Join(", ", op.Parameters.Select(parameter => $"{parameter.Name}={parameter.Value}:{parameter.Unit}"))}}}")
                )
                .Append($"outputs: {string.Join(", ", plan.Outputs)}")
        );

    static readonly Dictionary<string, EffectData> Compiled = new(StringComparer.Ordinal);

    /// <summary>One kernel through the real Raven front end, with no device.</summary>
    static EffectData Compile(string kernel) {
        lock (Compiled) {
            if (Compiled.TryGetValue(kernel, out var cached)) {
                return cached;
            }

            var name = TextureKernels.VariantName(kernel, TextureFormat.Rgba16Float);
            var source = TextureKernels.Variant(kernel, TextureFormat.Rgba16Float);
            var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel));

            Assert.NotNull(data);

            Compiled[kernel] = data;

            return data;
        }
    }

    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;
}
