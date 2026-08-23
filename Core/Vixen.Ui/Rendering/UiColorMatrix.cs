// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>The per-pixel colour transform a composited group's <c>filter</c> applies to its surface.</summary>
/// <param name="Red">The red output: three coefficients and an offset.</param>
/// <param name="Green">The green output.</param>
/// <param name="Blue">The blue output.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Three rows and not five, because none of the seven functions this represents touches
///         alpha and none of them reads it.</b> CSS's <c>feColorMatrix</c> is 4×5 — twenty
///         coefficients, an alpha row and an alpha column — and every one of <c>brightness</c>,
///         <c>contrast</c>, <c>grayscale</c>, <c>invert</c>, <c>saturate</c>, <c>sepia</c> and
///         <c>hue-rotate</c> leaves the alpha row at <c>0 0 0 1 0</c> and the alpha column at zero.
///         Storing the eight coefficients that are always the same buys nothing and costs thirty-two
///         bytes in a push constant range that has to fit beside the projection.
///     </para>
///     <para>
///         ⚠ <b>Applied to <i>premultiplied</i> colour, with the offset scaled by alpha, and that is
///         what makes the whole feature cost nothing.</b> A colour matrix is defined on
///         un-premultiplied colour: <c>c' = M·(c/a) + o</c>, so <c>c'·a = M·c + o·a</c>. The
///         premultiplied form needs no division and no reconstruction — see <see cref="Apply" /> —
///         which matters twice over. It means transparent black stays transparent black, so a
///         viewport-sized surface whose group inks a corner of it does not acquire a rectangle of
///         <c>invert(1)</c> white everywhere the group is not; and it means the transform is
///         <i>linear</i> in the sampled value, so it commutes exactly with a bilinear sampler and
///         with the Gaussian in <c>ui-blur.frag</c>. See <see cref="Vixen.Ui.Rendering.UiLayer.Filter" />,
///         which leans on that commutation to run the two filters in whichever order is cheap.
///     </para>
///     <para>
///         ⚠ <b>The arithmetic is in the engine's linear working space, and browsers do it in sRGB.</b>
///         Filter Effects 1 § 8.5 says the shorthand functions run with
///         <c>color-interpolation-filters: sRGB</c>, so a browser's <c>grayscale(1)</c> averages
///         gamma-encoded values. Everything in Vixen is linear from the parser down —
///         <c>StyleValueParser</c> converts on the way in — so matching a browser exactly would mean
///         an encode and a decode per pixel in both executors, to reproduce a rule the spec itself
///         calls a legacy default. What that costs is that a <c>grayscale-50</c> here is slightly
///         darker than the same class in a browser; what it buys is that the transform stays linear,
///         which is the property the paragraph above spends. Written down rather than discovered.
///     </para>
///     <para>
///         ⚠ <b><c>default</c> is not the identity, and the nullable is not decoration.</b> An
///         all-zero matrix maps every colour to transparent-adjacent black, so a consumer that read
///         a zeroed field as "no filter" would be right about the intent and wrong about the
///         picture. Both <see cref="Vixen.Ui.DrawCommand.Filter" /> and
///         <see cref="Vixen.Ui.Rendering.UiLayer.Filter" /> are <c>UiColorMatrix?</c> for exactly
///         that reason: absence is <c>null</c>, never a struct that happens to be zeroed.
///     </para>
/// </remarks>
public readonly record struct UiColorMatrix(Vector4 Red, Vector4 Green, Vector4 Blue) {
    /// <summary>The luminance weights <c>grayscale()</c> and <c>saturate()</c> are defined with.</summary>
    /// <remarks>
    ///     Filter Effects 1 § 8.5's, which are Rec. 709's. ⚠ Not the ones
    ///     <see cref="HueRotate" /> uses: the spec writes <c>0.213 0.715 0.072</c> in the hue-rotation
    ///     matrix and <c>0.2126 0.7152 0.0722</c> in the saturation one, and the discrepancy is the
    ///     spec's rather than a rounding here. Following each where it is written is what makes a
    ///     <c>saturate(0)</c> and a <c>grayscale(1)</c> the same picture, which is a thing anyone
    ///     checking this against a browser will try first.
    /// </remarks>
    public static readonly Vector3 Luminance = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>The transform that changes nothing.</summary>
    public static UiColorMatrix Identity { get; } = new(
        new Vector4(1f, 0f, 0f, 0f),
        new Vector4(0f, 1f, 0f, 0f),
        new Vector4(0f, 0f, 1f, 0f)
    );

    /// <summary>Whether this is <see cref="Identity" /> and so worth nothing to apply.</summary>
    /// <remarks>
    ///     ⚠ Exact rather than tolerant, and the tolerance would be the bug. A
    ///     <c>brightness(1.0001)</c> that this called identity would be a group whose surface the GPU
    ///     composited through the image pipeline and the software renderer transformed anyway — a
    ///     divergence <c>UiCompositingTests</c> would report as a compositing fault. The values that
    ///     reach here are computed from a stylesheet's own numbers by the same code on both paths, so
    ///     an exact comparison is the one that cannot disagree with itself.
    /// </remarks>
    public bool IsIdentity => this == Identity;

    /// <summary>Transforms one premultiplied colour.</summary>
    /// <param name="colour">The sample, premultiplied.</param>
    /// <returns>The transformed sample, premultiplied, with the same alpha.</returns>
    /// <remarks>
    ///     ⚠ <b>Clamped to <c>[0, a]</c> and not to <c>[0, 1]</c>, and the difference is the whole of
    ///     what makes <c>brightness-150</c> agree between the two executors.</b> Premultiplied colour
    ///     is valid only up to its own alpha — <c>c ≤ a</c> is what "this pixel is that colour, that
    ///     covered" means — and clamping there is the same thing as clamping the un-premultiplied
    ///     colour to <c>[0, 1]</c>, which is what CSS specifies. Left unclamped, the device would clamp
    ///     anyway on the way into an <c>Rgba8UNorm</c> target and the software renderer's float buffer
    ///     would not, so any filter that can exceed one would diverge on exactly its brightest pixels.
    /// </remarks>
    public Color4 Apply(Color4 colour) {
        var rgb = new Vector3(colour.R, colour.G, colour.B);

        return new Color4(
            Math.Clamp(Vector3.Dot(new Vector3(Red.X, Red.Y, Red.Z), rgb) + (Red.W * colour.A), 0f, colour.A),
            Math.Clamp(Vector3.Dot(new Vector3(Green.X, Green.Y, Green.Z), rgb) + (Green.W * colour.A), 0f, colour.A),
            Math.Clamp(Vector3.Dot(new Vector3(Blue.X, Blue.Y, Blue.Z), rgb) + (Blue.W * colour.A), 0f, colour.A),
            colour.A
        );
    }

    /// <summary>This transform, and then <paramref name="next" />.</summary>
    /// <param name="next">What runs second.</param>
    /// <returns>The single matrix the pair is worth.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Left to right, which is CSS's order and the opposite of how the matrices multiply.</b>
    ///         <c>filter: grayscale(1) blur(4px) brightness(2)</c> applies the functions in the order
    ///         written — Filter Effects 1 § 5 — so <c>a.Then(b)</c> is the matrix <c>B·A</c>. Naming
    ///         the method after the reading order rather than after the multiplication is deliberate:
    ///         the one thing a caller assembling a filter list can get wrong is the order, and
    ///         <c>foreach (var f in functions) m = m.Then(f);</c> cannot express the wrong one.
    ///     </para>
    ///     <para>
    ///         ⚠ The offset composes as <c>B·oa + ob</c> rather than <c>oa + ob</c>, which is the
    ///         affine part and the half that is easy to drop. <c>invert(1) brightness(2)</c> is a
    ///         doubled inversion and not an inversion plus a double.
    ///     </para>
    /// </remarks>
    public UiColorMatrix Then(in UiColorMatrix next) =>
        new(Row(next.Red), Row(next.Green), Row(next.Blue));

    /// <summary>Lightens or darkens: <c>c' = c · amount</c>.</summary>
    /// <param name="amount">One is unchanged, zero is black, above one over-exposes.</param>
    /// <returns>The matrix.</returns>
    public static UiColorMatrix Brightness(float amount) =>
        new(
            new Vector4(amount, 0f, 0f, 0f),
            new Vector4(0f, amount, 0f, 0f),
            new Vector4(0f, 0f, amount, 0f)
        );

    /// <summary>Pushes towards or away from mid grey: <c>c' = c · amount + (0.5 − 0.5 · amount)</c>.</summary>
    /// <param name="amount">One is unchanged, zero is flat mid grey.</param>
    /// <returns>The matrix.</returns>
    /// <remarks>
    ///     ⚠ The pivot is a literal 0.5 in the working space, which is what Filter Effects 1 § 8.5's
    ///     <c>feComponentTransfer</c> intercept says and is <i>not</i> mid grey to the eye once the
    ///     space is linear — see the class remark about sRGB. Following the spec's number rather than
    ///     picking a perceptual pivot keeps the one thing that is checkable checkable: two executors
    ///     agreeing, and a value somebody can compute by hand.
    /// </remarks>
    public static UiColorMatrix Contrast(float amount) {
        var offset = 0.5f - (0.5f * amount);

        return new(
            new Vector4(amount, 0f, 0f, offset),
            new Vector4(0f, amount, 0f, offset),
            new Vector4(0f, 0f, amount, offset)
        );
    }

    /// <summary>Drains colour towards luminance.</summary>
    /// <param name="amount">Zero is unchanged, one is fully grey.</param>
    /// <returns>The matrix.</returns>
    /// <remarks>
    ///     ⚠ Defined as <c>Saturate(1 − amount)</c> rather than written out, because the spec defines
    ///     it that way and because two hand-written matrices that were supposed to be the same
    ///     function is how <c>grayscale-100</c> and <c>saturate-0</c> would come to differ in the
    ///     fourth decimal place for no reason anyone could later find.
    /// </remarks>
    public static UiColorMatrix Grayscale(float amount) => Saturate(1f - amount);

    /// <summary>Scales the distance from luminance.</summary>
    /// <param name="amount">One is unchanged, zero is grey, above one exaggerates.</param>
    /// <returns>The matrix.</returns>
    public static UiColorMatrix Saturate(float amount) {
        var l = Luminance;
        var rest = 1f - amount;

        return new(
            new Vector4((l.X * rest) + amount, l.Y * rest, l.Z * rest, 0f),
            new Vector4(l.X * rest, (l.Y * rest) + amount, l.Z * rest, 0f),
            new Vector4(l.X * rest, l.Y * rest, (l.Z * rest) + amount, 0f)
        );
    }

    /// <summary>Ages towards a warm monochrome.</summary>
    /// <param name="amount">Zero is unchanged, one is fully sepia.</param>
    /// <returns>The matrix.</returns>
    /// <remarks>The nine constants are Filter Effects 1 § 8.5's, interpolated against the identity.</remarks>
    public static UiColorMatrix Sepia(float amount) =>
        Mix(
            new(
                new Vector4(0.393f, 0.769f, 0.189f, 0f),
                new Vector4(0.349f, 0.686f, 0.168f, 0f),
                new Vector4(0.272f, 0.534f, 0.131f, 0f)
            ),
            amount
        );

    /// <summary>Flips towards the complement: <c>c' = c + amount · (1 − 2c)</c>.</summary>
    /// <param name="amount">Zero is unchanged, one is a full inversion, a half is flat mid grey.</param>
    /// <returns>The matrix.</returns>
    public static UiColorMatrix Invert(float amount) {
        var scale = 1f - (2f * amount);

        return new(
            new Vector4(scale, 0f, 0f, amount),
            new Vector4(0f, scale, 0f, amount),
            new Vector4(0f, 0f, scale, amount)
        );
    }

    /// <summary>Rotates the hue about the luminance axis.</summary>
    /// <param name="degrees">How far round. Zero and any multiple of 360 are the identity.</param>
    /// <returns>The matrix.</returns>
    /// <remarks>
    ///     ⚠ <b>The spec's linear approximation and not a true HSL rotation, which is the difference
    ///     between matching a browser and being defensible.</b> Filter Effects 1 § 8.5 defines this as
    ///     a fixed 3×3 built from <c>cos</c> and <c>sin</c> — an approximation that does not preserve
    ///     luminance exactly and can push a saturated colour out of gamut, which <see cref="Apply" />
    ///     then clamps. Every browser produces exactly these numbers, so a "better" rotation here
    ///     would be a divergence nobody asked for on the one filter people compare side by side.
    /// </remarks>
    public static UiColorMatrix HueRotate(float degrees) {
        var radians = degrees * (MathF.PI / 180f);
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);

        return new(
            new Vector4(
                0.213f + (c * 0.787f) - (s * 0.213f),
                0.715f - (c * 0.715f) - (s * 0.715f),
                0.072f - (c * 0.072f) + (s * 0.928f),
                0f
            ),
            new Vector4(
                0.213f - (c * 0.213f) + (s * 0.143f),
                0.715f + (c * 0.285f) + (s * 0.140f),
                0.072f - (c * 0.072f) - (s * 0.283f),
                0f
            ),
            new Vector4(
                0.213f - (c * 0.213f) - (s * 0.787f),
                0.715f - (c * 0.715f) + (s * 0.715f),
                0.072f + (c * 0.928f) + (s * 0.072f),
                0f
            )
        );
    }

    /// <summary>Interpolates a fully-applied matrix against the identity.</summary>
    static UiColorMatrix Mix(in UiColorMatrix full, float amount) {
        var rest = 1f - amount;

        return new(
            (full.Red * amount) + (Identity.Red * rest),
            (full.Green * amount) + (Identity.Green * rest),
            (full.Blue * amount) + (Identity.Blue * rest)
        );
    }

    /// <summary>One row of <paramref name="outer" /> composed onto this whole matrix.</summary>
    Vector4 Row(in Vector4 outer) =>
        new(
            (outer.X * Red.X) + (outer.Y * Green.X) + (outer.Z * Blue.X),
            (outer.X * Red.Y) + (outer.Y * Green.Y) + (outer.Z * Blue.Y),
            (outer.X * Red.Z) + (outer.Y * Green.Z) + (outer.Z * Blue.Z),
            (outer.X * Red.W) + (outer.Y * Green.W) + (outer.Z * Blue.W) + outer.W
        );
}
