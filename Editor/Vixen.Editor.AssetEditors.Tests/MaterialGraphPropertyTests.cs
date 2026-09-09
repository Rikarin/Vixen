// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Rendering.Materials;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>Authoring a graph-composed material's properties in the panel rather than in the YAML.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/494">#494</a>'s gap, and it was the
///         plain one: a mechanism whose caller did not exist.</b> A <c>.vxmat</c> could name a graph's
///         surface, and <c>ShaderGraphMaterial.Values</c> could say exactly which properties an author
///         must fill in — and a sweep of <c>.cs</c> <em>and</em> <c>.vxml</c> found that method called
///         from tests and from nowhere else. So the only way to give a graph material its values was
///         to hand-edit the file.
///     </para>
///     <para>
///         ⚠ <b>The half that is easy to ship broken is the write, not the read.</b>
///         <c>MaterialAsset.Features</c> was carried and never written, precisely so that opening a
///         material with features and saving it did not delete them — so the first path that writes
///         one turns that guard into the loss it was guarding against unless it replaces in place.
///         <see cref="AnEditKeepsEveryOtherFeature" /> is that case, and it is the one this file
///         would be worth writing for on its own.
///     </para>
///     <para>
///         ⚠ <b>Ask what these say if the panel did nothing.</b> Every case below reads the
///         <em>document</em> back rather than the control it typed into, and
///         <see cref="TheRowsAreTheGraphSPropertiesAndNotEveryDeclaration" /> asserts a row count
///         against a graph that declares more properties than a material sets — so a panel that built
///         no rows fails, and one that built a row per declaration fails the other way.
///     </para>
/// </remarks>
public class MaterialGraphPropertyTests {
    /// <summary>A surface graph with a number, a colour and a texture the host owns.</summary>
    /// <remarks>
    ///     ⚠ The <c>Texture/Sample 2D</c> is what makes the second case able to fail. It declares a
    ///     <c>uint</c> slot index a host writes from the bindless table every frame, and
    ///     <c>ShaderGraphMaterial.Values</c> exists to keep it off an author's panel — so a graph with
    ///     only the two editable properties in it could not tell a filtered list from an unfiltered
    ///     one.
    /// </remarks>
    static string Graph() {
        NodeGraphModel graph = new() { Name = "AuthoredSurface" };
        var tint = graph.Add("Input/Colour Property");
        var rough = graph.Add("Input/Float Property");
        var sample = graph.Add("Texture/Sample 2D");
        var multiply = graph.Add("Math/Multiply");
        var master = graph.Add("Master/Surface");

        tint.SetText(ShaderProperties.Key, "tint");
        rough.SetText(ShaderProperties.Key, "roughness");
        sample.SetText(ShaderProperties.Key, "albedo");

        graph.Connect(new(sample.Id, "RGBA"), new(multiply.Id, "A"));
        graph.Connect(new(tint.Id, "Colour"), new(multiply.Id, "B"));
        graph.Connect(new(multiply.Id, "Out"), new(master.Id, "BaseColour"));
        graph.Connect(new(rough.Id, "Out"), new(master.Id, "Roughness"));

        return YamlSerializer.ToYaml(NodeGraphDocument.Save(graph));
    }

    /// <summary>A graph with a standalone master, which is a whole shader and not a surface.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Master/Unlit</c> rather than a graph that fails to compile</b>, because the two
    ///     failures the panel has to keep apart are "this does not compile" and "this compiles and is
    ///     the wrong shape". Only the second has a sentence an author can act on — add a
    ///     <c>Master/Surface</c> — and only the second can be lost by a record that carries a
    ///     compilation for a surface graph alone.
    /// </remarks>
    static string Standalone() {
        NodeGraphModel graph = new() { Name = "AuthoredStandalone" };
        var tint = graph.Add("Input/Colour Property");
        var master = graph.Add("Master/Unlit");

        tint.SetText(ShaderProperties.Key, "tint");
        graph.Connect(new(tint.Id, "Colour"), new(master.Id, "Colour"));

        return YamlSerializer.ToYaml(NodeGraphDocument.Save(graph));
    }

