// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     How a varying is interpolated across a primitive, written as
///     <c>[Interpolation("noperspective")]</c> on the declaration that carries it.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The author's half of a decision the compiler could only make for integers.</strong>
///         <c>Reflection.StageInterface.MustBeFlat</c> answers it for an <c>int</c> varying, where
///         there is exactly one legal answer and no reason to make anybody write it. For a float
///         there are four answers and the shader is the only thing that knows which one it means: a
///         screen-space value wants <c>noperspective</c>, a per-primitive value riding the provoking
///         vertex wants <c>flat</c>, and a value sampled at the covered centroid rather than the
///         pixel centre wants <c>centroid</c> — which is the difference between a correct edge texel
///         and one extrapolated outside the triangle at multisample rates.
///     </para>
///     <para>
///         One attribute with one word rather than four marker attributes, because the modes are
///         mutually exclusive: two markers on one declaration would be a state nothing can emit, and
///         the shape of the syntax is what stops it being writable at all.
///     </para>
///     <para>
///         ⚠ <c>Centroid</c> is a <em>sampling</em> qualifier in both targets rather than a third
///         interpolation function, so it means "smooth, sampled at the centroid". Raven spells it as
///         a mode anyway: the alternative is a second attribute that combines with this one, and a
///         combination is only worth its cost when somebody wants <c>noperspective centroid</c>,
///         which nothing in the library does.
///     </para>
/// </remarks>
public enum InterpolationMode {
    /// <summary>
    ///     Perspective-correct interpolation — what a varying does when nothing says otherwise, and
    ///     therefore the value a declaration with no attribute has.
    /// </summary>
    Smooth = 0,

    /// <summary>
    ///     No interpolation: every fragment reads the provoking vertex's value.
    /// </summary>
    /// <remarks>
    ///     The only legal mode for an integer varying, which is why <c>StageInterface.MustBeFlat</c>
    ///     applies it without being asked. Writing it on a float is a different statement — that the
    ///     value is per-primitive rather than per-vertex — and is the reason this mode is spellable.
    /// </remarks>
    Flat = 1,

    /// <summary>
    ///     Linear interpolation in screen space, without the perspective divide.
    /// </summary>
    NoPerspective = 2,

    /// <summary>
    ///     Perspective-correct, sampled at the centroid of the covered area rather than at the
    ///     pixel centre.
    /// </summary>
    Centroid = 3
}
