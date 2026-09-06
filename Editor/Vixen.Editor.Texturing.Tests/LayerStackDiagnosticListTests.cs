// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The diagnostic half of the panel's message list, over diagnostics a stack really raises.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> that had
///         no test with a reachable production input.</b> The one test of the new list feeds a
///         <c>Wobble</c> setting the filter node does not declare — which <c>LayerStackGraph</c>
///         catches itself and raises as a <c>LayerStackProblem.Warning</c>, the <em>other</em> loop.
///         Nothing exercised the <c>NodeDiagnostic</c> loop against anything a stack can express, so
///         the branch that reads <c>picture.Diagnostics</c> was covered only by a hand-built record.
///     </para>
///     <para>
///         <b>A mask effect is the reachable route.</b> <c>LayerStackGraph.Effect</c> writes an
///         effect's <c>Texts</c> straight onto the node whenever the type <em>declares</em> that
///         setting — so <c>Space/Crop</c> with <c>Filter = Box</c> is a stack an artist can author,
///         a setting the node accepts as a name and the compiler refuses as a value. It arrives here
///         as <c>TG0010</c> from the texture-graph compiler, which is the loop under test.
///     </para>
///     <para>
///         ⚠ <b>And it is what refutes #870's proposed edit.</b> Two layers carrying that one mistake
///         raise <em>fourteen</em> diagnostics — seven per layer, one per channel the set writes —
///         from fourteen distinct node ids with character-identical messages. Rendering
///         <c>NodeDiagnostic.Node</c> on the line, which is what #870 asked for, would print fourteen
///         lines for two mistakes and undo
///         <a href="https://github.com/Rikarin/Vixen/issues/842">#842</a>.
///     </para>
///     <para>
///         ⚠ <b>What the line names instead is the <em>layer</em>, and that is what closes both</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a>.
///         <c>LayerStackCompilation.Layers</c> says which layer emitted each node, so the fourteen
///         collapse to two lines rather than to one: seven per layer share a rendered line and the
///         two layers do not. ⚠ <b>Both halves have to be asserted together</b>, because either one
///         alone is satisfied by a defect — a renderer that named nothing passes the collapse and a
///         renderer that named the node passes the separation.
///     </para>
/// </remarks>
public class LayerStackDiagnosticListTests {
    /// <summary>A setting the compiler refuses reaches the panel's list from a stack an artist wrote.</summary>
    [Fact]
    public void A_compiler_diagnostic_from_a_mask_effect_is_listed() {
        var compilation = Compiled("only");
        var diagnostic = Assert.Single(new HashSet<string>(compilation.Diagnostics.Select(one => one.Id)));

        Assert.Equal("TG0010", diagnostic);

        var line = Assert.Single(Describe(compilation));

        Assert.StartsWith("Error — layer 'only' TG0010:", line, StringComparison.Ordinal);
        Assert.Contains("Crop", line, StringComparison.Ordinal);
    }

    /// <summary>⚠ A channel's own two nodes belong to no layer, and every other node belongs to one.</summary>
    /// <remarks>
    ///     <b>The instrument check for the map, and it is an equality rather than a spot check.</b> A
    ///     builder that filed every node it made under whichever layer ran last would pass every
    ///     assertion above — the lines would still name a layer and still collapse — while putting a
    ///     channel's <c>Output</c> under a layer an artist could select and find nothing wrong with.
    ///     Each channel contributes exactly two nodes of its own, its base constant and its
    ///     <c>Output</c>, and everything else in the graph was emitted inside some layer's walk.
    ///     ⚠ Counted rather than listed by type, because <c>Source/Uniform</c> is <em>also</em> what a
    ///     constant fill and a constant mask entry compile to — a check that excused every node of
    ///     that type would excuse most of the stack.
    /// </remarks>
    [Fact]
    public void A_channels_own_two_nodes_belong_to_no_layer() {
        var compilation = Compiled("only");
        var channels = LayerStackDocument.Starter("Hull").Sets[0].Channels.Count;
        var nodes = 0;

        foreach (var node in compilation.Graph.Nodes) {
            nodes++;

            // ⚠ `TryGetValue` rather than an indexer inside the message: xunit builds an assertion's
            // message eagerly, so an indexer there throws on the passing case rather than on the
            // failing one — a test that is red for the absence of the defect it looks for.
            if (string.Equals(node.Type, "Output/Output", StringComparison.Ordinal)
                && compilation.Layers.TryGetValue(node.Id, out var filed)) {
                Assert.Fail($"A channel's Output ({node.Id}) was filed under layer '{filed}'.");
            }
        }

        Assert.True(channels > 1, "One channel, so the per-channel arithmetic below proves nothing.");
        Assert.Equal(channels * 2, nodes - compilation.Layers.Count);
    }

