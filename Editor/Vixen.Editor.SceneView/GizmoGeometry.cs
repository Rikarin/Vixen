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
///         Lines rather than solid cones and rings, which is what a shipping gizmo eventually wants.
///         A line renderer exists and a mesh path for the editor does not, and an editor whose
///         handles are visible now is worth more than one whose handles are beautiful later.
///     </para>
/// </remarks>
public static class GizmoGeometry {
    /// <summary>How many segments a rotation ring is drawn with.</summary>
    /// <remarks>Thirty-two, which is what the hit test samples it at — see <c>TransformGizmo</c>.</remarks>
    public const int RingSegments = 32;

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
        var scale = gizmo.WorldPerPixel(camera, height) * gizmo.HandleLength;

        // What the pointer is over, or what is being dragged — the drag wins, because a drag that
        // stopped highlighting the arm it is moving looks like it let go.
        var active = gizmo.IsDragging ? gizmo.Active : gizmo.Hovered;

        if (gizmo.Mode == GizmoMode.Rotate) {
            Rings(into, origin, basis, scale, active);
        } else {
            Arms(into, origin, basis, scale, active, gizmo.Mode);
            Planes(into, origin, basis, scale, active, gizmo, camera.Forward);
        }

        return into.Count - before;
    }

    static void Arms(
        List<LineVertex> into,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        GizmoHandle active,
        GizmoMode mode
    ) {
        for (var axis = 0; axis < 3; axis++) {
            var direction = Axis(basis, axis);
            var end = origin + (direction * scale);
            var colour = AxisColour(axis, active == GizmoHandle.AxisX + axis);

            into.Add(new(origin, colour));
            into.Add(new(end, colour));

            // A head, so the two ends of an arm are told apart: a cross for scale, an open arrow for
            // translate. Four lines each, which is what a line list can say a shape with.
            var side = Axis(basis, (axis + 1) % 3) * (scale * 0.06f);
            var up = Axis(basis, (axis + 2) % 3) * (scale * 0.06f);

            if (mode == GizmoMode.Scale) {
                into.Add(new(end - side, colour));
                into.Add(new(end + side, colour));
                into.Add(new(end - up, colour));
                into.Add(new(end + up, colour));

                continue;
            }

            var back = end - (direction * (scale * 0.12f));

            into.Add(new(end, colour));
            into.Add(new(back + side, colour));
            into.Add(new(end, colour));
            into.Add(new(back - side, colour));
            into.Add(new(end, colour));
            into.Add(new(back + up, colour));
            into.Add(new(end, colour));
            into.Add(new(back - up, colour));
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
        Vector3 forward
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

            Segment(into, a, b, colour);
            Segment(into, b, c, colour);
            Segment(into, c, d, colour);
            Segment(into, d, a, colour);
        }
    }

    static void Rings(List<LineVertex> into, Vector3 origin, Matrix4x4 basis, float scale, GizmoHandle active) {
        for (var axis = 0; axis < 3; axis++) {
            var normal = Axis(basis, axis);
            var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(reference, normal));
            var v = Vector3.Cross(normal, u);
            var colour = AxisColour(axis, active == GizmoHandle.AxisX + axis);

            var previous = origin + (u * scale);

            for (var step = 1; step <= RingSegments; step++) {
                var angle = step / (float) RingSegments * MathF.Tau;
                var point = origin + (((u * MathF.Cos(angle)) + (v * MathF.Sin(angle))) * scale);

                Segment(into, previous, point, colour);
                previous = point;
            }
        }
    }

    static void Segment(List<LineVertex> into, Vector3 from, Vector3 to, Color4 colour) {
        into.Add(new(from, colour));
        into.Add(new(to, colour));
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
