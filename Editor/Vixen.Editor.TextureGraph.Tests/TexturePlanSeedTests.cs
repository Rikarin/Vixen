// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What <c>TexturePlan.SeedFor</c> is stable under, and how that is measured.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="TexturePlan.Seed" />'s own remarks once claimed a property the code did not
///         have, and <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a> was the finding.</b>
///         They said <c>SeedFor</c> "mixes the plan's seed with the op's own identity"; it mixed the
///         op's <em>index in <c>Ops</c></em>, which is the number an insertion moves. An op appearing
///         ahead of a seeded one redrew it, and
///         <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a> put three ops per masked
///         layer per channel ahead of everything with nothing anywhere reporting the pixel change.
///     </para>
///     <para>
///         <b>An op now carries <see cref="TextureOp.Identity" />, which
///         <c>TextureGraphCompiler</c> derives from the <c>NodeId</c> that emitted it</b> — plus its
///         ordinal within that node, since a reduction chain is one node and many ops. A node id is
///         written in the <c>.vxtexgraph</c> and <c>NodeGraphModel</c> never reuses one.
///     </para>
///     <para>
///         ⚠ <b>The insertion has to be one that <em>moves</em> the seeded op's index, and most do
///         not.</b> <c>NodeGraphModel.Ordered</c> is Kahn's algorithm with its queue seeded in
///         insertion order, so a node added to an open graph is always enqueued behind the nodes
///         already ready — adding a second source beside a <c>Source/Noise</c> leaves the noise at op
///         0, before this change as much as after it. A test written that way is green against the
///         defect and proves nothing. What moves a seeded op is a new node that becomes ready
///         <em>before</em> it: the graph below dithers in a <c>Colour/Levels</c> downstream of a
///         blend, and a second source feeding that blend is emitted ahead of it.
///     </para>
///     <para>
///         ⚠ <b>Nothing here can speak for the layer stack, which is the front end #875 was filed
///         about.</b> That model is rebuilt from the <c>.vxlayers</c> on every compile, so it names
///         its own nodes rather than taking the counter's —
///         <c>LayerStackGraph.Named</c>, asserted by
///         <c>LayerStackCompileTests.A_layers_seed_survives_a_layer_inserted_beneath_it</c> in the
///         assembly that can reference it.
///     </para>
///     <para>
///         ⚠ <b>The pin is the instrument, and what it is for is the report rather than the value.</b>
///         A constant that has to be re-blessed is what turns a silent pixel change into a line in a
///         commit message.
///     </para>
/// </remarks>
public class TexturePlanSeedTests {
    const int Side = 128;

    /// <summary>The plan's own seed, so nothing here depends on a default moving.</summary>
    const uint Seed = 5150u;

    /// <summary>What the bare noise of <see cref="Bare" /> draws from, measured 2026-09-06.</summary>
    /// <remarks>
    ///     ⚠ It moved when #875 landed, and it had to: the mix is over the emitting node's identity
    ///     now rather than over the op's index, so every existing material with a noise in it bakes
    ///     different pixels once.
    /// </remarks>
    const uint Pinned = 776312431u;

    /// <summary>⚠ The seed of a bare noise, pinned — so a change to it is reported rather than shipped.</summary>
    /// <remarks>
    ///     <b>Deliberately the smallest graph that has a seeded op in it</b>, so this goes red for a
    ///     change to how the compiler names ops and not for a change to any one node. If it is red
    ///     after a change you meant to make: the pixels of every existing material with a noise in it
    ///     have moved, which is allowed and is not silent — re-bless the number and say so.
    /// </remarks>
    [Fact]
    public void The_seed_of_a_bare_noise_is_pinned() {
        var plan = Bare();
        var index = IndexOf(plan, "Noise");

        Assert.True(
            plan.SeedFor(index) == Pinned,
            $"The bare noise's seed is {plan.SeedFor(index)} and this pinned it at {Pinned}. That is not a "
            + "defect in whatever changed: it means the name the compiler gives an op has moved, and every "
            + "existing material with a noise in it now bakes different pixels. Re-bless the number and say "
            + "so in the commit."
        );

        // And the pin is a fact about the mix rather than about the plan's own seed, which a caller
        // may set: the same op under another plan seed is another number. That is what a "reseed this
        // material" gesture means, and an identity that swallowed the plan's seed would lose it.
        TexturePlan other = new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = plan.Images,
            Ops = plan.Ops,
            Seed = Seed + 1
        };

