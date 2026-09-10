// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Where a LOD group's thresholds fall, drawn in the viewport the author is standing in.</summary>
/// <remarks>
///     <para>
///         <b>A threshold is a fraction of the viewport's height and there was no way to see one.</b>
///         Judging <c>0.06</c> meant typing it and walking backwards until the mesh changed — which is
///         <a href="https://github.com/Rikarin/Vixen/issues/1173">#1173</a>'s item (2), and the half
///         its editor gesture left.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is a closed form and not a recorded number.</b> The runtime
///         switches when <c>radius × ScreenHeightScale / distance</c> crosses the threshold, so the
///         shell is at <c>radius × ScreenHeightScale / threshold</c> — and for a unit cube at the
///         pane's 60° default those two factors are <c>√3/2</c> and <c>√3</c>, whose product is
///         exactly <c>3/2</c>. So a shell lands at <c>1.5 / threshold</c> metres and a test can say
///         where it is rather than where it was.
///     </para>
///     <para>
///         ⚠ <b>Two fields of view, because one is satisfied by dropping the term altogether.</b> The
///         missing <c>1 / tan(fov / 2)</c> is precisely how an earlier attempt at this would have been
///         wrong — by about 1.7× at 60° — and it is why the drawing is here, where the camera is,
///         rather than in a contributed gizmo, which is handed no view at all.
///     </para>
/// </remarks>
public class LodRangeTests : IDisposable {
    const int Height = 800;

    /// <summary>Two vertices a segment, twenty-four segments a ring.</summary>
    const int RingVertices = 24 * 2;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-lod-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly TransformSystem transforms = new();
    readonly Pane pane = new();

    public LodRangeTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        pane.Dispose();
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>One shell per threshold, each at the distance the runtime actually switches at.</summary>
    [Fact]
    public void A_selected_group_gets_one_shell_per_threshold_where_the_level_changes() {
        var lines = new SceneLines();
        var group = Group(0.5f, 0.25f);

        scene.Selection.Set(group);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Equal(2 * RingVertices, lines.World.Count);

        // `radius × scale` is √3/2 × √3 = 1.5 for this cube at this field of view, so the shells are
        // at 1.5/0.5 and 1.5/0.25 — and every vertex of a ring is at its own radius, so the two
        // extremes are the two rings.
        Assert.Equal(3f, lines.World.Min(Reach), 3);
        Assert.Equal(6f, lines.World.Max(Reach), 3);

        // On the ground through the group's centre, which is what makes it a distance somebody walks.
        Assert.All(lines.World, vertex => Assert.Equal(0f, vertex.Position.Y, 4));
        Assert.All(lines.World, vertex => Assert.Equal(lines.LodRangeColour, vertex.Colour));
    }

    /// <summary>A longer lens moves the switch further out, by exactly the projection's factor.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion the one above cannot make.</b> A drawing that dropped the
    ///     <c>1 / tan(fov / 2)</c> term entirely would put the shells at <c>radius / threshold</c> and
    ///     satisfy nothing here, but a drawing that used any <em>constant</em> in its place would
    ///     satisfy a single-camera test forever — and being wrong by a constant is exactly what a
    ///     handle drawn without a view is.
    /// </remarks>
    [Fact]
    public void The_shells_move_with_the_field_of_view() {
        var lines = new SceneLines();
        var group = Group(0.5f);

        scene.Selection.Set(group);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        var wide = lines.World.Max(Reach);

        // 90°, where `1 / tan(fov / 2)` is one and the shell is the sphere's own radius over the
        // threshold — a different number from every other camera's, and a smaller one.
        Viewport.Camera.FieldOfView = MathUtil.DegreesToRadians(90f);
        lines.Build(scene, Viewport, Height);

        Assert.Equal(3f, wide, 3);
        Assert.Equal(MathF.Sqrt(3f) / 2f / 0.5f, lines.World.Max(Reach), 3);
        Assert.True(lines.World.Max(Reach) < wide, "a wider lens did not bring the switch closer");
    }

