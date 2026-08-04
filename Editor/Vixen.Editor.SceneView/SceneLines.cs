// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Everything a viewport draws over the scene, collected once a frame.</summary>
/// <remarks>
///     <para>
///         <b>Three sources: the grid, the entities, the gizmo.</b> They are drawn together because
///         they are nearly the same kind of thing — world-space geometry with no material — and
///         collecting them here rather than in three places is what makes each of the passes below one
///         buffer write and one draw call.
///     </para>
///     <para>
///         ⚠ <b>Three lists, and none of the splits is cosmetic.</b> The grid and the markers are
///         depth-tested so an entity behind a wall is behind it; the gizmo is not, because a handle
///         you cannot reach through the thing it moves is a handle you cannot use. That much is two
///         <c>LineRenderer</c>s. The third is the gizmo's <i>solid</i> parts — the head on the end of
///         each arm — which are triangles rather than segments and want <c>MeshRenderer</c>, so they
///         cannot share a buffer with either of the other two however alike they look on screen.
///     </para>
///     <para>
///         <b>An entity with no mesh is drawn as a marker.</b> Until there is a mesh path in the
///         editor that is <i>every</i> entity, and even after there is one it stays true for the
///         lights, the cameras and the empties — which is what doc 11 means by visualisation gizmos.
///     </para>
/// </remarks>
public sealed class SceneLines {
    readonly List<LineVertex> world = [];
    readonly List<LineVertex> overlay = [];
    readonly List<MeshVertex> handles = [];
    readonly List<uint> handleIndices = [];
    readonly List<MeshVertex> centre = [];
    readonly List<uint> centreIndices = [];
    readonly Dictionary<PrimitiveKind, BoundingBox> extents = [];

    /// <summary>The segments drawn with the depth test on.</summary>
    public IReadOnlyList<LineVertex> World => world;

    /// <summary>The segments drawn over everything.</summary>
    public IReadOnlyList<LineVertex> Overlay => overlay;

    /// <summary>The gizmo's solid parts, drawn over everything.</summary>
    /// <remarks>
    ///     A span rather than an <see cref="IReadOnlyList{T}" />, which is what the two segment lists
    ///     hand back: this is read by <c>MeshRenderer.Upload</c>, which wants one. The same choice
    ///     <c>SceneMeshes</c> makes, and for the same reason.
    /// </remarks>
    public ReadOnlySpan<MeshVertex> Handles => CollectionsMarshal.AsSpan(handles);

    /// <summary>Three indices per triangle, into <see cref="Handles" />.</summary>
    public ReadOnlySpan<uint> HandleIndices => CollectionsMarshal.AsSpan(handleIndices);

    /// <summary>The gizmo's middle handle, drawn under its arms rather than over them.</summary>
    /// <remarks>
    ///     ⚠ <b>A fourth list for one small ball, and the reason is ordering rather than kind.</b>
    ///     Everything the gizmo draws has the depth test off, so what covers what is which pass ran
    ///     first — and a pass is a buffer. In <see cref="Handles" /> the ball is drawn after the
    ///     shafts and swallows the inner end of all three, which reads as handles stopping at an
    ///     obstacle; here <c>ScenePresenter</c> can put it between the world and the shafts, so the
    ///     arms cross it and it reads as a marker on the point they cross at. See
    ///     <c>GizmoGeometry.BuildCentre</c> for why this is not a range on <c>MeshRenderer.Record</c>.
    /// </remarks>
    public ReadOnlySpan<MeshVertex> Centre => CollectionsMarshal.AsSpan(centre);

    /// <summary>Three indices per triangle, into <see cref="Centre" />.</summary>
    public ReadOnlySpan<uint> CentreIndices => CollectionsMarshal.AsSpan(centreIndices);

    /// <summary>How big an entity's marker is, in world units.</summary>
    public float MarkerSize { get; set; } = 0.25f;

    /// <summary>The colour of an entity that is not selected.</summary>
    public Color4 MarkerColour { get; set; } = new(0.55f, 0.58f, 0.62f, 0.9f);