        Assert.NotEqual(plan.SeedFor(index), other.SeedFor(index));
    }

    /// <summary>⚠ A dither keeps its numbers when a node is wired in ahead of it.</summary>
    /// <remarks>
    ///     <b>#875, stated as the property rather than as the defect, on a graph edited in place.</b>
    ///     The second source and the blend are added to the model the <c>Levels</c> node is already
    ///     in; the <c>Levels</c> node itself is untouched and keeps its id. Its op index moves — which
    ///     is asserted first, because an insertion that left it alone would satisfy the equality
    ///     against the old arithmetic too — and its seed does not.
    /// </remarks>
    [Fact]
    public void A_dither_keeps_its_seed_when_an_op_is_emitted_ahead_of_it() {
        NodeGraphModel graph = new();
        var checker = graph.Add("Source/Checker");
        var levels = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        levels.SetValue("Dither", 1f);
        graph.Connect(new(checker.Id, "Out"), new(levels.Id, "Input"));
        graph.Connect(new(levels.Id, "Out"), new(output.Id, "Input"));

        var before = Compile(graph);
        var first = IndexOf(before, "Levels");

        // The edit: a second source, composited under what the Levels node was already reading. Its
        // op is emitted before the Levels op because both sources start ready and the blend is what
        // the Levels node now waits on.
        var second = graph.Add("Source/Noise");
        var blend = graph.Add("Colour/Blend");

        graph.Disconnect(new(levels.Id, "Input"));
        graph.Connect(new(checker.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(second.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(levels.Id, "Input"));

        var after = Compile(graph);
        var moved = IndexOf(after, "Levels");

        Assert.NotEqual(first, moved);
        Assert.Equal(before.SeedFor(first), after.SeedFor(moved));
    }

    /// <summary>⚠ Two noises in one graph still draw different pictures.</summary>
    /// <remarks>
    ///     <b>The property the per-op seed exists for, and the one an identity could quietly lose.</b>
    ///     A mix that folded the node away — or a compiler that named every op the same thing — would
    ///     satisfy every stability assertion above and make two noises side by side one picture, which
    ///     is <see cref="TexturePlan.Seed" />'s own stated reason for not being per plan.
    /// </remarks>
    [Fact]
    public void Two_noises_in_one_graph_draw_different_seeds() {
        NodeGraphModel graph = new();
        var first = graph.Add("Source/Noise");
        var second = graph.Add("Source/Noise");
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        graph.Connect(new(first.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(second.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        var plan = Compile(graph);
        HashSet<uint> seeds = [];
        var noises = 0;

        for (var op = 0; op < plan.Ops.Length; op++) {
            if (!string.Equals(plan.Ops[op].Kernel, "Noise", StringComparison.Ordinal)) {
                continue;
            }

            noises++;

            Assert.True(seeds.Add(plan.SeedFor(op)), "Two Noise ops draw the same seed, so they draw one picture.");
        }

        Assert.Equal(2, noises);
    }

    /// <summary>⚠ One node emitting several ops names each of them, and no two the same.</summary>
    /// <remarks>
    ///     <b>What the ordinal in the identity is for.</b> An <c>Colour/Auto Levels</c> node is a
    ///     reduction chain — one dispatch per level down to 1×1 — and every one of them is a
    ///     different op of the same node. An identity that was the node alone would give the whole
    ///     chain one seed; nothing in that chain is seeded today, which is precisely why this is
    ///     asserted here rather than left to be noticed when something is.
    /// </remarks>
    [Fact]
    public void One_node_emitting_several_ops_names_each_of_them() {
        NodeGraphModel graph = new();
        var checker = graph.Add("Source/Checker");
        var auto = graph.Add("Colour/Auto Levels");
        var output = graph.Add("Output/Output");

        graph.Connect(new(checker.Id, "Out"), new(auto.Id, "Input"));
        graph.Connect(new(auto.Id, "Out"), new(output.Id, "Input"));

        var plan = Compile(graph);
        HashSet<uint?> names = [];

        Assert.True(plan.Ops.Length > 3, "Too few ops for a node to have emitted several.");

        foreach (var op in plan.Ops) {
            Assert.NotNull(op.Identity);
            Assert.True(names.Add(op.Identity), $"Two ops are both named {op.Identity}, so they draw one seed.");
        }
    }

    /// <summary>
    ///     ⚠ Two sibling instances of one compound, nested inside another, draw two pictures.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/925">#925</a>, and it is the
    ///         narrowest graph in which the two ends of the walk are not enough.</b> An op inlined out
    ///         of a compound was named by <c>NodeOrigin.Source</c> — the outermost sub-graph node,
    ///         which stays outermost however deep the nesting goes — its <c>Inner</c> id in the
    ///         innermost file, and that file's type path. Two <c>Library/Speck</c> nodes inside one
    ///         <c>Library/Pair</c> share all three, so both noises were one op's name and drew one
    ///         picture: a compound built to scatter two different specks scattered the same one twice,
    ///         in register.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two levels of nesting are load-bearing and one is not.</b> Two <c>Speck</c> nodes
    ///         side by side in the author's own graph are two different <c>Source</c> ids and were
    ///         always distinct; it is the level of indirection that collapses them, because
    ///         <c>Source</c> stops moving as soon as there is a compound above it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_sibling_instances_of_one_compound_inside_another_draw_different_seeds() {
        var (compiler, graph, _) = Nested();
        var plan = Compiled(compiler, graph);

        var seeds = Seeds(plan, "Noise");

        Assert.Equal(2, seeds.Count);
    }

    /// <summary>
    ///     ⚠ And the chain that tells them apart does not move when the author adds a node beside the
    ///     compound.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half that says why <c>NodeOrigin.Expansion</c> could not be used for this.</b>
    ///         The expansion counter distinguishes the two instances perfectly well and is
    ///         <em>walk-ordered</em>, so naming an op with it would have reintroduced #875 one level
    ///         in — an insertion anywhere that reorders the walk redraws every noise inside every
    ///         compound. <c>SubGraphExpansion.Path</c> is a chain of <c>NodeId</c>s, each read out of
    ///         its own document, and a document never renumbers one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The insertion has to be one that actually moves both numbers, and neither is
    ///         easy to move by accident</b> — this file's own standing trap, twice over.
    ///         <c>NodeGraphModel.Ordered</c> seeds its queue in insertion order, so a compound
    ///         <em>added</em> to a graph is walked behind every compound already ready and the
    ///         expansion counter does not budge; what moves it is a compound that becomes ready
    ///         <em>before</em> an existing one, which is why the edit below is spliced in front of the
    ///         nested compound's own input rather than dropped beside it. And
    ///         <c>SubGraphs.Flatten</c> numbers inlined nodes from above the author's highest id, so
    ///         any insertion at all renumbers everything inside every compound. Both are asserted, so
    ///         that a plan which came out unchanged cannot make the equality vacuous.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_noise_inside_a_nested_compound_keeps_its_seed_when_a_compound_is_spliced_ahead_of_it() {
        var (compiler, graph, used) = Nested();
        var before = Compiled(compiler, graph);
        var inlined = compiler.Inlining.Origins.Keys.Select(id => id.Value).Order().ToArray();
        var walked = compiler.Inlining.Origins.Values.Select(origin => origin.Expansion).Max();

        // The edit: another compound spliced between the source and the nested one, so that it is
        // ready before the nested compound and the flattener descends into it first.
        var spliced = graph.Add("Library/Speck");

        graph.Disconnect(new(used.Id, "In"));
        graph.Connect(new(spliced.Id, "Out"), new(used.Id, "In"));

        var after = Compiled(compiler, graph);
        var renumbered = compiler.Inlining.Origins.Keys.Select(id => id.Value).Order().ToArray();
        var rewalked = compiler.Inlining.Origins.Values.Select(origin => origin.Expansion).Max();

        // The two instruments. The identities the flattener handed the compound's contents moved,
        // which is #875's mechanism; and the walk-ordered expansion counter moved too, which is why
        // it is not what an op is named by.
        Assert.NotEqual(inlined, renumbered);
        Assert.NotEqual(walked, rewalked);

        var kept = Seeds(before, "Noise");
        var now = Seeds(after, "Noise");

        // Three distinct noises afterwards — the nested pair and the one spliced in — and the pair's
        // two are the two they were.
        Assert.Equal(2, kept.Count);
        Assert.Equal(3, now.Count);
        Assert.Subset(now, kept);
    }

    /// <summary>A compound of two instances of a compound, inside the author's graph.</summary>
    /// <remarks>
    ///     <c>Library/Speck</c> is a noise behind a boundary; <c>Library/Pair</c> holds two of them
    ///     blended together over whatever arrives at its own input; the author's graph holds one
    ///     <c>Pair</c> fed by a checker. So the plan has two <c>Noise</c> ops that agree about
    ///     everything except which node inside <c>Pair</c> they came out of — and the <c>Pair</c> node
    ///     has an input, which is what lets a test splice a third compound in front of it.
    /// </remarks>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Used) Nested() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        NodeGraphModel speck = new() { Name = "Speck" };

        speck.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var noise = speck.Add("Source/Noise");
        var speckExit = speck.Add(SubGraphs.OutputType);

        speck.Connect(new(noise.Id, "Out"), new(speckExit.Id, "Out"));
        library.Publish("Library/Speck", speck, [], registry);

        NodeGraphModel pair = new() { Name = "Pair" };

        pair.Interface.Add(new("In", PortDirection.Input, PortKind.Image));
        pair.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var entry = pair.Add(SubGraphs.InputType);
        var left = pair.Add("Library/Speck");
        var right = pair.Add("Library/Speck");
        var specks = pair.Add("Colour/Blend");
        var onto = pair.Add("Colour/Blend");
        var pairExit = pair.Add(SubGraphs.OutputType);

        pair.Connect(new(left.Id, "Out"), new(specks.Id, "Background"));
        pair.Connect(new(right.Id, "Out"), new(specks.Id, "Foreground"));
        pair.Connect(new(entry.Id, "In"), new(onto.Id, "Background"));
        pair.Connect(new(specks.Id, "Out"), new(onto.Id, "Foreground"));
        pair.Connect(new(onto.Id, "Out"), new(pairExit.Id, "Out"));
        library.Publish("Library/Pair", pair, [], registry);

        NodeGraphModel graph = new();
        var checker = graph.Add("Source/Checker");
        var used = graph.Add("Library/Pair");
        var output = graph.Add("Output/Output");

        graph.Connect(new(checker.Id, "Out"), new(used.Id, "In"));
        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        return (
            new TextureGraphCompiler(registry) {
                BaseWidth = Side,
                BaseHeight = Side,
                Seed = Seed,
                SubGraphSource = library
            },
            graph,
            used
        );
    }

    /// <summary>The seeds of every op of one kernel, as a set — so a collision is a shorter set.</summary>
    static HashSet<uint> Seeds(TexturePlan plan, string kernel) {
        HashSet<uint> seeds = [];
        var found = 0;

        for (var op = 0; op < plan.Ops.Length; op++) {
            if (!string.Equals(plan.Ops[op].Kernel, kernel, StringComparison.Ordinal)) {
                continue;
            }

            found++;
            seeds.Add(plan.SeedFor(op));
        }

        Assert.True(found > 0, $"No '{kernel}' op in the plan, so there is no seed to read.");

        return seeds;
    }

    /// <summary>One compile of a graph with a library behind it, refusing a diagnostic.</summary>
    static TexturePlan Compiled(TextureGraphCompiler compiler, NodeGraphModel graph) {
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    /// <summary>A graph with one noise in it, compiled.</summary>
    static TexturePlan Bare() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(output.Id, "Input"));

        return Compile(graph);
    }

    static TexturePlan Compile(NodeGraphModel graph) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = Seed
        }.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }

    static int IndexOf(TexturePlan plan, string kernel) {
        var index = -1;

        for (var op = 0; op < plan.Ops.Length; op++) {
            if (string.Equals(plan.Ops[op].Kernel, kernel, StringComparison.Ordinal)) {
                Assert.Equal(-1, index);

                index = op;
            }
        }

        Assert.True(index >= 0, $"No '{kernel}' op in the plan, so there is no seed to read.");

        return index;
    }
}