    /// <summary>A group nobody selected draws nothing, however many groups the scene has.</summary>
    /// <remarks>
    ///     A shell is tens of metres across. One per group in a scene of trees is a scene of circles,
    ///     which is why this is the selection's rather than a <see cref="SceneShow" /> flag's — the
    ///     same argument <c>SceneLines.Cage</c> makes for not being one either.
    /// </remarks>
    [Fact]
    public void An_unselected_group_draws_nothing() {
        var lines = new SceneLines();

        Group(0.5f, 0.25f);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    /// <summary>An orthographic pane gets none, because nothing switches level in one.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a gap in the drawing but the drawing agreeing with the frame.</b> A plan view's
    ///     <c>RenderView.ScreenHeightScale</c> is zero — <c>CameraExtractionSystem</c> and
    ///     <c>EditorWorldRenderer.Aim</c> both say so — and <c>LodRenderFeature.Prepare</c> skips such
    ///     a view, so there is no distance at which anything changes and a ring would be a promise the
    ///     frame does not keep.
    /// </remarks>
    [Fact]
    public void An_orthographic_pane_gets_no_shells() {
        var lines = new SceneLines();
        var group = Group(0.5f);

        scene.Selection.Set(group);
        Viewport.Show = SceneShow.None;
        Viewport.Camera.IsOrthographic = true;

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    /// <summary>A group of one level has no threshold, and no threshold is not a shell at zero.</summary>
    /// <remarks>
    ///     Null and empty both mean "one level" to <c>LodGroupComponent</c>, and a group of one never
    ///     switches. A drawing that treated a missing list as a threshold of nothing would divide by
    ///     it and put a ring at infinity through the whole scene.
    /// </remarks>
    [Fact]
    public void A_group_with_no_thresholds_gets_no_shells() {
        var lines = new SceneLines();
        var group = Group();

        scene.Selection.Set(group);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    /// <summary>
    ///     A group whose levels have no extent yet gets nothing rather than a shell of a made-up size.
    /// </summary>
    /// <remarks>
    ///     The parent of a group the editor's own <c>entity.group-lod</c> builds is an <em>empty</em> —
    ///     the levels are its children — so a drawing that measured the selected entity would never
    ///     appear on the one hierarchy the editor itself makes. This is that claim from the other side:
    ///     with the children's <c>LodLevel</c> taken away there is no level to measure and no shell.
    /// </remarks>
    [Fact]
    public void A_group_whose_children_are_not_levels_gets_no_shells() {
        var lines = new SceneLines();
        var group = Group(0.5f);

        List<Entity> members = [];

        foreach (var child in Hierarchy.ChildrenOf(world, group)) {
            members.Add(child);
        }

        foreach (var child in members) {
            world.Remove<LodLevel>(child);
        }

        world.AdvanceVersion();
        scene.Selection.Set(group);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane under test, whose camera is the perspective default.</summary>
    SceneViewport Viewport => pane.Viewport;

    /// <summary>How far a vertex is from the group's centre, along the ground.</summary>
    static float Reach(LineVertex vertex) =>
        MathF.Sqrt((vertex.Position.X * vertex.Position.X) + (vertex.Position.Z * vertex.Position.Z));

    /// <summary>
    ///     A group at the origin with two levels under it, both unit cubes, and the thresholds given.
    /// </summary>
    /// <param name="thresholds">The switch points, descending.</param>
    /// <returns>The group's parent entity.</returns>
    Entity Group(params float[] thresholds) {
        var group = scene.Add("LOD Group", LocalTransform.Identity);

        for (var level = 0; level < 2; level++) {
            var member = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.Identity, group);

            world.Add(member, new LodLevel { Level = level });
        }

        world.Add(group, new LodGroupComponent { Thresholds = thresholds.Length == 0 ? null : thresholds });

        transforms.Resolve(world);
        world.AdvanceVersion();

        return group;
    }
}
