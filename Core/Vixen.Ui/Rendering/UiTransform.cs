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
///         ⚠ <b>Six floats and not a <see cref="Matrix3x3" />, because the third row of a 2D affine is
///         always <c>0 0 1</c> and this is carried on a <see cref="UiLayer" /> that both executors
///         compare by value.</b> Storing the constant row costs twelve bytes a group and gives a
///         consumer three numbers it must not read; worse, it admits a projective matrix the rest of
///         this design cannot honour. A perspective divide is exactly what the composite quad's
///         texture coordinates <i>cannot</i> survive — see <see cref="Apply" /> — so a type that could
///         not express one is the type that says so.
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
    /// <summary>The transform that moves nothing.</summary>
    public static UiTransform Identity => new(1f, 0f, 0f, 1f, 0f, 0f);

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
        && MathF.Abs(Dy) < 1e-3f;

    /// <summary>This transform's determinant, which is the area it multiplies by.</summary>
    /// <remarks>
    ///     Zero means the transform is degenerate — <c>scale-0</c>, or a scale with one axis at zero —
    ///     and there is no inverse. Both consumers check it rather than dividing and propagating an
    ///     infinity into a vertex position.
    /// </remarks>
    public float Determinant => (M11 * M22) - (M12 * M21);

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
            (M11 * then.M11) + (M12 * then.M21),
            (M11 * then.M12) + (M12 * then.M22),
            (M21 * then.M11) + (M22 * then.M21),
            (M21 * then.M12) + (M22 * then.M22),
            (Dx * then.M11) + (Dy * then.M21) + then.Dx,
            (Dx * then.M12) + (Dy * then.M22) + then.Dy
        );

    /// <summary>Where a point lands.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole of what the renderer does with a transform, and the reason the
    ///     feature costs no shader.</b> A composited group's quad is four vertices whose texture
    ///     coordinates name the surface the group was rasterised into; moving those four positions and
    ///     leaving the coordinates alone <i>is</i> the transform. Both executors interpolate the
    ///     coordinate across the two triangles linearly, and an affine map is exactly the class of map
    ///     for which that interpolation is not an approximation — which is also why
    ///     <c>perspective</c> is not in this type.
    /// </remarks>
    public Vector2 Apply(Vector2 point) =>
        new((M11 * point.X) + (M21 * point.Y) + Dx, (M12 * point.X) + (M22 * point.Y) + Dy);

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
        var m11 = M22 * inverse;
        var m12 = -M12 * inverse;
        var m21 = -M21 * inverse;
        var m22 = M11 * inverse;

        return new UiTransform(m11, m12, m21, m22, -((Dx * m11) + (Dy * m21)), -((Dx * m12) + (Dy * m22)));
    }

    /// <summary>The axis-aligned box that contains <paramref name="rectangle" /> once transformed.</summary>
    /// <remarks>
    ///     ⚠ <b>Used for bounds and never for painting, and the distinction is the whole of the
    ///     refusal this feature replaced.</b> Approximating a rotated element <i>by</i> this box is
    ///     what would draw a 45-point square where a 32-point one was asked for. Using it to decide
    ///     how much surface to allocate, and then painting the real quad into it, is not the same
    ///     thing: the box is a conservative bound on a picture that is drawn exactly.
    /// </remarks>
    public Rectangle Bounds(Rectangle rectangle) {
        var a = Apply(new Vector2(rectangle.X, rectangle.Y));
        var b = Apply(new Vector2(rectangle.X + rectangle.Width, rectangle.Y));
        var c = Apply(new Vector2(rectangle.X, rectangle.Y + rectangle.Height));
        var d = Apply(new Vector2(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height));

        var left = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
        var top = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
        var right = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        var bottom = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));

        return new Rectangle(left, top, right - left, bottom - top);
    }
}
