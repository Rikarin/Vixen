// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>The handles, as line segments something can draw.</summary>
/// <remarks>
///     <para>
///         <b>The other half of <see cref="TransformGizmo" />, kept apart from it on purpose.</b> The
///         gizmo decides where the handles are, what is under the pointer and what a drag means, and
///         all of that is testable with no device. This turns the same numbers into geometry, and it
///         is separate so that changing how a gizmo <i>looks</i> cannot change what it <i>does</i>.
///     </para>
///     <para>
///         ⚠ <b>The arms are built from the same <c>WorldPerPixel</c> and the same basis the hit test
///         uses.</b> Not similar arithmetic — the same calls. A gizmo drawn a little larger than it is
///         hit-tested is one where the last few pixels of every arm do nothing, which reads as the
///         tool being unreliable rather than as an arithmetic difference.
///     </para>
///     <para>
///         ⚠ <b>A thick line is several thin ones, and it has to be.</b>
///         <c>LineRenderer</c> draws one-pixel lines and deliberately refuses to offer anything else:
///         <c>lineWidth</c> above one is an optional Vulkan feature that most tiled GPUs lack, so a
///         renderer that offered it would draw a different picture on different machines. Its own
///         remarks say a thick line belongs to whoever wants one — this is that. Every segment is
///         emitted <see cref="TransformGizmo.Thickness" /> times, each shifted a pixel further across
///         the segment <i>on screen</i>, which is the only offset that thickens a line rather than
///         smearing it along itself or into the depth buffer.
///     </para>
///     <para>
///         Lines rather than solid cones and rings, which is what a shipping gizmo eventually wants.
///         A line renderer exists and a mesh path for the editor does not, and an editor whose
///         handles are visible now is worth more than one whose handles are beautiful later.
///     </para>
/// </remarks>
public static class GizmoGeometry {
    /// <summary>How many segments a rotation ring is drawn with.</summary>
    /// <remarks>Thirty-two, which is what the hit test samples it at — see <c>TransformGizmo</c>.</remarks>
    public const int RingSegments = 32;

    /// <summary>Which way the eye is from a point, for deciding what faces it.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="origin">The point, usually the gizmo's.</param>
    /// <returns>A unit vector from the point towards the eye.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>From the point, not the camera's forward.</b> They are the same only for something
    ///         in the middle of the pane; a gizmo at the corner of a wide view is seen from twenty
    ///         degrees off, and culling a ring's back half against the forward vector there hides a
    ///         slice of the front of it and shows a slice of the back.
    ///     </para>
    ///     <para>
    ///         Orthographic has no eye — every ray is parallel — so the forward vector <i>is</i> the
    ///         answer there, and a camera sitting exactly on the point gets it too rather than a
    ///         division by zero.
    ///     </para>
    /// </remarks>
    public static Vector3 TowardsEye(EditorCamera camera, Vector3 origin) {
        ArgumentNullException.ThrowIfNull(camera);

        if (camera.IsOrthographic) {
            return -camera.Forward;
        }

        var offset = camera.Position - origin;

        return offset.LengthSquared() > MathUtil.ZeroTolerance ? Vector3.Normalize(offset) : -camera.Forward;
    }

    /// <summary>How a segment is drawn: where the eye is, and how far apart the strokes go.</summary>
    /// <param name="Towards">Which way the camera looks, for the perpendicular a stroke is offset along.</param>
    /// <param name="Fallback">Which way to offset a segment that points straight at the camera.</param>
    /// <param name="WorldPerPixel">How many world units one render pixel is, at the gizmo's origin.</param>
    /// <param name="Strokes">How many parallel lines one segment is drawn with.</param>
    readonly record struct Pen(Vector3 Towards, Vector3 Fallback, float WorldPerPixel, int Strokes);

    /// <summary>The colour of the arm along an axis.</summary>
    /// <param name="axis">0 for x, 1 for y, 2 for z.</param>
    /// <param name="highlighted">Whether the pointer is over it.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     The three axis colours every editor uses and this repo already picked, in
    ///     <c>Viewport.AxisColor</c> — repeated here rather than shared because that one reads them
    ///     from the stylesheet and a scene's gizmo is not a styled element.
    /// </remarks>
    public static Color4 AxisColour(int axis, bool highlighted = false) {
        var colour = axis switch {
            0 => new Color4(0.87f, 0.29f, 0.33f, 1f),
            1 => new Color4(0.42f, 0.75f, 0.31f, 1f),
            _ => new Color4(0.29f, 0.51f, 0.90f, 1f)
        };

        // Towards white rather than brighter, so the highlight reads on the green arm too — a green
        // that is merely more saturated is a green nobody notices changing.
        return highlighted ? Lerp(colour, Color4.White, 0.55f) : colour;
    }

