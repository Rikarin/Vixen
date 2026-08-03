// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Lines a component's own gizmo draws, and who they are drawn for.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's <c>AddGizmo</c>.</b> <c>DeclaredContributionTests</c> asserts that the
///         <c>[DrawGizmo]</c> declaration <i>arrives</i> in both tiers; this asserts what the arriving
///         thing then does — which entities it runs for, and what turns it off.
///     </para>
///     <para>
///         ⚠ <b>A <c>ComponentBridge&lt;Camera&gt;</c> built here rather than the application's bridge
///         list.</b> The list is assembled by <c>ComponentsView.Default</c> in the application, which
///         this assembly cannot see and should not need to: what the pass depends on is
///         <c>IComponentBridge</c>, so a test that supplies one is testing the dependency rather than
///         the arrangement round it.
///     </para>
/// </remarks>
public class ComponentGizmoTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-gizmos-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly EditorRegistry registry = new();
    readonly ComponentGizmos gizmos;

    public ComponentGizmoTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
        gizmos = new([new ComponentBridge<Camera>("Camera")], registry);
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Records what it was called with rather than drawing anything.</summary>
    sealed class Spy {
        public List<GizmoPlacement> Placements { get; } = [];

        public List<bool> Selected { get; } = [];

        public void Draw(GizmoDraw draw, object component, GizmoPlacement placement, bool selected) {
            Placements.Add(placement);
            Selected.Add(selected);
        }
    }

    static GizmoDraw Sink() => new([]);

    [Fact]
    public void It_runs_for_the_entities_carrying_the_component_and_no_others() {
        var spy = new Spy();

        registry.Add(new ComponentGizmo(typeof(Camera), spy.Draw));

        scene.Add("Empty", LocalTransform.Identity);
        scene.CreateCamera(LocalTransform.At(new Vector3(1f, 2f, 3f)));
        scene.Add("Another Empty", LocalTransform.Identity);

        gizmos.Build(scene, Sink());

        var placement = Assert.Single(spy.Placements);

        Assert.Equal(new Vector3(1f, 2f, 3f), placement.Position);
    }

    /// <summary>
    ///     ⚠ <b>The unselected case first, because it is the one that would pass by accident.</b> A
    ///     pass that ignored <c>SelectedOnly</c> entirely draws for the selected entity too, so
    ///     asserting only "it draws when selected" would not notice the flag being unread — which is
    ///     the failure this whole document calls an attribute that looks like a mechanism.
    /// </summary>
    [Fact]
    public void Selected_only_draws_nothing_until_something_is_selected() {
        var spy = new Spy();

        registry.Add(new ComponentGizmo(typeof(Camera), spy.Draw, SelectedOnly: true));

        var camera = scene.CreateCamera(LocalTransform.Identity);

        gizmos.Build(scene, Sink());

        Assert.Empty(spy.Placements);

        scene.Selection.Set(camera);
        gizmos.Build(scene, Sink());

        Assert.Single(spy.Placements);
        Assert.True(Assert.Single(spy.Selected));
    }

    [Fact]
    public void A_component_nothing_bridges_is_skipped_rather_than_throwing() {
        var spy = new Spy();

        registry.Add(new ComponentGizmo(typeof(ComponentGizmoTests), spy.Draw));

        scene.CreateCamera(LocalTransform.Identity);
        gizmos.Build(scene, Sink());

        Assert.Empty(spy.Placements);
    }

    [Fact]
    public void Two_gizmos_run_in_order() {
        List<string> ran = [];

        registry.Add(new ComponentGizmo(typeof(Camera), (_, _, _, _) => ran.Add("second"), Order: 20));
        registry.Add(new ComponentGizmo(typeof(Camera), (_, _, _, _) => ran.Add("first"), Order: 10));

        scene.CreateCamera(LocalTransform.Identity);
        gizmos.Build(scene, Sink());

        Assert.Equal(["first", "second"], ran);
    }

    /// <summary>
    ///     ⚠ <b>Through <c>SceneLines</c> rather than the pass, because the show flag is the pane's
    ///     and the pass has never heard of it.</b> This is the only test that would catch the flag
    ///     being wired to the wrong bit — or to <c>SceneShow.Gizmos</c>, which is the transform
    ///     handles and would have made turning the handles off hide every trigger volume in the level.
    /// </summary>
    [Fact]
    public void The_component_show_flag_turns_them_off_and_leaves_the_handles_alone() {
        using var pane = new Pane();
        var lines = new SceneLines();

        registry.Add(
            new ComponentGizmo(
                typeof(Camera),
                static (draw, _, placement, _) => draw.Sphere(placement.Position, 1f, new(1f, 0f, 0f, 1f))
            )
        );

        scene.CreateCamera(LocalTransform.Identity);
        pane.Viewport.Gizmos = gizmos;

        pane.Viewport.Show = SceneShow.Default;
        lines.Build(scene, pane.Viewport, 600);

        var withGizmos = lines.World.Count;

        pane.Viewport.Show = SceneShow.Default & ~SceneShow.Components;
        lines.Build(scene, pane.Viewport, 600);

        var without = lines.World.Count;

        Assert.Equal(GizmoDraw.Segments * 3 * 2, withGizmos - without);

        // And the handles, which live in the other list, are untouched by either.
        pane.Viewport.Show = SceneShow.Default & ~SceneShow.Gizmos;
        lines.Build(scene, pane.Viewport, 600);

        Assert.Equal(withGizmos, lines.World.Count);
    }

    [Fact]
    public void A_pane_with_no_gizmos_draws_what_it_always_did() {
        using var pane = new Pane();
        var lines = new SceneLines();

        scene.CreateCamera(LocalTransform.Identity);
        lines.Build(scene, pane.Viewport, 600);

        var bare = lines.World.Count;

        pane.Viewport.Gizmos = gizmos;
        lines.Build(scene, pane.Viewport, 600);

        Assert.Equal(bare, lines.World.Count);
    }
}
