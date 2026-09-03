// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Shading;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     A project's shader graphs, as the Raven the two shader compilations are handed.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the "finished thing nothing calls" half of the graph story, and it is the half a
///     test can actually pin.</b> The compiler emitted correct Raven for as long as it has existed
///     and neither <c>EditorEffects</c> nor <c>ShaderBuildRunner</c> had ever seen a line of it,
///     because both enumerated <c>*.rvn</c> on disk and a graph is not one. What follows asserts the
///     enumeration, which is the piece both callers share; that each caller uses it is a two-line
///     read of those files, and is stated in their own comments.
/// </remarks>
public class ShaderGraphSourceTests : IDisposable {
    readonly string root = Directory.CreateTempSubdirectory("vixen-graph-sources").FullName;

    public void Dispose() {
        try {
            Directory.Delete(root, recursive: true);
        } catch (IOException) {
            // A temp directory a virus scanner still has open is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a graph into the fixture, the way the editor saves one.</summary>
    string Write(string name, Action<NodeGraphModel> build) {
        var graph = new NodeGraphModel { Name = name };

        build(graph);

        var path = Path.Combine(root, name + ShaderGraphSources.Extension);

        File.WriteAllText(path, YamlSerializer.ToYaml(NodeGraphDocument.Save(graph)));

        return path;
    }

    /// <summary>A surface graph becomes a shader source a material can name.</summary>
    /// <remarks>
    ///     The whole point. What this catches if it regresses is the state the graph shipped in for
    ///     its whole life: a graph compiling perfectly, and no compilation in the process holding a
    ///     line of it.
    /// </remarks>
    [Fact]
    public void A_surface_graph_becomes_a_shader_source() {
        Write("Painted", graph => graph.Add("Master/Surface"));

        var compiled = Assert.Single(ShaderGraphSources.All(root));

        Assert.True(compiled.Compiled);
        Assert.Equal("Painted", compiled.Name);
        Assert.Empty(compiled.Diagnostics);
        Assert.Contains("IMaterialSurface", compiled.Text, StringComparison.Ordinal);
    }

    /// <summary>A standalone graph is skipped rather than refused.</summary>
    /// <remarks>
    ///     It is a legitimate thing to have — a preview thumbnail is one — and nothing can name it as
    ///     a material, so it contributes no source and no complaint.
    /// </remarks>
    [Fact]
    public void A_standalone_graph_contributes_no_source_and_no_complaint() {
        Write("Preview", graph => graph.Add("Master/Unlit"));

        var compiled = Assert.Single(ShaderGraphSources.All(root));

        Assert.False(compiled.Compiled);
        Assert.Empty(compiled.Diagnostics);
    }

    /// <summary>
    ///     A graph that does not compile contributes nothing, and says why against its own file.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Contributing the text anyway is what would break the project rather than the
    ///     graph.</b> <c>RavenEffectCompiler</c>'s constructor throws on a source that will not
    ///     parse, and a shared compilation that throws is every material in the project refused under
    ///     one message about the library — so a graph an author is halfway through would take the
    ///     whole editor's shading with it.
    /// </remarks>
    [Fact]
    public void A_graph_with_no_master_contributes_nothing_and_says_why() {
        Write("Unfinished", graph => graph.Add("Input/UV"));

        var compiled = Assert.Single(ShaderGraphSources.All(root));

        Assert.False(compiled.Compiled);
        Assert.Contains(compiled.Diagnostics, note => note.StartsWith("SG0003", StringComparison.Ordinal));
    }

    /// <summary>An unopened graph is not a complaint.</summary>
    /// <remarks>
    ///     What "create shader graph" leaves behind. A build that complained about one would complain
    ///     every time somebody made a file and went to lunch.
    /// </remarks>
    [Fact]
    public void An_empty_file_is_not_a_complaint() {
        File.WriteAllText(Path.Combine(root, "New" + ShaderGraphSources.Extension), "");

        var compiled = Assert.Single(ShaderGraphSources.All(root));

        Assert.False(compiled.Compiled);
        Assert.Empty(compiled.Diagnostics);
    }

    /// <summary>Text that is not a graph is reported rather than thrown.</summary>
    [Fact]
    public void A_file_that_is_not_a_graph_is_reported() {
        File.WriteAllText(Path.Combine(root, "Broken" + ShaderGraphSources.Extension), "this: [is: not: a graph");

        var compiled = Assert.Single(ShaderGraphSources.All(root));

        Assert.False(compiled.Compiled);
        Assert.NotEmpty(compiled.Diagnostics);
    }

    /// <summary>Graphs come back in a stable order, whatever the file system's is.</summary>
    /// <remarks>
    ///     The source hash every artefact carries is taken over the texts in the order they were
    ///     read, so an order that depended on the file system would make a cache entry stale on a
    ///     machine that enumerated differently — <c>ShaderBuildRunner.Sources</c>'s own reason.
    /// </remarks>
    [Fact]
    public void The_order_is_stable() {
        Write("Zulu", graph => graph.Add("Master/Surface"));
        Write("Alpha", graph => graph.Add("Master/Surface"));
        Write("Mike", graph => graph.Add("Master/Surface"));

        Assert.Equal(
            ["Alpha", "Mike", "Zulu"],
            ShaderGraphSources.All(root).Select(compiled => compiled.Name)
        );
    }

    /// <summary>A directory with no graphs in it is empty rather than an error.</summary>
    [Fact]
    public void A_project_with_no_graphs_is_empty() {
        Assert.Empty(ShaderGraphSources.All(root));
        Assert.Empty(ShaderGraphSources.All(Path.Combine(root, "nothing-here")));
    }

    /// <summary>The importer claims the extension, which nothing did before.</summary>
    /// <remarks>
    ///     ⚠ One of the five kinds the editor's own Create menu wrote, opened and saved with no
    ///     importer at all — <c>RawImporter</c> took it as a <c>Blob</c> no typed reader resolves.
    /// </remarks>
    [Fact]
    public void The_registry_claims_a_shader_graph() {
        Assert.True(
            BuiltInImporters.Create().TryGetForFile("Painted" + ShaderGraphSources.Extension, out var importer)
        );

        Assert.IsType<ShaderGraphImporter>(importer);
    }
}