    /// <summary>The colour of a handle that belongs to no axis: the middle box, the screen ring.</summary>
    /// <param name="highlighted">Whether the pointer is over it.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     Grey, because it is the one handle that means "all of them" and any of the three axis
    ///     colours would claim it belongs to that axis — which for the uniform-scale box is precisely
    ///     the wrong thing to say.
    /// </remarks>
    public static Color4 NeutralColour(bool highlighted = false) =>
        highlighted ? new Color4(1f, 0.93f, 0.62f, 1f) : new Color4(0.78f, 0.80f, 0.84f, 1f);

    /// <summary>Builds the handles for a gizmo as it currently stands.</summary>
    /// <param name="gizmo">The gizmo.</param>
    /// <param name="camera">The camera it is seen from.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <param name="into">Where to put the vertices. Not cleared.</param>
    /// <returns>How many vertices were added.</returns>
    public static int Build(TransformGizmo gizmo, EditorCamera camera, int height, List<LineVertex> into) {
        ArgumentNullException.ThrowIfNull(gizmo);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(into);

        if (gizmo.Targets.Count == 0 || gizmo.Mode == GizmoMode.None) {
            return 0;
        }

        var before = into.Count;
        var origin = gizmo.Origin;
        var basis = gizmo.Basis(camera);
        var worldPerPixel = gizmo.WorldPerPixel(camera, height);
        var scale = worldPerPixel * gizmo.HandleLength;

        // ⚠ Rounded to whole strokes and floored at one. A thickness of zero — or of a third of a
        // pixel, which is what a caller who thinks in world units would set — would otherwise draw
        // nothing at all, and a gizmo that vanished because a number was small is worse than one
        // that is a hairline.
        var pen = new Pen(
            camera.Forward,
            camera.Right,
            worldPerPixel,
            Math.Max(1, (int) MathF.Round(gizmo.Thickness))
        );

        // What the pointer is over, or what is being dragged — the drag wins, because a drag that
        // stopped highlighting the arm it is moving looks like it let go.
        var active = gizmo.IsDragging ? gizmo.Active : gizmo.Hovered;

        // ⚠ A rotate gizmo has no wire half at all any more. Its rings are tubes through
        // <see cref="BuildSolid" />, and drawing a polyline down the middle of a five-pixel tube puts
        // a hairline over a solid shape that is already the shape the hit test measures — visible as
        // a seam on the near side of every ring and as nothing at all on the far side, which reads as
        // the ring being drawn twice at slightly different radii.
        if (gizmo.Mode != GizmoMode.Rotate) {
            Arms(into, camera, gizmo, origin, basis, scale, active, pen);

            // The outline over the filled quad, not instead of it. A translucent square with no edge
            // is a smudge at the angles where it is nearly edge-on, which is exactly where somebody
            // is trying to decide whether they can grab it.
            Planes(into, origin, basis, scale, active, gizmo, camera.Forward, pen);
        }

        return into.Count - before;
    }

    /// <summary>Builds the solid parts of a gizmo: the head on the end of each arm.</summary>
    /// <param name="gizmo">The gizmo.</param>
    /// <param name="camera">The camera it is seen from.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <param name="vertices">Where to put the vertices. Not cleared.</param>
    /// <param name="triangles">Where to put the indices, three per triangle. Not cleared, and offset.</param>
    /// <returns>How many vertices were added.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A cone on a translate arm and a cube on a scale one, and both used to be wire.</b>
    ///         An outlined arrowhead is four ribs and a square: from the one angle it was built for it
    ///         reads as an arrow, and from every other it is four unrelated lines crossing near the
    ///         end of a shaft. It is also the part of a gizmo people aim at — the head is the target,
    ///         the shaft only says which way — so it is exactly the wrong part to draw as a hint.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Solid, and therefore lit, and therefore drawn without the depth test.</b> A wire
    ///         head that is occluded still shows through as a few pixels; a solid one that is occluded
    ///         is simply gone. <c>MeshRenderer</c> has the overlay pipeline for this, and
    ///         <c>ScenePresenter</c> records the handles through it after everything else.
    ///     </para>
    ///     <para>
    ///         <b>The geometry is <c>MeshPrimitives</c>' and is built once.</b> A cone's normals are
    ///         the fiddly part — the tip is a <i>row</i> of vertices with different normals, or it is
    ///         lit as though a spotlight were on one side of it — and that is solved there and tested
    ///         there. What is left here is a matrix per head: a rotation taking the primitive's +Y
    ///         onto the arm, a scale, and a translation. The shapes are cached because they never
    ///         change; only the matrix does, and it changes every frame because the gizmo is a
    ///         constant size on screen.
    ///     </para>
    /// </remarks>
    public static int BuildSolid(
        TransformGizmo gizmo,
        EditorCamera camera,
        int height,
        List<MeshVertex> vertices,
        List<uint> triangles
    ) {
        ArgumentNullException.ThrowIfNull(gizmo);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);

