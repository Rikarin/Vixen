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

/// <summary>The corner brackets that say a thing is selected in a pane the editor did not shade.</summary>
/// <remarks>
///     <para>
///         <b>Two halves, and they fail differently.</b> <see cref="SelectionCage" /> is geometry — a
///         box, a matrix and a camera in, twenty-four segments out — and everything about its shape
///         can be asserted with no scene at all. What <see cref="SceneLines" /> adds is the decision:
///         which entities get one, what each one's extent is, and whether a show flag can take it
///         away. A cage of the right shape round the wrong entities is a passing geometry suite and a
///         broken viewport.
///     </para>
///     <para>
///         ⚠ <b>What none of this can see is whether the segments reach the screen.</b> That is
///         <c>ComposedPaneCaptureTests.A_selected_object_does_not_look_like_an_unselected_one</c>, on
///         a device, comparing pixels — because a collector that fills a list nobody records is the
///         defect this whole task existed to close.
///     </para>
/// </remarks>
public class SelectionCageTests : IDisposable {
    const int Height = 800;

    /// <summary>Two vertices a segment, three segments a corner, eight corners.</summary>
    const int CageVertices = 8 * 3 * 2;

    static readonly AssetReference Rock = new(new AssetId(Guid.Parse("2b1b6ad2-6f5e-4a53-9c8a-2d2f2a1a7c01")));

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-cage-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;
    readonly TransformSystem transforms = new();
    readonly Pane pane = new();

    public SelectionCageTests() {
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");

        // ⚠ Orthographic, so that `WorldPerPixel` is one number for the whole scene rather than one
        // per point — which is what lets a test state where a corner of the cage is instead of
        // recomputing the projection it is asserting about.
        pane.Camera.IsOrthographic = true;
    }

