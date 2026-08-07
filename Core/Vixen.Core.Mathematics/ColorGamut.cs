// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>The set of colours a display can actually show.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A gamut is a set of primaries, not a transfer function.</b> These three all share the
///         D65 white point and differ only in where their red, green and blue sit on the chromaticity
///         diagram, so converting between them is one 3×3 matrix on <i>linear</i> values with no
///         chromatic adaptation. Applying one of these to sRGB-encoded numbers produces something
///         that is not a colour in any space — see <see cref="ColorSpace" />.
///     </para>
///     <para>
///         ⚠ <b>Wider is not free.</b> The same eight bits spread across a wider gamut are coarser
///         per step, which is why <see cref="Rec2020" /> and <see cref="DisplayP3" /> band visibly at
///         eight bits where sRGB does not. A wide gamut is worth having only when it is paired with
///         ten-bit or half-float storage, and the swapchain treats that pairing as a rule.
///     </para>
/// </remarks>
public enum ColorGamut {
    /// <summary>Rec. 709 primaries — what an ordinary display shows, and what CSS means by default.</summary>
    Srgb,

    /// <summary>
    ///     Display P3: the DCI-P3 primaries on a D65 white point. Every recent Mac, iPhone and iPad
    ///     panel, and roughly 25% more area than sRGB.
    /// </summary>
    DisplayP3,

    /// <summary>ITU-R BT.2020 — the ultra-high-definition television gamut, wider than any current consumer panel fully covers.</summary>
    Rec2020
}
