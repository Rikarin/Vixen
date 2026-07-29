// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>One of the six axis-aligned views a numpad key snaps to.</summary>
public enum ViewDirection {
    /// <summary>Looking along −Z.</summary>
    Front,

    /// <summary>Looking along +Z.</summary>
    Back,

    /// <summary>Looking along −X.</summary>
    Right,

    /// <summary>Looking along +X.</summary>
    Left,

    /// <summary>Looking down.</summary>
    Top,

    /// <summary>Looking up.</summary>
    Bottom
}

/// <summary>Where a viewport is looking, saved so it can be gone back to.</summary>
/// <param name="Name">What the bookmark is called.</param>
/// <param name="Pivot">What the camera orbits.</param>
/// <param name="Distance">How far from it.</param>
/// <param name="Yaw">Rotation about the world's up, in radians.</param>
/// <param name="Pitch">Rotation above the horizon, in radians.</param>
/// <param name="IsOrthographic">Whether the projection is orthographic.</param>
public readonly record struct ViewBookmark(
    string Name,
    Vector3 Pivot,
    float Distance,
    float Yaw,
    float Pitch,
    bool IsOrthographic
);

/// <summary>The camera a scene viewport looks through.</summary>
/// <remarks>
///     <para>
///         <b>Stored as a pivot, a distance and two angles, not as a matrix or a transform.</b> Every
///         navigation a scene view has is an operation on those four: orbit turns the angles, pan
///         moves the pivot, zoom changes the distance, and focus-on-selection sets the pivot and
///         solves the distance. Storing a position and a rotation instead would make orbit the only
///         hard one, and it is the one people use most.
///     </para>
///     <para>
///         ⚠ <b>Pitch is clamped just short of straight up and straight down.</b> At exactly ninety
///         degrees the forward vector is parallel to the world up and the basis is undefined; the
///         camera flips over and the horizon spins. Every scene view has this bug once, and clamping
///         is what every one of them ends up doing.
///     </para>
///     <para>
///         <b>Flying is orbiting from where you are.</b> WASD moves the pivot along the camera's own
///         basis rather than switching to a different camera model, so leaving fly mode does not
///         teleport the view and the orbit afterwards is about something in front of you. The
///         alternative — a free camera with its own state — is two cameras that disagree about where
///         the view is.
///     </para>
/// </remarks>
public sealed class EditorCamera {
    /// <summary>How near the near plane gets, whatever anyone asks for.</summary>
    public const float MinimumDistance = 0.01f;

    /// <summary>How far off vertical the pitch is held, in radians.</summary>
    /// <remarks>A thousandth of a radian: invisible, and enough to keep the basis well conditioned.</remarks>
    public const float PitchLimit = (MathF.PI * 0.5f) - 0.001f;

    float distance = 10f;

    /// <summary>What the camera orbits and what focus moves.</summary>
    public Vector3 Pivot { get; set; }

    /// <summary>How far the camera is from the pivot.</summary>
    public float Distance {
        get => distance;
        set => distance = MathF.Max(MinimumDistance, value);
    }

    /// <summary>Rotation about the world's up, in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>Rotation above the horizon, in radians. Clamped short of vertical.</summary>
    public float Pitch { get; set; }

    /// <summary>Vertical field of view, in radians.</summary>
    public float FieldOfView { get; set; } = MathUtil.DegreesToRadians(60f);

    /// <summary>Distance to the near plane.</summary>
    public float NearPlane { get; set; } = 0.05f;

    /// <summary>Distance to the far plane.</summary>
    public float FarPlane { get; set; } = 5000f;

    /// <summary>Whether the projection is orthographic.</summary>
    public bool IsOrthographic { get; set; }

    /// <summary>How fast an orbit turns, in radians per render pixel.</summary>
    public float OrbitSpeed { get; set; } = 0.006f;

    /// <summary>Whether a vertical orbit drag goes the other way.</summary>
    /// <remarks>
    ///     The "invert Y" every editor offers, and it is a preference rather than a fix: which way an
    ///     orbit's vertical axis goes is the one navigation setting people genuinely disagree about.
    ///     Off is the turntable — see <see cref="Orbit" />.
    /// </remarks>
    public bool InvertOrbitY { get; set; }

