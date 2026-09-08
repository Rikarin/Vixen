// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     An orbit camera over one mesh, in the mesh's own space: the ray a pixel casts and the pixel a
///     point lands on.
/// </summary>
/// <remarks>
///     <para>
///         <b>The place to stand that
///         <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a> is about, in the half
///         that is arithmetic.</b> Everything the 3D paint path needs from a viewport is here —
///         <see cref="Ray" /> for <c>PaintProjector</c>, <see cref="Eye" /> for the footprint, and
///         <see cref="Project" /> for the rasteriser — and none of it is a scene, an entity or a
///         world transform. That is the whole reason the issue recommends the plugin's own pane over
///         the scene viewport: a <c>.vxlayers</c> names a model asset, and the mesh's own space is
///         the only space in which every one of the three is defined.
///     </para>
///     <para>
///         ⚠ <b><see cref="Ray" /> and <see cref="Project" /> are exact inverses and that is the
///         property everything above them rests on.</b> The rasteriser writes a triangle at a pixel
///         through one of them and the brush casts a ray at that pixel through the other; a pair
///         that disagreed by half a pixel would paint next to what the artist clicked, and would
///         look like a broken brush rather than like a broken camera. <c>PaintMeshViewTests</c>
///         closes that loop by aiming a ray at every pixel the rasteriser covered and comparing the
///         triangle, which no assertion about either one alone can do.
///     </para>
///     <para>
///         ⚠ <b>Every distance here is relative to the model and none of them is a constant in
///         units.</b> A texturing artist opens a 0.02-unit bolt and a 400-unit terrain in the same
///         session; a dolly step of "0.1 units", a near plane of "0.01" or a framing distance of
///         "3" is a camera that works on the model it was written against and on nothing else. The
///         only absolute numbers in this file are the pitch clamp and the field of view, both in
///         radians and therefore scale-free by construction.
///     </para>
/// </remarks>
sealed class PaintCamera {
    /// <summary>How far from straight up the pitch may get, in radians.</summary>
    /// <remarks>
    ///     ⚠ <b>A clamp and not a wrap, and it is <em>not</em> about a degenerate cross product —
    ///     which is what the first version of this remark said.</b> The usual reason to clamp a pitch
    ///     is that the right axis is <c>cross(forward, worldUp)</c>, which shortens to the sine of
    ///     the gap at the pole and is then handed to a <c>Vector3.Normalize</c> that gives up below
    ///     1e-6. <see cref="Right" /> is read straight off the yaw here, so it is unit at every
    ///     pitch including the pole and that failure does not exist. What does happen at the pole is
    ///     that <see cref="Up" /> <em>flips</em>: an orbit dragged through it reverses which way is
    ///     up and the model appears to somersault, which reads as a broken viewport. One degree short
    ///     is the margin, and in radians it is scale-free.
    /// </remarks>
    public const float PitchLimit = 1.5533431f;

    /// <summary>The vertical field of view, in radians.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant and not a setting, because nothing offers one.</b> A settable field of
    ///     view with no control behind it is a mechanism whose only caller passes the default, which
    ///     is this workstream's most-shipped defect — so the property, its clamp and its two bounds
    ///     are deliberately absent until there is a picker that moves them. Forty-five degrees is
    ///     what every modelling tool's default asset view uses, and <see cref="Frame" /> is written
    ///     in terms of it rather than around it.
    /// </remarks>
    public const float FieldOfView = 0.7853982f;

    float distance = 1f;
    float pitch;
    float yaw;
    float radius = 1f;

    /// <summary>What the camera orbits, in the mesh's own space.</summary>
    public Vector3 Target { get; set; }

    /// <summary>How far the eye is from <see cref="Target" />, in the mesh's own units.</summary>
    /// <remarks>
    ///     ⚠ <b>Floored at a fraction of the framed radius rather than at a constant.</b> A floor of
    ///     "0.01 units" is inside a bolt and a hundred times too coarse for a terrain; the fraction
    ///     is what keeps the same gesture usable at both scales, and what keeps the eye outside a
    ///     convex model — which is what makes <see cref="Project" />'s missing near-plane clip
    ///     unreachable by ordinary use.
    /// </remarks>
    public float Distance {
        get => distance;
        set => distance = Math.Clamp(Safe(value, radius), radius * 0.05f, radius * 200f);
    }

    /// <summary>How far round, in radians.</summary>
    public float Yaw {
        get => yaw;
        set => yaw = Safe(value, 0f);
    }

    /// <summary>How far up, in radians, clamped short of the poles by <see cref="PitchLimit" />.</summary>
    public float Pitch {
        get => pitch;
        set => pitch = Math.Clamp(Safe(value, 0f), -PitchLimit, PitchLimit);
    }

