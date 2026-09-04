// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>The 2D affine a composited group's <c>rotate</c> and <c>scale</c> place it under.</summary>
/// <param name="M11">The x contribution to x.</param>
/// <param name="M12">The x contribution to y.</param>
/// <param name="M21">The y contribution to x.</param>
/// <param name="M22">The y contribution to y.</param>
/// <param name="Dx">The x translation, in document pixels.</param>
/// <param name="Dy">The y translation, in document pixels.</param>
/// <remarks>
///     <para>
///         ⚠ <b>This used to be six floats that deliberately could not express a projective map, and
///         the argument for that was sound rather than mistaken.</b> It ran: the third row of a 2D
///         affine is always <c>0 0 1</c>, storing it costs bytes on a type both executors compare by
///         value, and — the real point — a perspective divide is exactly what the composite quad's
///         linearly interpolated texture coordinates cannot survive, so a type that cannot express
///         one is the type that says so.
///     </para>
///     <para>
///         ⚠ <b>What that argument did not reach is that the element is <i>planar</i>.</b> A plane
///         under a 3D transform and a perspective projects to a plane: the four corners of the border
///         box land at four points with four <c>w</c>s, and that map is exactly a 2D homography. So
///         <c>rotateX</c> and <c>perspective</c> need a 3×3 and not a 4×4, and the third
///         <i>column</i> — <see cref="M13" />, <see cref="M23" />, <see cref="M33" /> — is the whole
///         of the difference. The texture coordinates still cannot survive a linear interpolation,
///         and the answer to that is a <c>w</c> on the vertex rather than a type that refuses the
///         matrix: a rasteriser divides by <c>w</c> because that is what a rasteriser does.
///     </para>
///     <para>
///         ⚠ <b>The affine case is bit-for-bit what it was, and that is a requirement rather than a
///         nicety.</b> Every reference image in <c>Vixen.Graphics.Golden.Tests.UiCompositingTests</c>
///         was rendered through the six-float arithmetic, so a rounding that moved would have to be
///         accepted into every one of them — and "both executors changed together" is precisely what
///         a bug in a shared specification looks like. <see cref="Then" />, <see cref="Apply" />,
///         <see cref="Determinant" /> and <see cref="About(Vixen.Core.Mathematics.Vector2)" /> are
///         written so that their extra terms are exactly <c>+ 0</c> and <c>× 1</c> on an affine, which
///         IEEE-754 leaves untouched; <see cref="Invert" /> cannot be written that way and branches
///         instead, keeping the old expression verbatim. <c>UiTransformProjectiveTests</c> asserts the
///         equality rather than leaving it as reasoning.
///     </para>
///     <para>
///         ⚠ <b>And <c>default</c> is still what it always was, which is why the third column is
///         stored offset.</b> A homography's identity has <c>M33 = 1</c>, so a naively added field
///         would make every zeroed struct divide by zero — turning today's defined-but-wrong
///         collapse-to-the-origin into a <c>NaN</c> that propagates into a vertex buffer. The field
///         behind <see cref="M33" /> holds <c>M33 − 1</c>, so a zeroed <see cref="UiTransform" /> is
///         affine and behaves exactly as it did before this type could be projective.
///     </para>
///     <para>
///         ⚠ <b>Expressed in <i>absolute document space</i>, with the transform origin already folded
///         in.</b> CSS defines <c>rotate</c> about <c>transform-origin</c>, which is the border box's
///         centre by default, so the matrix that reaches here is already
///         <c>T(origin) · R · S · T(-origin)</c>. Carrying the origin separately would put the same
///         composition in the geometry builder and in the hit test, which is the two-copies-of-the-
///         arithmetic failure <c>UiDocument.Accumulate</c>'s own remark warns about for
///         <c>translate</c>. One matrix, composed once, read by both.
///     </para>
///     <para>
///         ⚠ <b><see cref="Identity" /> is the absence, and <c>default</c> is <i>not</i> it.</b> A
///         zeroed struct collapses every point to the origin, so a consumer that read a default field
///         as "no transform" would draw nothing at all. <see cref="UiLayer.Transform" /> and
///         <see cref="Vixen.Ui.DrawCommand.Transform" /> are therefore nullable, exactly as
///         <see cref="UiColorMatrix" /> is and for the identical reason.
///     </para>
/// </remarks>
public readonly record struct UiTransform(float M11, float M12, float M21, float M22, float Dx, float Dy) {
    /// <summary>How far <see cref="M33" /> is from one, which is what is actually stored.</summary>
    /// <remarks>
    ///     ⚠ <b>The offset is the whole reason a projective column could be added without touching a
    ///     single call site.</b> See the class remark: a zeroed struct has to stay affine, and the
    ///     identity's <c>M33</c> is one. Private, because nothing outside this type should ever have
    ///     to know that the storage differs from the name — <see cref="M33" /> is the number, this is
    ///     the representation.
    /// </remarks>
    readonly float m33MinusOne;

    /// <summary>The transform that moves nothing.</summary>
    public static UiTransform Identity => new(1f, 0f, 0f, 1f, 0f, 0f);

    /// <summary>How much a point's <i>x</i> contributes to its <c>w</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Its unit is the reciprocal of a length, which is what makes every epsilon in this type
    ///     wrong for it.</b> A <c>perspective(1000px)</c> puts about a thousandth here — the same
    ///     order as <see cref="IsIdentity" />'s bound on the linear cells, which are dimensionless.
    ///     Judging this column by that bound would call a real perspective the identity and never open
    ///     a group for it. See <see cref="IsIdentity" />, which carries a separate bound and says why.
    /// </remarks>
    public float M13 { get; init; }

    /// <summary>And its <i>y</i>.</summary>
    public float M23 { get; init; }

    /// <summary>The homogeneous scale at the origin: one for an affine, anything else for a homography.</summary>
    /// <remarks>
    ///     ⚠ <b>Stored as <c>M33 − 1</c>, so that <c>default(UiTransform)</c> is affine.</b> The class
    ///     remark argues it; the consequence to remember here is that this property is computed and
    ///     the record's value equality compares the offset, which is the same comparison.
    ///     <para>
    ///         ⚠ <b>Not normalised away, although a homography is only defined up to scale.</b>
    ///         Dividing the nine cells through by this one would save a float and would blow up
    ///         exactly where the arithmetic is already delicate — a composition whose <c>M33</c>
    ///         approaches zero is an origin approaching the eye plane, which is the case
    ///         <see cref="Invert" /> has to be able to refuse rather than the case it should divide by.
    ///     </para>
    /// </remarks>
    public float M33 {
        get => m33MinusOne + 1f;
        init => m33MinusOne = value - 1f;
    }

    /// <summary>Whether this is a plain affine, so no <c>w</c> anywhere can be anything but one.</summary>
    /// <remarks>
    ///     ⚠ <b>Exact rather than tolerant, and that is deliberate in a type whose other predicates
    ///     are not.</b> <see cref="IsIdentity" /> asks "is this near enough to nothing that a group is
    ///     not worth opening", which is a question about pictures and needs a tolerance. This asks
    ///     "was a projective term ever written", which is a question about <i>arithmetic</i>: it
    ///     selects the code path in <see cref="Invert" /> that reproduces the pre-projective rounding
    ///     exactly, and a tolerant version would let a transform that is very slightly projective take
    ///     the affine branch and come back subtly wrong. It is also what a consumer asks to decide
    ///     whether a quad needs a <c>w</c> at all.
    /// </remarks>
    public bool IsAffine => M13 == 0f && M23 == 0f && m33MinusOne == 0f;

    /// <summary>Whether this is <see cref="Identity" />, to the tolerance a pixel can tell.</summary>
    /// <remarks>
    ///     ⚠ <b>The gate on opening a group at all, which is why the tolerance is stated rather than
    ///     exact.</b> <c>rotate: 0deg</c> and <c>scale: 100%</c> are the initial values and are written
    ///     constantly — every <c>rotate-0</c> in a stylesheet, every animation that has finished
    ///     returning to rest. Each one that reached <c>DrawListBuilder</c> as "a transform" would cost
    ///     a viewport-sized surface and a render pass to draw the identical picture. An exact
    ///     comparison would miss the ones that arrive as <c>cos(0)</c> and a rounding.
    ///     <para>
    ///         ⚠ <b>The bound is on the coefficients, so it is a bound on how far a <i>unit</i> vector
    ///         moves — not on how far the element's corner does.</b> It is scale-free where the real
    ///         question is not, and the honest statement of the gap is this: an element whose origin
    ///         sits near the document's, rotated by under about a twentieth of a degree, is dropped
    ///         even though a corner a thousand points out would have moved most of a pixel. Anywhere
    ///         else in the document the translation column catches it, because the origin is folded in
    ///         and those coordinates are large. Tightening the bound instead would spend a surface on
    ///         every <c>rotate-0</c> that arrived with a rounding, which is the case this exists for;
    ///         scaling it by the element's extent would make the type depend on the element, which it
    ///         deliberately does not.
    ///     </para>
    /// </remarks>
    public bool IsIdentity =>
        MathF.Abs(M11 - 1f) < 1e-3f
        && MathF.Abs(M12) < 1e-3f
        && MathF.Abs(M21) < 1e-3f
        && MathF.Abs(M22 - 1f) < 1e-3f
        && MathF.Abs(Dx) < 1e-3f
        && MathF.Abs(Dy) < 1e-3f
        && MathF.Abs(M13) < PerspectiveEpsilon
        && MathF.Abs(M23) < PerspectiveEpsilon
        && MathF.Abs(m33MinusOne) < 1e-3f;

    /// <summary>How small a perspective term has to be before it counts as none.</summary>
    /// <remarks>
    ///     ⚠ <b>A thousand times tighter than the bound beside it, because the two columns are not in
    ///     the same units and the obvious value is catastrophically wrong.</b> <see cref="M13" /> and
    ///     <see cref="M23" /> are reciprocal lengths: a <c>perspective(1000px)</c> — an ordinary,
    ///     strong perspective — puts <c>1e-3</c> in one of them, which is exactly the bound the linear
    ///     cells use. Reusing it would declare that transform the identity, refuse to open a group for
    ///     it, and draw the element flat, with nothing anywhere reporting a thing.
    ///     <para>
    ///         The number is what a pixel can tell, worked from the other end: over a thousand-point
    ///         element, <c>1e-6</c> per point moves <c>w</c> by a thousandth, so the far edge is
    ///         scaled by a part in a thousand — a tenth of a pixel on that edge, and less everywhere
    ///         else. Below that the group is genuinely not worth a surface and a render pass.
    ///     </para>
    /// </remarks>
    const float PerspectiveEpsilon = 1e-6f;

    /// <summary>This transform's determinant.</summary>
    /// <remarks>
    ///     <para>
    ///         Zero means the transform is degenerate — <c>scale-0</c>, or a scale with one axis at
    ///         zero — and there is no inverse. Both consumers check it rather than dividing and
    ///         propagating an infinity into a vertex position.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It stopped being "the area it multiplies by" when the third column arrived, and
    ///         the old sentence is worth correcting rather than deleting.</b> An affine scales every
    ///         area by the same factor and this is that factor. A homography does not: it scales area
    ///         by <c>det / w³</c>, which varies across the element and is the whole visual point of a
    ///         perspective. What survives is the only thing either consumer asks — that a zero
    ///         determinant means no inverse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written so the affine case is the old expression exactly.</b> With the third
    ///         column at <c>(0, 0, 1)</c> every added term is <c>× 0</c> or <c>× 1</c>, and IEEE-754
    ///         leaves <c>a + 0</c> and <c>a × 1</c> alone — so this returns the identical float for
    ///         every transform that existed before the column did. See the class remark for why that
    ///         is a requirement.
    ///     </para>
    /// </remarks>
    public float Determinant =>
        (M11 * ((M22 * M33) - (M23 * Dy)))
        - (M12 * ((M21 * M33) - (M23 * Dx)))
        + (M13 * ((M21 * Dy) - (M22 * Dx)));

    /// <summary>A rotation about a point, in degrees clockwise.</summary>
    /// <remarks>
    ///     ⚠ <b>Clockwise, because y grows downwards here.</b> CSS Transforms 1 § 11 defines a
    ///     positive <c>rotate</c> as clockwise on screen, and a screen whose y axis points down gets
    ///     that from the <i>standard</i> counter-clockwise matrix rather than from a transposed one.
    ///     Writing the negation in explicitly would rotate <c>rotate-45</c> the wrong way, which is a
    ///     mistake that looks right in a unit test written from the same misunderstanding — so the
    ///     convention is named here and asserted against a known corner in
    ///     <c>Vixen.Ui.Tests.TransformTests</c>.
    /// </remarks>
    public static UiTransform Rotation(float degrees, Vector2 about) {
        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        return About(cos, sin, -sin, cos, about);
    }

    /// <summary>A scale about a point.</summary>
    public static UiTransform Scale(float x, float y, Vector2 about) => About(x, 0f, 0f, y, about);

    /// <summary>This transform performed about <paramref name="origin" /> rather than about zero.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>T(origin) · this · T(−origin)</c>, and the one thing worth saying about it is why it
    ///         is a method rather than a parameter on the two factories above. A
    ///         <c>&lt;transform-list&gt;</c> is several functions sharing <b>one</b> origin, not one
    ///         origin each: <c>transform: rotate(45deg) translate(20px)</c> rotates about the box's
    ///         centre and translates in the rotated frame. Re-centring each function separately gives
    ///         a different picture, and the list has to be composed first and re-centred once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Composing several already-centred transforms is <i>not</i> a mistake, though</b>,
    ///         which is why <see cref="Rotation" /> and <see cref="Scale" /> still fold the origin in
    ///         themselves. Adjacent <c>T(o) · L · T(−o)</c> factors cancel their inner translations
    ///         exactly, so <c>rotate</c> and <c>scale</c> as separate <i>properties</i> compose to the
    ///         same matrix either way. It is only a list of functions with a translation among them
    ///         that can tell the two apart.
    ///     </para>
    /// </remarks>
    /// <param name="origin">The point to perform it about, in the same space as this transform.</param>
    /// <returns>The re-centred transform.</returns>
    public UiTransform About(Vector2 origin) {
        // The re-centred homogeneous scale, which is the factor the translation column below is in
        // terms of. ⚠ On an affine it is `1 - (0 + 0)`, so every use of it downstream is `× 1` and the
        // two translation cells come out as the expression this method has always had.
        var scale = M33 - ((origin.X * M13) + (origin.Y * M23));
        var (x, y) = (M13, M23);

        return new UiTransform(
            M11 + (M13 * origin.X),
            M12 + (M13 * origin.Y),
            M21 + (M23 * origin.X),
            M22 + (M23 * origin.Y),

            // ⚠ The *original* linear cells, not the re-centred ones above. `T(−o) · M · T(o)` puts
            // the untouched row into this product; substituting the new ones is the plausible slip,
            // and on an affine — where the two are equal — it is invisible.
            Dx + (scale * origin.X) - ((M11 * origin.X) + (M21 * origin.Y)),
            Dy + (scale * origin.Y) - ((M12 * origin.X) + (M22 * origin.Y))
        ) {
            M13 = x,
            M23 = y,
            M33 = scale
        };
    }

    /// <summary>A linear map re-centred on <paramref name="about" />.</summary>
    /// <remarks>
    ///     <c>T(about) · M · T(-about)</c>, written out rather than composed from three
    ///     <see cref="UiTransform" />s, because the translation columns cancel to two subtractions and
    ///     the intermediate products are exactly the rounding a reader would then have to reason about.
    /// </remarks>
    static UiTransform About(float m11, float m12, float m21, float m22, Vector2 about) =>
        new(
            m11,
            m12,
            m21,
            m22,
            about.X - ((m11 * about.X) + (m21 * about.Y)),
            about.Y - ((m12 * about.X) + (m22 * about.Y))
        );

    /// <summary>This transform followed by <paramref name="then" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The argument is applied <i>second</i>, which is the opposite of CSS's written order and
    ///     the same as its evaluation order.</b> <c>rotate</c> and <c>scale</c> are separate properties
    ///     in Transforms 2 § 3 and compose translate, then rotate, then scale — so a point is scaled
    ///     first and rotated after. Naming the parameter for when it runs rather than where it is
    ///     written is what keeps the call sites readable.
    /// </remarks>
    public UiTransform Then(in UiTransform then) =>
        new(
            (M11 * then.M11) + (M12 * then.M21) + (M13 * then.Dx),
            (M11 * then.M12) + (M12 * then.M22) + (M13 * then.Dy),
            (M21 * then.M11) + (M22 * then.M21) + (M23 * then.Dx),
            (M21 * then.M12) + (M22 * then.M22) + (M23 * then.Dy),

            // ⚠ `M33 * then.Dx` where the affine version wrote `then.Dx`, which is the same float
            // because `M33` is one on an affine. Every added term on this constructor is `× 0` or
            // `× 1` for the same reason — see the class remark, and `UiTransformProjectiveTests`,
            // which asserts the equality rather than trusting this paragraph.
            (Dx * then.M11) + (Dy * then.M21) + (M33 * then.Dx),
            (Dx * then.M12) + (Dy * then.M22) + (M33 * then.Dy)
        ) {
            M13 = (M11 * then.M13) + (M12 * then.M23) + (M13 * then.M33),
            M23 = (M21 * then.M13) + (M22 * then.M23) + (M23 * then.M33),
            M33 = (Dx * then.M13) + (Dy * then.M23) + (M33 * then.M33)
        };

    /// <summary>Where a point lands, projected.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the whole of what the renderer does with a transform, and the reason the
    ///         feature costs no shader <i>while it is affine</i>.</b> A composited group's quad is four
    ///         vertices whose texture coordinates name the surface the group was rasterised into;
    ///         moving those four positions and leaving the coordinates alone <i>is</i> the transform.
    ///         Both executors interpolate the coordinate across the two triangles linearly, and an
    ///         affine map is exactly the class of map for which that interpolation is not an
    ///         approximation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Under a homography it still returns the right point and the interpolation between
    ///         two of them is no longer right, which is why <see cref="Project" /> exists beside
    ///         it.</b> The <c>w</c> this divides away is exactly what a rasteriser needs in order to
    ///         interpolate the coordinate correctly, so a caller that is placing <i>vertices</i> wants
    ///         the homogeneous form and a caller that wants <i>a point</i> — a hit test, a bound —
    ///         wants this one. Dividing here and multiplying back would be the same arithmetic done
    ///         twice and rounded twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A point with <c>w ≤ 0</c> is behind the eye and this returns its
    ///         <i>reflection</i>.</b> The division is defined and the answer is finite and plausible,
    ///         which is the whole danger: it lands on the far side of the vanishing point, where
    ///         nothing is drawn. There is no value this could return that would say so, so it does not
    ///         try — a caller that can be handed such a point asks <see cref="Project" /> and reads the
    ///         sign. On an affine, <c>w</c> is one everywhere and the question cannot arise.
    ///     </para>
    /// </remarks>
    public Vector2 Apply(Vector2 point) {
        var projected = Project(point);

        // ⚠ On an affine this is a division by exactly 1f, which IEEE-754 defines as the identity —
        // so this returns the same two floats the six-float version did, for every transform that
        // existed before the third column. Guarding the affine case with a branch instead would be a
        // second expression to keep in step for no arithmetic difference at all.
        return new Vector2(projected.X / projected.Z, projected.Y / projected.Z);
    }

    /// <summary>Where a point lands, before the divide: <c>x</c>, <c>y</c> and <c>w</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a vertex wants.</b> A rasteriser interpolates <c>u/w</c>, <c>v/w</c> and
    ///         <c>1/w</c> and divides per fragment, which is what makes a texture coordinate correct
    ///         across a projected quad — so the position handed to it has to carry the <c>w</c> rather
    ///         than have been divided by it. Hardware does this for free; see the software rasterizer,
    ///         which does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sign of <c>w</c> is the only thing that says a point is in front of the
    ///         eye</b>, and it is discarded by the division. Positive is in front. Zero is on the eye
    ///         plane and has no image at all; negative is behind, and its projection is a finite point
    ///         reflected through the vanishing point — a plausible answer to a question that has none.
    ///     </para>
    /// </remarks>
    /// <param name="point">The point, in this transform's own space.</param>
    /// <returns><c>(x, y, w)</c>, unprojected.</returns>
    public Vector3 Project(Vector2 point) =>
        new(
            (M11 * point.X) + (M21 * point.Y) + Dx,
            (M12 * point.X) + (M22 * point.Y) + Dy,

            // ⚠ `0 · x + 0 · y + 1` on an affine, which is exactly `1f` — so the division in `Apply`
            // is the identity and not merely close to it.
            (M13 * point.X) + (M23 * point.Y) + M33
        );

    /// <summary>The transform that undoes this one, or null where there is none.</summary>
    /// <remarks>
    ///     ⚠ <b>Null for a degenerate transform, and the hit test reads that as "nothing here".</b> A
    ///     <c>scale-0</c> element paints zero pixels, so there is no point on the screen that could be
    ///     a click on it; returning an identity instead would leave the element clickable at its
    ///     untransformed box, which is a control that is invisible and still takes the pointer.
    /// </remarks>
    public UiTransform? Invert() {
        var determinant = Determinant;

        if (MathF.Abs(determinant) < 1e-9f) {
            return null;
        }

        var inverse = 1f / determinant;

        // ⚠ <b>The affine arithmetic verbatim, and this is the one operation on this type that could
        // not be generalised without moving a rounding.</b> The adjugate below computes the same
        // matrix by a different sequence of multiplies, so its answers differ in the last bit — which
        // is geometrically nothing and is still a difference in the inverse the hit test walks with.
        // Every other method here reduces to its old expression exactly; this one is a branch instead,
        // so that the claim "nothing affine changed" stays true of all of them.
        if (IsAffine) {
            var a11 = M22 * inverse;
            var a12 = -M12 * inverse;
            var a21 = -M21 * inverse;
            var a22 = M11 * inverse;

            return new UiTransform(a11, a12, a21, a22, -((Dx * a11) + (Dy * a21)), -((Dx * a12) + (Dy * a22)));
        }

        // The adjugate, transposed into this type's row-vector layout and scaled by 1/det. ⚠ The
        // third column is what an affine inverse never had: a homography's inverse is a homography,
        // and its perspective terms are the ones that map the far plane back.
        return new UiTransform(
            ((M22 * M33) - (M23 * Dy)) * inverse,
            ((M13 * Dy) - (M12 * M33)) * inverse,
            ((M23 * Dx) - (M21 * M33)) * inverse,
            ((M11 * M33) - (M13 * Dx)) * inverse,
            ((M21 * Dy) - (M22 * Dx)) * inverse,
            ((M12 * Dx) - (M11 * Dy)) * inverse
        ) {
            M13 = ((M12 * M23) - (M13 * M22)) * inverse,
            M23 = ((M13 * M21) - (M11 * M23)) * inverse,
            M33 = ((M11 * M22) - (M12 * M21)) * inverse
        };
    }

    /// <summary>The axis-aligned box that contains <paramref name="rectangle" /> once transformed.</summary>
    /// <remarks>
    ///     ⚠ <b>An empty rectangle where there is no bound</b> — see <see cref="TryBounds" />, which
    ///     is the form to call from anywhere a projective transform can reach, because "no bound" and
    ///     "a bound of no size" are different answers and this signature cannot tell them apart. Every
    ///     caller today holds an affine, for which the question does not arise.
    /// </remarks>
    public Rectangle Bounds(Rectangle rectangle) => TryBounds(rectangle, out var bounds) ? bounds : default;

    /// <summary>
    ///     The axis-aligned box that contains <paramref name="rectangle" /> once transformed, or false
    ///     where it has none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An affine always has one and a homography need not, which is why this exists and
    ///         <see cref="Bounds" /> alone is no longer enough.</b> Under a perspective, part of the
    ///         plane can lie behind the eye. The four corners of a rectangle straddling that line
    ///         project to four finite points — the ones behind are reflected through the vanishing
    ///         point — so the corner-wise box below is not merely loose, it is <i>wrong</i>: it
    ///         reports a bound for a shape that runs to infinity, and every corner it was computed
    ///         from is on the wrong side of the screen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the sign of <c>w</c> is checked before the box, and this refuses rather than
    ///         approximating.</b> Clipping the quad against the <c>w = 0</c> plane and bounding what
    ///         is left is the right answer and it belongs to the rasterizers, which have to clip
    ///         anyway; a bound computed here would be a second implementation of that clip, in a type
    ///         that draws nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Used for bounds and never for painting, and the distinction is the whole of the
    ///         refusal this feature replaced.</b> Approximating a rotated element <i>by</i> this box is
    ///         what would draw a 45-point square where a 32-point one was asked for. Using it to decide
    ///         how much surface to allocate, and then painting the real quad into it, is not the same
    ///         thing: the box is a conservative bound on a picture that is drawn exactly.
    ///     </para>
    /// </remarks>
    /// <param name="rectangle">The rectangle, in this transform's own space.</param>
    /// <param name="bounds">The box, or <c>default</c> where there is none.</param>
    /// <returns>Whether every corner is in front of the eye.</returns>
    public bool TryBounds(Rectangle rectangle, out Rectangle bounds) {
        var a = Project(new Vector2(rectangle.X, rectangle.Y));
        var b = Project(new Vector2(rectangle.X + rectangle.Width, rectangle.Y));
        var c = Project(new Vector2(rectangle.X, rectangle.Y + rectangle.Height));
        var d = Project(new Vector2(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height));

        // ⚠ Strictly positive. A corner exactly on the eye plane has no image at all, and the divide
        // below would hand back an infinity that `MathF.Min` propagates into every edge of the box.
        if (a.Z <= 0f || b.Z <= 0f || c.Z <= 0f || d.Z <= 0f) {
            bounds = default;
            return false;
        }

        var (ax, ay) = (a.X / a.Z, a.Y / a.Z);
        var (bx, by) = (b.X / b.Z, b.Y / b.Z);
        var (cx, cy) = (c.X / c.Z, c.Y / c.Z);
        var (dx, dy) = (d.X / d.Z, d.Y / d.Z);

        var left = MathF.Min(MathF.Min(ax, bx), MathF.Min(cx, dx));
        var top = MathF.Min(MathF.Min(ay, by), MathF.Min(cy, dy));
        var right = MathF.Max(MathF.Max(ax, bx), MathF.Max(cx, dx));
        var bottom = MathF.Max(MathF.Max(ay, by), MathF.Max(cy, dy));

        bounds = new Rectangle(left, top, right - left, bottom - top);
        return true;
    }
}
