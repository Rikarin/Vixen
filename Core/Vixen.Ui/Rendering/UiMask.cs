// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>The per-pixel coverage a composited group's <c>mask-image</c> multiplies its surface by.</summary>
/// <param name="Centre">The mask box's centre, in document pixels.</param>
/// <param name="Half">Half the mask box's size, in document pixels.</param>
/// <param name="Axis">
///     The gradient's direction. Its length is not read — <see cref="Coverage" /> normalises it — but
///     its <i>angle</i> is, and for <see cref="GradientShape.Conic" /> it is the <c>from &lt;angle&gt;</c>.
/// </param>
/// <param name="Alphas">The three stops' coverages: <c>X</c> the from, <c>Y</c> the via, <c>Z</c> the to.</param>
/// <param name="Stops">Where those three sit along the gradient line.</param>
/// <param name="Shape">Which gradient function draws the ramp.</param>
/// <param name="Via">Whether the middle stop is read at all.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Coverage and not colour, and the difference is not a simplification — it is what
///         <c>mask-image</c> means.</b> CSS masks an element by the <i>alpha</i> of the mask image
///         (<c>mask-mode: alpha</c>, which is what <c>match-source</c> resolves to for every image
///         that is not an SVG <c>&lt;mask&gt;</c>). So the three colours of
///         <c>linear-gradient(to right, black, transparent)</c> never reach this type: only their
///         alphas do, as <see cref="Alphas" />.
///     </para>
///     <para>
///         ⚠ <b>Which is also why there is no <see cref="GradientSpace" /> here, and its absence is
///         load-bearing rather than owed.</b> A background gradient carries the space its stops are
///         interpolated in because Oklab, sRGB and linear RGB disagree about what is halfway between
///         two <i>colours</i>. They do not disagree about what is halfway between two alphas: every
///         branch of the renderer's <c>MixStops</c> lerps the alpha channel plainly, in every space.
///         A mask that stored a space would be storing a field nothing could read differently.
///     </para>
///     <para>
///         ⚠ <b>The box is carried, rather than being taken from <see cref="UiLayer.Bounds" />, and a
///         blur is the reason.</b> A group's bounds are its <i>ink</i>, already outset by the blur's
///         kernel radius and already narrowed by the entry clip. CSS resolves <c>mask-image</c>
///         against the element's border box, which moves for neither. Reading the layer's bounds would
///         make <c>blur-sm mask-linear-to-r</c> draw a different ramp from <c>mask-linear-to-r</c>
///         alone — a gradient that slides when you soften it.
///     </para>
///     <para>
///         ⚠ <b>Document pixels, and not the group's own UVs, because both executors have to arrive at
///         one number.</b> Every layer surface is the size of the viewport (see <see cref="UiLayer" />),
///         so a composite quad's texture coordinate times the surface size <i>is</i> the document
///         pixel — on the device and in <c>SoftwareUiRasterizer</c> alike. That leaves neither path an
///         origin to subtract and so neither can subtract it differently, which is the same argument
///         the viewport-sized surface was chosen for.
///     </para>
///     <para>
///         ⚠ <b>This does not commute with the Gaussian, and that is the one thing about it that is
///         genuinely unlike <see cref="UiColorMatrix" />.</b> A colour matrix is the <i>same</i>
///         affine map at every pixel, so it passes through a weighted sum — <c>M(Σ wᵢsᵢ) = Σ wᵢM(sᵢ)</c>
///         — which is what lets the two executors apply it in two different places and still agree.
///         A mask is a scalar that <i>varies</i> with position, so <c>m(p)·Σ wᵢsᵢ ≠ Σ wᵢ·m(pᵢ)·sᵢ</c>
///         whenever <c>m</c> is not constant over the kernel, and it does not commute with the
///         bilinear sampler for the same reason. The consequence is a rule and not a caveat: <b>both
///         executors apply the mask at the composite draw</b>, after the blur and after the matrix,
///         reading the same texture coordinate. Neither may fold it into the surface. See
///         <c>UiRenderer.SubmitDraw</c> and <c>SoftwareUiRasterizer.Frame.Execute</c>, which is why
///         the software renderer grew a mask lookup beside its surface lookup rather than a
///         <c>Masked</c> pass beside its <c>Filtered</c> one.
///     </para>
///     <para>
///         ⚠ <b>Applied to premultiplied colour, which means all four channels and not just the
///         alpha.</b> A layer surface holds premultiplied colour, so scaling coverage by <c>m</c> is
///         <c>(rgb·m, a·m)</c> — the whole vector. Masking a <i>straight</i>-alpha sample would be
///         <c>(rgb, a·m)</c>, and reaching for that form here is the premultiply mistake
///         <c>ui-image.frag</c>'s <c>varying_shape.x</c> exists to prevent, wearing a new hat.
///     </para>
/// </remarks>
public readonly record struct UiMask(
    Vector2 Centre,
    Vector2 Half,
    Vector2 Axis,
    Vector3 Alphas,
    GradientStops Stops,
    GradientShape Shape,
    bool Via
) {
    /// <summary>A mask that hides everything, which is what an unresolvable <c>mask-image</c> is not.</summary>
    /// <remarks>
    ///     ⚠ Present so that the <i>refusal</i> path has somewhere to point and deliberately not used
    ///     by it. CSS's answer to a <c>mask-image</c> it cannot fetch is to mask nothing — an
    ///     unloadable mask must not black out the element — so <c>DrawListBuilder</c> drops the
    ///     declaration and leaves <see cref="DrawCommand.Mask" /> null. This is here for tests that
    ///     need the degenerate case by name.
    /// </remarks>
    public static UiMask Hidden => new(
        Vector2.Zero,
        Vector2.One,
        Vector2.UnitX,
        Vector3.Zero,
        GradientStops.Default,
        GradientShape.Linear,
        Via: false
    );

    /// <summary>Whether this mask leaves every pixel of the box exactly as it found it.</summary>
    /// <remarks>
    ///     ⚠ <b>Exact, and the comparison is against one everywhere rather than against a tolerance.</b>
    ///     The same choice <see cref="UiColorMatrix.IsIdentity" /> makes and for the same reason: a
    ///     mask that is <i>nearly</i> opaque is a mask somebody wrote on purpose, and rounding it away
    ///     would make the group it opened disappear along with it. A fully opaque ramp is worth
    ///     detecting only because <c>mask-linear-from-100%</c> is a real thing to write while tuning
    ///     one, and it should cost nothing while it says nothing.
    /// </remarks>
    public bool IsOpaque =>
        Alphas.X >= 1f && Alphas.Z >= 1f && (!Via || Alphas.Y >= 1f);

    /// <summary>The coverage at a point, in document pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A transcription of <c>ui-mask.frag</c>'s <c>mask_coverage</c>, and the two are kept in
    ///     step by <c>UiCompositingTests</c> alone.</b> The parameterisation is
    ///     <c>SoftwareUiRasterizer.Progress</c>'s, deliberately and to the constant: a
    ///     <c>mask-image</c> and a <c>background-image</c> written with the same gradient have to
    ///     produce ramps that line up, and the only way to be sure of that is to compute the same
    ///     number the same way rather than a number that ought to agree.
    /// </remarks>
    public float Coverage(Vector2 point) {
        var offset = point - Centre;
        var progress = Progress(offset);

        return Via
            ? progress < Stops.Via
                ? Lerp(Alphas.X, Alphas.Y, Span(progress, Stops.From, Stops.Via))
                : Lerp(Alphas.Y, Alphas.Z, Span(progress, Stops.Via, Stops.To))
            : Lerp(Alphas.X, Alphas.Z, Span(progress, Stops.From, Stops.To));
    }

    /// <summary>Where a point sits along the gradient line, from zero at the start to one at the end.</summary>
    float Progress(Vector2 offset) {
        if (Shape == GradientShape.Radial) {
            // `ellipse farthest-corner at center`, exactly as the box shader reads it: the point over
            // the half size puts the farthest *side* at one and the corner at root two, so the
            // reciprocal of root two is the whole of `farthest-corner`.
            var normalised = new Vector2(
                offset.X / MathF.Max(Half.X, 1e-4f),
                offset.Y / MathF.Max(Half.Y, 1e-4f)
            );

            return normalised.Length() * 0.70710678f;
        }

        if (Shape == GradientShape.Conic) {
            // CSS starts at twelve o'clock and sweeps clockwise; screen space is y-down, so up is -y
            // and `Atan2(x, -y)` is already CSS's angle. The axis's own angle is the `from <angle>`.
            var angle = MathF.Atan2(offset.X, -offset.Y) - MathF.Atan2(Axis.X, -Axis.Y);
            var turns = (angle / MathF.Tau) + 1f;

            return turns - MathF.Floor(turns);
        }

        var axis = Axis.LengthSquared() > 1e-12f ? Vector2.Normalize(Axis) : Vector2.UnitX;
        var reach = MathF.Abs(axis.X * Half.X) + MathF.Abs(axis.Y * Half.Y);

        return ((((offset.X * axis.X) + (offset.Y * axis.Y)) / MathF.Max(reach, 1e-4f)) * 0.5f) + 0.5f;
    }

    /// <summary>Where <c>t</c> sits between two stops, flat outside them.</summary>
    /// <remarks>
    ///     A zero-width span is a hard edge rather than a division by zero, which is what
    ///     <c>from-50% to-50%</c> means and what the background gradient's <c>Span</c> already does.
    /// </remarks>
    static float Span(float t, float from, float to) {
        var width = to - from;

        return width > 1e-4f
            ? Math.Clamp((t - from) / width, 0f, 1f)
            : t < from ? 0f : 1f;
    }

    static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