    /// <summary>The colour of a reference volume's wireframe.</summary>
    /// <remarks>Deliberately neither the selection's nor a marker's: a scale reference is neither
    ///     something in the scene nor something you chose, and it has to read as an annotation.</remarks>
    public Color4 ReferenceColour { get; set; } = new(0.35f, 0.75f, 0.55f, 0.55f);

    /// <summary>The colour of the tape measure.</summary>
    public Color4 MeasureColour { get; set; } = new(0.98f, 0.78f, 0.28f, 0.95f);

    /// <summary>The colour of a selected one.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection is shown by <i>colour</i> and not only by the gizmo sitting on it.</b>
    ///     With several things selected the gizmo is at one place and the other nineteen have nothing
    ///     saying they are going to move — which is the state in which somebody drags and is
    ///     surprised.
    ///     <para>
    ///         <see cref="SceneMeshes.SelectedColour" />'s amber, and deliberately not
    ///         distinct from a shape's amber tint: a marker cross and a shape's tint are
    ///         both saying "this one is selected", and the rim is saying where it ends.
    ///     </para>
    /// </remarks>
    public Color4 SelectedColour { get; set; } = new(1f, 0.62f, 0.15f, 1f);

    /// <summary>What an unselected post-process volume is drawn in.</summary>
    /// <remarks>
    ///     A cool violet, chosen to be nothing else on screen: a light is tinted its own colour, a
    ///     bound is grey and a reference volume is green, so a box in this colour is unambiguous even
    ///     in a scene with all four.
    /// </remarks>
    public Color4 VolumeColour { get; set; } = new(0.62f, 0.45f, 0.95f, 0.7f);