        var before = vertices.Count;

        if (gizmo.Targets.Count == 0 || gizmo.Mode == GizmoMode.None) {
            return 0;
        }

        var origin = gizmo.Origin;
        var basis = gizmo.Basis(camera);
        var scale = gizmo.WorldPerPixel(camera, height) * gizmo.HandleLength;
        var active = gizmo.IsDragging ? gizmo.Active : gizmo.Hovered;

        // Rotation is four tubes and nothing else: no arms, no heads, no middle box.
        if (gizmo.Mode == GizmoMode.Rotate) {
            Tori(vertices, triangles, gizmo, camera, height, origin, basis, active);
            return vertices.Count - before;
        }

        // ⚠ Before the heads, and that is what makes the translucent quads read. Both go into one
        // buffer drawn with the depth test off, so what covers what is the order they were appended
        // in — and an opaque cone drawn under a half-transparent square is a cone with a coloured
        // film over it.
        PlaneQuads(vertices, triangles, gizmo, camera, origin, basis, scale, active);

        var cube = gizmo.Mode == GizmoMode.Scale;
        var shape = cube ? Cube : Cone;
        var length = scale * (cube ? gizmo.HeadRadius * 2f : gizmo.HeadLength);
        var radius = scale * gizmo.HeadRadius;

        for (var axis = 0; axis < 3; axis++) {
            var direction = Axis(basis, axis);

            // The same call the shafts and the hit test ask, so a head is drawn exactly when the arm
            // it belongs to is grabbable.
            if (!gizmo.IsAxisVisible(direction, camera)) {
                continue;
            }

            // ⚠ Centred on its own middle, not on the tip. Both primitives straddle the origin in
            // their local Y, so placing one at the end of the arm would bury half of it in the shaft
            // and leave the arm looking short by half a head.
            var centre = origin + (direction * (scale - (length * 0.5f)));

            Append(
                vertices,
                triangles,
                shape,
                Frame(direction, radius * 2f, length, centre),
                AxisColour(axis, active == GizmoHandle.AxisX + axis),
                camera
            );
        }

        Middle(gizmo, camera, height, vertices, triangles, origin, basis, active);

