// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Explode: the real graph, one-way, and the plan it compiles to.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 exit criterion 6, in the half that needs no device.</b> "A stack and its
///         explosion are byte-identical" is measured on a GPU by
///         <c>LayerStackBakeDeviceTests</c>; what is here is the sharper instrument and the one that
///         always runs — the two <c>TexturePlan</c>s compared op by op, which catches an inserted or
///         reordered op that identical pixels would hide.
///     </para>
///     <para>
///         ⚠ <b>Both halves go through one compiler on purpose, and that is what the differential is
///         and is not.</b> § D1 says the stack must not get an evaluator of its own, so
///         <c>LayerStackGraph</c> builds a graph and <c>TextureGraphCompiler</c> compiles it — there
///         are not two compilers here to disagree. What the test therefore measures is the
///         <em>round trip and the decoration</em>: whether the comments, the layout and the YAML
///         write-and-read leave the compilation alone. That is a real failure mode with real
///         instances — a setting the writer drops, a node order the loader does not keep — and it is
///         where the two paths actually differ. It is not a comparison of two independent emitters,
///         and this remark is here so nobody reads it as one.
///     </para>
/// </remarks>
public class LayerStackExplodeTests {
    /// <summary>The differential: the stack's plan and its explosion's plan are the same plan.</summary>
    [Fact]
    public void A_stack_and_its_explosion_compile_to_the_same_plan() {
        var stack = LayerStackDifferential.Stack();
        var (direct, exploded) = LayerStackDifferential.Both(stack);

        LayerStackDifferential.AssertSamePlan(direct.Plan!, exploded.Plan!);

        // And the same map is the same image, which the plan comparison alone does not say: two
        // plans with identical op lists could still name different images as their outputs.
        foreach (var output in direct.Outputs) {
            Assert.Equal(output.Image, LayerStackDifferential.ImageOf(exploded, output.Usage));
        }
    }

    /// <summary>The stack under test is big enough for an ordering mistake to show.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, checked before the measurement.</b> The differential above compares
    ///     two op lists; on a stack that compiled to three ops it would be green whatever the
    ///     explosion did with the fourth. This is the assertion that says the list is long enough to
    ///     have an order — and it is written as a floor rather than an equality so that a slice
    ///     adding a layer kind does not have to come back and edit a number.
    /// </remarks>
    [Fact]
    public void The_differential_is_measured_on_a_plan_with_an_order() {
        var stack = LayerStackDifferential.Stack();
        var (direct, _) = LayerStackDifferential.Both(stack);

        Assert.True(
            direct.Plan!.Ops.Length >= 20,
            $"the differential stack compiles to {direct.Plan.Ops.Length} ops, which is not enough for an "
            + "insertion or a reorder to be visible; the stack in LayerStackDifferential has shrunk"
        );

        Assert.Equal(3, direct.Plan.Outputs.Length);
    }

    /// <summary>⚠ The differential's mask reaches the compiler rather than folding into an opacity.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/895">#895</a>, and it is an instrument
    ///     check rather than a feature.</b> The fixture's summary said it contained a mask and so did
    ///     <c>docs/overview.md</c>; <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>'s
    ///     fold had quietly made both false, because a bare constant mask inside the unit interval
    ///     compiles to no ops at all. The mask now has two entries, and this is what says so — read
    ///     off the builder's own notes, which are where the fold announces itself, rather than off a
    ///     node count that a change anywhere else in the stack would move.
    /// </remarks>
    [Fact]
    public void The_differentials_mask_is_compiled_and_not_folded() {
        var stack = LayerStackDifferential.Stack();
        var build = LayerStackGraph.Build(stack, stack.Sets[0]);
        var masked = 0;

        foreach (var note in build.Notes) {
            Assert.DoesNotContain("folded into the opacity", note.Text, StringComparison.Ordinal);

            if (note.Text.Contains("mask", StringComparison.Ordinal)) {
                masked++;
            }
        }

        Assert.True(masked > 0, "No layer in the differential stack carries a mask at all.");
    }

    /// <summary>An exploded graph carries the header and a comment per composite.</summary>
    [Fact]
    public void An_exploded_graph_says_it_is_one_way_and_names_its_layers() {
        var stack = LayerStackDifferential.Stack();
        var exploded = LayerStackExplode.Explode(stack, stack.Sets[0]);
        var texts = exploded.Graph.Comments.ConvertAll(comment => comment.Text);

        Assert.Contains(LayerStackExplode.ExplodedHeader, texts);
        Assert.Contains(texts, text => text.Contains("Layer 'Grime colour'", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("Constant mask", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("Channel 'roughness'", StringComparison.Ordinal));
    }

    /// <summary>The decoration is comments, and comments only.</summary>
    /// <remarks>
    ///     ⚠ <b>The property the differential rests on, asserted rather than assumed.</b> An
    ///     explosion that inserted a node would move every op index, and <c>TexturePlan.SeedFor</c>
    ///     mixes the op's index into its seed — so a noise several layers up would draw a different
    ///     picture. This says the decoration touched no node and no wire; the differential says the
    ///     compilation agrees. Either one alone leaves a hole.
    /// </remarks>
    [Fact]
    public void Exploding_adds_no_node_and_no_wire() {
        var stack = LayerStackDifferential.Stack();
        var plain = LayerStackGraph.Build(stack, stack.Sets[0]);
        var exploded = LayerStackExplode.Explode(stack, stack.Sets[0]);

        Assert.Equal(plain.Graph.Nodes.Count, exploded.Graph.Nodes.Count);
        Assert.Equal(plain.Graph.Edges.Count, exploded.Graph.Edges.Count);
        Assert.Empty(plain.Graph.Comments);
        Assert.NotEmpty(exploded.Graph.Comments);
    }

    /// <summary>Two builds of one stack are the same graph, node for node and wire for wire.</summary>
    /// <remarks>
    ///     ⚠ <b>Node <em>order</em> and not just count.</b> <c>NodeGraphModel.Ordered</c> breaks ties
    ///     by insertion order — its own comment says a golden source test needs that — and a plan's
    ///     op indices come out of that order, so a builder that enumerated a dictionary somewhere
    ///     would give two builds two different seeds without changing a single wire.
    /// </remarks>
    [Fact]
    public void Building_one_stack_twice_gives_one_graph() {
        var stack = LayerStackDifferential.Stack();
        var first = LayerStackGraph.Build(stack, stack.Sets[0]);
        var second = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Equal(Order(first), Order(second));
        Assert.Equal(Wires(first), Wires(second));
    }

    /// <summary>The written file is the graph, and it opens as a <c>.vxtexgraph</c> would.</summary>
    [Fact]
    public void Exploding_writes_a_graph_beside_the_stack_and_leaves_the_stack_alone() {
        var stack = LayerStackDifferential.Stack();
        var directory = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try {
            var path = Path.Combine(directory, "Differential" + LayerStackExplode.Extension);

            LayerStackExplode.Write(stack, stack.Sets[0], path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"), "the temporary the write moves from was left behind");

            var text = File.ReadAllText(path);

            Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);

            var reloaded = LayerStackExplode.Read(text, out var diagnostics);

            Assert.Empty(diagnostics);
            Assert.Contains(reloaded.Nodes, node => node.Type == "Output/Output");
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    static string Order(LayerStackBuild build) =>
        string.Join("\n", build.Graph.Nodes.Select(node => $"{node.Id.Value} {node.Type}"));

    static string Wires(LayerStackBuild build) =>
        string.Join("\n", build.Graph.Edges.Select(edge => $"{edge.From} -> {edge.To}"));
}