    /// <summary>
    ///     ⚠ One mistake on one layer is still one line, and the line says how many nodes raised it.
    /// </summary>
    /// <remarks>
    ///     The per-channel multiplicity, measured rather than assumed: the count in the line is the
    ///     number of diagnostics the compile actually produced, so a stack whose set grows a channel
    ///     moves both halves together and neither is a literal.
    /// </remarks>
    [Fact]
    public void One_layers_mistake_is_one_line_that_says_how_many_nodes_raised_it() {
        var compilation = Compiled("only");
        var line = Assert.Single(Describe(compilation));

        Assert.True(compilation.Diagnostics.Length > 1, "One diagnostic, so there is no collapse to test.");

        Assert.EndsWith(
            $"· {compilation.Diagnostics.Length} nodes in the exploded graph",
            line,
            StringComparison.Ordinal
        );
    }

    /// <summary>⚠ Two layers making the same mistake are two lines, each naming its own layer.</summary>
    /// <remarks>
    ///     <b>The defect #870 named, resolved rather than counted.</b> Before #880 these two stacks
    ///     produced the same single sentence and nothing in it distinguished one mistake from two;
    ///     the batch before this one gave that sentence a node count, which said <em>that</em> there
    ///     were two without saying <em>which</em>. Naming the layer is what makes the second stack
    ///     two lines an artist can act on, and the counts are still there — seven each, because the
    ///     per-channel multiplicity is real and did not go away.
    /// </remarks>
    [Fact]
    public void Two_layers_making_the_same_mistake_are_two_lines_that_name_their_layers() {
        Assert.Single(Describe(Compiled("first")));

        var both = Compiled("first", "second");
        var lines = Describe(both);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.StartsWith("Error — layer 'first' TG0010:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Error — layer 'second' TG0010:", StringComparison.Ordinal));

        // ⚠ And each line still stands for the whole per-channel fan-out rather than for one node:
        // measured off the compile, so a set that grows a channel moves both halves together.
        var each = both.Diagnostics.Length / 2;

        Assert.True(each > 1, "One diagnostic per layer, so the collapse half of this proves nothing.");

        foreach (var line in lines) {
            Assert.EndsWith($"· {each} nodes in the exploded graph", line, StringComparison.Ordinal);
        }
    }

    /// <summary>The lines the panel would render for one compilation.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>LayerStackPicture</c> and not through a fixture, because the compile is
    ///     pure.</b> Everything under test here is known before a device is asked for, so a test that
    ///     opened a panel would be asserting this and a graphics service at once — and would skip on
    ///     the machine that has neither.
    /// </remarks>
    static IReadOnlyList<string> Describe(LayerStackCompilation compilation) {
        LayerStackPicture picture = new(null, "baseColor", 0, 0, "") {
            Problems = compilation.Problems,
            Diagnostics = compilation.Diagnostics,
            Layers = compilation.Layers
        };

        return LayerStackView.Describe(picture);
    }

    /// <summary>A starter stack with one filter layer per name, each masked through a refused crop.</summary>
    static LayerStackCompilation Compiled(params string[] layers) {
        var stack = LayerStackDocument.Starter("Hull");

        foreach (var id in layers) {
            stack.Sets[0]
                .Layers.Add(
                    new() {
                        Id = id,
                        Name = id,
                        Kind = LayerKind.Filter,
                        Filter = LayerFilterKind.Blur,
                        Mask = new() {
                            Source = LayerMaskSource.Constant,
                            Value = 1f,

                            // ⚠ A setting `Space/Crop` declares and the kernel refuses. `Effect` drops a
                            // setting the type does not declare with a `LayerStackProblem`, which is the
                            // other loop — this one has to survive the builder to reach the compiler.
                            Effects = { new() { Node = "Space/Crop", Texts = { ["Filter"] = "Box" } } }
                        }
                    }
                );
        }

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotEmpty(compilation.Diagnostics);

        return compilation;
    }
}
