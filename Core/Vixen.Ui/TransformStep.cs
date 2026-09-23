// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>Which primitive a <see cref="TransformStep" /> is.</summary>
/// <remarks>
///     ⚠ <b><see cref="RotateX" /> and <see cref="RotateY" /> are kinds of their own rather than a
///     <see cref="Rotate3d" /> with a fixed axis, and the reason is bits rather than meaning.</b> The
///     two have closed-form matrices that the Rodrigues formula reproduces only to the last bit — its
///     <c>(1 − cos) + cos</c> on the axis cell is not exactly one — and every committed screenshot was
///     rendered through the closed forms. They interpolate as <c>rotate3d()</c> does, which is what
///     CSS calls their primitive.
/// </remarks>
enum TransformStepKind : byte {
    /// <summary><c>translate()</c>, its three axis forms and <c>translate3d()</c>.</summary>
    Translate,

    /// <summary><c>scale()</c>, its three axis forms and <c>scale3d()</c>.</summary>
    Scale,

    /// <summary><c>rotate()</c> and <c>rotateZ()</c> — a turn in the plane.</summary>
    Rotate,

    /// <summary><c>rotateX()</c>.</summary>
    RotateX,

    /// <summary><c>rotateY()</c>.</summary>
    RotateY,

    /// <summary><c>rotate3d()</c>, its axis already normalised.</summary>
    Rotate3d,

    /// <summary><c>skew()</c> and its two axis forms.</summary>
    Skew,

    /// <summary><c>perspective()</c>; a distance of <see cref="float.PositiveInfinity" /> is its identity.</summary>
    Perspective,

    /// <summary><c>matrix()</c> and <c>matrix3d()</c>, or the result of interpolating two matrices.</summary>
    Matrix
}

/// <summary>One <c>&lt;transform-function&gt;</c>, resolved to numbers but not yet to a matrix.</summary>
/// <param name="Kind">Which primitive.</param>
/// <param name="X">A translation's or a scale's x, a rotation axis's x, a skew's x angle, or a perspective's distance.</param>
/// <param name="Y">The y of the same.</param>
/// <param name="Z">The z of the same.</param>
/// <param name="Angle">A rotation's angle, in degrees.</param>
/// <param name="Spatial">
///     Whether the function was one of the three-dimensional spellings. It selects
///     <c>TransformReader</c>'s four-dimensional branch, and it is the author's choice rather than a
///     test on the numbers — see that reader's <c>Function</c>.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>The step exists because a transition cannot interpolate a matrix one function at a
///         time and still be CSS.</b> <c>rotate(0deg)</c> to <c>rotate(360deg)</c> is a full turn;
///         the two matrices are both the identity, and any interpolation of the matrices moves
///         nothing. So CSS Transforms 2 interpolates the <i>arguments</i> of paired functions, and
///         only falls back to matrices where a pair shares no primitive — which means a transform has
///         to exist, for a moment, as numbers with a name. #174.
///     </para>
///     <para>
///         Lengths are already resolved to points and angles to degrees, against the element's own
///         box and <see cref="LengthContext" /> — which is what lets <c>translateX(2em)</c>
///         interpolate against <c>translateX(40px)</c> at all, where the styling assembly, holding
///         specified values, can only compare units.
///     </para>
/// </remarks>
readonly record struct TransformStep(
    TransformStepKind Kind,
    float X,
    float Y,
    float Z,
    float Angle,
    bool Spatial
) {
    /// <summary>The cells, for <see cref="TransformStepKind.Matrix" /> only.</summary>
    public Matrix4x4 Cells { get; init; }
}
