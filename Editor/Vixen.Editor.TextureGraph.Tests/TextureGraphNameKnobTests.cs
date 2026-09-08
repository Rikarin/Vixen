// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1060">#1060</a>'s cheap half: a published
///     graph exposing an inner node's <c>[Setting]</c> as a knob of its own.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every case here is a plan differential and not a diagnostic count</b>, because the
///         defect this feature can have is silence: <c>TextureSettings.Enum</c> falls back to the
///         node's declared default for anything it cannot parse, so a substitution that never
///         happened draws exactly the picture a knob at its default draws. What separates the two is
///         a knob turned to something else.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day <c>Substitute</c> stops running.</b> The
///         unsubstituted text stays in the setting, and
///         <see cref="A_compound_forwards_its_knob_into_an_inner_nodes_setting" /> then reads
///         <c>TG0010</c> — "'Fold' is '$Fold', which is not one of X, Y, Corner" — on the default
///         case as well as the turned one. Both halves go red, which is what makes the default case
///         worth asserting at all.
///     </para>
/// </remarks>
public class TextureGraphNameKnobTests {
    /// <summary>Which way <c>Space/Mirror</c> folded, read off the plan rather than off a setting.</summary>
    /// <remarks>
    ///     ⚠ <b>The op's own parameter, which is downstream of everything.</b> <c>MirrorNode</c>
    ///     parses its <c>Axis</c> setting into a <c>TextureMirrorAxis</c> and writes it as
    ///     <c>axis</c>; a knob that reached the file and not the kernel is invisible to an assertion
    ///     over settings and visible here.
    /// </remarks>
    static float Axis(TexturePlan plan) =>
        plan.Ops.Single(op => op.Kernel == "Mirror").Parameters.Single(one => one.Name == "axis").Value;

    /// <summary>One name knob over <c>Space/Mirror</c>'s three axes.</summary>
    /// <param name="name">What the knob is called.</param>
    /// <param name="choice">Which axis it defaults to.</param>
    static SettingDefinition Knob(string name, string choice) =>
        new(name, choice, "Which way it folds.", SettingKind.Text, Accepted: ["X", "Y", "Corner"]);

    /// <summary>A published graph whose mirror takes its fold from a knob of the graph.</summary>
    /// <param name="written">What the inner node's <c>Axis</c> setting holds.</param>
    /// <param name="parameters">What the graph declares, or none.</param>
    static NodeGraphModel Folding(string written, params SettingDefinition[] parameters) {
        NodeGraphModel graph = new() { Name = "Folded" };

        graph.Interface.Add(new("In", PortDirection.Input, PortKind.Image));
        graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var entry = graph.Add(SubGraphs.InputType);
        var mirror = graph.Add("Space/Mirror");
        var exit = graph.Add(SubGraphs.OutputType);

        graph.Connect(new(entry.Id, "In"), new(mirror.Id, "Input"));
        graph.Connect(new(mirror.Id, "Out"), new(exit.Id, "Out"));
        mirror.SetText("Axis", written);
        graph.Parameters.AddRange(parameters);

        return graph;
    }