    /// <summary>How much one wheel notch changes the distance, as a fraction.</summary>
    public float ZoomSpeed { get; set; } = 0.12f;

    /// <summary>How fast WASD moves, in world units per second at a distance of one.</summary>
    public float FlySpeed { get; set; } = 4f;

    /// <summary>Where the camera is.</summary>
    public Vector3 Position => Pivot - (Forward * Distance);

    /// <summary>Which way it looks.</summary>
    public Vector3 Forward {
        get {
            var cosPitch = MathF.Cos(Pitch);

            // Yaw zero looks along −Z, which is the engine's forward (Conventions.md § Handedness).
            return new(
                -cosPitch * MathF.Sin(Yaw),
                MathF.Sin(Pitch),
                -cosPitch * MathF.Cos(Yaw)
            );
        }
    }

    /// <summary>Its own +X, in world space.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY)) is var right && right.IsZero
        ? Vector3.UnitX
        : right;

    /// <summary>Its own +Y, in world space.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    /// <summary>The camera's orientation, which is what the corner gizmo shows.</summary>
    public Matrix4x4 Rotation {
        get {
            var right = Right;
            var up = Up;
            var backward = -Forward;

            return new(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                backward.X, backward.Y, backward.Z, 0f,
                0f, 0f, 0f, 1f
            );
        }
    }

    /// <summary>The world-to-view matrix.</summary>
    public Matrix4x4 View => Matrix4x4.LookAt(Position, Pivot, Vector3.UnitY);

    /// <summary>
    ///     How tall the orthographic view is, derived from the distance so that switching projection
    ///     does not change how big anything looks.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than stored.</b> An orthographic height that was its own field would
    ///     mean pressing the projection key rescaled the picture, and zooming in perspective and then
    ///     switching would land somewhere unrelated to what was on screen.
    /// </remarks>
    public float OrthographicHeight => 2f * Distance * MathF.Tan(FieldOfView * 0.5f);

