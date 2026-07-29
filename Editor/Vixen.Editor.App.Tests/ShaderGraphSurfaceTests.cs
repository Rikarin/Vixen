// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's B5 shader-graph row, driven the way somebody reaches it: from the editor.</summary>
/// <remarks>
///     ⚠ <b>The node library and the compiler had been ✅ for a while and none of it was
///     reachable.</b> There was no document, no factory and no create command, so the only way to
///     open a shader graph was to write the file by hand outside the editor — which is the same gap
///     E5 closed for the VFX graph. These drive the two halves that make it reachable: a file made
///     from the Assets menu, and a material's own link to the graph it was authored in.
/// </remarks>
public class ShaderGraphSurfaceTests {
    /// <summary>The row is registered under the name doc 11's table calls it.</summary>
    [Fact]
    public void The_registry_has_a_shader_graph_row() {
        var registry = StandardEditors.CreateWorldless();

        Assert.True(registry.TryGetByName("Shader Graph", out var editor));
        Assert.Contains(ShaderGraphDocument.Extension, editor!.Extensions);
    }

    /// <summary>A graph is made from the Assets menu, opens, and its panel is the graph editor.</summary>
    [Fact]
    public void A_shader_graph_is_created_and_opens_in_its_editor() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.CanRun("assets.create-shader-graph"));

        fixture.Run("assets.create-shader-graph").Settle();

        var created = Directory
            .EnumerateFiles(fixture.Project.Paths.Assets, "*" + ShaderGraphDocument.Extension, SearchOption.AllDirectories)
            .ToArray();

        Assert.Single(created);

        var document = Assert.Single(fixture.Project.Documents.OfType<ShaderGraphDocument>());

        Assert.True(fixture.Project.Assets.TryGetByPath(fixture.Project.Paths.Relative(created[0]), out var entry));

        var view = fixture.Control<ShaderGraphView>("asset." + entry.Guid);

        // Opening compiled it, so the pane holds Raven and the graph's own name is in it.
        Assert.Contains("shader New", view.Generated.Source, StringComparison.Ordinal);
        Assert.Empty(document.SourceDiagnostics);
    }

    /// <summary>The panel's factory runs again on a reopen, and nothing durable lived in the view.</summary>
    [Fact]
    public void The_panel_survives_being_closed_and_reopened() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-shader-graph").Settle();

        var path = Directory
            .EnumerateFiles(fixture.Project.Paths.Assets, "*" + ShaderGraphDocument.Extension, SearchOption.AllDirectories)
            .First();

        Assert.True(fixture.Project.Assets.TryGetByPath(fixture.Project.Paths.Relative(path), out var entry));

        var panel = "asset." + entry.Guid;

        fixture.Close(panel).Settle();
        fixture.Open(panel).Settle();

        Assert.Contains(
            "shader New",
            fixture.Control<ShaderGraphView>(panel).Generated.Source,
            StringComparison.Ordinal
        );
    }

    /// <summary>A material's "Open shader graph" opens the graph, which is what it had never done.</summary>
    /// <remarks>
    ///     ⚠ <b>The button and its event were built with the material editor and nothing listened.</b>
    ///     <c>Vixen.Editor.AssetEditors</c> has no panels and no docking, so opening a second document
    ///     is the application's — and until a shader graph editor existed there was nothing to open.
    /// </remarks>
    [Fact]
    public void A_material_opens_the_graph_it_was_authored_in() {
        using var fixture = EditorSession.Start();

        // Written rather than created from the menu, because the create command opens what it made —
        // and what is under test is the material opening it.
        var graphPath = Path.Combine(fixture.Project.Paths.Assets, "Paint" + ShaderGraphDocument.Extension);

        File.WriteAllText(graphPath, string.Empty);
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(fixture.Project.Paths.Relative(graphPath), out var graph));

        var materialPath = Path.Combine(fixture.Project.Paths.Assets, "Painted.vxmat");

        File.WriteAllText(materialPath, new MaterialAsset { Graph = graph.Guid }.ToYaml());
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/Painted.vxmat", out var material));

        fixture.Editor.OpenAsset(material.Guid);
        fixture.Settle();

        // One document so far: the material. The graph opens when the button is pressed and not
        // before, which is the whole difference between a link and an eager load.
        Assert.Empty(fixture.Project.Documents.OfType<ShaderGraphDocument>());

        fixture.Ui.Contains("Open shader graph").Click();
        fixture.Settle();

        var opened = Assert.Single(fixture.Project.Documents.OfType<ShaderGraphDocument>());

        Assert.Equal(graph.Guid, opened.Asset);
    }
}