    public void Dispose() {
        pane.Dispose();
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    // ── The geometry, with no scene in it ───────────────────────────────────────────────────────

    /// <summary>Three segments at each of the eight corners, and nothing along the middle of an edge.</summary>
    [Fact]
    public void A_cage_is_three_segments_at_each_of_eight_corners() {
        List<LineVertex> into = [];

        SelectionCage.Draw(new(into), Unit, Matrix4x4.Identity, Flat, Height, Color4.White);

        Assert.Equal(CageVertices, into.Count);
    }

    /// <summary>Half the length a wire box would be, which is what makes it read as brackets.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim the vertex count cannot make.</b> Twenty-four segments is also what a box
    ///     drawn with each edge visited twice would be, and that box is <see cref="SceneShow.Bounds" />
    ///     in the selection's colour — the exact drawing these brackets exist not to be. What
    ///     separates them is total length: twelve edges times <c>2 × Corner</c> of each, against
    ///     twelve whole edges, so a cage is <c>2 × Corner</c> of a box and is a box at
    ///     <c>Corner = 0.5</c>.
    /// </remarks>
    [Fact]
    public void A_cage_is_a_known_fraction_of_the_box_it_surrounds() {
        List<LineVertex> into = [];

        // ⚠ An orthographic camera, so the standoff is one number rather than one per corner and the
        // box the cage is drawn on is a cube of a size this test can state. The standoff itself is
        // asserted below; what is being measured here is the shape.
        SelectionCage.Draw(new(into), Unit, Matrix4x4.Identity, Flat, Height, Color4.White);

        var total = 0f;

        for (var index = 0; index < into.Count; index += 2) {
            total += (into[index + 1].Position - into[index].Position).Length();
        }

        var edge = 1f + (2f * Standoff);

        Assert.Equal(12f * edge * 2f * SelectionCage.Corner, total, 3);
        Assert.True(SelectionCage.Corner < 0.5f, "at a half the brackets meet and the cage is a box");
    }

    /// <summary>Every vertex is outside the extent, by the standoff, on the axis it is furthest along.</summary>
    [Fact]
    public void A_cage_stands_off_the_extent_it_is_drawn_round() {
        List<LineVertex> into = [];

        SelectionCage.Draw(new(into), Unit, Matrix4x4.Identity, Flat, Height, Color4.White);

        foreach (var vertex in into) {
            var reach = MathF.Max(
                MathF.Abs(vertex.Position.X),
                MathF.Max(MathF.Abs(vertex.Position.Y), MathF.Abs(vertex.Position.Z))
            );

            // Each of the eight corners is at the standoffed half-size on all three axes, and a
            // bracket runs inwards along one of them — so every vertex is still at full reach on the
            // other two.
            Assert.Equal(0.5f + Standoff, reach, 3);
        }
    }

    /// <summary>A stretched object gets the same gap on every side rather than a stretched gap.</summary>
    /// <remarks>
    ///     ⚠ <b>The bug a world-space standoff has and a pixel one does not fix by itself.</b> The gap
    ///     is a distance in the world and the extent is in the object's own space, so it has to be
    ///     divided by that axis's scale on the way in. Without the division a crate scaled fourfold on
    ///     X carries four times the gap on X, and a bracket further from one face than from the next
    ///     reads as a bug in the bracket rather than as a scale on the object.
    /// </remarks>
    [Fact]
    public void The_gap_does_not_stretch_with_the_object() {
        List<LineVertex> into = [];

        var stretched = Matrix4x4.FromScale(new Vector3(4f, 1f, 1f));

        SelectionCage.Draw(new(into), Unit, stretched, Flat, Height, Color4.White);

        var x = into.Max(vertex => MathF.Abs(vertex.Position.X));
        var y = into.Max(vertex => MathF.Abs(vertex.Position.Y));

        Assert.Equal((0.5f * 4f) + Standoff, x, 3);
        Assert.Equal(0.5f + Standoff, y, 3);
    }

    /// <summary>An object flattened to nothing on an axis still gets a cage rather than a division by zero.</summary>
    [Fact]
    public void A_flattened_object_still_gets_a_finite_cage() {
        List<LineVertex> into = [];

        SelectionCage.Draw(
            new(into),
            Unit,
            Matrix4x4.FromScale(new Vector3(1f, 0f, 1f)),
            Flat,
            Height,
            Color4.White
        );

        Assert.Equal(CageVertices, into.Count);
        Assert.All(into, vertex => Assert.True(float.IsFinite(vertex.Position.Length()), "a vertex is not finite"));
    }

    // ── The decision: who gets one ──────────────────────────────────────────────────────────────

    /// <summary>Selecting a shape adds a cage; nothing else about the frame changes.</summary>
    [Fact]
    public void A_shape_gets_a_cage_when_it_is_selected_and_not_before() {
        var lines = new SceneLines();
        var entity = Shape(PrimitiveKind.Cube, Vector3.Zero);

        lines.Build(scene, Viewport, Height);
        var before = lines.World.Count;

        scene.Selection.Set(entity);
        lines.Build(scene, Viewport, Height);

        Assert.Equal(before + CageVertices, lines.World.Count);
    }

    /// <summary>Every flag off, something selected: the cage is still there.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim that this is not a show flag.</b> A flag names a class of thing the scene
    ///     has whether or not anybody asked to see it; whether the click just made landed is not
    ///     something a viewport may be configured to stop reporting. So <c>SceneShow.None</c> is an
    ///     empty pane <em>and</em> a cage, and the suite's neighbouring
    ///     <c>Turning_every_flag_off_draws_nothing_at_all</c> is only true because nothing there is
    ///     selected.
    /// </remarks>
    [Fact]
    public void No_show_flag_can_hide_the_cage() {
        var lines = new SceneLines();
        var entity = Shape(PrimitiveKind.Cube, Vector3.Zero);

        scene.Selection.Set(entity);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Equal(CageVertices, lines.World.Count);
        Assert.Empty(lines.Overlay);
    }

    /// <summary>An entity with no geometry gets no cage, because it has no extent to draw one on.</summary>
    /// <remarks>
    ///     It is not left unanswered: <c>SceneLines.Markers</c> already draws a light, a camera and an
    ///     empty as a cross in the selection's colour and at 1.6 times the size, and that reaches a
    ///     composed pane the same way this does.
    /// </remarks>
    [Fact]
    public void An_entity_with_no_extent_gets_no_cage() {
        var lines = new SceneLines();
        var entity = scene.Add("Empty", LocalTransform.Identity);

        transforms.Resolve(world);
        world.AdvanceVersion();

        Viewport.Show = SceneShow.None;
        scene.Selection.Set(entity);

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    /// <summary>A hidden entity gets no cage, exactly as it gets no bounds box.</summary>
    [Fact]
    public void A_hidden_entity_gets_no_cage() {
        var lines = new SceneLines();
        var entity = Shape(PrimitiveKind.Cube, Vector3.Zero);

        scene.Selection.Set(entity);
        scene.SetHidden(entity, true);
        Viewport.Show = SceneShow.None;

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    /// <summary>A mesh entity's cage is the size of its mesh, not of a primitive it does not have.</summary>
    [Fact]
    public void A_mesh_entity_is_caged_at_its_own_size() {
        var lines = new SceneLines();
        var entity = scene.Add("Rock", LocalTransform.Identity);

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(Rock));
        transforms.Resolve(world);
        world.AdvanceVersion();

        Viewport.Meshes = new Meshes();
        Viewport.Show = SceneShow.None;
        scene.Selection.Set(entity);

        lines.Build(scene, Viewport, Height);

        Assert.Equal(CageVertices, lines.World.Count);

        // The stub's mesh is four units across on X and one on Y — a cage taken from a unit cube or
        // from `EditorApplication.Around`'s half-unit fallback would be neither.
        var x = lines.World.Max(vertex => MathF.Abs(vertex.Position.X));
        var y = lines.World.Max(vertex => MathF.Abs(vertex.Position.Y));

        Assert.Equal(2f + Standoff, x, 3);
        Assert.Equal(0.5f + Standoff, y, 3);
    }

    /// <summary>Without a source, a mesh entity waits rather than being caged at a made-up size.</summary>
    /// <remarks>
    ///     <c>IMeshSource</c> is ask-don't-wait, so the cage appears on the frame the geometry does —
    ///     which is the frame the object appears on. A fallback extent would draw a box of the wrong
    ///     size round nothing at all for as long as the disk took, and then jump.
    /// </remarks>
    [Fact]
    public void A_mesh_that_has_not_loaded_is_not_caged() {
        var lines = new SceneLines();
        var entity = scene.Add("Rock", LocalTransform.Identity);

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(Rock));
        transforms.Resolve(world);
        world.AdvanceVersion();

        Viewport.Show = SceneShow.None;
        scene.Selection.Set(entity);

        lines.Build(scene, Viewport, Height);

        Assert.Empty(lines.World);
    }

    // ── The collision with the bounds box ───────────────────────────────────────────────────────

    /// <summary>With the bounds flag on, one drawing is amber and the other is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure a picture found and a counter cannot.</b> The bounds box used to turn
    ///     <c>SelectedColour</c> for a selected entity, from a build in which nothing else round a
    ///     selected object was amber. Rendered together with a cage four pixels outside it in the same
    ///     colour, the two read as one doubled box — a box and a set of brackets are only
    ///     distinguishable if the box is not competing. So the two questions get one answer each: the
    ///     box says what extent an object has, the cage says which object is selected.
    /// </remarks>
    [Fact]
    public void The_bounds_box_no_longer_answers_the_question_the_cage_answers() {
        var lines = new SceneLines();
        var entity = Shape(PrimitiveKind.Cube, Vector3.Zero);

        scene.Selection.Set(entity);
        Viewport.Show = SceneShow.Bounds;

        lines.Build(scene, Viewport, Height);

        // Twenty-four for the box's twelve edges, plus the cage.
        Assert.Equal(24 + CageVertices, lines.World.Count);

        foreach (var vertex in lines.World) {
            var reach = MathF.Max(
                MathF.Abs(vertex.Position.X),
                MathF.Max(MathF.Abs(vertex.Position.Y), MathF.Abs(vertex.Position.Z))
            );

            // On the extent is the box; outside it is the cage. Only the second may be amber.
            if (reach < 0.5f + (Standoff * 0.5f)) {
                Assert.NotEqual(lines.SelectedColour, vertex.Colour);
            }
        }

        Assert.Contains(lines.World, vertex => vertex.Colour == lines.SelectedColour);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The unit cube's extent, which is what every built-in primitive is measured against.</summary>
    static BoundingBox Unit => new(new Vector3(-0.5f), new Vector3(0.5f));

    /// <summary>The pane under test.</summary>
    SceneViewport Viewport => pane.Viewport;

    /// <summary>Its camera, which the constructor has made orthographic.</summary>
    EditorCamera Flat => pane.Camera;

    /// <summary>How far the cage sits outside the extent, in world units, for this camera.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the camera rather than written down.</b> A number copied out of the
    ///     projection is a number that agrees with the code until somebody changes the field of view,
    ///     and then reports a cage in the wrong place as a cage that moved.
    /// </remarks>
    float Standoff => SelectionCage.Standoff * Flat.WorldPerPixel(Vector3.Zero, Height);

    Entity Shape(PrimitiveKind kind, Vector3 position) {
        var entity = scene.CreateShape(kind, LocalTransform.At(position));

        transforms.Resolve(world);
        world.AdvanceVersion();

        return entity;
    }

    /// <summary>A source whose one mesh is four units across and one tall, which is nothing's default.</summary>
    sealed class Meshes : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "rock",
                Bounds = new(new Vector3(-2f, -0.5f, -0.5f), new Vector3(2f, 0.5f, 0.5f))
            };

            return true;
        }
    }
}