        return vertices.Count - before;
    }

    /// <summary>The box in the middle: uniform scale, or a drag in the view plane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A solid cube, and it used to be a flat outlined square held square to the camera.</b>
    ///         The reason it faced the camera was that it belongs to no axis and a square on the
    ///         object's own axes is one you have to orbit to see square — which is true of a
    ///         <i>square</i> and is the whole problem with using one. A cube reads as a cube from every
    ///         angle, so it can sit on the gizmo's own basis, where its faces line up with the arms and
    ///         it reads as the middle of a solid object rather than as a sticker on the front of one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sized to fit inside the circle the hit test uses, rather than to match its
    ///         radius.</b> <c>HitTest</c> answers for a circle of <c>CentreRadius</c> pixels, and the
    ///         old square's half-side <i>was</i> that radius — so its four corners stuck out to
    ///         <c>√2 × CentreRadius</c> and did not answer clicks. A cube's corners reach
    ///         <c>√3 × </c> its half-extent, so that is what the half-extent is divided by: every drawn
    ///         pixel of it is inside the region that grabs it. This is the same rule
    ///         <c>TransformGizmo.Tolerance</c> follows for the arms and it fails the same way when it
    ///         is broken — at the edges of a handle, which reads as the tool being unreliable rather
    ///         than as a number being wrong.
    ///     </para>
    /// </remarks>
    static void Middle(
        TransformGizmo gizmo,
        EditorCamera camera,
        int height,
        List<MeshVertex> vertices,
        List<uint> triangles,
        Vector3 origin,
        Matrix4x4 basis,
        GizmoHandle active
    ) {
        var handle = CentreHandle(gizmo.Mode);

        if (handle == GizmoHandle.None) {
            return;
        }

        var half = gizmo.WorldPerPixel(camera, height) * gizmo.CentreRadius / MathF.Sqrt(3f);

        // On the gizmo's basis, so the cube's faces are perpendicular to the arms leaving it. In
        // screen space that is the camera's basis and the cube faces the viewer, which is the same
        // answer the flat square used to give and now falls out rather than being asserted.
        var side = Axis(basis, 0) * (half * 2f);
        var up = Axis(basis, 1) * (half * 2f);
        var forward = Axis(basis, 2) * (half * 2f);

        Append(
            vertices,
            triangles,
            Cube,
            new Matrix4x4(
                side.X, side.Y, side.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                origin.X, origin.Y, origin.Z, 1f
            ),
            NeutralColour(active == handle),
            camera
        );
    }

    /// <summary>How translucent a plane handle's fill is when it is not under the pointer.</summary>
    /// <remarks>
    ///     ⚠ <b>Low, and it has to be.</b> The three quads sit between the arms and over whatever is
    ///     being moved, so an opaque fill hides the object at exactly the moment somebody is looking
    ///     at where it will land. Under the pointer it goes to <see cref="PlaneHighlightAlpha" />,
    ///     which is what says "this is the one you would grab" without hiding anything for longer than
    ///     the pointer is there.
    /// </remarks>
    public const float PlaneFillAlpha = 0.22f;

    /// <summary>And when it is.</summary>
    public const float PlaneHighlightAlpha = 0.45f;

    /// <summary>The three quads that drag in a plane, as filled squares.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The hit test has always treated these as filled and the picture said
    ///         otherwise.</b> <c>TransformGizmo</c>'s plane test is <c>InQuad</c> — a point-in-polygon
    ///         test over the whole square — not a distance to its border, so the middle of an outlined
    ///         plane handle answered clicks and looked like empty space between three arms. An outline
    ///         understating what answers a click by the whole of its middle is the same class of fault
    ///         as a grab radius narrower than the line it grabs, and it reads the same way: the tool
    ///         being unreliable rather than a number being wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Skipped edge-on, on the same threshold the outline and the hit test use.</b> The
    ///         three have to agree or a handle is drawn where it cannot be grabbed, or grabbable where
    ///         nothing is drawn.
    ///     </para>
    /// </remarks>
    static void PlaneQuads(
        List<MeshVertex> vertices,
        List<uint> triangles,
        TransformGizmo gizmo,
        EditorCamera camera,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        GizmoHandle active
    ) {
        if (gizmo.Mode is not (GizmoMode.Translate or GizmoMode.Transform)) {
            return;
        }

        Span<GizmoHandle> handles = [GizmoHandle.PlaneYZ, GizmoHandle.PlaneZX, GizmoHandle.PlaneXY];

        for (var index = 0; index < 3; index++) {
            var first = Axis(basis, (index + 1) % 3);
            var second = Axis(basis, (index + 2) % 3);
            var normal = Vector3.Cross(first, second);

            if (MathF.Abs(Vector3.Dot(normal, camera.Forward)) < PlaneEdgeOn) {
                continue;
            }

            var highlighted = active == handles[index];
            var axis = AxisColour(index, highlighted);

            var colour = new Color4(
                axis.R,
                axis.G,
                axis.B,
                highlighted ? PlaneHighlightAlpha : PlaneFillAlpha
            );

            var corner = origin + ((first + second) * (scale * gizmo.PlaneOffset));
            var side = first * (scale * gizmo.PlaneSize);
            var up = second * (scale * gizmo.PlaneSize);

            var start = (uint) vertices.Count;

            vertices.Add(new(corner, normal, colour));
            vertices.Add(new(corner + side, normal, colour));
            vertices.Add(new(corner + side + up, normal, colour));
            vertices.Add(new(corner + up, normal, colour));

            // Two triangles round the four corners in order. The pipeline is two-sided, so which way
            // round they wind decides nothing here — see `Frame`'s remarks for why that is still not
            // an excuse to get it wrong.
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }
    }

    /// <summary>How nearly edge-on a plane handle may be before it is neither drawn nor grabbable.</summary>
    /// <remarks>
    ///     One number in one place, read by the fill, by the outline and — through
    ///     <c>TransformGizmo</c>'s own copy of the same threshold — by the hit test. A quad seen along
    ///     its edge projects to a sliver lying on the third arm and would take that arm's clicks.
    /// </remarks>
    public const float PlaneEdgeOn = 0.15f;

    /// <summary>How many sides a rotation ring's tube has.</summary>
    /// <remarks>
    ///     Six, for a tube five pixels across: each flat is under two pixels wide, and the silhouette
    ///     of a hexagon that size is a circle. More sides is four hundred more vertices a frame for a
    ///     shape nobody can tell apart from this one.
    /// </remarks>
    public const int TubeSides = 6;

    /// <summary>The four rotation rings, as tubes.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A torus rather than a polyline, and the argument is the arm heads' argument.</b> A
    ///         ring drawn as several parallel one-pixel strokes is a ring that reads as a ring only
    ///         from the angle the strokes were offset for; where it turns towards the eye the strokes
    ///         converge and it thins to a hairline. A tube is the same shape from every angle, and it
    ///         is what makes the near side of a ring look nearer than the far side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cut to the half facing the camera, exactly as the wire rings were and the hit test
    ///         still is.</b> Three full circles about one point cross each other twelve times, and at
    ///         every crossing the front of one ring is over the back of another. Solid geometry makes
    ///         that worse rather than better: the back half is no longer something you can see the
    ///         front through.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The run is broken rather than the sample skipped</b>, which for a tube means no
    ///         quad is emitted across the horizon — the wire version's chord, in three dimensions.
    ///     </para>
    /// </remarks>
    static void Tori(
        List<MeshVertex> vertices,
        List<uint> triangles,
        TransformGizmo gizmo,
        EditorCamera camera,
        int height,
        Vector3 origin,
        Matrix4x4 basis,
        GizmoHandle active
    ) {
        var worldPerPixel = gizmo.WorldPerPixel(camera, height);
        var radius = worldPerPixel * gizmo.HandleLength;
        var tube = worldPerPixel * MathF.Max(1f, gizmo.Thickness) * 0.5f;
        var towards = TowardsEye(camera, origin);

        for (var axis = 0; axis < 3; axis++) {
            var normal = Axis(basis, axis);
            var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(reference, normal));
            var v = Vector3.Cross(normal, u);

            Tube(
                vertices,
                triangles,
                origin,
                u,
                v,
                radius,
                tube,
                AxisColour(axis, active == GizmoHandle.AxisX + axis),
                towards
            );
        }

        // The screen ring is a whole circle: it lies in the view plane, so every part of it faces the
        // camera and there is no far half to cut.
        Tube(
            vertices,
            triangles,
            origin,
            camera.Right,
            camera.Up,
            radius * gizmo.ScreenRingScale,
            tube,
            NeutralColour(active == GizmoHandle.Screen)
        );
    }

    /// <summary>One tube swept round a circle, or round the half of it facing a direction.</summary>
    static void Tube(
        List<MeshVertex> vertices,
        List<uint> triangles,
        Vector3 origin,
        Vector3 u,
        Vector3 v,
        float radius,
        float tube,
        Color4 colour,
        Vector3? towards = null
    ) {
        var axis = Vector3.Cross(u, v);
        var previous = -1;

        for (var step = 0; step <= RingSegments; step++) {
            var angle = step / (float) RingSegments * MathF.Tau;
            var radial = (u * MathF.Cos(angle)) + (v * MathF.Sin(angle));

            if (towards is { } eye && Vector3.Dot(radial, eye) < 0f) {
                previous = -1;
                continue;
            }

            var centre = origin + (radial * radius);
            var start = vertices.Count;

            for (var side = 0; side < TubeSides; side++) {
                // ⚠ The cross-section's own normal is the outward direction of the tube at that
                // point, which is what makes a ring look round rather than flat. It is not the
                // ring's radial: half of the tube faces along the ring's axis.
                var around = side / (float) TubeSides * MathF.Tau;
                var normal = (radial * MathF.Cos(around)) + (axis * MathF.Sin(around));

                vertices.Add(new(centre + (normal * tube), normal, colour));
            }

            if (previous >= 0) {
                for (var side = 0; side < TubeSides; side++) {
                    var next = (side + 1) % TubeSides;

                    var a = (uint) (previous + side);
                    var b = (uint) (previous + next);
                    var c = (uint) (start + next);
                    var d = (uint) (start + side);

                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(d);
                }
            }

            previous = start;
        }
    }

    /// <summary>The three shafts. The heads on the ends of them are <see cref="BuildSolid" />'s.</summary>
    static void Arms(
        List<LineVertex> into,
        EditorCamera camera,
        TransformGizmo gizmo,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        GizmoHandle active,
        Pen pen
    ) {
        for (var axis = 0; axis < 3; axis++) {
            var direction = Axis(basis, axis);

            // ⚠ Hidden when it is a dot, and hidden by the same call the hit test uses. An arm
            // pointing at the eye draws as a smudge over the middle of the gizmo and would win every
            // click in it — see `TransformGizmo.MinimumAxisLength`.
            if (!gizmo.IsAxisVisible(direction, camera)) {
                continue;
            }

            var end = origin + (direction * scale);
            var colour = AxisColour(axis, active == GizmoHandle.AxisX + axis);

            // ⚠ Started where the hit test starts it, so the middle of the gizmo belongs to the
            // centre handle in the picture as well as in the arithmetic. An arm drawn through a
            // region that answers for something else is the visible half of the oldest gizmo
            // complaint there is: "it grabbed the wrong axis".
            Segment(into, origin + (direction * (scale * gizmo.ArmStart)), end, colour, pen);
        }
    }

    /// <summary>The three quads that drag in a plane, drawn as squares on the arms.</summary>
    /// <remarks>
    ///     ⚠ <b>Skipped when seen edge-on, exactly as the hit test skips them.</b> A handle that is
    ///     drawn and not grabbable is worse than one that is neither — see <c>TransformGizmo</c>'s
    ///     plane test for the same threshold and the same reason.
    /// </remarks>
    static void Planes(
        List<LineVertex> into,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        GizmoHandle active,
        TransformGizmo gizmo,
        Vector3 forward,
        Pen pen
    ) {
        if (gizmo.Mode is not (GizmoMode.Translate or GizmoMode.Transform)) {
            return;
        }

        Span<GizmoHandle> handles = [GizmoHandle.PlaneYZ, GizmoHandle.PlaneZX, GizmoHandle.PlaneXY];

        for (var index = 0; index < 3; index++) {
            var first = Axis(basis, (index + 1) % 3);
            var second = Axis(basis, (index + 2) % 3);

            if (MathF.Abs(Vector3.Dot(Vector3.Cross(first, second), forward)) < PlaneEdgeOn) {
                continue;
            }

            var colour = AxisColour(index, active == handles[index]);
            colour = new Color4(colour.R, colour.G, colour.B, active == handles[index] ? 0.9f : 0.5f);

            var corner = origin + ((first + second) * (scale * gizmo.PlaneOffset));
            var a = corner;
            var b = corner + (first * (scale * gizmo.PlaneSize));
            var c = corner + ((first + second) * (scale * gizmo.PlaneSize));
            var d = corner + (second * (scale * gizmo.PlaneSize));

            Segment(into, a, b, colour, pen);
            Segment(into, b, c, colour, pen);
            Segment(into, c, d, colour, pen);
            Segment(into, d, a, colour, pen);
        }
    }

    /// <summary>Which handle the middle of a gizmo answers for, if any.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The scale gizmo's has always been answered for and was never drawn.</b>
    ///         <c>HitTest</c> returns <see cref="GizmoHandle.Uniform" /> for the middle of a scale
    ///         gizmo, so the most-used handle of the three was invisible — a click that scaled
    ///         everything uniformly, discoverable only by accident.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Translate's was missing rather than merely undrawn.</b> Dragging in the view plane
    ///         is how anything gets moved that is not along an axis, and without it the middle of a
    ///         translate gizmo — where three arms cross and every click is ambiguous — was answered by
    ///         whichever arm the loop reached first. One box, two meanings, and in both cases it means
    ///         "the thing the arms cannot do".
    ///     </para>
    /// </remarks>
    static GizmoHandle CentreHandle(GizmoMode mode) =>
        mode switch {
            GizmoMode.Scale or GizmoMode.Transform => GizmoHandle.Uniform,
            GizmoMode.Translate => GizmoHandle.Screen,
            _ => GizmoHandle.None
        };


    /// <summary>One segment, drawn as many parallel strokes as the pen has.</summary>
    /// <remarks>
    ///     <para>
    ///         The offset is <c>segment × view</c>: perpendicular to the segment, and perpendicular
    ///         to the direction it is being looked at from. That is what "across the line on screen"
    ///         is in world space, and it is why the strokes stay a fixed number of pixels apart from
    ///         every angle rather than collapsing into one line at some of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A segment pointing straight at the camera has no such perpendicular</b>, and the
    ///         cross product is the zero vector rather than an error. It projects to a point, so any
    ///         direction across it is as good as any other and the camera's own right is used —
    ///         without the fallback the strokes would all be offset by nothing, which is the
    ///         arm nearest the eye quietly going back to one pixel wide.
    ///     </para>
    /// </remarks>
    static void Segment(List<LineVertex> into, Vector3 from, Vector3 to, Color4 colour, Pen pen) {
        if (pen.Strokes <= 1) {
            into.Add(new(from, colour));
            into.Add(new(to, colour));

            return;
        }

        var across = Vector3.Cross(to - from, pen.Towards);

        across = across.LengthSquared() > MathUtil.ZeroTolerance
            ? Vector3.Normalize(across)
            : pen.Fallback;

        // Centred on the segment, so thickening a handle does not also move it off the line the hit
        // test measures against.
        var first = (pen.Strokes - 1) * -0.5f;

        for (var stroke = 0; stroke < pen.Strokes; stroke++) {
            var shift = across * ((first + stroke) * pen.WorldPerPixel);

            into.Add(new(from + shift, colour));
            into.Add(new(to + shift, colour));
        }
    }

    /// <summary>How many divisions a solid head is built with.</summary>
    /// <remarks>
    ///     Twelve, for a cone about twenty pixels across: the flats are under two pixels wide, which
    ///     is past the point where more of them change the picture. <c>MeshPrimitives</c>' own default
    ///     is for shapes somebody is modelling with rather than aiming at.
    /// </remarks>
    public const int HeadSegments = 12;

    /// <summary>The translate arm's head, a unit cone standing on +Y.</summary>
    /// <remarks>
    ///     ⚠ <b>Built once, at type initialisation, and never mutated.</b> A shape's geometry does not
    ///     change; what changes every frame is the matrix it is placed by, because the gizmo is a
    ///     constant size on screen and so its head is a different size in world units at every
    ///     distance. Rebuilding thirty-eight vertices three times a frame would be all of this pass's
    ///     cost and none of its output — the same reason <c>SceneMeshes</c> caches by kind.
    /// </remarks>
    static readonly MeshData Cone = MeshPrimitives.Cone(0.5f, 1f, HeadSegments);

    /// <summary>The scale arm's head, a unit cube.</summary>
    static readonly MeshData Cube = MeshPrimitives.Cube();

    /// <summary>The transform that puts a unit shape on an arm.</summary>
    /// <param name="direction">Where the shape's local +Y goes, unit length.</param>
    /// <param name="width">How wide to make it.</param>
    /// <param name="length">How long, along <paramref name="direction" />.</param>
    /// <param name="centre">Where its middle goes.</param>
    /// <returns>The local-to-world transform.</returns>
    /// <remarks>
    ///     ⚠ <b>The two axes across the arm are arbitrary and that is fine — for these two shapes.</b>
    ///     A cone is symmetric about its axis, so which way round its cross-section is turned is
    ///     invisible; a cube is not, and a cube whose sides face nowhere in particular is still a
    ///     cube. What this must not be used for is a head that has a front.
    /// </remarks>
    static Matrix4x4 Frame(Vector3 direction, float width, float length, Vector3 centre) {
        var across = Perpendicular(direction);

        // ⚠ `across × direction`, not the other way round. Either produces a frame the shape fits
        // into, and the wrong one is a mirror — every triangle wound backwards. Nothing here would
        // show it, because the pipeline is two-sided and the normals go through the inverse transpose
        // and come out right regardless, which is precisely why it would be left in place to be
        // discovered by whatever turns culling on next.
        var side = across * width;
        var forward = Vector3.Cross(across, direction) * width;
        var up = direction * length;

        return new(
            side.X, side.Y, side.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            centre.X, centre.Y, centre.Z, 1f
        );
    }

    /// <summary>Any unit vector at right angles to another.</summary>
    /// <remarks>
    ///     ⚠ The cross product with a fixed axis is the zero vector when the two are parallel, and
    ///     <c>Vector3.Normalize</c> of that is <c>NaN</c> rather than zero — which no <c>IsZero</c>
    ///     test catches and which spreads through every number it touches. Which of the two candidates
    ///     is used is chosen before the divide, from the component that is largest.
    /// </remarks>
    static Vector3 Perpendicular(Vector3 direction) =>
        Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));

    /// <summary>Places one shape's triangles into a frame's buffers.</summary>
    /// <remarks>
    ///     ⚠ <b>Indices are offset by where this shape's vertices started.</b> <c>MeshRenderer</c>
    ///     deliberately does not do it — a caller building a frame knows where each mesh began and it
    ///     does not — and an unoffset index names another shape's vertex, which draws a triangle
    ///     stretched between two heads.
    /// </remarks>
    static void Append(
        List<MeshVertex> vertices,
        List<uint> triangles,
        MeshData mesh,
        in Matrix4x4 transform,
        Color4 colour,
        EditorCamera camera
    ) {
        var first = (uint) vertices.Count;

        // ⚠ Through the inverse transpose, for the reason `SceneMeshes.Append` gives: a head is
        // scaled unevenly — a cone is longer than it is wide — so its own matrix leaves the normals
        // no longer perpendicular to the slope, and the shading slides across it as the camera
        // approaches. A matrix that will not invert is a zero-size head with nothing to light.
        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            var normal = hasNormals
                ? Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[index], normals))
                : Vector3.UnitY;

            vertices.Add(new(Matrix4x4.TransformPosition(mesh.Positions[index], transform), normal, colour));
        }

        // A mesh with no normals cannot be asked which way its faces point, so it keeps every
        // triangle and the picture it produces is the one it produced before.
        if (!hasNormals) {
            foreach (var index in mesh.Indices) {
                triangles.Add(first + (uint) index);
            }

            return;
        }

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            var a = vertices[(int) first + mesh.Indices[index]];
            var b = vertices[(int) first + mesh.Indices[index + 1]];
            var c = vertices[(int) first + mesh.Indices[index + 2]];

            if (!Faces(a, b, c, camera)) {
                continue;
            }

            triangles.Add(first + (uint) mesh.Indices[index]);
            triangles.Add(first + (uint) mesh.Indices[index + 1]);
            triangles.Add(first + (uint) mesh.Indices[index + 2]);
        }
    }

    /// <summary>Whether a triangle's outward side is the side the camera is on.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the back-face cull, done here rather than by the rasteriser, and it is
    ///         what stopped the middle box being drawn inside out.</b> The handles go into the pass
    ///         that has <i>no depth test and no depth write</i> — a gizmo you cannot reach through the
    ///         thing it moves is a gizmo you cannot use — and the pipeline they share is two-sided. So
    ///         nothing decided which of a solid shape's own faces was in front of which, and the
    ///         answer was "whichever was appended last", which for a cube is the far side. What you
    ///         saw was the inside of the box; the arm heads had the same fault and hid it better,
    ///         because a cone's far side is mostly behind its near one from any angle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the vertex normals, not against the winding.</b> Which winding the
    ///         rasteriser calls front depends on the API, on the projection's handedness and on
    ///         whether the viewport flips Y — three things this file has no business knowing, and the
    ///         reason every pipeline in the engine is two-sided in the first place. An outward normal
    ///         is the mesh's own statement about which side is outside, and
    ///         <c>MeshPrimitives</c> supplies one per vertex.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exact for a convex shape and only for a convex shape.</b> Both callers pass one —
    ///         a cone and a cube — and that is the whole of what makes dropping the far half
    ///         equivalent to sorting it. The rings are a torus and are not convex, which is why they
    ///         are built by <c>Tori</c> and cut to the half facing the camera instead.
    ///     </para>
    /// </remarks>
    static bool Faces(MeshVertex a, MeshVertex b, MeshVertex c, EditorCamera camera) {
        var normal = a.Normal + b.Normal + c.Normal;

        // ⚠ <see cref="TowardsEye" />, which is the same call the rings and the arms are cut by — so a
        // head and the ring beside it disappear at the same angle rather than a degree apart. It is
        // also what handles the orthographic case, where there is no eye to be towards.
        return Vector3.Dot(normal, TowardsEye(camera, (a.Position + b.Position + c.Position) / 3f)) > 0f;
    }

    static Vector3 Axis(Matrix4x4 basis, int index) =>
        index switch {
            0 => Vector3.Normalize(new(basis.M11, basis.M12, basis.M13)),
            1 => Vector3.Normalize(new(basis.M21, basis.M22, basis.M23)),
            _ => Vector3.Normalize(new(basis.M31, basis.M32, basis.M33))
        };

    static Color4 Lerp(Color4 from, Color4 to, float amount) =>
        new(
            from.R + ((to.R - from.R) * amount),
            from.G + ((to.G - from.G) * amount),
            from.B + ((to.B - from.B) * amount),
            from.A + ((to.A - from.A) * amount)
        );
}
