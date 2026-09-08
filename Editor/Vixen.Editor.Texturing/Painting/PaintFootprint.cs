// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>The camera, reduced to the one thing a brush asks of it: how big a pixel is out there.</summary>
/// <remarks>
///     <para>
///         <b>⚠ Not an <c>EditorCamera</c> and not a <c>RenderView</c>, and the narrowness is the
///         point.</b> Either of those would put a <c>Vixen.Editor.SceneView</c> or a
///         <c>Vixen.Rendering</c> view type into the signature of every conversion here, and the
///         thing being asked is one scalar function of depth. A viewport fills this in two lines
///         from whatever it holds, a test fills it from three numbers, and the arithmetic below
///         cannot accidentally start depending on a projection matrix.
///     </para>
///     <para>
///         ⚠ <b>Both projections, as one affine answer.</b> An orthographic camera's pixel is the
///         same size everywhere and a perspective camera's grows linearly with depth, so
///         <see cref="Constant" /> plus <see cref="PerUnit" /> covers both exactly rather than
///         approximately — and a viewport that switched between them mid-drag would still be
///         answering with one type. The scene viewport has both modes, which is why this is not
///         hypothetical generality.
///     </para>
/// </remarks>
readonly record struct PaintEye {
    /// <summary>Where the camera is, in the mesh's own space.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Which way it looks, unit, in the mesh's own space.</summary>
    public Vector3 Forward { get; init; }

    /// <summary>World units per render pixel at zero depth — an orthographic camera's whole answer.</summary>
    public float Constant { get; init; }

    /// <summary>How much that grows per unit of depth — a perspective camera's whole answer.</summary>
    public float PerUnit { get; init; }

    /// <summary>A perspective camera.</summary>
    /// <param name="position">Where it is, in the mesh's own space.</param>
    /// <param name="forward">Which way it looks. Normalised here.</param>
    /// <param name="verticalFieldOfView">Its vertical field of view, in <b>radians</b>.</param>
    /// <param name="pixelHeight">How tall the pane is, in render pixels.</param>
    /// <returns>The eye.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The field of view or the height is not positive.</exception>
    /// <remarks>
    ///     ⚠ <b>Render pixels and not layout pixels.</b> The brush radius an artist sets is a size on
    ///     the screen, and on a retina display the pane is twice as many render pixels as it is
    ///     layout ones — so a conversion fed the wrong one is out by exactly the scale factor, which
    ///     is a brush that is half the size it should be on precisely the machines this is developed
    ///     on. The same mistake sized a probe import wrongly in this repository once already.
    /// </remarks>
    public static PaintEye Perspective(Vector3 position, Vector3 forward, float verticalFieldOfView, int pixelHeight) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(verticalFieldOfView);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(verticalFieldOfView, MathF.PI);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        return new() {
            Position = position,
            Forward = Vector3.Normalize(forward),
            Constant = 0f,
            PerUnit = 2f * MathF.Tan(verticalFieldOfView * 0.5f) / pixelHeight
        };
    }

    /// <summary>An orthographic camera.</summary>
    /// <param name="position">Where it is, in the mesh's own space.</param>
    /// <param name="forward">Which way it looks. Normalised here.</param>
    /// <param name="verticalExtent">How many world units tall the view is.</param>
    /// <param name="pixelHeight">How tall the pane is, in render pixels.</param>
    /// <returns>The eye.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent or the height is not positive.</exception>
    public static PaintEye Orthographic(Vector3 position, Vector3 forward, float verticalExtent, int pixelHeight) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(verticalExtent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        return new() {
            Position = position,
            Forward = Vector3.Normalize(forward),
            Constant = verticalExtent / pixelHeight,
            PerUnit = 0f
        };
    }

    /// <summary>How many world units one render pixel covers at a point.</summary>
    /// <param name="point">The point, in the mesh's own space.</param>
    /// <returns>World units per pixel, never negative.</returns>
    /// <remarks>
    ///     ⚠ <b>The depth along <see cref="Forward" /> and not the distance to the eye.</b> They are
    ///     the same only in the middle of the pane; at the corner of a 60° view they differ by about
    ///     a sixth, which is a brush that grows as the artist paints towards the edge of the
    ///     viewport. A point behind the camera reads zero rather than a negative size, which is what
    ///     keeps a radius a radius when a ray is cast from inside the mesh.
    /// </remarks>
    public float WorldPerPixel(Vector3 point) =>
        Constant + (PerUnit * MathF.Max(Vector3.Dot(point - Position, Forward), 0f));
}

