// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Which family of gradient a box is filled with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A discriminator rather than three axis conventions, because radial has no axis.</b>
///         The two-stop implementation used a zero <see cref="BoxStyle.GradientAxis" /> as its
///         "no gradient" sentinel, which works exactly as long as every gradient has a direction. A
///         radial one does not — its parameter is a distance from the centre — so a radial fill would
///         have had to write a meaningless non-zero axis to avoid being erased, and the first person
///         to read that axis would have believed it.
///     </para>
///     <para>
///         The numbers are the shader's: they are written into <c>UiShape.Size.w</c>, whose previous
///         life was a zero-or-one gradient flag. <see cref="None" /> is that flag's zero and
///         <see cref="Linear" /> is its one, on purpose, so growing the record moved no picture.
///     </para>
/// </remarks>
public enum GradientShape : byte {
    /// <summary>No gradient. The box is filled with its own colour.</summary>
    None,

    /// <summary>CSS's <c>linear-gradient()</c>: the ramp runs along an axis.</summary>
    Linear,

    /// <summary>CSS's <c>radial-gradient()</c>: an ellipse from the centre out.</summary>
    Radial,

    /// <summary>CSS's <c>conic-gradient()</c>: the ramp sweeps around the centre.</summary>
    Conic
}

/// <summary>Which space a gradient's stops are interpolated in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three answers coexisted before this enum existed, and none of them was wrong on its
///         own terms.</b> Vixen paints in linear RGB and lerped there; CSS's default for a gradient
///         with no <c>in &lt;space&gt;</c> hint is sRGB; and Tailwind v4 emits <c>in oklab</c> on
///         every gradient it generates. Picking one and calling it "the" answer would have made two of
///         the three sources of gradients in this engine draw something their author did not write, so
///         the space travels with the gradient instead.
///     </para>
///     <para>
///         ⚠ <b><see cref="Linear" /> is zero because it is what the shader already did.</b> A
///         gradient built through <see cref="BoxStyle.Vertical" /> — the engine's own programmatic
///         API, which has no CSS text and therefore no hint to honour — keeps interpolating exactly
///         where it did, so growing <c>UiShape</c> moved no committed picture. Only the CSS path
///         chooses, and it chooses by reading what the author wrote.
///     </para>
///     <para>
///         ⚠ <b>The choice is most visible at the midpoint, which is the only place it matters.</b>
///         Two complements — the palette's blue and its amber, say — meet at a desaturated grey in
///         linear RGB, at a muddier and darker grey in sRGB, and at a colour that still reads as a
///         colour in Oklab. Since the engine's default palette now ships as Tailwind v4.3.3's, quoted
///         <c>oklch</c>, interpolating two of its swatches in linear RGB throws away the uniformity
///         they were chosen for at precisely the pixel where it shows.
///     </para>
/// </remarks>
public enum GradientSpace : byte {
    /// <summary>Linear RGB, componentwise. What the engine paints in and what it lerped in before.</summary>
    Linear,

    /// <summary>sRGB, componentwise. CSS's default when a gradient carries no interpolation hint.</summary>
    Srgb,

    /// <summary>Oklab. Perceptually uniform, and what <c>in oklab</c> asks for.</summary>
    Oklab
}

/// <summary>Where a gradient's three stops sit along its ramp, as fractions.</summary>
/// <param name="From">The first stop. Zero at the near end.</param>
/// <param name="Via">The middle stop, read only when there is one.</param>
/// <param name="To">The last stop. One at the far end.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Positions, not colours, and that is a whole capability rather than a refinement.</b>
///         The shader's parameter runs zero to one across the box, so <c>from-10% to-40%</c> cannot be
///         honoured by moving a colour: the ramp has to be remapped before the interpolation, and
///         everything outside <c>[From, To]</c> is the flat end colour. Painting the stops at the ends
///         instead puts the transition in the wrong place over the whole box.
///     </para>
///     <para>
///         ⚠ <b>The all-zero value means <see cref="Default" />, and that is the same argument
///         <see cref="BoxStyle.GradientAxis" /> makes for its own zero.</b> Three stops all at zero
///         put every pixel at or past the last one, which is a flat fill of the end colour — not a
///         gradient anybody can mean, so the value is free to carry "nobody said". That is what lets
///         <c>default(BoxStyle)</c> and a hand-written <see cref="BoxStyle.Vertical" /> describe the
///         natural 0 / 50% / 100% ramp without either of them having to spell it.
///     </para>
/// </remarks>
public readonly record struct GradientStops(float From, float Via, float To) {
    /// <summary>The natural ramp: the ends, and the middle halfway between them.</summary>
    public static GradientStops Default => new(0f, 0.5f, 1f);

    /// <summary>This ramp, with the all-zero value read as <see cref="Default" />.</summary>
    /// <returns>This, or <see cref="Default" /> when nobody said.</returns>
    /// <remarks>
    ///     ⚠ <b>A method and not a property, and the reason is a stack overflow rather than taste.</b>
    ///     A record's generated <c>PrintMembers</c> walks its public instance <i>properties</i>, so an
    ///     instance property whose type is the record's own type makes <c>ToString</c> call itself
    ///     forever. Nothing calls <c>ToString</c> on this in the frame path, which is exactly why it
    ///     took a test framework formatting a failure message to find it — and why the shape of the
    ///     member is worth pinning here rather than rediscovering.
    /// </remarks>
    public GradientStops OrNatural() => this == default ? Default : this;
}