    /// <summary>Where the eye is, in the mesh's own space.</summary>
    public Vector3 Position => Target + (Back * distance);

    /// <summary>Which way it looks. Unit.</summary>
    public Vector3 Forward => -Back;

    /// <summary>Its right axis. Unit, horizontal, and never degenerate — see <see cref="PitchLimit" />.</summary>
    public Vector3 Right {
        get {
            var (sin, cos) = MathF.SinCos(yaw);

            // Straight off the yaw rather than a cross product with world up, which is the same
            // vector and cannot be short: the pitch clamp is what keeps the cross product safe and
            // this does not need it at all.
            return new(cos, 0f, -sin);
        }
    }

    /// <summary>Its up axis. Unit, and perpendicular to both the others.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>Where the eye sits relative to the target, as a unit direction.</summary>
    Vector3 Back {
        get {
            var (sinYaw, cosYaw) = MathF.SinCos(yaw);
            var (sinPitch, cosPitch) = MathF.SinCos(pitch);

            return new(cosPitch * sinYaw, sinPitch, cosPitch * cosYaw);
        }
    }

    /// <summary>Points the camera at a box so the whole of it is on screen.</summary>
    /// <param name="bounds">The box, in the mesh's own space.</param>
    /// <remarks>
    ///     ⚠ <b>The half-diagonal and not the tallest side, because the camera turns.</b> A framing
    ///     computed from the height puts the corners of a long model outside the pane the moment the
    ///     artist orbits forty-five degrees, which reads as a viewport that loses the model rather
    ///     than as a framing that was measured on one axis. The sphere is turn-invariant, so this is
    ///     the one number that is right from every angle.
    /// </remarks>
    public void Frame(BoundingBox bounds) {
        var size = bounds.Size;
        var half = size.Length() * 0.5f;

        // A degenerate box — one triangle edge-on, or a mesh of a single point — still has to give a
        // radius, or every rate below becomes zero and the camera cannot be moved at all.
        radius = half > 0f && float.IsFinite(half) ? half : 1f;
        Target = bounds.Center;

        // The sphere subtends the field of view exactly at radius / sin(fov/2); the margin is what
        // keeps the silhouette off the edge of the pane.
        distance = radius / MathF.Max(MathF.Sin(FieldOfView * 0.5f), 1e-3f) * 1.15f;
    }

    /// <summary>Turns the camera around its target.</summary>
    /// <param name="alongPixels">How far the pointer went across the pane, in pixels.</param>
    /// <param name="upPixels">And down it — a positive value tips the camera up.</param>
    /// <param name="paneHeight">How tall the pane is, in pixels.</param>
    /// <remarks>
    ///     A drag of the pane's own height is half a turn, so the gesture is the same on a docked
    ///     strip and on a maximised panel rather than being a rate per pixel that a small pane makes
    ///     unusable.
    /// </remarks>
    public void Orbit(float alongPixels, float upPixels, int paneHeight) {
        if (paneHeight <= 0) {
            return;
        }

        var rate = MathF.PI / paneHeight;

        Yaw = yaw - (Safe(alongPixels, 0f) * rate);
        Pitch = pitch + (Safe(upPixels, 0f) * rate);
    }

    /// <summary>Slides the target across the view plane.</summary>
    /// <param name="alongPixels">How far right, in pixels.</param>
    /// <param name="upPixels">How far up.</param>
    /// <param name="paneHeight">How tall the pane is, in pixels.</param>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="WorldPerPixel" /> rather than a rate of its own</b>, so the point
    ///     under the pointer stays under the pointer: a pan measured in a fraction of the radius
    ///     drifts away from the cursor as the artist dollies in, which is the thing that makes a
    ///     viewport feel like it is fighting back.
    /// </remarks>
    public void Pan(float alongPixels, float upPixels, int paneHeight) {
        if (paneHeight <= 0) {
            return;
        }

        var scale = WorldPerPixel(paneHeight) * distance;

        Target -= (Right * Safe(alongPixels, 0f) * scale) + (Up * Safe(upPixels, 0f) * scale);
    }

    /// <summary>Dollies in or out.</summary>
    /// <param name="ticks">Wheel notches. Positive comes closer.</param>
    /// <remarks>
    ///     ⚠ <b>Multiplicative, which is what makes a wheel notch mean the same thing at every
    ///     distance.</b> A step of "one unit" is a jump across a bolt and imperceptible on a
    ///     terrain, and the clamp on <see cref="Distance" /> is what stops the geometric series
    ///     reaching zero.
    /// </remarks>
    public void Zoom(float ticks) => Distance = distance * MathF.Pow(0.88f, Safe(ticks, 0f));

