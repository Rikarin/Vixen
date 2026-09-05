// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The <c>.vxtexgraph</c> document: what a new one is, and what a saved one reads back as.</summary>
public class TextureGraphDocumentTests {
    [Fact]
    public void A_new_graph_is_a_colour_wired_into_an_output() {
        using var fixture = new TexturingFixture();

        var document = Open(fixture, "New");

        Assert.Empty(document.LoadDiagnostics);

        // ⚠ Both halves, and the wire. A graph with no `Output` produces no images at all, so a file
        // that opened with just a source node would report nothing to bake — which reads as a broken
        // evaluator rather than as an unfinished graph.
        Assert.Contains(document.Graph.Nodes, node => node.Type == "Source/Uniform");
        Assert.Contains(document.Graph.Nodes, node => node.Type == "Output/Output");
        Assert.Single(document.Graph.Edges);
    }

    [Fact]
    public void Every_node_the_evaluator_declares_is_offered() {
        using var fixture = new TexturingFixture();

        var document = Open(fixture, "Library");

        // ⚠ Named rather than counted. A count is a test that goes red when batch 5 adds a node, which
        // teaches whoever added it to change the number; these eight are the ones a graph saved today
        // can contain, and one of them disappearing is a saved graph that no longer loads.
        foreach (var path in new[] {
                     "Source/Uniform", "Source/Noise", "Colour/Blend", "Colour/Levels",
                     "Filters/Blur", "Space/Transform 2D", "Analysis/Distance", "Output/Output"
                 }) {
            Assert.True(document.Registry.TryGet(path, out _), path + " is not in the library");
        }
    }

    [Fact]
    public void A_saved_graph_reads_back_as_the_graph_that_was_saved() {
        using var fixture = new TexturingFixture();

        var asset = fixture.AddGraph("Round");
        var written = new TextureGraphDocument(fixture.Project, asset, Path(fixture, "Round"));

        var noise = written.Graph.Add("Source/Noise", new(200f, 300f));

        written.Save();

        var read = new TextureGraphDocument(fixture.Project, asset, Path(fixture, "Round"));

        Assert.Empty(read.LoadDiagnostics);
        Assert.Equal(written.Graph.Nodes.Count, read.Graph.Nodes.Count);
        Assert.Contains(read.Graph.Nodes, node => node.Id == noise.Id && node.Type == "Source/Noise");

        // The position too, because a graph whose layout does not survive a save is a graph an author
        // rearranges once.
        Assert.Equal(new Vector2(200f, 300f), read.Graph.Nodes.Single(node => node.Id == noise.Id).Position);
    }

    /// <summary>⚠ A file this build cannot read opens, and says so, rather than throwing.</summary>
    /// <remarks>
    ///     The panel that could show the problem is only reachable if the document opens. A throw here
    ///     is a double-click that produces a stack trace and no way in.
    /// </remarks>
    [Fact]
    public void A_graph_that_does_not_parse_opens_empty_with_a_diagnostic() {
        using var fixture = new TexturingFixture();

        var asset = fixture.AddGraph("Broken", "nodes: [ this is not\n  yaml: {");
        var document = new TextureGraphDocument(fixture.Project, asset, Path(fixture, "Broken"));

        Assert.NotEmpty(document.LoadDiagnostics);
        Assert.Empty(document.Graph.Nodes);
    }

    /// <summary>
    ///     ⚠ Doc 48 § 4.9's shipped compounds are node types a document offers, and a graph
    ///     containing one compiles to a plan.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#799 and #803 as one case, because they were one missing call.</b>
    ///         <c>TextureCompoundLibrary.Publish</c> — and through it
    ///         <c>TextureGraphLibrary.Publish</c>, the only way a graph becomes a node another graph
    ///         can contain — had no caller outside its own tests, so the four shipped compounds were
    ///         embedded, loadable, compilable and in no menu anywhere, and every sub-graph fix batch
    ///         7 made was latent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, and the second is the one that matters.</b> A published node type in
    ///         the registry with no <c>SubGraphSource</c> behind it is <em>worse</em> than no node
    ///         type: the search popup offers it, the author places it, and the compilation says
    ///         <c>TG0001</c> — "nothing inlined it". So this asserts the node is offered
    ///         <em>and</em> that a graph containing it compiles with no diagnostic at all, which is
    ///         only true if the same call produced both.
    ///     </para>
    ///     <para>
    ///         <b>Derived from what ships, not from a list of four.</b> The compounds are read off
    ///         <c>TextureCompoundLibrary.Shipped</c>, so a sibling shipping a fifth is covered here
    ///         by existing — and the instrument is asserted first, because an empty <c>Shipped</c>
    ///         would make an <c>Assert.All</c> over it a pass over nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shipped_compound_is_offered_as_a_node_and_the_graph_containing_it_compiles() {
        using var fixture = new TexturingFixture();

        var document = Open(fixture, "Compounds");

        Assert.NotEmpty(TextureGraph.TextureCompoundLibrary.Shipped);

        Assert.All(
            TextureGraph.TextureCompoundLibrary.Shipped,
            path => Assert.True(document.Registry.TryGet(path, out _), path + " is not in the library")
        );

        // The whole point of the pair: the compound is placed and its contents actually reach the
        // plan. Its image inputs are fed, because an unwired one is a TG0002 about this graph rather
        // than anything to do with publishing.
        var compound = document.Graph.Add(TextureGraph.TextureCompoundLibrary.Shipped[0]);
        var output = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        document.Graph.Connect(new(compound.Id, "Out"), new(output.Id, "Input"));

        foreach (var port in document.Registry.Types
            .Single(type => type.Path == compound.Type)
            .Ports
            .Where(port => port is { Direction: PortDirection.Input, Kind: PortKind.Image })) {
            document.Graph.Connect(new(document.Graph.Add("Source/Noise").Id, "Out"), new(compound.Id, port.Name));
        }

        var compilation = document.Compile();

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Artefact);
        Assert.NotEmpty(compilation.Artefact!.Ops);
    }

    /// <summary>
    ///     ⚠ The instrument for the case above: with no source, the same graph is a <c>TG0001</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Otherwise "it compiles" is a claim about a node type, not about the wire.</b> A
    ///     document handed its own registry publishes nothing and has no <c>SubGraphSource</c> — and
    ///     the compound is then not a node type at all, so this asserts the state the editor was in
    ///     before this change: the graph cannot even be built, because the menu has no such node.
    /// </remarks>
    [Fact]
    public void A_document_given_its_own_registry_offers_no_compounds() {
        using var fixture = new TexturingFixture();

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Atomic"),
            Path(fixture, "Atomic"),
            TextureNodeLibrary.Create()
        );

        Assert.Null(document.SubGraphs);

        Assert.All(
            TextureGraph.TextureCompoundLibrary.Shipped,
            path => Assert.False(document.Registry.TryGet(path, out _), path + " should not be published here")
        );

        // And the atomic nodes are still all there, so this is "no compounds" rather than "no
        // library" — the failure that would make the assertion above true for the wrong reason.
        Assert.True(document.Registry.TryGet("Source/Noise", out _));
    }

    static string Path(TexturingFixture fixture, string name) =>
        fixture.Paths.Absolute("Assets/" + name + TextureGraphDocument.Extension);

    static TextureGraphDocument Open(TexturingFixture fixture, string name) =>
        new(fixture.Project, fixture.AddGraph(name), Path(fixture, name));
}
