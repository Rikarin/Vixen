// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>How one entry of a mask list combines with the entries below it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The numbering is the wire format and <see cref="Add" /> is deliberately zero.</b>
///         <c>ui-mask.frag</c> reads this out of the storage buffer as an integer and branches on it,
///         so the values here are not an implementation detail of the enum — they are the contract.
///         <c>GradientShape</c> is the cautionary tale: its zero is <c>None</c>, the shader was
///         written against a zero-based numbering of its own, and every linear mask took the radial
///         branch and drew a plausible round fade. This enum has no <c>None</c> for that reason, and
///         its zero is the operator CSS Masking 1 § 5.4 gives as the initial value — so a default
///         <see cref="UiMask" /> composites the way an unstated <c>mask-composite</c> does rather
///         than in whichever way the first name happened to sort.
///     </para>
///     <para>
///         Each operator is Porter-Duff on the coverage alone, with the entry as the source and the
///         already-composed entries below it as the backdrop. There is no colour here to composite —
///         see <see cref="UiMask" />'s first remark — so the four are four one-line expressions
///         rather than four blend equations.
///     </para>
/// </remarks>
public enum MaskComposite {
    /// <summary><c>source-over</c>: <c>s + b(1 - s)</c>. CSS's initial value.</summary>
    Add = 0,

    /// <summary><c>source-out</c>: <c>s(1 - b)</c> — the entry, with everything below it punched out.</summary>
    Subtract = 1,

    /// <summary><c>source-in</c>: <c>s·b</c> — what Tailwind's edge ramps combine with.</summary>
    Intersect = 2,

    /// <summary><c>xor</c>: <c>s(1 - b) + b(1 - s)</c>.</summary>
    Exclude = 3
}

/// <summary>The per-pixel coverage a composited group's <c>mask-image</c> multiplies its surface by.</summary>
/// <param name="Centre">The mask box's centre, in document pixels.</param>
/// <param name="Half">Half the mask box's size, in document pixels.</param>
/// <param name="Axis">
///     The gradient's direction. Its length is not read — <see cref="Coverage(Vixen.Core.Mathematics.Vector2)" /> normalises it — but
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
    /// <summary>How this entry combines with the entries below it in the same list.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Init-only and defaulting to <see cref="MaskComposite.Add" />, which is CSS's
    ///         initial value and not merely the first name in the enum.</b> A <see cref="UiMask" />
    ///         written by a test or a host that has never heard of lists composites the way a
    ///         one-entry <c>mask-image</c> with no <c>mask-composite</c> beside it does — see
    ///         <see cref="Compose" />, under which every operator is the identity on a list of one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the entry rather than on the list, because CSS puts it there.</b>
    ///         <c>mask-composite</c> is a per-layer property: <c>mask-composite: add, intersect</c>
    ///         is two operators for two layers, and a list that carried one operator could not
    ///         express it. It also means the field travels with the entry through the storage buffer
    ///         instead of needing a parallel array the shader would have to index twice.
    ///     </para>
    /// </remarks>
    public MaskComposite Composite { get; init; }

    /// <summary>A mask that hides everything, which is what an unresolvable <c>mask-image</c> is not.</summary>
    /// <remarks>
    ///     ⚠ Present so that the <i>refusal</i> path has somewhere to point and deliberately not used
    ///     by it. CSS's answer to a <c>mask-image</c> it cannot fetch is to mask nothing — an
    ///     unloadable mask must not black out the element — so <c>DrawListBuilder</c> drops the
    ///     declaration and emits no list entry for it. This is here for tests that
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

    /// <summary>The coverage a whole mask list gives at a point.</summary>
    /// <param name="masks">The list, in CSS order: the topmost layer first.</param>
    /// <param name="point">Where to evaluate it, in document pixels.</param>
    /// <returns>The composed coverage, clamped to <c>[0, 1]</c>. One for an empty list.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A transcription of <c>ui-mask.frag</c>'s <c>mask_list</c>, down to the direction
    ///         of the loop, and <c>UiCompositingTests</c> is the only thing that holds the two
    ///         together.</b> The same bargain <see cref="Coverage(Vixen.Core.Mathematics.Vector2)" /> already makes with
    ///         <c>mask_coverage</c>, and a list makes it matter more rather than less: two
    ///         implementations of a fold can agree on every entry and still disagree on the order
    ///         they fold in, which shows up only where the operators are not commutative — which is
    ///         three of the four.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bottom-up, because <c>mask-composite</c> describes how a layer meets what is
    ///         <i>under</i> it.</b> CSS lists mask layers topmost-first, exactly as
    ///         <c>background-image</c> does, and Masking 1 § 5.4 gives each layer's operator the
    ///         already-composed layers below it as its backdrop. So the walk starts at the last
    ///         entry and works forwards, and the operator that is read at each step is the
    ///         <i>source's</i> — never the backdrop's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bottom entry is taken as itself, and this is a deliberate departure from one
    ///         sentence of the specification.</b> Read literally, the bottom layer composites against
    ///         a transparent-black backdrop, which makes <c>intersect</c> — <c>s·0</c> — erase
    ///         everything. That reading cannot be the one browsers implement, because
    ///         <c>mask-composite: intersect</c> is what Tailwind emits for every one of its edge
    ///         ramps and those ramps visibly work; under the literal reading every
    ///         <c>mask-t-from-*</c> in the world would blank its element. Starting from the bottom
    ///         entry itself makes all four operators the identity on a list of one, which is the
    ///         property that actually has to hold: adding <c>mask-composite</c> to a single-layer
    ///         mask must not change the picture.
    ///     </para>
    /// </remarks>
    public static float Coverage(ReadOnlySpan<UiMask> masks, Vector2 point) {
        if (masks.Length == 0) {
            return 1f;
        }

        var result = masks[^1].Coverage(point);

        for (var index = masks.Length - 2; index >= 0; index--) {
            result = Compose(masks[index].Composite, masks[index].Coverage(point), result);
        }

        return Math.Clamp(result, 0f, 1f);
    }

    /// <summary>Combines one entry's coverage with the coverage of everything below it.</summary>
    /// <param name="composite">The <i>source's</i> operator.</param>
    /// <param name="source">This entry's coverage.</param>
    /// <param name="backdrop">The composed coverage of the entries below it.</param>
    /// <returns>The combined coverage.</returns>
    /// <remarks>
    ///     Porter-Duff on the coverage alone. ⚠ Not clamped here — <see cref="Coverage(Vixen.Core.Mathematics.Vector2)" /> clamps
    ///     once at the end, because clamping every step would quietly turn <c>subtract</c> into a
    ///     different operator on any input a caller had already pushed outside <c>[0, 1]</c>, and
    ///     the shader has to be able to make the same choice.
    /// </remarks>
    public static float Compose(MaskComposite composite, float source, float backdrop) =>
        composite switch {
            MaskComposite.Subtract => source * (1f - backdrop),
            MaskComposite.Intersect => source * backdrop,
            MaskComposite.Exclude => (source * (1f - backdrop)) + (backdrop * (1f - source)),
            _ => source + (backdrop * (1f - source))
        };

    static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
