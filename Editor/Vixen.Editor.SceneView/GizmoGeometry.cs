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

        if (gizmo.Mode == GizmoMode.Rotate) {
            Rings(into, camera, origin, basis, scale, active, pen);
            ScreenRing(into, camera, origin, scale * gizmo.ScreenRingScale, active, pen);
        } else {
            Arms(into, camera, gizmo, origin, basis, scale, active, pen);
            Planes(into, origin, basis, scale, active, gizmo, camera.Forward, pen);
            Centre(into, camera, origin, worldPerPixel * gizmo.CentreRadius, active, gizmo.Mode, pen);
        }

        return into.Count - before;
    }

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
        var mode = gizmo.Mode;

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

            // A head, so the two ends of an arm are told apart: a cube for scale, an arrow for
            // translate. Both are closed shapes rather than a cross, which at this thickness reads
            // as a handle to grab instead of as a smudge at the end of a line.
            var side = Axis(basis, (axis + 1) % 3) * (scale * 0.07f);
            var up = Axis(basis, (axis + 2) % 3) * (scale * 0.07f);

            if (mode == GizmoMode.Scale) {
                Cube(into, end, direction * (scale * 0.07f), side, up, colour, pen);
                continue;
            }

            var back = end - (direction * (scale * 0.16f));

            // Four ribs from the tip to a square around the shaft, and the square itself. An open
            // arrow — four ribs and nothing joining them — is four unrelated lines from every angle
            // that is not the one it was drawn for.
            Segment(into, end, back + side, colour, pen);
            Segment(into, end, back - side, colour, pen);
            Segment(into, end, back + up, colour, pen);
            Segment(into, end, back - up, colour, pen);

            Box(into, back, side, up, colour, pen);
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

            if (MathF.Abs(Vector3.Dot(Vector3.Cross(first, second), forward)) < 0.15f) {
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

    /// <summary>The box in the middle: uniform scale, or a drag in the view plane.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Drawn for exactly the modes whose hit test offers a middle handle.</b> The test
    ///         has always answered <see cref="GizmoHandle.Uniform" /> for a square in the middle of a
    ///         scale gizmo and nothing drew one, so the most-used handle of the three was invisible —
    ///         a click that scaled everything uniformly, discoverable only by accident.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Translate has one too, and it is the handle that was missing rather than merely
    ///         undrawn.</b> Dragging in the view plane is how anything gets moved that is not along an
    ///         axis, and without it the middle of a translate gizmo — where three arms cross and every
    ///         click is ambiguous — was answered by whichever arm the loop reached first. One square,
    ///         two meanings, and in both cases it means "the thing the arms cannot do".
    ///     </para>
    /// </remarks>
    static void Centre(
        List<LineVertex> into,
        EditorCamera camera,
        Vector3 origin,
        float radius,
        GizmoHandle active,
        GizmoMode mode,
        Pen pen
    ) {
        var handle = mode switch {
            GizmoMode.Scale or GizmoMode.Transform => GizmoHandle.Uniform,
            GizmoMode.Translate => GizmoHandle.Screen,
            _ => GizmoHandle.None
        };

        if (handle == GizmoHandle.None) {
            return;
        }

        // Square to the camera rather than to the gizmo's basis, because it belongs to no axis: a
        // box on the object's own axes would be one the user has to orbit to see square.
        Box(into, origin, camera.Right * radius, camera.Up * radius, NeutralColour(active == handle), pen);
    }

    /// <summary>The three rings that turn about the gizmo's own axes.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the half of each facing the camera is drawn, exactly as the hit test only offers
    ///     that half.</b> Three full circles about one point cross each other twelve times, and at
    ///     every crossing the front of one ring is drawn over the back of another — so aiming at the
    ///     green ring where the red one passes behind it is a coin toss, and the space inside the
    ///     gizmo that looks empty is criss-crossed by the far sides of all three. Hiding the back half
    ///     is also what makes a rotate gizmo read as a sphere rather than as a flat knot.
    /// </remarks>
    static void Rings(
        List<LineVertex> into,
        EditorCamera camera,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        GizmoHandle active,
        Pen pen
    ) {
        var towards = TowardsEye(camera, origin);

        for (var axis = 0; axis < 3; axis++) {
            var normal = Axis(basis, axis);
            var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(reference, normal));
            var v = Vector3.Cross(normal, u);

            Ring(into, origin, u, v, scale, AxisColour(axis, active == GizmoHandle.AxisX + axis), pen, towards);
        }
    }

    /// <summary>The ring that turns about whatever the camera is looking along.</summary>
    /// <remarks>
    ///     ⚠ <b>The other handle the hit test has always offered and nothing drew.</b>
    ///     <c>TransformGizmo.HitTest</c> answers <see cref="GizmoHandle.Screen" /> for a circle at
    ///     <c>HandleLength × ScreenRingScale</c>, and a rotate gizmo that did not draw it had a band
    ///     of pixels outside the axis rings that turned the selection for no visible reason.
    /// </remarks>
    static void ScreenRing(
        List<LineVertex> into,
        EditorCamera camera,
        Vector3 origin,
        float radius,
        GizmoHandle active,
        Pen pen
    ) =>
        Ring(into, origin, camera.Right, camera.Up, radius, NeutralColour(active == GizmoHandle.Screen), pen);

    /// <summary>A circle, or the half of one that faces a direction.</summary>
    /// <param name="into">Where to put the vertices.</param>
    /// <param name="origin">The middle.</param>
    /// <param name="u">One axis of its plane, unit length.</param>
    /// <param name="v">The other.</param>
    /// <param name="radius">How big.</param>
    /// <param name="colour">What to draw it in.</param>
    /// <param name="pen">How to draw it.</param>
    /// <param name="towards">Which way to face, or <see langword="null" /> for the whole circle.</param>
    static void Ring(
        List<LineVertex> into,
        Vector3 origin,
        Vector3 u,
        Vector3 v,
        float radius,
        Color4 colour,
        Pen pen,
        Vector3? towards = null
    ) {
        var previous = Vector3.Zero;
        var had = false;

        for (var step = 0; step <= RingSegments; step++) {
            var angle = step / (float) RingSegments * MathF.Tau;
            var offset = (u * MathF.Cos(angle)) + (v * MathF.Sin(angle));

            // ⚠ The run is broken rather than the point skipped. Joining the last point before the
            // horizon to the first one after it draws a chord straight across the gizmo, which is a
            // line that belongs to no handle and that the hit test does not know about.
            if (towards is { } eye && Vector3.Dot(offset, eye) < 0f) {
                had = false;
                continue;
            }

            var point = origin + (offset * radius);

            if (had) {
                Segment(into, previous, point, colour, pen);
            }

            previous = point;
            had = true;
        }
    }

    /// <summary>A closed wire box, given its middle and half of each of its three sides.</summary>
    /// <remarks>
    ///     ⚠ <b>A box and not a square, and the difference is the angle it is seen from.</b> The head
    ///     of the x arm is drawn in the plane of the other two axes, which is the plane the camera is
    ///     looking straight along whenever it is lined up with x — and a flat square seen edge-on is a
    ///     line, so the handle that says "this arm scales" disappears at exactly the angle somebody
    ///     lined the view up to scale along that arm from.
    /// </remarks>
    static void Cube(
        List<LineVertex> into,
        Vector3 centre,
        Vector3 along,
        Vector3 side,
        Vector3 up,
        Color4 colour,
        Pen pen
    ) {
        Box(into, centre - along, side, up, colour, pen);
        Box(into, centre + along, side, up, colour, pen);

        for (var corner = 0; corner < 4; corner++) {
            var offset = ((corner & 1) == 0 ? side : -side) + ((corner & 2) == 0 ? up : -up);

            Segment(into, centre + offset - along, centre + offset + along, colour, pen);
        }
    }

    /// <summary>A closed square, given its middle and half of each side.</summary>
    static void Box(List<LineVertex> into, Vector3 centre, Vector3 side, Vector3 up, Color4 colour, Pen pen) {
        var a = centre - side - up;
        var b = centre + side - up;
        var c = centre + side + up;
        var d = centre - side + up;

        Segment(into, a, b, colour, pen);
        Segment(into, b, c, colour, pen);
        Segment(into, c, d, colour, pen);
        Segment(into, d, a, colour, pen);
    }

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