/// <summary>What a screen-space brush covers in the atlas: an ellipse, in texels.</summary>
/// <param name="Radius">Its equal-area radius — the number that goes on <c>PaintBrush.Radius</c>.</param>
/// <param name="Aspect">
///     How much longer the long axis is than the short one, one or more. One for a chart that is not
///     stretched, which is every case the 2D view has.
/// </param>
/// <param name="Angle">Which way the long axis points in the atlas, in radians.</param>
/// <remarks>
///     ⚠ <b>Three numbers and not one, which is
///     <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>.</b> A circular brush on the
///     screen covers an ellipse in the atlas wherever the layout is not isometric, and collapsing it
///     to its equal-area radius paints a chart stretched four to one with a brush twice too wide in
///     one direction and twice too narrow in the other. ⚠ On a cube that defect is <em>invisible</em>
///     — every triangle of a box unwrap is conformal — which is how it shipped.
/// </remarks>
readonly record struct PaintFootprintShape(float Radius, float Aspect, float Angle) {
    /// <summary>Nothing measurable to paint with: an unmeasurable triangle, or no brush.</summary>
    public static PaintFootprintShape None { get; } = new(0f, 1f, 0f);

    /// <summary>Whether there is a brush here at all.</summary>
    public bool IsMeasurable => Radius > 0f && float.IsFinite(Radius);
}

/// <summary>The other half of a stroke on a model: how wide the brush is, in texels, where it landed.</summary>
/// <remarks>
///     <para>
///         <b>⚠ Two multiplies and not one, and issue
///         <a href="https://github.com/Rikarin/Vixen/issues/574">#574</a> says so in as many
///         words.</b> A brush radius on a model is in <em>screen</em> pixels; a
///         <c>PaintBrush.Radius</c> is in texels of the atlas. Between them are two conversions that
///         nothing in this repository had:
///     </para>
///     <list type="number">
///         <item>
///             <b>Screen to surface</b>, which is the camera's projection at the hit's depth — and
///             then the grazing stretch, because a disc on the screen lands on a surface tilted away
///             from the viewer as an ellipse whose long axis is <c>1 / cos θ</c> times its short one.
///         </item>
///         <item>
///             <b>Surface to atlas</b>, which is <see cref="PaintDensity" /> — the hit triangle's
///             own Jacobian, not the chart's average and not a constant.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The grazing half is the one that would have been left out.</b> Doc 48's own
///         prediction named the density and stopped there, and a conversion without the cosine is
///         exactly right when the artist paints face-on — which is how anybody testing it by hand
///         would hold the model. It goes wrong at the silhouette, where every stroke an artist makes
///         to reach round the far side of a shape is.
///     </para>
///     <para>
///         ⚠ <b>And the cosine is floored, because at the silhouette itself it is zero.</b> The
///         ellipse there is infinitely long: a screen disc on a surface edge-on to the camera covers
///         an unbounded strip of it, and the honest answer — an unbounded brush — is not one an
///         artist can use. <see cref="GrazingFloor" /> caps the stretch, which makes the brush
///         smaller than the truth exactly where the truth is that the artist cannot see what they
///         are painting.
///     </para>
/// </remarks>
static class PaintFootprint {
    /// <summary>How far past face-on the grazing stretch is allowed to run before it is capped.</summary>
    /// <remarks>
    ///     A tenth is about 84° off the normal. Past that the surface is under six pixels wide per
    ///     ten it occupies, and the stroke is being placed by guesswork whatever radius it gets.
    /// </remarks>
    public const float GrazingFloor = 0.1f;

