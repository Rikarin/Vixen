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
    /// <param name="deltaY">How far it moved vertically.</param>
    public void Orbit(float deltaX, float deltaY) {
        Yaw -= deltaX * OrbitSpeed;
        Pitch = Math.Clamp(Pitch - (deltaY * OrbitSpeed), -PitchLimit, PitchLimit);
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
}
