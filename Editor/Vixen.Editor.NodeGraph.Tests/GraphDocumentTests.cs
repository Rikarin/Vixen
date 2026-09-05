// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What a graph is besides its nodes and edges, and whether it survives being rebuilt.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/780">#780</a> was not a bug in the
///         flattener so much as a shape that guarantees one.</b> <c>Flattener.Run</c> built a fresh
///         <see cref="NodeGraphModel" /> and copied the three side tables that existed the day it was
///         written; two more were added to the model on the day it was last read, and a graph
///         declaring 512×512 with a seed compiled at its host's numbers the moment it contained a
///         sub-graph node — <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>'s own
///         failure, reached by another route, on the day it closed.
///     </para>
///     <para>
///         <b>So the interesting test here is not that the two new tables survive — it is
///         <see cref="Every_side_table_of_a_graph_is_carried_or_exempt" />.</b> That one reads the
///         model's own properties rather than a list of them, so the <em>next</em> side table
///         somebody adds is red here on the day it is declared, naming itself, rather than silently
///         absent from every flattened graph.
///     </para>
/// </remarks>
public class GraphDocumentTests {
    /// <summary>What <c>NodeGraphModel.CopyDocumentTo</c> is expected to carry, and how to see it.</summary>
    /// <remarks>
    ///     The key is the property's name, which is what the roll call below matches against; the
    ///     value fills it on one graph and reads it back off another, so an entry proves the table
    ///     crosses rather than merely being listed.
    /// </remarks>
    static readonly Dictionary<string, (Action<NodeGraphModel> Fill, Func<NodeGraphModel, bool> Carried)> Carried =
        new(StringComparer.Ordinal) {
            ["Name"] = (graph => graph.Name = "Rust", graph => graph.Name == "Rust"),
            ["Groups"] = (
                graph => graph.Groups.Add(new() { Title = "Masks" }),
                graph => graph.Groups.Any(group => group.Title == "Masks")
            ),
            ["Comments"] = (
                graph => graph.Comments.Add(new() { Text = "the edge is deliberate" }),
                graph => graph.Comments.Any(comment => comment.Text == "the edge is deliberate")
            ),
            ["Interface"] = (
                graph => graph.Interface.Add(new("Amount", PortDirection.Input, PortKind.Float)),
                graph => graph.Interface.Any(port => port.Name == "Amount")
            ),
            ["Settings"] = (
                graph => graph.Settings["texture.baseWidth"] = "512",
                graph => graph.SettingOf("texture.baseWidth") == "512"
            ),
            ["Parameters"] = (
                graph => graph.Parameters.Add(new("Grunge", "0.5", "", SettingKind.Float, 0f, 1f)),
                graph => graph.Parameters.Any(parameter => parameter.Name == "Grunge")
            )
        };

