// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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

    static string Path(TexturingFixture fixture, string name) =>
        fixture.Paths.Absolute("Assets/" + name + TextureGraphDocument.Extension);

    static TextureGraphDocument Open(TexturingFixture fixture, string name) =>
        new(fixture.Project, fixture.AddGraph(name), Path(fixture, name));
}
