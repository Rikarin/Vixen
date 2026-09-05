// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A plugin claims a file extension and gives it back, which is what #739 was for.</summary>
/// <remarks>
///     ⚠ <b>The first asset editor in this build registered by anything other than the
///     application.</b> <c>StandardEditors.CreateDefault</c> builds the registry once for the life of
///     the process, so nothing had ever needed <c>AssetEditorRegistry.Add</c> to hand back a removal
///     — and a plugin that registered there without one leaked its whole assembly, permanently and
///     with no error anywhere.
/// </remarks>
public class TexturingClaimTests {
    [Fact]
    public void The_module_claims_the_extension_and_the_create_entry_opens() {
        using var fixture = new TexturingFixture(editors: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.True(fixture.Editors.TryGetForFile("Assets/Bricks" + TextureGraphDocument.Extension, out var editor));
        Assert.Equal("Texture Graph", editor.Name);

        // ⚠ Both extensions, and this is the tripwire #806 predicted firing correctly. It read
        // `Assert.Single(…)` — so registering `.vxlayers` turned it red the moment the fifteen
        // missing lines existed, which is exactly what a roll call over registrations is for. Grown
        // to *name* both rather than to count two: the finding was that one of the two was missing,
        // and a count cannot tell which.
        Assert.True(fixture.Editors.TryGetForFile("Assets/Hull" + Layers.LayerStackDocument.Extension, out var stack));
        Assert.Equal("Layer Stack", stack.Name);

        Assert.Equal(
            [Layers.LayerStackDocument.Extension, TextureGraphDocument.Extension],
            fixture.Extensions.All<NewAssetKind>().Select(kind => kind.Extension).Order(StringComparer.Ordinal)
        );

        // ⚠ Derived rather than declared. The kind said `Opens: false` for as long as claiming an
        // extension was not undoable, and a constant either way is a claim about the host rather than
        // about this one.
        Assert.All(fixture.Extensions.All<NewAssetKind>(), kind => Assert.True(kind.Opens));
    }

    /// <summary>And a double-click opens the graph the registry's own way.</summary>
    [Fact]
    public void The_registry_opens_a_texture_graph_through_the_plugins_factory() {
        using var fixture = new TexturingFixture(editors: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var asset = fixture.AddGraph("Bricks");

        Assert.True(fixture.Editors.TryOpen(fixture.Project, asset, out var document));

        var graph = Assert.IsType<TextureGraphDocument>(document);

        Assert.Contains(graph.Graph.Nodes, node => node.Type == "Output/Output");
    }

    /// <summary>⚠ And unloading gives the extension back, so a reload can claim it again.</summary>
    /// <remarks>
    ///     The half that had no way to happen. Both dictionaries: a removal that freed the name and
    ///     left the extension taken would let a reload past the first check and fail at the second,
    ///     reported against the plugin doing the reloading.
    /// </remarks>
    [Fact]
    public void Unloading_gives_the_extension_back() {
        using var fixture = new TexturingFixture(editors: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        // Two factories now — the graph's and the stack's — and both are the module's to give back.
        Assert.Equal(2, fixture.Editors.Count);
        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        Assert.Equal(0, fixture.Editors.Count);
        Assert.False(fixture.Editors.TryGetForFile("Assets/Bricks" + TextureGraphDocument.Extension, out _));
        Assert.False(fixture.Editors.TryGetForFile("Assets/Hull" + Layers.LayerStackDocument.Extension, out _));

        // The claim a reload rests on, asserted rather than assumed.
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Equal(2, fixture.Editors.Count);
    }

    /// <summary>A host with no asset-editor registry still gets the module, and says so honestly.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TryGet</c> rather than <c>Require</c>.</b> A module that demanded the registry
    ///     would refuse to start in every host that is not the editor — which is every test of
    ///     everything else it does — and doc 36's rule for an extension point a plugin can do without
    ///     is that it carries on without it.
    /// </remarks>
    [Fact]
    public void A_host_with_no_registry_gets_a_create_entry_that_does_not_open() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Empty(fixture.Host.Diagnostics);

        // Every kind, because the honesty is per-kind: a module that derived `Opens` for one document
        // and hard-coded it for the other would satisfy any assertion over just the first.
        Assert.All(fixture.Extensions.All<NewAssetKind>(), kind => Assert.False(kind.Opens));
        Assert.Equal(2, fixture.Extensions.All<NewAssetKind>().Count);
    }
}