    /// <summary>A material linked to the surface graph, open.</summary>
    static MaterialDocument Open(ViewHarness harness, params IMaterialFeature[] features) =>
        Open(harness, Graph(), features);

    /// <summary>A material linked to a graph this test wrote, open.</summary>
    /// <param name="harness">The project and UI the document is opened over.</param>
    /// <param name="graph">The <c>.vxshadergraph</c>'s YAML.</param>
    /// <param name="features">What the material already carries.</param>
    /// <returns>The open document.</returns>
    /// <remarks>
    ///     ⚠ The sidecar and the rescan are what make the link <em>resolve</em>. The fixture opens the
    ///     project before a test writes anything, so a graph written and not scanned is
    ///     indistinguishable from one that was deleted — which is a real state this panel reports and
    ///     a useless one to test the property rows through.
    /// </remarks>
    static MaterialDocument Open(ViewHarness harness, string graph, params IMaterialFeature[] features) {
        var linked = AssetId.Parse("0123456789abcdef0123456789abcdef");

        harness.Project.WriteAsset(
            "Assets/AuthoredSurface.vxshadergraph",
            graph,
            "guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 1\n"
        );

        MaterialAsset asset = new() { Shader = "AuthoredSurface", Graph = linked, Features = [.. features] };
        var path = harness.Project.WriteAsset("Assets/stone.vxmat", asset.ToYaml());

        harness.Project.Project.Assets.Scan();

        return new(harness.Project.Project, AssetId.New(), path);
    }

    /// <summary>A graph with a standalone master is named as one rather than called broken.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1117">#1117</a>'s trap, from the
    ///         side that loses a sentence.</b> <c>ReadGraph</c> now takes its compilation from
    ///         <c>ShaderGraphSources</c>, which packages a standalone graph as a record whose
    ///         generated <em>text</em> is empty — deliberately, since a standalone graph contributes
    ///         no Raven to a shader build. A panel that read "no text" as "did not compile" would
    ///         answer an author who needs to add a <c>Master/Surface</c> with a sentence about a
    ///         compilation that in fact succeeded, and there is nothing in that sentence to act on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted because either alone passes on the wrong tree.</b> The
    ///         standalone sentence and the compile-failure sentence both leave
    ///         <c>GraphSource</c> null, so a test reading only that cannot tell them apart — which is
    ///         precisely the difference this case exists for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AStandaloneGraphIsNamedAsOneRatherThanReportedAsABrokenCompile() {
        using var harness = new ViewHarness();
        var document = Open(harness, Standalone());

        Assert.Null(document.GraphSource);

        var problem = Assert.IsType<string>(document.GraphProblem);

        Assert.Contains("standalone shader", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("does not compile", problem, StringComparison.Ordinal);
    }

    /// <summary>The linked graph compiles and reports the two properties a person fills in.</summary>
    [Fact]
    public void TheRowsAreTheGraphSPropertiesAndNotEveryDeclaration() {
        using var harness = new ViewHarness();
        var document = Open(harness);

        Assert.Null(document.GraphProblem);

        var source = Assert.IsType<ShaderGraphSource>(document.GraphSource);

        // The instrument: the graph declares the texture's slot index too, so a `Values` that had
        // stopped filtering would show three here and this would not be a claim about filtering.
        Assert.Contains(source.Properties, property => property.Type == "uint");

        Assert.Equal(
            ["roughness", "tint"],
            ShaderGraphMaterial.Values(source).Select(property => property.Name).Order(StringComparer.Ordinal)
        );

        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.Equal(2, view.GraphProperties.Children.Count);

        // ⚠ The width rule, which is the one decision this surface takes from `LayerStackView`
        // rather than re-deciding: a lane a number, from the property's type. A row of one field for
        // a `float4` is the narrowing #1097 is about — the first nudge would write nought over the
        // three lanes it did not draw.
        Assert.Equal([1, 4], Fields(view));
    }

    /// <summary>How many number fields each row drew, in the order the rows were built.</summary>
    static int[] Fields(MaterialView view) => [
        .. view.GraphProperties.Children.Select(row => row.Children.OfType<NumericInput>().Count())
    ];