    /// <summary>What it deliberately does not carry, and why.</summary>
    /// <remarks>
    ///     ⚠ <b>A reason each, because an exemption with no reason is how a roll call becomes a
    ///     rubber stamp.</b> Both of these are what the callers of a document copy exist to rewrite:
    ///     an inlining renumbers node identities as it copies and traces every edge through the
    ///     boundary nodes it is deleting, so a copy that brought them along would be the inlining
    ///     with an opinion about which half to keep.
    /// </remarks>
    static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal) {
        ["Nodes"] = "the copy's whole purpose is to rewrite these — an inlining renumbers them",
        ["Edges"] = "traced through the boundary nodes the inlining removes, not copied"
    };

    /// <summary>Every public table of the model is either carried by a copy or exempt with a reason.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off the type rather than listed here, which is the whole of the test.</b> A list
    ///     of side tables written down in a test file is exactly the artefact that made #780
    ///     possible — it agrees with the model on the day it is written and never again. This one
    ///     fails on the property's own name the moment somebody declares one.
    /// </remarks>
    [Fact]
    public void Every_side_table_of_a_graph_is_carried_or_exempt() {
        List<string> unaccounted = [];

        foreach (var property in typeof(NodeGraphModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!Carried.ContainsKey(property.Name) && !Exempt.ContainsKey(property.Name)) {
                unaccounted.Add(property.Name);
            }
        }

        Assert.True(
            unaccounted.Count == 0,
            $"NodeGraphModel declares {string.Join(", ", unaccounted)}, which NodeGraphModel.CopyDocumentTo "
            + "neither copies nor this file exempts. A side table nothing copies is silently absent from "
            + "every flattened graph — see #780."
        );

        // The instrument: the roll call is only worth anything if it is reading something. Six
        // carried and two exempt today, and the count is a floor rather than an equality so that a
        // slice adding a table fails on the line above with its name rather than here on a number.
        Assert.True(Carried.Count + Exempt.Count >= 8, "the roll call is reading fewer properties than it did");
    }

    /// <summary>And each of them actually crosses a copy.</summary>
    /// <remarks>
    ///     <b>One assertion per entry, driven by the same dictionary the roll call checks.</b> That
    ///     is what stops an entry being added to satisfy the roll call and carrying nothing —
    ///     "listed" and "copied" are two claims and only the second one is a picture.
    /// </remarks>
    [Fact]
    public void Everything_a_copy_claims_to_carry_arrives() {
        foreach (var (name, entry) in Carried) {
            NodeGraphModel source = new();
            NodeGraphModel target = new();

            entry.Fill(source);

            Assert.False(entry.Carried(target), $"'{name}' reads as carried on a graph nothing filled");

            source.CopyDocumentTo(target);

            Assert.True(entry.Carried(target), $"'{name}' did not survive NodeGraphModel.CopyDocumentTo");
        }
    }

    /// <summary>A published graph's knobs are the settings of the node that stands for it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The seam <a href="https://github.com/Rikarin/Vixen/issues/730">#730</a> widened
    ///         <see cref="SettingDefinition" /> for, and until this it had no declaration site at
    ///         all.</b> The kind, the range and the group were carried, saved, loaded and drawn —
    ///         <c>NodeSettingMember</c> turns a bounded numeric setting into a slider — and nothing
    ///         in the tree ever produced one: no <c>[Setting]</c> in any node library declares a
    ///         kind, because every setting those libraries hold is an enumeration spelled as a name,
    ///         which is a kind <see cref="SettingKind" /> does not have. A published graph's
    ///         parameters are the numeric knobs, and <see cref="SubGraphs.Definition" /> is the one
    ///         place a graph becomes a node type.
    ///     </para>
    ///     <para>
    ///         <b>So it dropped them</b>, exactly as the flattener dropped the settings bag beside
    ///         them: a node type built from the interface and nothing else, on a model whose own
    ///         remarks say "doc 48 § D9 says its exposed parameters are its settings".
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_published_graphs_parameters_are_the_settings_of_its_node_type() {
        NodeGraphModel published = new() { Name = "Grunge" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Float));
        published.Parameters.Add(new("Amount", "0.25", "How much", SettingKind.Float, 0f, 1f, "Wear"));

        var setting = Assert.Single(SubGraphs.Definition(published, "Sub-graphs/Grunge").Settings);

        Assert.Equal("Amount", setting.Name);
        Assert.Equal(SettingKind.Float, setting.Kind);
        Assert.Equal("Wear", setting.Group);
        Assert.True(setting.IsBounded);

        // And the range reaches a row rather than stopping at the definition, which is the half that
        // makes this a knob an artist can turn instead of a box in which "ture" is a value.
        Assert.NotNull(new NodeSettingMember(new(), setting).Range);

        // The instrument: a published graph declaring no parameters gets no settings, so the single
        // entry above is the graph's declaration and not something every sub-graph node has.
        published.Parameters.Clear();

        Assert.Empty(SubGraphs.Definition(published, "Sub-graphs/Grunge").Settings);
    }

    /// <summary>A graph's declarations survive being flattened, which is what #780 was about.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="SubGraphs.Flatten" /> rather than through the copy</b>, because the
    ///     defect was never in the copy — there was no copy. Every declaration test in this
    ///     repository used a flat graph, and a flat graph is precisely the one input for which
    ///     <c>Flatten</c> is not called at all.
    /// </remarks>
    [Fact]
    public void A_graph_that_contains_a_sub_graph_keeps_its_own_declarations() {
        SubGraphLibrary library = new();
        NodeGraphModel published = new() { Name = "Tint" };

        published.Interface.Add(new("In", PortDirection.Input, PortKind.Float));
        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Float));

        var entry = published.Add(SubGraphs.InputType);
        var exit = published.Add(SubGraphs.OutputType);

        published.Connect(new(entry.Id, "In"), new(exit.Id, "Out"));
        library.Add("Sub-graphs/Tint", published);

        NodeGraphModel host = new() { Name = "Rust" };

        host.Settings["texture.baseWidth"] = "512";
        host.Settings["texture.seed"] = "90210";
        host.Parameters.Add(new("Grunge", "0.5", "", SettingKind.Float, 0f, 1f));
        host.Groups.Add(new() { Title = "Masks" });
        host.Add("Sub-graphs/Tint", new Vector2(40f, 40f));

        var flattened = SubGraphs.Flatten(host, library, out var diagnostics, out _);

        Assert.Empty(diagnostics);
        Assert.Equal("512", flattened.SettingOf("texture.baseWidth"));
        Assert.Equal("90210", flattened.SettingOf("texture.seed"));
        Assert.Equal("Grunge", Assert.Single(flattened.Parameters).Name);
        Assert.Equal("Masks", Assert.Single(flattened.Groups).Title);

        // The instrument: the flatten actually did something — the sub-graph node is gone from the
        // result — so the declarations above survived an inlining rather than a no-op.
        Assert.DoesNotContain(flattened.Nodes, node => node.Type == "Sub-graphs/Tint");
    }
}
