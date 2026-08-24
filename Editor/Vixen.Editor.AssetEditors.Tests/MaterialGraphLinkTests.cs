// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The material panel's graph link: shown, greyed, and what it says while it is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The state wave 7's whole-tree dumps never reached.</b> Four states were dumped and all
///         four were byte-identical, but every one of them had an empty <c>Header.Graph</c> — so the
///         link was <c>hidden</c> throughout and only one of its three arms was ever drawn. The port
///         replaced three imperative property writes in <c>Restate</c> with one
///         <c>MaterialGraphLink</c> record behind three bindings, and the failure that record exists
///         to make unstateable lives precisely in the arm the dumps missed: a button that says
///         "Open shader graph" while sitting greyed out, because the label binding and the
///         <c>Disabled</c> binding depended on different things and only one of them re-ran.
///     </para>
///     <para>
///         ⚠ <b>Resolved against the asset database rather than assumed to exist</b>, which is the
///         behaviour under test as much as the bindings are. A material outlives the graph it was
///         generated from often enough — a graph deleted, a branch without it — and a button that
///         opened nothing is worse than one that says why it cannot.
///     </para>
/// </remarks>
public class MaterialGraphLinkTests {
    static MaterialDocument Open(ViewHarness harness) {
        var asset = new MaterialAsset {
            Shader = "Standard",
            Parameters = [new ScalarParameter { Name = "roughness", Value = 0.25f }]
        };

        var path = harness.Project.WriteAsset("Assets/stone.vxmat", asset.ToYaml());

        return new(harness.Project.Project, AssetId.New(), path);
    }

    /// <summary>A material naming no graph offers no button, and the label is the resting one.</summary>
    [Fact]
    public void AMaterialWithNoGraphHidesTheButton() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.True(view.OpenGraph.HasClass("hidden"));
        Assert.False(view.OpenGraph.Disabled);
    }

    /// <summary>
    ///     ⚠ A material naming a graph the project no longer has shows the button, greys it, <b>and</b>
    ///     says why — all three, which is the whole point of the three facts being one record.
    /// </summary>
    [Fact]
    public void AMaterialNamingAMissingGraphSaysSoOnTheButtonItGreys() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        document.Header.Graph = AssetId.New();
        view.Rebuild();
        harness.Ui.Frames(3);

        Assert.False(view.OpenGraph.HasClass("hidden"));
        Assert.True(view.OpenGraph.Disabled);
        Assert.Equal("Shader graph is missing from this project", view.OpenGraph.Label);
    }

    /// <summary>
    ///     ⚠ And it goes back. Clearing the graph re-hides the button and puts the resting label back,
    ///     which is the direction a stale binding fails in — the record changed, so all three bindings
    ///     re-ran or none did.
    /// </summary>
    [Fact]
    public void ClearingTheGraphPutsTheButtonBack() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);

        document.Header.Graph = AssetId.New();
        view.Rebuild();
        harness.Ui.Frames(3);

        Assert.True(view.OpenGraph.Disabled);

        document.Header.Graph = AssetId.Empty;
        view.Rebuild();
        harness.Ui.Frames(3);

        Assert.True(view.OpenGraph.HasClass("hidden"));
        Assert.False(view.OpenGraph.Disabled);
        Assert.Equal("Open shader graph", view.OpenGraph.Label);
    }
}
