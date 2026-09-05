// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What an <c>Output</c> node means by a size, which is the graph's and not its branch's.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/805">#805</a>, and it is a decision
///         rather than a repair.</b> #779 made a node's output the size of what it reads — one rule,
///         everywhere inside a graph, and right there. At the <em>terminus</em> that rule says
///         something the rest of the tree contradicts: a texture set is one material's maps over one
///         atlas, so <c>MaterialBake</c> refuses maps of two sizes, and a graph with
///         <c>Resample(Half) → Output("baseColor")</c> beside a bare <c>Output("roughness")</c>
///         validated, baked both maps and then threw out of a background bake.
///     </para>
///     <para>
///         <b>The resolution of a map is declared twice already and neither place is a node.</b>
///         <c>TexturePlan.BaseWidth</c> is what the author asked the graph for and
///         <c>TexturePlan.BakeLevelOffset</c> is what the bake asked for on top of it. A
///         <c>Space/Resample</c> is a statement about where in the graph the <em>work</em> happens —
///         which is already how it behaves everywhere else, because <c>Rescale</c> magnifies a
///         half-resolution branch back the instant it meets a base-resolution sibling at any node.
///         The terminus is one more such meeting, and what it meets is the texture set.
///     </para>
///     <para>
///         ⚠ <b>So the image is brought back to the base and the author is <em>told</em>, which is
///         the half neither shape in #805 had.</b> Refusing would make a legal-looking graph illegal;
///         rescaling in silence would undo what the author wrote with nothing on screen. A warning
///         naming the Output node is what makes the rescale a fact the author can see and act on,
///         and a bake that draws a picture rather than throwing is what the rest of this tree does
///         everywhere.
///     </para>
/// </remarks>
public class TextureOutputLevelTests {
    const int Side = 256;

    /// <summary>Two Output nodes at two levels keep two images of the graph's one size.</summary>
    /// <remarks>
    ///     ⚠ <b>#805's own input, and the assertion is over <c>plan.Outputs</c> rather than over the
    ///     ops.</b> Before this rule the two entries measured 128² and 256², both maps baked, and the
    ///     throw came out of <c>MaterialBake.Extent</c> — a stack trace where a picture was asked
    ///     for. Asserting the op sizes would have missed it entirely: every op in that plan was the
    ///     right size for what it read.
    /// </remarks>
    [Fact]
    public void Two_outputs_on_branches_at_different_levels_are_both_the_graphs_size() {
        var compiler = Compiler();
        NodeGraphModel graph = new();

        var noise = graph.Add("Source/Noise");
        var resample = graph.Add("Space/Resample");
        var colour = graph.Add("Output/Output");
        var rough = graph.Add("Output/Output");

        colour.SetText("Usage", "baseColor");
        rough.SetText("Usage", "roughness");

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(colour.Id, "Input"));
        graph.Connect(new(noise.Id, "Out"), new(rough.Id, "Input"));

        var compilation = compiler.Compile(graph);
        var plan = compilation.Value;

        Assert.Empty(plan.Validate());
        Assert.Equal(2, plan.Outputs.Length);

        // The one assertion #805 is about: a texture set is one size, so the map computed at half
        // resolution is stored at the graph's.
        foreach (var image in plan.Outputs) {
            Assert.Equal(new(Side, Side), plan.SizeOf(image));
        }

        // ⚠ And it is the *baseColor* entry that moved rather than the graph having quietly dropped
        // the half-resolution branch: the 128² image the resample wrote is still read by an op.
        var half = Assert.Single(plan.Ops, op => op.Kernel == "Resample" && plan.SizeOf(op.Output).X == 128);