    /// <summary>Collects a frame's lines.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <param name="viewport">The pane drawing it.</param>
    /// <param name="height">How tall the pane is, in render pixels.</param>
    /// <remarks>
    ///     ⚠ <b>Every source asks <see cref="SceneViewport.Show" /> and none of them is a
    ///     <c>continue</c> inside its own loop.</b> A flag that is off has to cost nothing rather than
    ///     cost a walk over the scene that emits no vertices — which for the parent links, the only
    ///     one that is a test per entity, is the difference between skipping a branch and skipping the
    ///     pass.
    /// </remarks>
    public void Build(SceneDocument document, SceneViewport viewport, int height) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewport);

        world.Clear();
        overlay.Clear();
        handles.Clear();
        handleIndices.Clear();
        centre.Clear();
        centreIndices.Clear();

        var show = viewport.Show;

        if ((show & SceneShow.Grid) != 0) {
            Grid(viewport, height);
        }

        if ((show & (SceneShow.Markers | SceneShow.Parents)) != 0) {
            Markers(document, show);
        }

        if ((show & SceneShow.Lights) != 0) {
            LightShapes(document);
        }

        if ((show & SceneShow.Bounds) != 0) {
            Boxes(document);
        }

        if ((show & SceneShow.Volumes) != 0) {
            Volumes(document);
        }

        // ⚠ Into `world` rather than `overlay`, so a contributed gizmo is occluded by the level the
        // way a light shape is. A trigger volume drawn through a wall reads as being in front of it,
        // which for the one thing a gizmo exists to communicate — where in the scene it is — is the
        // wrong answer.
        if ((show & SceneShow.Components) != 0) {
            viewport.Gizmos?.Build(document, new(world));
        }

        // ⚠ Not behind a show flag, unlike everything above it. A show flag is a class of thing a
        // scene has whether or not you asked for it; a reference volume and a measurement are things
        // the user put there a moment ago, and hiding them behind a second switch is how somebody
        // places one, sees nothing and concludes the tool is broken. Turning them off is taking them
        // out, which is what the commands do.
        if (!viewport.References.IsEmpty) {
            References(viewport);
        }

        if (viewport.Measure.Points.Count > 0) {
            Tape(viewport);
        }

        // ⚠ Not behind a show flag either, and for a sharper version of the reason above: the element
        // cage is the only thing on screen that says a mesh is being edited at all. A designer who
        // pressed `2` and saw no change would conclude the mode did not work.
        if (viewport.Editing is { } editing && editing.IsActive) {
            Elements(document, viewport, editing, height);
        }

        if ((show & SceneShow.Gizmos) == 0) {
            return;
        }

        GizmoGeometry.BuildCentre(viewport.Gizmo, viewport.Camera, height, centre, centreIndices);
        GizmoGeometry.Build(viewport.Gizmo, viewport.Camera, height, overlay);
        GizmoGeometry.BuildSolid(viewport.Gizmo, viewport.Camera, height, handles, handleIndices);
    }

    void Grid(SceneViewport viewport, int height) {
        foreach (var line in viewport.Grid.Build(viewport.Camera, height)) {
            // ⚠ A colour per end, not one for the line. The grid fades its lines out towards their
            // far ends so that a level runs out into nothing instead of stopping at a rectangle, and
            // that fade only exists if both ends are carried through — writing `line.Colour` twice
            // draws the rectangle back.
            world.Add(new(line.From, line.Colour));
            world.Add(new(line.To, line.ToColour));
        }
    }

    /// <summary>A three-axis cross at every entity, in the selection's colour when it is in it.</summary>
    /// <remarks>
    ///     A cross rather than a box, because a box implies a size the entity does not have and a
    ///     cross says only "something is here", which is the truth about an entity with no mesh. The
    ///     arms are along the entity's <i>own</i> axes, so a rotated empty looks rotated.
    /// </remarks>
    void Markers(SceneDocument document, SceneShow show) {
        var crosses = (show & SceneShow.Markers) != 0;
        var links = (show & SceneShow.Parents) != 0;

        foreach (var entity in document.Entities) {
            if (!document.World.IsAlive(entity) || !document.World.Has<WorldTransform>(entity)) {
                continue;
            }

            var transform = new Transform(document.World, entity);
            var origin = transform.Position;
            var selected = document.Selection.Contains(entity);
            var colour = selected ? SelectedColour : MarkerColour;
            var size = MarkerSize * (selected ? 1.6f : 1f);

            if (crosses) {
                Cross(origin, transform.Right * size, colour);
                Cross(origin, transform.Up * size, colour);
                Cross(origin, transform.Forward * size, colour);
            }

            // A line to the parent, so a hierarchy is visible in the viewport rather than only in the
            // panel. Faded, because it is a relationship and not a thing.
            if (links && transform.Parent is { IsNull: false } parent && document.World.Has<WorldTransform>(parent)) {
                var faded = new Color4(colour.R, colour.G, colour.B, 0.25f);

                world.Add(new(origin, faded));
                world.Add(new(new Transform(document.World, parent).Position, faded));
            }
        }
    }

    /// <summary>A wire box round every shaped entity, in the entity's own axes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The shape's bounds through the entity's matrix, not an axis-aligned box round the
    ///         result.</b> A world-aligned box round a rotated crate is bigger than the crate on every
    ///         axis and touches it at four points, which answers a question — "what would the
    ///         broadphase see" — that nobody looking at a viewport is asking. What this shows is the
    ///         extent the geometry actually occupies, turned the way the geometry is turned.
    ///     </para>
    ///     <para>
    ///         Shaped entities only: an empty has no extent, and a box round a marker cross would be a
    ///         box round an arbitrary constant.
    ///     </para>
    /// </remarks>
    void Boxes(SceneDocument document) {
        // ⚠ Outside the loop. A stack allocation per entity is a stack that grows with the scene and
        // has no way to shrink until the method returns, which for a large scene is an overflow
        // rather than a slowdown — CA2014's whole point.
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var entity in document.Entities) {
            if (!PrimitiveShapes.TryGet(document.World, entity, out var kind)
                || !document.World.Has<WorldTransform>(entity)
                || document.IsHidden(entity)) {
                continue;
            }

            var bounds = Extent(kind);
            var matrix = document.World.Read<WorldTransform>(entity).Value;

            var colour = document.Selection.Contains(entity)
                ? SelectedColour
                : new Color4(MarkerColour.R, MarkerColour.G, MarkerColour.B, 0.45f);

            var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
            var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;

            for (var index = 0; index < 8; index++) {
                var local = centre + new Vector3(
                    (index & 1) == 0 ? -extent.X : extent.X,
                    (index & 2) == 0 ? -extent.Y : extent.Y,
                    (index & 4) == 0 ? -extent.Z : extent.Z
                );

                corners[index] = Matrix4x4.TransformPosition(local, matrix);
            }

            // The twelve edges of a box whose corners are indexed by which side of each axis they are
            // on: two corners are joined exactly when their indices differ in one bit.
            for (var from = 0; from < 8; from++) {
                for (var bit = 1; bit < 8; bit <<= 1) {
                    var to = from | bit;

                    if (to != from) {
                        Segment(corners[from], corners[to], colour);
                    }
                }
            }
        }
    }

    /// <summary>Each post-process volume, as its box and a second one at its blend radius.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two boxes, and the outer one is the point.</b> A volume that looks right and does
    ///         nothing is nearly always one whose blend radius the camera never enters — the inner box
    ///         is where it fully applies and the outer is where it starts to. A single wireframe would
    ///         show the number in the inspector and not the thing it means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unbound volume draws no box at all, and a marker instead.</b> Its extents mean
    ///         nothing — it applies everywhere — so drawing the box would be drawing a boundary that
    ///         does not exist, which is worse than drawing nothing. The cross says "there is a volume
    ///         here and it is not a place".
    ///     </para>
    /// </remarks>
    void Volumes(SceneDocument document) {
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var entity in document.Entities) {
            if (!document.World.TryGet<PostProcessVolume>(entity, out var volume)
                || !document.World.Has<WorldTransform>(entity)
                || document.IsHidden(entity)) {
                continue;
            }

            var matrix = document.World.Read<WorldTransform>(entity).Value;
            var selected = document.Selection.Contains(entity);
            var colour = selected ? SelectedColour : VolumeColour;

            if (volume.Unbound) {
                var origin = Matrix4x4.TransformPosition(Vector3.Zero, matrix);

                Ring(origin, Vector3.UnitX, Vector3.UnitZ, MarkerSize * 2f, colour);
                Ring(origin, Vector3.UnitX, Vector3.UnitY, MarkerSize * 2f, colour);

                continue;
            }

            Wireframe(corners, matrix, volume.Extents, colour);

            // ⚠ The radius is added in the volume's own space, so a scaled entity scales the falloff
            // with it — which is what the runtime does, because the containment test runs in that
            // same space. Adding it in world units here would draw a box the fold does not use.
            if (volume.BlendRadius > 0f) {
                var faded = new Color4(colour.R, colour.G, colour.B, colour.A * 0.35f);

                Wireframe(corners, matrix, volume.Extents + new Vector3(volume.BlendRadius), faded);
            }
        }
    }

    /// <summary>Twelve edges of a box, transformed.</summary>
    void Wireframe(Span<Vector3> corners, in Matrix4x4 matrix, Vector3 extents, Color4 colour) {
        for (var index = 0; index < 8; index++) {
            var local = new Vector3(
                (index & 1) == 0 ? -extents.X : extents.X,
                (index & 2) == 0 ? -extents.Y : extents.Y,
                (index & 4) == 0 ? -extents.Z : extents.Z
            );

            corners[index] = Matrix4x4.TransformPosition(local, matrix);
        }

        // Two corners are joined exactly when their indices differ in one bit.
        for (var from = 0; from < 8; from++) {
            for (var bit = 1; bit < 8; bit <<= 1) {
                var to = from | bit;

                if (to != from) {
                    Segment(corners[from], corners[to], colour);
                }
            }
        }
    }

    /// <summary>The reference volumes the pane is showing, as wireframe boxes.</summary>
    /// <remarks>
    ///     ⚠ <b>Lines rather than entities, which is doc 24's own word for it: "drawn and not
    ///     shipped".</b> Nothing to select, nothing to save, and nothing to leave in a level by
    ///     accident — which is the whole difference between this and the cube everybody scales to 1.8
    ///     and then forgets about.
    /// </remarks>
    void References(SceneViewport viewport) {
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var (volume, at) in viewport.References.Placed) {
            var centre = volume.CentreAt(at);
            var extent = volume.Size * 0.5f;

            for (var index = 0; index < 8; index++) {
                corners[index] = centre + new Vector3(
                    (index & 1) == 0 ? -extent.X : extent.X,
                    (index & 2) == 0 ? -extent.Y : extent.Y,
                    (index & 4) == 0 ? -extent.Z : extent.Z
                );
            }

            for (var from = 0; from < 8; from++) {
                for (var bit = 1; bit < 8; bit <<= 1) {
                    var to = from | bit;

                    if (to != from) {
                        Segment(corners[from], corners[to], ReferenceColour);
                    }
                }
            }
        }
    }

    /// <summary>The tape measure: the run between the points, and a cross at each of them.</summary>
    /// <remarks>
    ///     ⚠ <b>Drawn in the overlay rather than in the world list.</b> A measurement between two
    ///     corners of a wall is a line lying exactly in that wall's surface, and a depth-tested one is
    ///     a line that is half there — which is the one thing a tape measure cannot be.
    /// </remarks>
    void Tape(SceneViewport viewport) {
        var points = viewport.Measure.Points;

        for (var index = 1; index < points.Count; index++) {
            overlay.Add(new(points[index - 1], MeasureColour));
            overlay.Add(new(points[index], MeasureColour));
        }

        var arm = MarkerSize * 0.5f;

        foreach (var point in points) {
            overlay.Add(new(point - new Vector3(arm, 0f, 0f), MeasureColour));
            overlay.Add(new(point + new Vector3(arm, 0f, 0f), MeasureColour));
            overlay.Add(new(point - new Vector3(0f, arm, 0f), MeasureColour));
            overlay.Add(new(point + new Vector3(0f, arm, 0f), MeasureColour));
            overlay.Add(new(point - new Vector3(0f, 0f, arm), MeasureColour));
            overlay.Add(new(point + new Vector3(0f, 0f, arm), MeasureColour));
        }
    }

    /// <summary>The colour the edit cage — every edge of the mesh being edited — is drawn in.</summary>
    /// <remarks>
    ///     ⚠ <b>Faint, because it is drawn over everything and there is one line per edge.</b> A cage
    ///     at full strength on a mesh of a few hundred edges is a mesh you cannot see the shape of,
    ///     which is the opposite of what it is for.
    /// </remarks>
    public Color4 CageColour { get; set; } = new(0.04f, 0.05f, 0.06f, 0.9f);

    /// <summary>The colour a selected element is drawn in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Sky blue rather than the entity selection's amber, and the two disagreeing is the
    ///         point.</b> An entity selection and an element selection are different claims — "this
    ///         object" against "this face of it" — and a face highlighted in the same amber as the
    ///         object around it is a face you cannot see is chosen. It is also the hue the interface's
    ///         accent and the object outline already use, so "chosen" reads the same way in the
    ///         outliner, round the object and on its faces.
    ///     </para>
    ///     <para>
    ///         Bright, because this is drawn over a surface
    ///         rather than against the background.
    ///     </para>
    /// </remarks>
    public Color4 ElementColour { get; set; } = new(0.35f, 0.72f, 1f, 1f);

    /// <summary>The colour the element under the pointer is drawn in.</summary>
    /// <remarks>
    ///     ⚠ <b>A third colour, not a brighter selection.</b> Hover and selection answer different
    ///     questions — "what would a click take" against "what a click took" — and an element that is
    ///     both has to read as selected, which it does because the selection is drawn after this.
    ///
    ///     ⚠ <b>Near-white now that the selection is blue.</b> The two were a blue and an amber and
    ///     could be any pair; they are a blue and a white because a hover is a lighter statement than a
    ///     selection and because a third *hue* on a surface that is already carrying two is one more
    ///     thing to learn.
    /// </remarks>
    public Color4 HoverColour { get; set; } = new(0.95f, 0.98f, 1f, 1f);

    /// <summary>How big a vertex handle is, in render pixels.</summary>
    /// <remarks>Smaller than <c>SubObjectPicker.DefaultTolerance</c>, deliberately and for the reason
    ///     that constant gives: what is aimed at is drawn smaller than what answers.</remarks>
    public float VertexSize { get; set; } = 3.5f;

    /// <summary>The cage, the vertices, and whatever is selected or hovered, for one edited mesh.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Drawn in the overlay rather than in the world list, which is what makes it an
    ///         edit cage rather than a wireframe.</b> Half of what a designer selects while modelling is
    ///         on the far side of the surface — the back wall of a room seen from inside it — and a
    ///         depth-tested cage would show the elements that are already reachable and hide the ones
    ///         that are not. It is the same argument the gizmo's own overlay makes, and it is the
    ///         drawn half of <c>SubObjectPicker</c>'s stated "nothing is occluded".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A face is filled rather than outlined, and it goes through the solid handles.</b>
    ///         An n-gon's border alone reads as a loop of selected edges — which is a different mode's
    ///         selection — so a face has to have an interior. Premultiplied alpha over the surface is
    ///         what makes a tint rather than a patch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Straight off the <c>EditMesh</c> and not through <see cref="MeshElements" />.</b>
    ///         Drawing needs positions, edges and corner loops, all of which the kernel already holds;
    ///         deriving a second table once a frame would be the whole cost of the pass and would
    ///         reintroduce the by-kind caching this deliberately does not do.
    ///     </para>
    /// </remarks>
    void Elements(SceneDocument document, SceneViewport viewport, MeshEdit editing, int height) {
        if (editing.Mesh is not { } mesh || !document.World.Has<WorldTransform>(editing.Target)) {
            return;
        }

        var transform = document.World.Read<WorldTransform>(editing.Target).Value;
        var selection = editing.Selection;
        var hover = editing.Hover;

        for (var edge = 0; edge < mesh.Edges.Count; edge++) {
            var (a, b) = mesh.Edges[edge];
            var colour = CageColour;

            if (selection.Kind == MeshElementKind.Edge && selection.Contains(edge)) {
                colour = ElementColour;
            } else if (hover.Kind == SubObjectKind.Edge && hover.Index == edge) {
                colour = HoverColour;
            }

            overlay.Add(new(Place(mesh.Positions[a], transform), colour));
            overlay.Add(new(Place(mesh.Positions[b], transform), colour));
        }

        if (selection.Kind == MeshElementKind.Vertex || hover.Kind == SubObjectKind.Vertex) {
            Vertices(mesh, transform, viewport, editing, height);
        }

        if (selection.Kind == MeshElementKind.Face) {
            foreach (var face in selection.Indices) {
                Fill(mesh, transform, face, ElementColour);
            }
        }

        if (hover.Kind == SubObjectKind.Face && !selection.Contains(hover.Index)) {
            Fill(mesh, transform, hover.Index, HoverColour);
        }
    }

    /// <summary>A screen-sized cross at every shared position, coloured by what it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Sized in pixels rather than in world units, exactly as the gizmo's handles are.</b> A
    ///     vertex handle a fixed number of metres across is one that fills the pane on a small mesh and
    ///     vanishes on a large one — and a block-out is both, in one level.
    /// </remarks>
    void Vertices(EditMesh mesh, in Matrix4x4 transform, SceneViewport viewport, MeshEdit editing, int height) {
        var selection = editing.Selection;
        var hover = editing.Hover;

        for (var position = 0; position < mesh.PositionCount; position++) {
            var at = Place(mesh.Positions[position], transform);
            var colour = CageColour;
            var scale = 1f;

            if (selection.Kind == MeshElementKind.Vertex && selection.Contains(position)) {
                colour = ElementColour;
                scale = 1.6f;
            } else if (hover.Kind == SubObjectKind.Vertex && hover.Index == position) {
                colour = HoverColour;
                scale = 1.6f;
            }

            var arm = viewport.Camera.WorldPerPixel(at, height) * VertexSize * scale;

            // Across the view rather than along the mesh's axes: a handle is a mark on the picture,
            // and one drawn in world axes is invisible whenever an axis points at the camera.
            Mark(at, viewport.Camera.Right * arm, colour);
            Mark(at, viewport.Camera.Up * arm, colour);
        }
    }

    /// <summary>One face, as a fan of triangles in the solid overlay.</summary>
    void Fill(EditMesh mesh, in Matrix4x4 transform, int face, Color4 colour) {
        if ((uint) face >= (uint) mesh.FaceCount) {
            return;
        }

        var loop = mesh.CornersOf(face);
        var facing = Matrix4x4.TransformDirection(mesh.Normal(face), transform);
        var first = (uint) handles.Count;

        // Premultiplied, because the pipeline the solid overlay is drawn with expects it — a straight
        // alpha colour here is a face highlight that is brighter the more transparent it is asked to be.
        var tint = new Color4(colour.R * Alpha, colour.G * Alpha, colour.B * Alpha, Alpha);

        foreach (var corner in loop) {
            handles.Add(new(Place(mesh.Positions[corner], transform), facing, tint));
        }

        for (var corner = 1u; corner + 1 < (uint) loop.Length; corner++) {
            handleIndices.Add(first);
            handleIndices.Add(first + corner);
            handleIndices.Add(first + corner + 1);
        }
    }

    /// <summary>How solid a selected face's tint is.</summary>
    /// <remarks>Enough to read as chosen and little enough to leave the shading under it visible, which
    ///     is what tells a designer whether the face they took is the one facing them.</remarks>
    const float Alpha = 0.35f;

    static Vector3 Place(Vector3 local, in Matrix4x4 transform) => Matrix4x4.TransformPosition(local, transform);

    /// <summary>A shape's extent in its own space, built once per kind.</summary>
    /// <remarks>
    ///     ⚠ <b>The mesh's own bounds rather than the unit cube.</b> Every primitive is built to fit
    ///     the unit cube, which is not the same as filling it: a torus is a tenth of a unit tall and a
    ///     plane has no height at all, so a unit box round either says the entity occupies four times
    ///     the space it does. Cached because the box never changes and the geometry it is measured
    ///     from is hundreds of vertices.
    /// </remarks>
    BoundingBox Extent(PrimitiveKind kind) {
        if (!extents.TryGetValue(kind, out var bounds)) {
            bounds = MeshPrimitives.Create(kind).Bounds;
            extents[kind] = bounds;
        }

        return bounds;
    }

    void Cross(Vector3 origin, Vector3 arm, Color4 colour) {
        world.Add(new(origin - arm, colour));
        world.Add(new(origin + arm, colour));
    }

    /// <summary><see cref="Cross" />'s counterpart in the overlay, for handles that must not be hidden.</summary>
    void Mark(Vector3 origin, Vector3 arm, Color4 colour) {
        overlay.Add(new(origin - arm, colour));
        overlay.Add(new(origin + arm, colour));
    }

    /// <summary>How far a directional light's rays are drawn, in world units.</summary>
    /// <remarks>
    ///     A fixed length, because a directional light has no range to take one from — the sun does
    ///     not fall off, which is the whole difference between it and the other four.
    /// </remarks>
    const float SunLength = 1.5f;

    /// <summary>How many segments a gizmo's circles are drawn with.</summary>
    const float Segments = 24;

    /// <summary>What each light in the scene reaches, drawn in its own colour.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A light is invisible, so without this it is a name in the hierarchy and a marker
    ///         cross in the viewport.</b> Which way a spot points and how far a point light carries
    ///         are the two things somebody placing lights is actually adjusting, and neither is
    ///         legible from a transform gizmo — a spot aimed at the ceiling and one aimed at the floor
    ///         look identical until the cone is drawn.
    ///     </para>
    ///     <para>
    ///         <b>In the light's own colour rather than a fixed one</b>, dimmed towards the marker
    ///         colour so a scene full of white lights does not read as a scene full of selections.
    ///         Selected still wins, for the reason <see cref="SelectedColour" /> gives.
    ///     </para>
    /// </remarks>
    void LightShapes(SceneDocument document) {
        foreach (var entity in document.Entities) {
            if (!Lights.TryGet(document.World, entity, out var light) || !document.World.Has<WorldTransform>(entity)) {
                continue;
            }

            var transform = new Transform(document.World, entity);
            var selected = document.Selection.Contains(entity);
            var colour = selected ? SelectedColour : Tint(light.Colour);

            var origin = transform.Position;
            var forward = transform.Forward;
            var right = transform.Right;
            var up = transform.Up;

            switch (light.Kind) {
                case LightKind.Directional: {
                    // Parallel rays, which is what makes it read as a sun rather than as a spot: four
                    // of them from a disc, all pointing the same way and none of them spreading.
                    var reach = origin + (forward * SunLength);

                    Ring(origin, right, up, MarkerSize, colour);
                    Segment(origin, reach, colour);

                    for (var i = 0; i < 4; i++) {
                        var offset = Around(right, up, i / 4f) * MarkerSize;
                        Segment(origin + offset, reach + offset, colour);
                    }

                    break;
                }

                case LightKind.Point:
                    // Three rings at the range, which is the sphere a wireframe can afford: a full
                    // one is hundreds of segments per light and says nothing the three do not.
                    Ring(origin, right, up, light.Range, colour);
                    Ring(origin, up, forward, light.Range, colour);
                    Ring(origin, forward, right, light.Range, colour);

                    break;

                case LightKind.Spot: {
                    // The outer cone and not the inner: the outer is where the light stops, and two
                    // cones drawn together are a shape nobody can read at a glance.
                    var apex = origin;
                    var centre = origin + (forward * light.Range);
                    var radius = light.Range * MathF.Tan(light.OuterAngle);

                    Ring(centre, right, up, radius, colour);

                    for (var i = 0; i < 4; i++) {
                        Segment(apex, centre + (Around(right, up, i / 4f) * radius), colour);
                    }

                    break;
                }

                case LightKind.Rect: {
                    // The rectangle it emits from and the way it faces. One-sided, so the normal is
                    // the difference between a softbox lighting the room and lighting the wall.
                    var across = right * light.HalfLength;
                    var down = up * light.Radius;

                    Segment(origin - across - down, origin + across - down, colour);
                    Segment(origin + across - down, origin + across + down, colour);
                    Segment(origin + across + down, origin - across + down, colour);
                    Segment(origin - across + down, origin - across - down, colour);

                    Segment(origin, origin + (forward * MarkerSize * 2f), colour);
                    break;
                }

                case LightKind.Tube: {
                    var end = right * light.HalfLength;

                    Segment(origin - end, origin + end, colour);
                    Ring(origin - end, up, forward, light.Radius, colour);
                    Ring(origin + end, up, forward, light.Radius, colour);

                    break;
                }

                default:
                    break;
            }
        }
    }

    /// <summary>A light's colour as a line colour: never black, never a full-strength selection orange.</summary>
    /// <remarks>
    ///     ⚠ <b>Lifted off the floor, because a light may legitimately be almost black</b> — a dim
    ///     blue fill at 0.02 is a real thing to author — and a gizmo drawn in it is a gizmo that is
    ///     not there. The hue survives; only the floor is imposed.
    /// </remarks>
    static Color4 Tint(Color3 colour) =>
        new(
            MathF.Max(colour.R, 0.35f),
            MathF.Max(colour.G, 0.35f),
            MathF.Max(colour.B, 0.35f),
            0.7f
        );

    /// <summary>A point on the unit circle spanned by two axes.</summary>
    /// <param name="first">One axis.</param>
    /// <param name="second">The other.</param>
    /// <param name="turn">How far round, from 0 to 1.</param>
    static Vector3 Around(Vector3 first, Vector3 second, float turn) {
        var angle = turn * MathF.Tau;
        return (first * MathF.Cos(angle)) + (second * MathF.Sin(angle));
    }

    void Segment(Vector3 from, Vector3 to, Color4 colour) {
        world.Add(new(from, colour));
        world.Add(new(to, colour));
    }

    /// <summary>A circle in the plane two axes span. Nothing is drawn for a radius of nothing.</summary>
    void Ring(Vector3 centre, Vector3 first, Vector3 second, float radius, Color4 colour) {
        if (radius <= 0f) {
            return;
        }

        var previous = centre + (first * radius);

        for (var i = 1; i <= Segments; i++) {
            var next = centre + (Around(first, second, i / Segments) * radius);

            Segment(previous, next, colour);
            previous = next;
        }
    }
}
