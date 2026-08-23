// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.Assets;
using Vixen.Editor.SceneView;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A component of the shape a game declares, so the pane sees what a game's scene holds.</summary>
[Component]
[DataContract("CompiledSceneTestHealth")]
public struct CompiledHealth {
    /// <summary>How much is left.</summary>
    public int Value { get; set; }
}

/// <summary>A second one, so two entities can differ in archetype rather than only in value.</summary>
[Component]
[DataContract("CompiledSceneTestArmour")]
public struct CompiledArmour {
    /// <summary>How much it absorbs.</summary>
    public float Absorption { get; set; }
}

/// <summary>The pane that shows what a build makes of the open scene.</summary>
/// <remarks>
///     <para>
///         <b>What is asserted is the compile, not the pixels.</b> The tables are a projection of a
///         <c>SceneContent</c> and a list of diagnostics; the interesting claims are that the pane
///         compiles the document it is showing, that it groups by archetype the way a build does,
///         that a prefab is compiled as a prefab, and that a scene which would fail a build says so
///         here instead of looking fine.
///     </para>
///     <para>
///         ⚠ <b>The elements are asserted too, but for one reason only</b>: a projection that
///         produced the right numbers and drew none of them would pass every test about the numbers.
///     </para>
/// </remarks>
public sealed class CompiledSceneTests {
    public CompiledSceneTests() {
        SceneComponentRegistry.Register<CompiledHealth>();
        SceneComponentRegistry.Register<CompiledArmour>();
    }

    /// <summary>Nothing is compiled until asked, and the pane says so.</summary>
    /// <remarks>
    ///     ⚠ Opening a level would otherwise pay for a full compile before the first frame, for a
    ///     tab nobody may look at.
    /// </remarks>
    [Fact]
    public void ShowingADocumentDoesNotCompileIt() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(Scene(harness, world));