        Assert.Contains(plan.Ops, op => op.Inputs.Contains(half.Output) && plan.SizeOf(op.Output).X == Side);
    }

    /// <summary>An Output whose branch is not at the base says so, and still compiles.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes this a decision rather than a silent override.</b> A rescale
    ///     nobody is told about is #805's own objection to the shape it tried at the merge — a
    ///     terminal <c>Resample</c> would do nothing and say nothing. The warning names the node and
    ///     its <c>Usage</c> port, so the author can either accept the cost or resample back.
    /// </remarks>
    [Fact]
    public void An_output_below_the_base_is_a_warning_naming_the_node_and_not_a_refusal() {
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var output = Assert.Single(graph.Nodes, node => string.Equals(node.Type, "Output/Output", StringComparison.Ordinal));

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(output.Id, "Input"));

        var compilation = Compiler().Compile(graph);
        var caution = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0022");

        Assert.Equal(output.Id, caution.Node);
        Assert.Equal("Usage", caution.Port);
        Assert.Equal(NodeSeverity.Warning, caution.Severity);
        Assert.Contains("128", caution.Message, StringComparison.Ordinal);
        Assert.NotNull(compilation.Artefact);

        // The instrument: a graph whose terminus *is* the base says nothing, so the warning is about
        // the level the Output was handed rather than about the presence of a Resample.
        var back = graph.Add("Space/Resample");

        back.SetText("Size", "Double");
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(back.Id, "Input"));
        graph.Connect(new(back.Id, "Out"), new(output.Id, "Input"));

        Assert.Empty(Compiler().Compile(graph).Diagnostics);
    }

    /// <summary>⚠ And the terminus rescale of a magnified branch is boxed, because it goes down.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/829">#829</a>, and the aliasing was
    ///         already in the tree.</b> A level offset is signed: <c>Resample(Quadruple)</c> is level
    ///         −2, so a terminus that brings it back to zero is a <b>4:1 minification</b> — the first
    ///         one <c>Rescale</c> has ever been asked for, and it passed <c>Bilinear</c>, which reads
    ///         four texels of sixteen and drops the other twelve. <c>Resample.rvn</c>'s header names
    ///         the rule this asserts: "<c>Box</c> is the default and the only correct choice going
    ///         down".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two <c>Resample</c> ops are told apart by the size they write and not by
    ///         their order</b>, because the node's own resample and the compiler's rescale run the
    ///         same kernel: the node writes the 1024² image and the terminus writes the 256² one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_terminus_rescale_of_a_magnified_branch_is_boxed() {
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var output = Assert.Single(graph.Nodes, node => string.Equals(node.Type, "Output/Output", StringComparison.Ordinal));

        resample.SetText("Size", "Quadruple");
        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Artefact!;
        var terminus = Assert.Single(plan.Ops, op => op.Kernel == "Resample" && plan.SizeOf(op.Output).X == Side);

        // The premise: what it reads really is four times wider than what it writes.
        Assert.Equal(Side * 4, plan.SizeOf(terminus.Inputs[0]).X);
        Assert.Equal((float)TextureFilter.Box, terminus.Find("filter")!.Value.Value);
    }

    /// <summary>⚠ And the ordinary rescale, which goes up, is still bilinear.</summary>
    /// <remarks>
    ///     <b>The half that makes the filter derived rather than a second constant.</b> Every
    ///     assertion above holds of a compiler that boxed unconditionally — and a box going up
    ///     degenerates to a single sample, which is <c>Point</c> under another name and is what
    ///     <c>Resample.rvn</c> says to reach for <c>Bilinear</c> instead of. This is the same method
    ///     and the same op, in the direction the file used to claim was the only one.
    /// </remarks>
    [Fact]
    public void A_rescale_that_goes_up_is_still_bilinear() {
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var output = Assert.Single(graph.Nodes, node => string.Equals(node.Type, "Output/Output", StringComparison.Ordinal));

        resample.SetText("Size", "Half");
        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(output.Id, "Input"));

        var plan = Compiler().Compile(graph).Artefact!;
        var terminus = Assert.Single(plan.Ops, op => op.Kernel == "Resample" && plan.SizeOf(op.Output).X == Side);

        Assert.Equal(Side / 2, plan.SizeOf(terminus.Inputs[0]).X);
        Assert.Equal((float)TextureFilter.Bilinear, terminus.Find("filter")!.Value.Value);
    }

    /// <summary>An Output already at the base neither resamples nor warns.</summary>
    /// <remarks>
    ///     ⚠ <b>The predicate that could not be false without this.</b> Every other assertion here
    ///     holds just as well of a compiler that resampled every output unconditionally — which
    ///     would put a wasted dispatch and a whole extra texture into every plan the editor ever
    ///     compiles, and no test above would notice.
    /// </remarks>
    [Fact]
    public void An_output_already_at_the_base_costs_nothing() {
        var compiler = Compiler();
        var plan = Compiler().Compile(Graph("Source/Noise", out _)).Value;

        Assert.Empty(compiler.Compile(Graph("Source/Noise", out _)).Diagnostics);
        Assert.DoesNotContain(plan.Ops, op => op.Kernel == "Resample");
        Assert.Equal(new(Side, Side), plan.SizeOf(Assert.Single(plan.Outputs)));
    }

    /// <summary>The rescale tracks the bake rather than the file, so a 4K bake keeps 4K maps.</summary>
    /// <remarks>
    ///     ⚠ <b>Level zero and not <c>BaseWidth</c>, and the difference is a whole bake.</b> A
    ///     terminus pinned to the authoring width would hand a 4K bake a 1K map and put
    ///     <c>MaterialBake</c>'s throw straight back, one level further out — which is exactly the
    ///     shape of the defect this file exists to close.
    /// </remarks>
    [Fact]
    public void A_bake_above_the_authoring_resolution_keeps_its_outputs_at_the_bakes_size() {
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var output = Assert.Single(graph.Nodes, node => string.Equals(node.Type, "Output/Output", StringComparison.Ordinal));

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(Registry()) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 5150,
            BakeLevelOffset = -2
        };

        var plan = compiler.Compile(graph).Artefact!;

        Assert.NotNull(plan);
        Assert.Equal(new(Side * 4, Side * 4), plan.SizeOf(Assert.Single(plan.Outputs)));
    }

    /// <summary>A node that both reads an image and bakes a table never resamples the table.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one shape that could reach <c>Rescale</c>'s external short-circuit, which is
    ///         why this test replaces it.</b> That branch read as a design rule — "an external is
    ///         never rescaled" — and was a guard nothing could execute:
    ///         <c>TextureGraphCompiler.External</c> hands an index straight back to the node and
    ///         never puts it in <c>imageOf</c>, and <c>Rescale</c> is only ever reached from
    ///         <c>Upstream</c>, which reads nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unreachable branch is a claim, and a claim belongs in a test rather than in
    ///         code that cannot run.</b> <c>Colour/Gradient Map</c> is the shape that comes closest:
    ///         it reads an image port <em>and</em> bakes a 256×1 ramp, so downstream of a resample it
    ///         is a node at level 1 holding an image at level 0. The ramp is a dispatch input rather
    ///         than a port, so it never passes through <c>Read</c> at all — and if that ever changed,
    ///         the plan would hold a resample writing an image nothing may write and
    ///         <c>Validate</c> would say so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_table_baked_by_a_node_below_the_base_is_read_at_its_own_size() {
        var compiler = Compiler();
        var graph = Graph("Source/Noise", out var noise);
        var resample = graph.Add("Space/Resample");
        var map = graph.Add("Colour/Gradient Map");
        var output = Assert.Single(graph.Nodes, node => string.Equals(node.Type, "Output/Output", StringComparison.Ordinal));

        graph.Connect(new(noise.Id, "Out"), new(resample.Id, "Input"));
        graph.Connect(new(resample.Id, "Out"), new(map.Id, "Input"));
        graph.Disconnect(new(output.Id, "Input"));
        graph.Connect(new(map.Id, "Out"), new(output.Id, "Input"));

        var plan = compiler.Compile(graph).Artefact!;
        var ramp = Assert.Single(compiler.Externals).Image;
        var table = Assert.Single(plan.Ops, op => op.Kernel == "GradientMap");

        Assert.Empty(plan.Validate());

        // The node sits a level below the base and the table it reads does not, which is the whole
        // premise: were the compiler to bring an external to a node's level, it would be here.
        Assert.Equal(128, plan.SizeOf(table.Output).X);
        Assert.Contains(ramp, table.Inputs);
        Assert.DoesNotContain(plan.Ops, op => op.Inputs.Contains(ramp) && op.Kernel == "Resample");
        Assert.DoesNotContain(plan.Ops, op => op.Output == ramp);
    }

    /// <summary>A graph with one node of a type, wired to an Output, and the node it made.</summary>
    static NodeGraphModel Graph(string type, out GraphNode node) {
        NodeGraphModel graph = new();

        node = graph.Add(type);

        var output = graph.Add("Output/Output");

        output.SetText("Usage", "baseColor");
        graph.Connect(new(node.Id, "Out"), new(output.Id, "Input"));

        return graph;
    }

    static TextureGraphCompiler Compiler() =>
        new(Registry()) { BaseWidth = Side, BaseHeight = Side, Seed = 5150 };

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }
}