    /// <summary>⚠ Typing into a row writes the file, which is the half a document test cannot see.</summary>
    /// <remarks>
    ///     Every other case here calls <c>SetGraphValue</c> directly, so all of them would pass
    ///     against a panel whose fields were wired to nothing — which is this workstream's commonest
    ///     defect exactly. This one moves the control.
    /// </remarks>
    [Fact]
    public void MovingAFieldWritesTheMaterial() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        // ⚠ Nothing yet, and building the rows must not have written anything: `Number` is a
        // `[UiProperty]` with a `Changed` callback, so a panel that subscribed before it assigned the
        // opening value would have written every property out at nought by now.
        Assert.Null(document.Surface);

        var tint = view.GraphProperties.Children
            .First(row => row.Children.OfType<NumericInput>().Count() == 4)
            .Children.OfType<NumericInput>()
            .ToArray();

        tint[2].Number = 0.75d;
        harness.Ui.Frames(2);

        var surface = Assert.IsType<GraphSurfaceFeature>(document.Surface);

        Assert.Equal("tint", Assert.Single(surface.Vectors).Name);
        Assert.Equal(new Vector4(0f, 0f, 0.75f, 0f), surface.Vectors[0].Value);
    }

    /// <summary>Setting a property writes a graph feature, and a float4 keeps all four lanes.</summary>
    [Fact]
    public void SettingAPropertyWritesTheFeatureTheRuntimeReads() {
        using var harness = new ViewHarness();
        var document = Open(harness);

        // ⚠ Nothing is written before an edit. A panel that seeded every property at its type's zero
        // would replace each graph default with black on open, which is this renderer's standing
        // "zero is a valid-looking value" trap and is invisible in a file diff nobody reads.
        Assert.Null(document.Surface);

        Assert.True(document.SetGraphValue("tint", new(0.8f, 0.2f, 0.1f, 1f)));
        Assert.True(document.SetGraphValue("roughness", new(0.35f, 9f, 9f, 9f)));

        var surface = Assert.IsType<GraphSurfaceFeature>(document.Surface);

        Assert.Equal("AuthoredSurface", surface.Shader);
        Assert.Equal(new Vector4(0.8f, 0.2f, 0.1f, 1f), Assert.Single(surface.Vectors).Value);

        // ⚠ And a `float` property takes X and drops the rest rather than being stored as a vector.
        // The runtime reads `Numbers` and `Vectors` as different parameter kinds, so a scalar written
        // into the wrong list is a parameter the shader never sees.
        Assert.Equal(0.35f, Assert.Single(surface.Numbers).Value);
        Assert.Equal("roughness", surface.Numbers[0].Name);

        // The graph's map, so that a host has a pairing to bind an index into at all.
        Assert.Equal("albedo", Assert.Single(surface.Maps).Texture);

        var written = MaterialAsset.FromYaml(document.ToYaml());

        Assert.Equal(0.35f, Assert.IsType<GraphSurfaceFeature>(Assert.Single(written.Features)).Numbers[0].Value);
    }

    /// <summary>⚠ Writing a graph feature does not delete the features the file already carried.</summary>
    /// <remarks>
    ///     The whole of what <c>MaterialAsset.Features</c>' own remarks warn about: the member exists
    ///     so that opening a material with features and saving it is not destructive, and the first
    ///     path that <em>writes</em> a feature is where that stops being true for free.
    /// </remarks>
    [Fact]
    public void AnEditKeepsEveryOtherFeature() {
        using var harness = new ViewHarness();
        var document = Open(harness, new MetalRoughnessFeature());

        Assert.True(document.SetGraphValue("roughness", new(0.5f, 0f, 0f, 0f)));

        Assert.Equal(2, document.Material.Features.Count);
        Assert.IsType<MetalRoughnessFeature>(document.Material.Features[0]);
        Assert.IsType<GraphSurfaceFeature>(document.Material.Features[1]);

        // And a second edit replaces the graph feature in place rather than appending another.
        Assert.True(document.SetGraphValue("roughness", new(0.75f, 0f, 0f, 0f)));

        Assert.Equal(2, document.Material.Features.Count);
        Assert.Equal(0.75f, Assert.IsType<GraphSurfaceFeature>(document.Material.Features[1]).Numbers[0].Value);
    }

    /// <summary>An undo puts the feature back exactly as it was, including having been absent.</summary>
    /// <remarks>
    ///     ⚠ The absent case is the one worth asserting. A command whose undo wrote an empty feature
    ///     back would leave the material composing a shader with every property at zero, which draws
    ///     and looks wrong rather than failing.
    /// </remarks>
    [Fact]
    public void UndoingTheFirstEditLeavesNoFeatureAtAll() {
        using var harness = new ViewHarness();
        var document = Open(harness);

        Assert.True(document.SetGraphValue("roughness", new(0.5f, 0f, 0f, 0f)));
        document.Stack.Seal();

        Assert.NotNull(document.Surface);

        Assert.True(document.Stack.Undo());

        Assert.Null(document.Surface);
        Assert.Empty(document.Material.Features);
    }

    /// <summary>A material naming a graph the project has not got says so and offers no rows.</summary>
    [Fact]
    public void AMissingGraphIsASentenceRatherThanAnEmptySection() {
        using var harness = new ViewHarness();

        MaterialAsset asset = new() { Shader = "Standard", Graph = AssetId.New() };
        var path = harness.Project.WriteAsset("Assets/orphan.vxmat", asset.ToYaml());
        MaterialDocument document = new(harness.Project.Project, AssetId.New(), path);

        Assert.Null(document.GraphSource);
        Assert.NotNull(document.GraphProblem);
        Assert.Contains("not in the project", document.GraphProblem, StringComparison.Ordinal);

        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.Empty(view.GraphProperties.Children);
        Assert.False(view.GraphBroken.HasClass("hidden"));
    }

    /// <summary>⚠ Re-reading the panel after an edit pushes nothing, so an undo can reach the file.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one case that makes the equality guard in the row's handler load-bearing.</b>
    ///         <c>Reload</c> assigns <c>Number</c> on every field with the change handler attached —
    ///         which is what a field re-reading the document <em>means</em> — so without the guard a
    ///         re-read is an edit, and the undo that caused the re-read is followed straight back by
    ///         a command restoring what it undid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written after the ordering it was assumed to be was measured and was not.</b>
    ///         Assigning the opening value before subscribing looks like the thing preventing this,
    ///         and reversing those two lines leaves every other case here green: a
    ///         <c>[UiProperty]</c> raises <c>Changed</c> only on a real change, and an unset
    ///         property's opening value is the field's own default already.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ReloadingAfterAnEditPushesNothing() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.True(document.SetGraphValue("roughness", new(0.5f, 0f, 0f, 0f)));
        document.Stack.Seal();

        view.Reload();
        harness.Ui.Frames(2);

        Assert.Equal(0.5f, Assert.IsType<GraphSurfaceFeature>(document.Surface).Numbers[0].Value);

        // One undo, and the material is back to having no feature at all. A reload that had pushed a
        // command of its own would be undone by this instead, leaving the feature in place.
        Assert.True(document.Stack.Undo());
        Assert.Null(document.Surface);
    }

    /// <summary>A property the graph does not declare is refused rather than written.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a <c>GraphSurfaceNumber</c> naming nothing is not an error anywhere
    ///     downstream.</b> <c>GraphSurfaceFeature.Compile</c> sets a parameter by name and a name no
    ///     shader declares is a parameter nothing reads — so a typo would be saved, drawn, and
    ///     silent.
    /// </remarks>
    [Fact]
    public void APropertyTheGraphDoesNotDeclareIsRefused() {
        using var harness = new ViewHarness();
        var document = Open(harness);

        Assert.False(document.SetGraphValue("roughnes", new(0.5f, 0f, 0f, 0f)));
        Assert.Null(document.Surface);

        // ⚠ And the texture's slot index is refused by the same gate, which is `Values`' filter doing
        // the work rather than a second list here: it is a declared property of the graph.
        Assert.False(document.SetGraphValue("albedoIndex", new(3f, 0f, 0f, 0f)));
        Assert.Null(document.Surface);
    }

    /// <summary>⚠ Clearing the link takes the rows with it, and the rows stop being able to write.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One click on the asset field's <em>Clear</em>, which is a gesture and not an edge
    ///         case.</b> The rebuild first went in after <c>Restate</c>'s existing early return for
    ///         an empty link, so the unlinked branch never reached it: the previous graph's rows
    ///         stayed on screen, still bound, and nudging one wrote a <c>GraphSurfaceFeature</c>
    ///         naming a shader the material no longer links — beside a <c>Graph:</c> of all zeroes in
    ///         the same file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because the visible one is not the dangerous one.</b> Rows nobody
    ///         can see are a cosmetic defect; a <c>SetGraphValue</c> that still succeeds is a
    ///         <c>.vxmat</c> the content build then has to resolve a surface for. So this asserts the
    ///         panel is empty <em>and</em> that the document refuses the write.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ClearingTheLinkTakesTheRowsAndTheWritesWithIt() {
        using var harness = new ViewHarness();
        var document = Open(harness);
        var view = harness.Ui.Document.Root.Add<MaterialView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.Equal(2, view.GraphProperties.Children.Count);

        document.Header.Graph = AssetId.Empty;

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.Empty(view.GraphProperties.Children);
        Assert.False(document.SetGraphValue("roughness", new(0.5f, 0f, 0f, 0f)));
    }

    /// <summary>⚠ Moving the link to another graph does not carry the old graph's texture slots over.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of a relink that is invisible.</b> The numbers are obviously the old
    ///         graph's and an author would see them; the <c>Maps</c> are not, and they decide where
    ///         the bindless table is written: <c>AssetMaterialSource.Pair</c> keys every slot on
    ///         <c>{shader}.{chain}.{graph}.{slot}</c>. A feature carrying graph A's slots under graph
    ///         B's name means none of B's own slots is ever written, and each one falls back to the
    ///         table's placeholder view — a wrong texture on the surface, with nothing logged.
    ///     </para>
    ///     <para>
    ///         ⚠ The old code took <c>before.Maps</c> whenever a feature existed while always
    ///         re-reading <c>Shader</c> from the fresh compilation, so the two halves of the feature
    ///         came from two different graphs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void RelinkingToAnotherGraphReSeedsTheFeatureRatherThanKeepingTheOldOnes() {
        using var harness = new ViewHarness();
        var document = Open(harness);

        Assert.True(document.SetGraphValue("roughness", new(0.25f, 0f, 0f, 0f)));

        var first = Assert.IsType<GraphSurfaceFeature>(document.Surface);

        Assert.Equal("AuthoredSurface", first.Shader);
        Assert.NotEmpty(first.Numbers);

        // A second graph, under its own guid and its own name, with one property and no texture — so
        // "the maps came from the other graph" is a difference this fixture can see.
        NodeGraphModel second = new() { Name = "SecondSurface" };
        var rough = second.Add("Input/Float Property");
        var master = second.Add("Master/Surface");

        rough.SetText(ShaderProperties.Key, "roughness");
        second.Connect(new(rough.Id, "Out"), new(master.Id, "Roughness"));

        harness.Project.WriteAsset(
            "Assets/SecondSurface.vxshadergraph",
            YamlSerializer.ToYaml(NodeGraphDocument.Save(second)),
            "guid: fedcba9876543210fedcba9876543210\nmetaVersion: 1\n"
        );

        harness.Project.Project.Assets.Scan();

        document.Header.Graph = AssetId.Parse("fedcba9876543210fedcba9876543210");
        document.ReadGraph();

        Assert.True(document.SetGraphValue("roughness", new(0.75f, 0f, 0f, 0f)));

        var after = Assert.IsType<GraphSurfaceFeature>(document.Surface);

        Assert.Equal("SecondSurface", after.Shader);

        // The instrument: the first graph *had* a map, so an empty list here is a re-seed and not a
        // fixture that never had one to carry.
        Assert.NotEmpty(first.Maps);
        Assert.Empty(after.Maps);

        // And the numbers are the new graph's own, not the old graph's entry kept and shadowed.
        Assert.Equal(0.75f, Assert.Single(after.Numbers).Value);
    }
}