    /// <summary>A graph containing one published type, fed a noise and wired to an output.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Used) Containing(
        TextureGraphLibrary library,
        NodeTypeRegistry registry,
        string path
    ) {
        NodeGraphModel graph = new();
        var used = graph.Add(path);
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(used.Id, "In"));
        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 64,
            BaseHeight = 64,
            Seed = 3,
            SubGraphSource = library
        };

        return (compiler, graph, used);
    }

    /// <summary>Publishes each graph as a node type, over the whole atomic library.</summary>
    /// <param name="graphs">The graphs, by the path each is published at.</param>
    static (NodeTypeRegistry Registry, TextureGraphLibrary Library) Published(
        params (string Path, NodeGraphModel Graph)[] graphs
    ) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphLibrary library = new();

        foreach (var (path, graph) in graphs) {
            library.Publish(path, graph, [], registry);
        }

        return (registry, library);
    }

    /// <summary>A knob of the containing graph reaches a setting inside the published one.</summary>
    /// <remarks>
    ///     <b>The two plans must differ from each other</b>, which is the assertion a fallback cannot
    ///     satisfy. A compiler that dropped the whole mechanism gives the mirror's own default for
    ///     both, so the pair is equal; one that read the knob and forgot the override gives the
    ///     declared <c>X</c> for both, and the pair is equal again.
    /// </remarks>
    [Fact]
    public void A_compound_forwards_its_knob_into_an_inner_nodes_setting() {
        var (registry, library) = Published((
            "Library/Folded",
            Folding("$Fold", Knob("Fold", "X"))
        ));

        var (compiler, graph, used) = Containing(library, registry, "Library/Folded");
        var untouched = compiler.Compile(graph);

        Assert.Empty(untouched.Diagnostics);
        Assert.Equal(0f, Axis(untouched.Value));

        used.SetText("Fold", "Corner");

        var turned = compiler.Compile(graph);

        Assert.Empty(turned.Diagnostics);
        Assert.Equal(2f, Axis(turned.Value));
    }

    /// <summary>The knob is a choice at the node, so a picker draws it rather than a text box.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes a name knob an <em>authoring</em> feature rather than a file
    ///     format.</b> <c>NodeSettingMember</c> offers a dropdown exactly when
    ///     <see cref="SettingDefinition.IsChoice" />, so a list that stopped crossing
    ///     <c>TextureGraphParameters.Settings</c> would leave the compiler working and the artist
    ///     typing <c>Corner</c> into a box.
    /// </remarks>
    [Fact]
    public void The_node_standing_for_the_graph_offers_the_knobs_names() {
        var (registry, _) = Published((
            "Library/Folded",
            Folding("$Fold", Knob("Fold", "X"))
        ));

        var setting = Assert.Single(registry.Types.Single(type => type.Path == "Library/Folded").Settings);

        Assert.True(setting.IsChoice);
        Assert.Equal(["X", "Y", "Corner"], setting.Accepted);
        Assert.Equal("X", setting.Default);
    }

    /// <summary>A compound hands its own knob down to a compound inside it.</summary>
    /// <remarks>
    ///     ⚠ <b>Three numbers, and each excludes a different wrong implementation.</b> The inner
    ///     graph's own default is <c>X</c> (0), so a walk that never followed the chain reads 0; the
    ///     outer graph's declared default is <c>Y</c> (1), so a walk that followed it one scope and
    ///     stopped at the declaration reads 1; the container turns it to <c>Corner</c> (2), which is
    ///     the only reading that means the override travelled the whole way.
    /// </remarks>
    [Fact]
    public void A_knob_a_container_hands_down_reaches_the_innermost_setting() {
        var inner = Folding(
            "$Fold",
            Knob("Fold", "X")
        );

        NodeGraphModel outer = new() { Name = "Outer" };

        outer.Interface.Add(new("In", PortDirection.Input, PortKind.Image));
        outer.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var entry = outer.Add(SubGraphs.InputType);
        var nested = outer.Add("Library/Folded");
        var exit = outer.Add(SubGraphs.OutputType);

        outer.Connect(new(entry.Id, "In"), new(nested.Id, "In"));
        outer.Connect(new(nested.Id, "Out"), new(exit.Id, "Out"));
        nested.SetText("Fold", "$Outward");
        outer.Parameters.Add(
            Knob("Outward", "Y")
        );

        var (registry, library) = Published(("Library/Folded", inner), ("Library/Outer", outer));
        var (compiler, graph, used) = Containing(library, registry, "Library/Outer");

        var declared = compiler.Compile(graph);

        Assert.Empty(declared.Diagnostics);
        Assert.Equal(1f, Axis(declared.Value));

        used.SetText("Outward", "Corner");

        var overridden = compiler.Compile(graph);

        Assert.Empty(overridden.Diagnostics);
        Assert.Equal(2f, Axis(overridden.Value));
    }

    /// <summary>A reference to a knob the graph has not got is said, and the text is left alone.</summary>
    /// <remarks>
    ///     ⚠ <b>Two diagnostics, deliberately.</b> <c>TG0023</c> is the forwarding saying the knob is
    ///     missing and <c>TG0010</c> is the node saying what it was left holding — and the pair is
    ///     what tells an author which of the two they have to fix. Writing the node's own default
    ///     instead would leave one arrangement quietly standing in for another.
    /// </remarks>
    [Fact]
    public void A_reference_to_a_knob_that_is_not_declared_is_reported_rather_than_guessed() {
        var (registry, library) = Published(("Library/Folded", Folding("$Fold")));
        var (compiler, graph, _) = Containing(library, registry, "Library/Folded");

        var compilation = compiler.Compile(graph);

        Assert.Single(compilation.Diagnostics, one => one.Id == "TG0023");
        Assert.Single(
            compilation.Diagnostics,
            one => one.Id == "TG0010" && one.Message.Contains("$Fold", StringComparison.Ordinal)
        );
    }

    /// <summary>A name the knob does not accept keeps the declared choice and says so.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is here rather than at the node, which is the point of doing it at
    ///     all.</b> Substituting <c>Sideways</c> would make the node report a name the author never
    ///     typed, on a node they never wrote; refusing it here leaves one sentence, about the knob
    ///     they did turn, and a picture that is the declared default rather than an accident.
    /// </remarks>
    [Fact]
    public void A_name_outside_the_knobs_list_keeps_the_default_and_says_so() {
        var (registry, library) = Published((
            "Library/Folded",
            Folding("$Fold", Knob("Fold", "Y"))
        ));

        var (compiler, graph, used) = Containing(library, registry, "Library/Folded");

        used.SetText("Fold", "Sideways");

        var compilation = compiler.Compile(graph);
        var said = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0023");

        Assert.Equal(NodeSeverity.Warning, said.Severity);
        Assert.DoesNotContain(compilation.Diagnostics, one => one.Id == "TG0010");
        Assert.Equal(1f, Axis(compilation.Value));
    }

    /// <summary>A name written in another case is accepted, and substituted as the knob spells it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two assertions and only one of them could have been a compilation.</b> That
    ///         <c>corner</c> is <em>accepted</em> is visible in the plan, because a refusal would fold
    ///         the knob back to <c>X</c>; that it is substituted as <c>Corner</c> is not, and the
    ///         attempt to assert it through a plan is a case worth writing down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The plan cannot see the spelling and a test that asserted through one was green
    ///         under sabotage.</b> <c>TextureSettings.Enum</c> parses ignoring case, so a
    ///         substitution that copied <c>corner</c> straight through produces the identical
    ///         <c>axis</c> — the picture is the same and the file is not, which is exactly
    ///         <c>SettingDefinition.Canonical</c>'s finding
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1044">#1044</a>): what the wrong
    ///         spelling costs is a picker that cannot show the value as selected, and no plan has an
    ///         opinion about that. So the spelling is asserted where it is decided.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_name_written_in_another_case_is_stored_as_the_knob_spells_it() {
        var (registry, library) = Published((
            "Library/Folded",
            Folding("$Fold", Knob("Fold", "X"))
        ));

        var (compiler, graph, used) = Containing(library, registry, "Library/Folded");

        used.SetText("Fold", "corner");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(2f, Axis(compilation.Value));

        Assert.True(
            TextureGraphParameters.TryChoose(
                [new("Fold", TextureGraphParameterKind.Name, Choice: "X", Accepted: ["X", "Y", "Corner"])],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Fold"] = "corner" },
                "Fold",
                out var chosen,
                out var problem
            )
        );

        Assert.Empty(problem);
        Assert.Equal("Corner", chosen);
    }

    /// <summary>A setting that merely begins with the prefix is left exactly as it is.</summary>
    /// <remarks>
    ///     <b>The instrument.</b> Every case above would pass just as well against a pass that
    ///     rewrote every setting it could find, so this is the half that says the convention has an
    ///     edge: <c>$</c> followed by something that is not an identifier is a value, not a
    ///     reference, and produces neither a substitution nor a complaint about a knob nobody
    ///     declared.
    /// </remarks>
    [Fact]
    public void A_value_that_is_not_an_identifier_after_the_prefix_is_not_a_reference() {
        Assert.False(TextureGraphParameters.IsReference("$", out _));
        Assert.False(TextureGraphParameters.IsReference("$1st", out _));
        Assert.False(TextureGraphParameters.IsReference("$Assets/Rust.png", out _));
        Assert.False(TextureGraphParameters.IsReference("Max", out _));
        Assert.True(TextureGraphParameters.IsReference("$Fold", out var named));
        Assert.Equal("Fold", named);
    }

    /// <summary>
    ///     ⚠ A text parameter with no list of names stays the scalar every file written before this
    ///     kind existed meant.
    /// </summary>
    /// <remarks>
    ///     <b>The compatibility statement, and it is not a detail.</b> <c>SettingKind</c>'s zero is
    ///     <c>Text</c>, and the shipped compounds declare <c>default: '0.5'</c> with no <c>kind:</c>
    ///     key at all — so reading every text-kind parameter as a name would turn
    ///     <c>Generators/Dust</c>'s <c>facing</c> into a choice whose default is the string "0.5", in
    ///     eleven files that already exist. The list is what says which is meant, because no file
    ///     that predates the kind can have one.
    /// </remarks>
    [Fact]
    public void A_text_parameter_with_no_list_is_still_a_number() {
        var parameters = TextureGraphParameters.Declared([
            new("facing", "0.5", "How far off horizontal.", SettingKind.Text),
            new("mode", "Max", "How they combine.", SettingKind.Text, Accepted: ["Max", "Add"])
        ]);

        Assert.Equal(TextureGraphParameterKind.Scalar, parameters[0].Kind);
        Assert.Equal(0.5f, parameters[0].Default);

        Assert.Equal(TextureGraphParameterKind.Name, parameters[1].Kind);
        Assert.Equal("Max", parameters[1].Choice);
        Assert.Equal(["Max", "Add"], parameters[1].Accepted);
    }

    /// <summary>A name knob survives the file, list and all.</summary>
    /// <remarks>
    ///     ⚠ <b>The list had nowhere to live in <c>GraphParameterAsset</c> until this change</b>, so
    ///     a graph could declare a choice in memory and reopen with a free-text knob — which is the
    ///     shape <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a> was, one member over.
    /// </remarks>
    [Fact]
    public void A_name_knob_survives_a_save_and_a_load() {
        NodeGraphModel graph = new() { Name = "Knobbed" };

        graph.Parameters.Add(
            Knob("Fold", "Corner")
        );

        var reopened = NodeGraphDocument.Load(NodeGraphDocument.Save(graph), out var diagnostics);

        Assert.Empty(diagnostics);

        var parameter = Assert.Single(TextureGraphParameters.Declared(reopened.Parameters));

        Assert.Equal(TextureGraphParameterKind.Name, parameter.Kind);
        Assert.Equal("Corner", parameter.Choice);
        Assert.Equal(["X", "Y", "Corner"], parameter.Accepted);
        Assert.Empty(TextureGraphParameters.Check([parameter]));
    }

    /// <summary>
    ///     ⚠ A shipped compound's new knob defaults to exactly what it used to hard-wire, and turning
    ///     it changes the plan.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The caller, and the half a unit test cannot make.</b> <c>Patterns/Tile Random</c>
    ///         held <c>texts: { Accumulation: Max }</c> and now holds <c>$Accumulation</c> over a
    ///         parameter defaulting to <c>Max</c> — so the claim that the picture is unchanged is a
    ///         claim about a file in the library rather than about a fixture, and it is asserted
    ///         against the number the kernel actually receives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because either alone is satisfied by a wrong implementation.</b> A
    ///         substitution that never happened leaves the kernel on its own fallback, which is also
    ///         <c>Max</c>; a knob wired to nothing is at <c>Max</c> for both readings. Only the pair
    ///         separates them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shipped_tile_random_exposes_the_accumulation_it_used_to_hard_wire() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);

        NodeGraphModel graph = new();
        var used = graph.Add("Patterns/Tile Random");
        var noise = graph.Add("Source/Noise");
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(used.Id, "Pattern"));
        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 64,
            BaseHeight = 64,
            Seed = 3,
            SubGraphSource = library
        };

        var settled = compiler.Compile(graph);

        Assert.Empty(settled.Diagnostics);
        Assert.Equal(
            (float)TexturePlacementAccumulation.Max,
            Accumulation(settled.Value)
        );

        used.SetText("Accumulation", "Add");

        var added = compiler.Compile(graph);

        Assert.Empty(added.Diagnostics);
        Assert.Equal((float)TexturePlacementAccumulation.Add, Accumulation(added.Value));
    }

    /// <summary>How the tile sampler was told to combine overlapping instances.</summary>
    static float Accumulation(TexturePlan plan) =>
        plan.Ops
            .Single(op => op.Kernel == "TileSampler")
            .Parameters.Single(one => one.Name == "accumulation")
            .Value;

    /// <summary>A name knob with no list is refused at publish, and the sentence says why.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than allowed as a free-text field</b>, because an empty list is
    ///     exactly what a scalar knob saved by an older build has — see
    ///     <see cref="A_text_parameter_with_no_list_is_still_a_number" />. The two would otherwise be
    ///     the same declaration meaning two things.
    /// </remarks>
    [Fact]
    public void A_name_knob_states_the_names_it_accepts() {
        var problems = TextureGraphParameters.Check([
            new("Fold", TextureGraphParameterKind.Name, Choice: "X"),
            new(
                "Turn",
                TextureGraphParameterKind.Name,
                Choice: "Sideways",
                Accepted: ImmutableArray.Create("X", "Y")
            )
        ]);

        Assert.Equal(2, problems.Length);
        Assert.Contains("states no list", problems[0], StringComparison.Ordinal);
        Assert.Contains("Sideways", problems[1], StringComparison.Ordinal);
    }

    /// <summary>⚠ Compiling a graph does not rewrite the graph.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The case a reviewer found missing, and the defect it covers destroys an authored
    ///         file.</b> The first version of this feature substituted into <c>node.Texts</c> during
    ///         <c>Begin</c> — and the model reaching <c>Begin</c> is only a <em>copy</em> when
    ///         flattening ran. A graph with no sub-graph node in it is the model the panel has open,
    ///         so every preview compile replaced <c>$Tiling</c> with <c>Wrap</c> and the next save
    ///         wrote that down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both shipped compounds that use a name knob are exactly that shape</b> —
    ///         <c>Patterns/Tile Random</c> and <c>Utility/Safe Transform</c> declare knobs and
    ///         contain no compound of their own — so the one path with no test was the one the
    ///         feature shipped on.
    ///     </para>
    ///     <para>
    ///         The assertion is on the <em>model</em> rather than on a saved file, because that is
    ///         where the damage happens; a round trip through YAML would pass on a model already
    ///         overwritten in memory.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_compile_leaves_the_authors_own_graph_holding_its_references() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new() { Name = "Turned" };

        graph.Parameters.Add(Knob("Fold", "Y"));

        var mirror = graph.Add("Space/Mirror");

        mirror.SetText("Axis", TextureGraphParameters.ReferencePrefix + "Fold");

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 32, BaseHeight = 32, Seed = 7u };

        // The graph has no sub-graph node, so nothing flattens and `Begin` is handed this very model.
        Assert.DoesNotContain(graph.Nodes, node => node.Type.StartsWith("Library/", StringComparison.Ordinal));

        compiler.Compile(graph);

        Assert.Equal(
            TextureGraphParameters.ReferencePrefix + "Fold",
            graph.Nodes.Single(node => node.Id == mirror.Id).Texts["Axis"]
        );

        // ⚠ And twice, because a rewrite is idempotent and would look stable after the first one.
        compiler.Compile(graph);

        Assert.Equal(
            TextureGraphParameters.ReferencePrefix + "Fold",
            graph.Nodes.Single(node => node.Id == mirror.Id).Texts["Axis"]
        );
    }
}