    /// <summary>The ray under a pane pixel, in the mesh's own space.</summary>
    /// <param name="x">Its column, in pane pixels, with 0 at the left edge.</param>
    /// <param name="y">Its row, with 0 at the <b>top</b> — which is the way a pointer arrives.</param>
    /// <param name="paneWidth">How wide the pane is, in pixels.</param>
    /// <param name="paneHeight">How tall.</param>
    /// <returns>The ray, with a unit direction.</returns>
    /// <remarks>
    ///     ⚠ <b>The pixel's centre and not its corner.</b> Half a pixel is invisible in a picture
    ///     and is exactly the offset that makes <see cref="Project" /> stop being this method's
    ///     inverse — so the rasteriser would write a triangle at the pixel next to the one the ray
    ///     finds, at the silhouette where the two triangles are different things.
    /// </remarks>
    public Ray Ray(float x, float y, int paneWidth, int paneHeight) {
        var scale = WorldPerPixel(paneHeight);
        var right = x + 0.5f - (paneWidth * 0.5f);
        var up = (paneHeight * 0.5f) - y - 0.5f;

        return new(Position, Forward + (Right * right * scale) + (Up * up * scale));
    }

    /// <summary>Where a point in the mesh's own space lands on the pane.</summary>
    /// <param name="point">The point.</param>
    /// <param name="paneWidth">How wide the pane is, in pixels.</param>
    /// <param name="paneHeight">How tall.</param>
    /// <param name="pixel">Its column and row, in the same frame <see cref="Ray" /> takes.</param>
    /// <param name="depth">How far in front of the eye it is, along <see cref="Forward" />.</param>
    /// <returns>Whether it is in front of the camera at all.</returns>
    /// <remarks>
    ///     ⚠ <b>There is no near-plane clip here and a triangle that straddles the eye plane is
    ///     dropped by its caller rather than cut.</b> Clipping a triangle is four cases and a
    ///     re-interpolation of every attribute, and the state it exists for — the eye inside the
    ///     surface — is one <see cref="Distance" />'s floor keeps out of reach for a closed model.
    ///     What it costs is a hole in the picture when an artist dollies into an open shell, which
    ///     is visible, recoverable by dollying back out, and cannot paint anything wrong: a pixel
    ///     with no triangle takes no stroke.
    /// </remarks>
    public bool Project(Vector3 point, int paneWidth, int paneHeight, out Vector2 pixel, out float depth) {
        pixel = Vector2.Zero;

        var offset = point - Position;

        depth = Vector3.Dot(offset, Forward);

        // Relative to the framing, for this file's whole argument: a constant near plane is either
        // inside a bolt or outside a terrain.
        if (!(depth > radius * 1e-5f)) {
            return false;
        }

        var scale = WorldPerPixel(paneHeight) * depth;
        var right = Vector3.Dot(offset, Right) / scale;
        var up = Vector3.Dot(offset, Up) / scale;

        pixel = new(right + (paneWidth * 0.5f) - 0.5f, (paneHeight * 0.5f) - 0.5f - up);

        return true;
    }

    /// <summary>The camera as <c>PaintFootprint</c> reads it.</summary>
    /// <param name="paneHeight">How tall the pane is, in pixels.</param>
    /// <returns>The eye.</returns>
    /// <remarks>
    ///     ⚠ <b>Pane pixels and not layout pixels, and the two differ by the display scale.</b> The
    ///     pane rasterises at its own resolution and every coordinate this class takes is in that
    ///     resolution, so an eye built from a layout height would report a brush half the size it is
    ///     on exactly the retina machines this is developed on — which is the mistake
    ///     <c>PaintEye.Perspective</c>'s own remarks name.
    /// </remarks>
    public PaintEye Eye(int paneHeight) =>
        PaintEye.Perspective(Position, Forward, FieldOfView, Math.Max(paneHeight, 1));

    /// <summary>How much of the view one pixel is worth, per unit of depth.</summary>
    /// <param name="paneHeight">How tall the pane is, in pixels.</param>
    /// <returns>The scale, in world units per pixel per unit of depth.</returns>
    static float WorldPerPixel(int paneHeight) => 2f * MathF.Tan(FieldOfView * 0.5f) / Math.Max(paneHeight, 1);

    /// <summary>A number, or a fallback where a caller handed over a NaN.</summary>
    /// <param name="value">The number.</param>
    /// <param name="fallback">What to answer instead when it is not finite.</param>
    /// <returns>One of the two.</returns>
    /// <remarks>
    ///     ⚠ <b>A NaN reaching any of these is permanent, which is what makes the guard worth its
    ///     line.</b> Every setter here folds its argument into a field the next frame reads, so one
    ///     bad pointer delta — a divide by a zero-sized pane, a wheel event with no position — turns
    ///     the whole camera into NaN and the pane goes black for the rest of the session with
    ///     nothing on screen to say why.
    /// </remarks>
    static float Safe(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
