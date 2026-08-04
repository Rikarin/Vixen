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
    /// <param name="Light">Which way the key light travels — see <see cref="KeyLight" />.</param>
    readonly record struct Pen(
        Vector3 Towards,
        Vector3 Fallback,
        float WorldPerPixel,
        int Strokes,
        Vector3 Light
    );

    /// <summary>The colour of the arm along an axis.</summary>
    /// <param name="axis">0 for x, 1 for y, 2 for z.</param>
    /// <param name="highlighted">Whether the pointer is over it.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     <para>
    ///         The three axis colours every editor uses. Not shared with the corner axis cross's, in
    ///         <c>Viewport.AxisColor</c>: that one reads the stylesheet and a scene's gizmo is not a
    ///         styled element — and the two want <i>different</i> reds, for the reason below. The
    ///         cross is drawn flat, as a two-pixel stroke; nothing here is ever drawn flat.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Saturated well past what a flat swatch wants, and that is the point.</b> These are
    ///         never drawn as themselves: every pixel of a handle is this multiplied by
    ///         <see cref="Shade" />, which is <see cref="Ambient" /> — a third — where a surface faces
    ///         away from the key light. A colour chosen to look right flat is a colour whose shadowed
    ///         half is mud, which is what the earlier, gentler triple produced: an arm that read as
    ///         grey-brown from below and as nothing at all against a lit floor. Picking the lit end of
    ///         the ramp and letting the shading walk it down is why the dark side of a head here is
    ///         still recognisably red.
    ///     </para>
    /// </remarks>
    public static Color4 AxisColour(int axis, bool highlighted = false) {
        var colour = axis switch {
            0 => new Color4(1f, 0.11f, 0.16f, 1f),
            1 => new Color4(0.24f, 0.93f, 0.13f, 1f),
            _ => new Color4(0.10f, 0.42f, 1f, 1f)
        };

        return highlighted ? Highlight(colour) : colour;
    }

    /// <summary>The colour of a handle that belongs to no axis: the middle ball, the screen ring.</summary>
    /// <param name="highlighted">Whether the pointer is over it.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     Grey, because it is the one handle that means "all of them" and any of the three axis
    ///     colours would claim it belongs to that axis — which for the uniform-scale ball is precisely
    ///     the wrong thing to say.
    /// </remarks>
    public static Color4 NeutralColour(bool highlighted = false) {
        var colour = new Color4(0.90f, 0.92f, 0.95f, 1f);

        return highlighted ? Highlight(colour) : colour;
    }

    /// <summary>How much of its colour a handle keeps while the pointer is over it.</summary>
    /// <remarks>
    ///     ⚠ <b>Under one: a handle goes <i>darker</i> under the pointer, where it used to go pale.</b>
    ///     Both directions are visible, and the reason to pick this one is what the other end of the
    ///     range already means. Every pixel here is its colour times <see cref="Shade" />, so brightness
    ///     is the channel that says which way a surface faces — a hovered arm lightened towards white
    ///     is an arm that has lost its shading, and next to an unhovered one it reads as a differently
    ///     lit object rather than as the same object under the pointer. Darkening rides the same
    ///     channel in the direction nothing else uses: nothing on a gizmo is darker than its own
    ///     ambient, so below that is unambiguous.
    /// </remarks>
    public const float HighlightShade = 0.45f;

    /// <summary>A handle's colour while the pointer is over it.</summary>
    /// <remarks>
    ///     ⚠ <b>A scale and not a blend, so the hue and the saturation come through untouched.</b>
    ///     Mixing towards a dark colour would drag all three arms towards the same one, and the whole
    ///     job of an axis colour is to say which axis. Multiplying only moves the value.
    /// </remarks>
    static Color4 Highlight(Color4 colour) =>
        new(colour.R * HighlightShade, colour.G * HighlightShade, colour.B * HighlightShade, colour.A);

    /// <summary>Which way the light a handle is shaded by travels.</summary>
    /// <param name="camera">The camera it is seen from.</param>
    /// <returns>A unit vector, in world space.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Over the viewer's left shoulder, and therefore not a direction in the world at
    ///         all.</b> A gizmo has no place in the scene's lighting — it is not an object, it is a
    ///         control drawn on top of one — and the fixed downward key <c>MeshRenderer</c> defaults
    ///         to has one specific failure that matters here: a shape whose axis runs <i>along</i> the
    ///         key is lit dead flat, because every normal on it is at right angles to the light. That
    ///         is the vertical arm, from every camera angle, on every gizmo. The arm that is hardest
    ///         to tell from a painted line is the one that never gets a gradient.
    ///     </para>
    ///     <para>
    ///         A key that follows the camera cannot land along an arm for more than the moment the arm
    ///         points at the eye — which is the moment <c>IsAxisVisible</c> stops drawing it anyway.
    ///         Down and to the left of the line of sight is the convention every modelling tool's
    ///         headlight uses, and it is the direction a reader already assumes when they judge which
    ///         way a shape bulges.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves of a handle have to be given this, and they are given it separately.</b>
    ///         The heads are shaded on the GPU from <c>MeshRenderer.LightDirection</c>, which
    ///         <c>ScenePresenter</c> sets from this call once a frame; the shafts are shaded here on
    ///         the CPU — see <see cref="Segment" />. A presenter that set one and not the other draws
    ///         a cone lit from the side its own arm is dark on, which reads as the head belonging to
    ///         something else.
    ///     </para>
    /// </remarks>
    public static Vector3 KeyLight(EditorCamera camera) {
        ArgumentNullException.ThrowIfNull(camera);

        return Vector3.Normalize((camera.Forward * 0.55f) + (camera.Right * 0.45f) - (camera.Up * 0.7f));
    }

    /// <summary>How much light a surface facing away from the key still receives.</summary>
    /// <remarks>
    ///     ⚠ <b>Lower than <c>MeshRenderer.Ambient</c>'s own default, and <c>ScenePresenter</c> pushes
    ///     this one instead.</b> That default is for a shape somebody is looking at, where the job is
    ///     to be legible; a handle is a shape somebody is aiming at, where the job is to be
    ///     unmistakably solid, and the difference is contrast. A quarter puts four times the range
    ///     between the lit and the shadowed side of a twenty-pixel cone. It goes no lower because the
    ///     ambient term is also what keeps a handle's dark side a colour rather than a silhouette —
    ///     which for the axis whose arm is pointing away from the key is most of it.
    /// </remarks>
    public const float Ambient = 0.25f;

    /// <summary>How brightly a surface facing some way is lit, from <see cref="Ambient" /> to one.</summary>
    /// <param name="normal">Which way it faces, unit length.</param>
    /// <param name="light">Which way the light travels, unit length.</param>
    /// <returns>What to multiply its colour by.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Wrapped, and <c>Mesh.rvn</c> wraps it identically.</b> A plain
    ///         <c>max(dot, 0)</c> takes every surface past ninety degrees from the key to exactly the
    ///         ambient term, so the whole far side of a cone is one flat colour and the shape stops
    ///         being readable precisely where it curves most. Remapping the dot product from −1…1 to
    ///         0…1 and squaring it keeps the gradient running round the back — the far side is darker
    ///         than the near side rather than equal to it — which is what makes a twenty-pixel head
    ///         look round at all.
    ///     </para>
    ///     <para>
    ///         The square is what puts the midpoint back where a Lambert term had it. Without it a
    ///         surface at right angles to the light comes back at three quarters brightness and
    ///         everything looks flatly overlit, which is the usual complaint about wrapped diffuse.
    ///     </para>
    /// </remarks>
    public static float Shade(Vector3 normal, Vector3 light) {
        var wrapped = (Vector3.Dot(normal, -light) * 0.5f) + 0.5f;

        return Ambient + ((1f - Ambient) * wrapped * wrapped);
    }

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
            Math.Max(1, (int) MathF.Round(gizmo.Thickness)),
            KeyLight(camera)
        );

        // What the pointer is over, or what is being dragged — the drag wins, because a drag that
        // stopped highlighting the arm it is moving looks like it let go.
        var active = gizmo.IsDragging ? gizmo.Active : gizmo.Hovered;

        // ⚠ A rotate gizmo has no wire half at all any more. Its rings are tubes through
        // <see cref="BuildSolid" />, and drawing a polyline down the middle of an eight-pixel tube puts
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
        var length = scale * HeadDepth(gizmo);
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

        // ⚠ Last, so the ball is over the three shafts running into it rather than under them. Both
        // orderings draw the same geometry — the arms start at the origin either way — and the
        // difference is whether the middle of the gizmo is a ball with three arms leaving it or a
        // crossing of three arms with a ball behind. The first is the one that says "this is a
        // handle": the arms are what you follow and the ball is what you aim at, and a target you can
        // see the lines through is a target that reads as a decoration on them.
        Middle(gizmo, camera, height, vertices, triangles, origin, active);

        return vertices.Count - before;
    }

    /// <summary>The ball in the middle: uniform scale, or a drag in the view plane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A solid sphere, and it has been a flat outlined square and then a cube.</b> Each
    ///         change answered the previous one's complaint. The square faced the camera because the
    ///         handle belongs to no axis, and a square on the object's own axes is one you have to
    ///         orbit to see square. The cube answered that — a cube reads as a cube from every angle,
    ///         so it could sit on the gizmo's own basis with its faces lined up with the arms — and
    ///         introduced its own: a cube has an orientation, three of its faces are flat planes, and
    ///         a shape whose faces are flat planes is a shape with three brightnesses rather than a
    ///         gradient. Read next to the round heads it looked like a different object stuck on.
    ///     </para>
    ///     <para>
    ///         A sphere is the shape that has no orientation at all, which is the honest picture of a
    ///         handle that means <i>all three axes</i>, and it is the shape a light gradient reads best
    ///         on — every normal on the visible half, so <see cref="Shade" /> runs its whole range
    ///         across twenty-odd pixels. That it no longer needs the basis is the tell that the basis
    ///         was never what the handle was about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Drawn at exactly the radius the hit test answers within, which no shape before it
    ///         could be.</b> <c>HitTest</c> takes the middle for a circle of <c>CentreRadius</c>
    ///         pixels. The old square's half-side <i>was</i> that radius, so its four corners stuck out
    ///         to <c>√2 ×</c> it and did not answer clicks, and the cube that followed had to be
    ///         divided by <c>√3</c> to pull its corners back inside — which left it drawn at a bit over
    ///         half the region it stood for. A sphere's every point is the same distance out, so it is
    ///         the one shape whose silhouette <i>is</i> the circle: what is drawn and what is grabbed
    ///         are the same disc, and there is no ring of pixels that looks like the handle and is not,
    ///         nor any that answers for it and looks like nothing. This is the same rule
    ///         <c>TransformGizmo.Tolerance</c> follows for the arms and it fails the same way when it
    ///         is broken — at the edges of a handle, which reads as the tool being unreliable rather
    ///         than as a number being wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The arms are built through it and it is drawn over them.</b>
    ///         <c>TransformGizmo.ArmStart</c> is zero, so every shaft is a segment from the origin
    ///         outwards and the inner <c>CentreRadius</c> pixels of all three are hidden by this. The
    ///         geometry runs through and the picture does not, which is deliberate: the arms are what
    ///         you follow and the ball is what you aim at, so the ball has to sit on top of them the
    ///         way a control sits on top of what it controls. Nothing has to fit — an arm cannot leave
    ///         a gap or overshoot when it starts inside the shape that covers it, which is what the
    ///         old <c>ArmStart</c> had to be tuned for.
    ///     </para>
    /// </remarks>
    static void Middle(
        TransformGizmo gizmo,
        EditorCamera camera,
        int height,
        List<MeshVertex> vertices,
        List<uint> triangles,
        Vector3 origin,
        GizmoHandle active
    ) {
        var handle = CentreHandle(gizmo.Mode);

        if (handle == GizmoHandle.None) {
            return;
        }

        var half = gizmo.WorldPerPixel(camera, height) * gizmo.CentreRadius;

        // ⚠ No basis, where the cube needed one and the square before it needed the camera's. A
        // sphere on the gizmo's axes and a sphere on the camera's are the same picture, so there is
        // nothing left here to get wrong — a plain scale about the origin is the whole transform.
        Append(
            vertices,
            triangles,
            Ball,
            new Matrix4x4(
                half * 2f, 0f, 0f, 0f,
                0f, half * 2f, 0f, 0f,
                0f, 0f, half * 2f, 0f,
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
    ///     Eight, for a tube <c>TransformGizmo.Thickness</c> pixels across: each flat is about three
    ///     pixels wide, and the silhouette of an octagon that size is a circle. It was six when the
    ///     tube was five pixels across and the same argument gave the same answer; a wider tube on the
    ///     old count is a ring you can see the corners of, which reads as the ring being made of
    ///     segments rather than being round. More sides than this is hundreds more vertices a frame
    ///     for a shape nobody can tell apart from this one.
    /// </remarks>
    public const int TubeSides = 8;

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

    /// <summary>How far back from the tip of an arm its head reaches, as a fraction of the arm.</summary>
    /// <remarks>
    ///     One number asked by two places — <see cref="BuildSolid" />, which puts the head there, and
    ///     <see cref="Arms" />, which stops the shaft inside it. They cannot be allowed to disagree:
    ///     a shaft that outruns its head is the needle through the arrow, and one that stops short is
    ///     a gap between the two.
    /// </remarks>
    static float HeadDepth(TransformGizmo gizmo) =>
        gizmo.Mode == GizmoMode.Scale ? gizmo.HeadRadius * 2f : gizmo.HeadLength;

    /// <summary>The three shafts. The heads on the ends of them are <see cref="BuildSolid" />'s.</summary>
    /// <remarks>
    ///     ⚠ <b>A shaft stops at the middle of its own head, not at the tip of the arm.</b> The head
    ///     is opaque, convex and drawn after the lines, so ending the shaft inside it hides the join;
    ///     running it to the tip does not, because the strokes are offset <i>across</i> the segment by
    ///     a few pixels and a cone is only a few pixels wide near its point. What that draws is a
    ///     needle sticking out of the arrowhead — three of them, at every camera angle. Half the head
    ///     rather than all of it because that is the depth at which the cone is still wide enough to
    ///     cover the full width of the pen.
    /// </remarks>
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
        var shaft = scale * (1f - (HeadDepth(gizmo) * 0.5f));

        for (var axis = 0; axis < 3; axis++) {
            var direction = Axis(basis, axis);

            // ⚠ Hidden when it is a dot, and hidden by the same call the hit test uses. An arm
            // pointing at the eye draws as a smudge over the middle of the gizmo and would win every
            // click in it — see `TransformGizmo.MinimumAxisLength`.
            if (!gizmo.IsAxisVisible(direction, camera)) {
                continue;
            }

            var end = origin + (direction * shaft);
            var colour = AxisColour(axis, active == GizmoHandle.AxisX + axis);

            // ⚠ Started where the hit test starts it, so the middle of the gizmo belongs to the
            // centre handle in the picture as well as in the arithmetic. An arm drawn through a
            // region that answers for something else is the visible half of the oldest gizmo
            // complaint there is: "it grabbed the wrong axis".
            Segment(into, origin + (direction * (scale * gizmo.ArmStart)), end, colour, pen, round: true);
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
    /// <param name="into">Where the strokes go.</param>
    /// <param name="from">One end.</param>
    /// <param name="to">The other.</param>
    /// <param name="colour">What colour, before any shading.</param>
    /// <param name="pen">How thick, and which way across.</param>
    /// <param name="round">Whether to shade the strokes as the surface of a cylinder.</param>
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
    ///     <para>
    ///         <b>What <paramref name="round" /> does is the whole reason a six-stroke arm stops
    ///         looking like a six-stroke arm.</b> The strokes already sit across the segment on
    ///         screen, so which stroke a pixel is on <i>is</i> where round a cylinder it would be: the
    ///         middle one faces the eye, the outermost two face along <c>across</c>, and everything
    ///         between is the arc joining them. Shading each by <see cref="Shade" /> of that normal
    ///         turns a flat ribbon into a lit shaft, at the cost of one dot product per stroke and no
    ///         extra geometry — and it is the same lighting the head on the end of the arm gets from
    ///         <c>Mesh.rvn</c>, which is what makes the two read as one object rather than as a solid
    ///         cone stuck on a flat stick.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The colour is shaded and the position is not.</b> A cylinder's surface bulges
    ///         towards the eye and this deliberately does not: the strokes stay in the plane the hit
    ///         test measures against, so an arm that looks round is still an arm that is grabbed
    ///         where it is drawn.
    ///     </para>
    /// </remarks>
    static void Segment(
        List<LineVertex> into,
        Vector3 from,
        Vector3 to,
        Color4 colour,
        Pen pen,
        bool round = false
    ) {
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

        // The cross-section is spanned by `across` and by the direction the eye is in, which is the
        // opposite of the way the camera looks. Normalised so that the outermost stroke is at ±1 —
        // the silhouette of the shaft — and the middle one at zero, facing the viewer squarely.
        var towardsEye = -pen.Towards;
        var edge = MathF.Max(1f, -first);

        for (var stroke = 0; stroke < pen.Strokes; stroke++) {
            var offset = first + stroke;
            var shift = across * (offset * pen.WorldPerPixel);
            var shaded = colour;

            if (round) {
                var sideways = offset / edge;

                // ⚠ Clamped before the square root, not after. `offset / edge` is exactly ±1 at the
                // outermost stroke and floating-point rounding puts it a hair past, which is a square
                // root of a small negative number — NaN, in a colour, spreading to every pixel the
                // stroke covers.
                var facing = MathF.Sqrt(MathF.Max(0f, 1f - (sideways * sideways)));
                var shade = Shade((across * sideways) + (towardsEye * facing), pen.Light);

                shaded = new(colour.R * shade, colour.G * shade, colour.B * shade, colour.A);
            }

            into.Add(new(from + shift, shaded));
            into.Add(new(to + shift, shaded));
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

    /// <summary>The middle handle, a unit sphere.</summary>
    /// <remarks>
    ///     Sixteen round and eight over the pole, for a ball under thirty pixels across: the widest
    ///     flat on it is about three pixels, which is past the point where more of them change the
    ///     picture. <c>MeshPrimitives</c>' own defaults are twice that in both directions, for shapes
    ///     somebody is modelling with rather than aiming at — see <see cref="HeadSegments" />.
    /// </remarks>
    static readonly MeshData Ball = MeshPrimitives.Sphere(0.5f, HeadSegments + 4, (HeadSegments + 4) / 2);

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

}