        Assert.Null(view.Content);
        Assert.Empty(view.Reported);
        Assert.False(view.Status.HasClass("hidden"));
    }

    /// <summary>Compiling produces the content a build would, grouped by archetype.</summary>
    [Fact]
    public void CompilingGroupsTheEntitiesTheWayABuildDoes() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        // Two carrying one component, one carrying two, one carrying none — four entities that a
        // build has to put in three blocks.
        Entity(scene, world, "Guard", new CompiledHealth { Value = 10 });
        Entity(scene, world, "Sentry", new CompiledHealth { Value = 20 });

        var armoured = Entity(scene, world, "Knight", new CompiledHealth { Value = 30 });

        world.Add(armoured, new CompiledArmour { Absorption = 0.5f });

        Entity(scene, "Marker");

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(scene);

        Assert.True(view.Refresh());
        Assert.NotNull(view.Content);
        Assert.Equal(4, view.Content.Count);

        // Three archetypes: Health, Health+Armour, and nothing.
        Assert.Equal(3, view.Content.Blocks.Length);

        // ⚠ The numbers are right when `Refresh` returns and the tables are right on the next
        // flush, which is `EffectScheduler`'s contract rather than this panel's choice (ADR-007).
        // The two claims are asserted on either side of the frame deliberately: the pane is a
        // markup panel since wave 5, and what a caller may still read back synchronously is
        // exactly `Refresh`'s answer, `Content` and `Reported`.
        harness.Ui.Frame();

        Assert.True(view.Status.HasClass("hidden"));

        // And the pane drew them, which is the half a claim about the numbers does not make.
        Assert.Equal(3, view.Blocks.Children.Count);
        Assert.NotEmpty(view.Facts.Children);
    }

    /// <summary>
    ///     ⚠ <b>The pane compiles what is in front of the author, so an edit changes it.</b>
    /// </summary>
    /// <remarks>
    ///     This is the whole of the choice the pane makes — compile the open document rather than
    ///     read the artefact the last import wrote — and it is the difference an author would notice
    ///     first. Adding an entity and compiling again has to move the count without anything being
    ///     saved or imported.
    /// </remarks>
    [Fact]
    public void AnEditChangesWhatTheNextCompileShows() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        Entity(scene, world, "Guard", new CompiledHealth { Value = 10 });

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(scene);
        Assert.True(view.Refresh());
        Assert.Equal(1, view.Content!.Count);

        Entity(scene, world, "Sentry", new CompiledHealth { Value = 20 });

        Assert.True(view.Refresh());
        Assert.Equal(2, view.Content!.Count);
    }

    /// <summary>Stripping the names changes the asset, which is why the switch is on the pane.</summary>
    [Fact]
    public void StrippingTheNamesIsVisibleInTheResult() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        Entity(scene, world, "Guard", new CompiledHealth { Value = 10 });

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(scene);
        view.Refresh();

        Assert.NotEmpty(view.Content!.Names);

        // Flipping it recompiles, because there is now something to recompile — see the handler.
        view.KeepNames.IsChecked = false;

        Assert.NotNull(view.Content);
        Assert.Empty(view.Content!.Names);
    }

    /// <summary>
    ///     ⚠ <b>A prefab is compiled as a prefab</b>, so its one-root rule is checked here as the
    ///     build checks it — rather than this being the one pane where a two-rooted prefab looks fine.
    /// </summary>
    [Fact]
    public void APrefabWithTwoRootsFailsInThePaneAsItWouldInABuild() {
        using var harness = new ViewHarness();
        using var world = new World("Prefab");

        var prefab = new SceneDocument(harness.Project.Project, world, AssetId.Empty, "Turret") {
            Writer = new PrefabFileWriter(System.IO.Path.Combine(harness.Project.Paths.Root, "Turret.vxprefab"))
        };

        Entity(prefab, world, "Base", new CompiledHealth { Value = 1 });

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(prefab);
        Assert.True(view.Refresh());

        // A second root is what a build refuses.
        Entity(prefab, world, "Loose", new CompiledHealth { Value = 2 });

        Assert.False(view.Refresh());
        Assert.Null(view.Content);
        Assert.Contains(view.Reported, entry => entry.Severity == ImportSeverity.Error);

        harness.Ui.Frame();

        // And it is drawn as well as recorded, with the status saying the build would fail too.
        Assert.NotEmpty(view.Diagnostics.Children);
        Assert.False(view.Status.HasClass("hidden"));
    }

    /// <summary>The same document opened as a scene compiles, which is what makes the above a rule.</summary>
    /// <remarks>
    ///     Without this, the test above would pass equally well against a pane that refused every
    ///     document with two roots — including the levels that are supposed to have them.
    /// </remarks>
    [Fact]
    public void TheSameTwoRootedDocumentCompilesAsAScene() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        Entity(scene, world, "One", new CompiledHealth { Value = 1 });
        Entity(scene, world, "Two", new CompiledHealth { Value = 2 });

        var view = harness.Ui.Document.Root.Add<CompiledSceneView>();

        view.Show(scene);

        Assert.True(view.Refresh());
        Assert.Equal(2, view.Content!.Count);
    }

    static SceneDocument Scene(ViewHarness harness, World world) =>
        new(harness.Project.Project, world, AssetId.Empty, "Level") {
            Writer = new SceneFileWriter(System.IO.Path.Combine(harness.Project.Paths.Root, "Level.vxscene"))
        };

    /// <summary>
    ///     An entity in the document, made the way a reader does rather than the way a button does.
    /// </summary>
    /// <remarks>
    ///     <c>Create</c> rather than reaching into the world, because the document has to know the
    ///     entity exists — the compiled pane reads `SceneSerializer.ToFile`, and an entity the
    ///     document never adopted is not in the file it produces.
    /// </remarks>
    static Entity Entity(SceneDocument scene, string name) =>
        scene.Create(name, LocalTransform.At(Vector3.Zero));

    static Entity Entity<T>(SceneDocument scene, World world, string name, in T component) {
        var entity = Entity(scene, name);

        world.Add(entity, in component);

        return entity;
    }
}