    /// <summary>The brush's radius in texels, where the ray landed.</summary>
    /// <param name="eye">The camera the ray came from, which is what sizes a pixel.</param>
    /// <param name="ray">The ray itself, which is what the surface is tilted with respect to.</param>
    /// <param name="hit">Where it landed.</param>
    /// <param name="density">The hit triangle's texel density.</param>
    /// <param name="screenRadius">How wide the brush is, in render pixels.</param>
    /// <returns>The radius in texels, or zero when there is nothing measurable to convert.</returns>
    /// <remarks>
    ///     ⚠ <b>Zero for an unmeasurable triangle rather than a fallback radius.</b> A triangle with
    ///     no area in the atlas cannot be painted at any radius, and a caller handed a plausible
    ///     number would lay a stamp somewhere the pointer was not. <c>PaintTool.MinimumRadius</c> is
    ///     the floor a *tool* applies to a number an artist typed; this is a measurement, and its
    ///     honest answer for an unmeasurable place is that there is none.
    /// </remarks>
    public static float Radius(PaintEye eye, Ray ray, PaintHit hit, PaintDensity density, float screenRadius) =>
        Ellipse(eye, ray, hit, density, screenRadius).Radius;

    /// <summary>The whole footprint: the equal-area radius, and the shape it is really that area of.</summary>
    /// <param name="eye">The camera the ray came from, which is what sizes a pixel.</param>
    /// <param name="ray">The ray itself, which is what the surface is tilted with respect to.</param>
    /// <param name="hit">Where it landed.</param>
    /// <param name="density">The hit triangle's texel density, and the direction it stretches.</param>
    /// <param name="screenRadius">How wide the brush is, in render pixels.</param>
    /// <returns>The ellipse, or <see cref="PaintFootprintShape.None" /> when there is nothing to convert.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <em>layout's</em> anisotropy, and not the tilt's.</b> Two things turn the
    ///         artist's disc into an ellipse and only one of them is here. This one — the hit
    ///         triangle's Jacobian — is a property of the atlas and is what
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a> is about. The other
    ///         is the grazing tilt, which stretches the disc <em>on the surface</em> along the view
    ///         direction's projection into the tangent plane before the layout ever sees it, and it
    ///         is still collapsed to the area factor below.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That second collapse is of the same order as the first and it is stated rather
    ///         than implied.</b> At 60° off the normal the tilt ellipse is 2:1, which is the same
    ///         factor a 4:1 chart contributes; composing the two exactly means carrying the
    ///         triangle's plane basis out of <c>PaintProjection.Density</c> so the two 2×2 maps can
    ///         be multiplied before the singular values are taken, and that is a change to what
    ///         <see cref="PaintDensity" /> is rather than an extra multiply here. It is filed.
    ///     </para>
    /// </remarks>
    public static PaintFootprintShape Ellipse(
        PaintEye eye,
        Ray ray,
        PaintHit hit,
        PaintDensity density,
        float screenRadius
    ) {
        if (!hit.Found || !density.IsMeasurable || !(screenRadius > 0f) || !float.IsFinite(screenRadius)) {
            return PaintFootprintShape.None;
        }

        var perPixel = eye.WorldPerPixel(hit.Point);

        if (!(perPixel > 0f)) {
            return PaintFootprintShape.None;
        }

        // Step one: the disc on the screen, as a disc on the plane facing the camera.
        var surface = screenRadius * perPixel;

        // …and the tilt, which turns that disc into an ellipse on the surface with axes r and
        // r / cos θ. Its area-equivalent radius is r / √cos θ, which is the same compromise
        // `PaintDensity.Area` makes one step later and is made the same way for the same reason.
        //
        // ⚠ The angle is to the ray that struck, and the ray is a parameter rather than the line
        // from the eye to the hit. They are the same under a perspective camera and they are *not*
        // under an orthographic one, where every ray is parallel to the forward axis however far
        // from the centre of the pane the pointer is — so deriving the direction here would have
        // made an orthographic brush grow towards the corners of the viewport, by a term that has
        // no counterpart in what the artist can see.
        var along = ray.Direction.Length();
        var cosine = along > 0f
            ? MathF.Max(MathF.Abs(Vector3.Dot(ray.Direction / along, hit.Normal)), GrazingFloor)
            : 1f;

        surface /= MathF.Sqrt(cosine);

        // Step two: the surface, in texels of this atlas, on this triangle.
        return new(surface * density.Area, density.Anisotropy, density.Orientation);
    }
}