    /// <summary>The view-to-clip matrix.</summary>
    /// <param name="aspectRatio">Width over height.</param>
    /// <returns>The projection, reverse-Z in both modes as the rest of the engine is.</returns>
    public Matrix4x4 Projection(float aspectRatio) {
        var aspect = aspectRatio > 0f ? aspectRatio : 1f;

        return IsOrthographic
            ? Matrix4x4.Orthographic(OrthographicHeight * aspect, OrthographicHeight, NearPlane, FarPlane)
            : Matrix4x4.PerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane);
    }

    /// <summary>The world-to-clip matrix.</summary>
    /// <param name="aspectRatio">Width over height.</param>
    /// <returns>View times projection, in that order.</returns>
    public Matrix4x4 ViewProjection(float aspectRatio) => View * Projection(aspectRatio);

    /// <summary>What the camera can see, for culling.</summary>
    /// <param name="aspectRatio">Width over height.</param>
    /// <returns>The frustum in world space.</returns>
    public BoundingFrustum Frustum(float aspectRatio) => new(ViewProjection(aspectRatio));

    /// <summary>Turns the camera about its pivot.</summary>
    /// <param name="deltaX">How far the pointer moved horizontally, in render pixels.</param>
    /// <param name="deltaY">How far it moved vertically, positive downwards.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The scene follows the pointer on both axes.</b> Dragging right swings what you are
    ///         looking at to the right and dragging up tips its top towards you, which is the
    ///         turntable Blender, Maya and 3ds Max all present: the gesture is "grab the thing and
    ///         turn it", and it is one gesture rather than two rules.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the horizontal axis used to do that.</b> The vertical carried the
    ///         <i>camera</i> instead — drag up, camera climbs — so the two halves of one drag
    ///         disagreed about what was being held, and a diagonal drag rotated the scene one way and
    ///         the eye the other. It survived review because the scene was also being presented
    ///         mirrored (see <c>Viewport.FlipVertically</c>), and the two wrongs cancelled on screen.
    ///     </para>
    ///     <para>
    ///         Unity and Unreal orbit the other way on <i>both</i> axes, and people who come from them
    ///         want the whole gesture reversed rather than half of it. That is
    ///         <see cref="InvertOrbitY" /> together with a negated <paramref name="deltaX" />, not a
    ///         different rule here.
    ///     </para>
    /// </remarks>
    public void Orbit(float deltaX, float deltaY) =>
        Turn(-deltaX * OrbitSpeed, (InvertOrbitY ? deltaY : -deltaY) * OrbitSpeed);

    /// <summary>Turns the camera about its pivot by an angle rather than by a drag.</summary>
    /// <param name="yaw">How far to turn about the world's up, in radians.</param>
    /// <param name="pitch">How far to raise the eye, in radians. Clamped short of vertical.</param>
    /// <remarks>
    ///     What the numpad's four orbit keys press, and what <see cref="Orbit" /> is in terms of once
    ///     the pixels have become angles. Separate because a keyboard orbit that had to invent a
    ///     pointer delta would go the wrong way the moment <see cref="InvertOrbitY" /> was set.
    /// </remarks>
    public void Turn(float yaw, float pitch) {
        Yaw += yaw;
        Pitch = Math.Clamp(Pitch + pitch, -PitchLimit, PitchLimit);
    }

    /// <summary>Turns the camera about a point that is not its pivot.</summary>
    /// <param name="anchor">What to swing around, in world space.</param>
    /// <param name="deltaX">How far the pointer moved horizontally, in render pixels.</param>
    /// <param name="deltaY">How far it moved vertically, positive downwards.</param>
    /// <remarks>
    ///     <para>
    ///         <b>What "orbit around selection" is.</b> The camera's four numbers can only orbit the
    ///         pivot, so orbiting something else means turning the whole rig — eye and pivot together
    ///         — about the anchor, and that is what this does: the pivot's offset from the anchor is
    ///         read in the camera's own basis before the turn and rebuilt in the basis after it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured against the basis the turn actually produced, not the one it asked
    ///         for.</b> <see cref="Turn" /> clamps the pitch, so at the poles the rotation applied is
    ///         smaller than the rotation requested — and rebuilding the offset from the requested one
    ///         would slide the pivot a little further every frame somebody held the drag at the top of
    ///         its travel. That is a view that drifts only when it is already at its limit, which is
    ///         the hardest kind of drift to be shown.
    ///     </para>
    /// </remarks>
    public void OrbitAround(Vector3 anchor, float deltaX, float deltaY) {
        var (right, up, forward) = (Right, Up, Forward);
        var offset = Pivot - anchor;

        var local = new Vector3(
            Vector3.Dot(offset, right),
            Vector3.Dot(offset, up),
            Vector3.Dot(offset, forward)
        );

        Orbit(deltaX, deltaY);

        Pivot = anchor + (Right * local.X) + (Up * local.Y) + (Forward * local.Z);
    }

    /// <summary>Slides the view sideways and up, keeping the direction.</summary>
    /// <param name="deltaX">How far the pointer moved horizontally, in render pixels.</param>
    /// <param name="deltaY">How far it moved vertically.</param>
    /// <param name="viewportHeight">How tall the viewport is, in render pixels.</param>
    /// <remarks>
    ///     ⚠ <b>Scaled by the distance</b>, so that a pan drags whatever is under the pointer by
    ///     roughly the distance the pointer moved. A pan in fixed world units feels glacial when
    ///     zoomed out and uncontrollable when zoomed in, which is the same complaint from both ends.
    /// </remarks>
    public void Pan(float deltaX, float deltaY, float viewportHeight) {
        if (viewportHeight <= 0f) {
            return;
        }

        var worldPerPixel = OrthographicHeight / viewportHeight;
        Pivot += (Right * (-deltaX * worldPerPixel)) + (Up * (deltaY * worldPerPixel));
    }

    /// <summary>Moves the camera towards or away from its pivot.</summary>
    /// <param name="notches">Wheel notches. Positive comes closer.</param>
    /// <remarks>
    ///     ⚠ <b>Multiplicative, not additive.</b> A fixed step per notch takes forty notches to get
    ///     near something across a level and then punches straight through it; a fraction of the
    ///     current distance takes the same number of notches to halve the distance wherever you are.
    /// </remarks>
    public void Zoom(float notches) => Distance *= MathF.Pow(1f - ZoomSpeed, notches);

    /// <summary>Moves the camera towards or away from a point, keeping that point still on screen.</summary>
    /// <param name="target">What to zoom at, in world space. Taken to be at the pivot's depth.</param>
    /// <param name="notches">Wheel notches. Positive comes closer.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Blender's "zoom to mouse position", and the reason it is worth having.</b> A zoom
    ///         about the middle of the pane means approaching anything off-centre is zoom, pan, zoom,
    ///         pan — and the thing being approached leaves the view between every pair of them. Zooming
    ///         at the pointer is one gesture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pivot is moved by the factor the distance actually changed by</b>, which is
    ///         not the factor asked for once <see cref="MinimumDistance" /> has clamped it. Using the
    ///         requested one would keep hauling the pivot towards the target after the camera had
    ///         stopped being able to approach it, so a wheel held down at the floor of the zoom would
    ///         slide the view sideways for ever.
    ///     </para>
    /// </remarks>
    public void ZoomTowards(Vector3 target, float notches) {
        var before = Distance;

        Zoom(notches);

        // Similar triangles: the whole rig scales about the target, so a point at the pivot's depth
        // keeps the screen position it had.
        Pivot = target + ((Pivot - target) * (Distance / before));
    }

    /// <summary>Where a ray crosses the plane through the pivot that faces the camera.</summary>
    /// <param name="ray">The ray, usually the one under the pointer.</param>
    /// <returns>The point, or the pivot when the ray runs parallel to the plane.</returns>
    /// <remarks>
    ///     The depth a scene view assumes when it has not been told one: it is where the grid is when
    ///     the grid is what you are looking at, and it is what <see cref="ZoomTowards" /> and a pan
    ///     both measure against. An editor with a depth buffer to sample would use that instead —
    ///     Blender calls it auto-depth — and this is the fallback that needs no device.
    /// </remarks>
    public Vector3 OnPivotPlane(Ray ray) {
        var normal = Forward;
        var denominator = Vector3.Dot(ray.Direction, normal);

        if (MathF.Abs(denominator) < MathUtil.ZeroTolerance) {
            return Pivot;
        }

        return ray.Origin + (ray.Direction * (Vector3.Dot(Pivot - ray.Origin, normal) / denominator));
    }

    /// <summary>Moves the view along its own basis, for WASDQE flight.</summary>
    /// <param name="right">How far right, in units of speed.</param>
    /// <param name="up">How far up.</param>
    /// <param name="forward">How far forward.</param>
    /// <param name="seconds">How long the frame was.</param>
    /// <param name="fast">Whether the shift key is held.</param>
    /// <remarks>
    ///     Scaled by the distance for the reason <see cref="Pan" /> is: flying across a terrain and
    ///     flying around a bolt are the same keys and want speeds three orders of magnitude apart.
    /// </remarks>
    public void Fly(float right, float up, float forward, float seconds, bool fast = false) {
        var speed = FlySpeed * Distance * seconds * (fast ? 4f : 1f);
        Pivot += ((Right * right) + (Vector3.UnitY * up) + (Forward * forward)) * speed;
    }

    /// <summary>Points the camera at something, from where it already is.</summary>
    /// <param name="bounds">What to look at.</param>
    /// <param name="margin">How much room to leave around it, as a fraction of its size.</param>
    /// <remarks>
    ///     ⚠ <b>The direction is kept and only the pivot and distance move.</b> Focus that also
    ///     reset the angle is the one people undo by hand every time: you lined the view up and then
    ///     asked to see something in it.
    /// </remarks>
    public void Focus(BoundingBox bounds, float margin = 1.4f) {
        Pivot = (bounds.Minimum + bounds.Maximum) * 0.5f;

        var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;
        var radius = MathF.Max(extent.Length(), MinimumDistance);

        // The distance at which a sphere of that radius fills the vertical field of view, widened by
        // the margin. A zero-size selection — a light, an empty — still gets a sensible distance
        // because the radius is floored.
        Distance = radius * margin / MathF.Tan(FieldOfView * 0.5f);
    }

    /// <summary>Looks along an axis, keeping the pivot.</summary>
    /// <param name="direction">Which way.</param>
    /// <remarks>
    ///     The numpad views. Orthographic is <i>not</i> forced: an axis view in perspective is a
    ///     legitimate thing to want, and a key that changed two things at once would be one people
    ///     stop pressing.
    /// </remarks>
    public void LookFrom(ViewDirection direction) {
        (Yaw, Pitch) = direction switch {
            ViewDirection.Front => (0f, 0f),
            ViewDirection.Back => (MathF.PI, 0f),
            ViewDirection.Right => (MathF.PI * 0.5f, 0f),
            ViewDirection.Left => (-MathF.PI * 0.5f, 0f),
            ViewDirection.Top => (0f, -PitchLimit),
            _ => (0f, PitchLimit)
        };
    }

    /// <summary>Records where the camera is.</summary>
    /// <param name="name">What the bookmark is called.</param>
    /// <returns>The bookmark.</returns>
    public ViewBookmark Bookmark(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new(name, Pivot, Distance, Yaw, Pitch, IsOrthographic);
    }

    /// <summary>Goes back to a bookmark.</summary>
    /// <param name="bookmark">The bookmark.</param>
    public void Restore(ViewBookmark bookmark) {
        Pivot = bookmark.Pivot;
        Distance = bookmark.Distance;
        Yaw = bookmark.Yaw;
        Pitch = Math.Clamp(bookmark.Pitch, -PitchLimit, PitchLimit);
        IsOrthographic = bookmark.IsOrthographic;
    }

    /// <summary>The ray under a point in the viewport.</summary>
    /// <param name="point">Where, in render pixels from the viewport's top-left.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The ray, in world space.</returns>
    public Ray PickingRay(Vector2 point, int width, int height) {
        var viewport = new Viewport(0f, 0f, width, height);

        return viewport.GetPickingRay(point, ViewProjection(viewport.AspectRatio));
    }

    /// <summary>Where a world point lands in the viewport.</summary>
    /// <param name="world">The point.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>Its x and y in render pixels, and its depth in z.</returns>
    public Vector3 Project(Vector3 world, int width, int height) {
        var viewport = new Viewport(0f, 0f, width, height);

        return viewport.Project(world, ViewProjection(viewport.AspectRatio));
    }

    /// <summary>Where a world point lands in the viewport, if it lands in it at all.</summary>
    /// <param name="world">The point.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="point">Its x and y in render pixels.</param>
    /// <returns>Whether the point is in front of the eye and has a screen position.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A perspective projection has an answer for points behind the camera and the
    ///         answer is a lie.</b> The divide by <c>w</c> is a divide by a negative number back
    ///         there, so a point behind the eye comes back mirrored through the middle of the pane —
    ///         a real pixel position, on the wrong side, moving the wrong way. Nothing downstream can
    ///         tell that from a real one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is how a gizmo answers clicks in empty space.</b>
    ///         <c>TransformGizmo.HitTest</c> measures the pointer's distance to the projected arms; an
    ///         arm whose far end folded round to the other side of the pane is a segment lying across
    ///         the viewport that nothing drew, and the pointer is regularly within its grab radius.
    ///         The selection then leaps when the drag starts, because the ray and the projection
    ///         disagree about where the handle was. This is the check that makes that case answer
    ///         "no handle" instead.
    ///     </para>
    ///     <para>
    ///         Orthographic needs none of it: the projection is affine, there is no divide, and a
    ///         point behind the camera projects exactly where the maths says — which is why the test
    ///         is on the perspective path only rather than being a general clip.
    ///     </para>
    /// </remarks>
    public bool TryProject(Vector3 world, int width, int height, out Vector2 point) {
        point = default;

        if (width <= 0 || height <= 0) {
            return false;
        }

        if (!IsOrthographic && Vector3.Dot(world - Position, Forward) <= NearPlane) {
            return false;
        }

        var projected = Project(world, width, height);

        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y)) {
            return false;
        }

        point = new(projected.X, projected.Y);
        return true;
    }
}
