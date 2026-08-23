// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>The shadow a composited group's <c>filter: drop-shadow()</c> casts from its own alpha.</summary>
/// <param name="Offset">How far the shadow is displaced, in document pixels. Positive y is down.</param>
/// <param name="Blur">The Gaussian standard deviation, in document pixels. As <see cref="UiLayer.Blur" />.</param>
/// <param name="Colour">
///     What the silhouette is painted in, <b>straight</b> rather than premultiplied — the two halves
///     are consumed by different machinery, and <see cref="Tint" /> says which goes where.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>A shadow of the <i>alpha silhouette</i> and not of the box, which is the whole
///         difference between this and <c>box-shadow</c> and the reason the two share no code.</b>
///         <c>DrawListBuilder.EmitShadow</c> emits a rounded rectangle the shape of the border box
///         and lets <c>ui-box.frag</c> resolve its falloff analytically, because a box's silhouette
///         is known in closed form. A <c>drop-shadow</c>'s is not: it is whatever the subtree
///         rasterised to — text, an icon's path, a partly transparent image, a child with its own
///         mask — so it can only be had by blurring the coverage that was actually drawn. Filter
///         Effects 1 § 8.4 defines it that way, and it is why this is a member of
///         <see cref="UiLayer" /> rather than a second <c>DrawCommandKind.Shadow</c>.
///     </para>
///     <para>
///         ⚠ <b>The standard deviation, and it is <i>not</i> the half-extent
///         <see cref="Vixen.Ui.DrawCommand.Thickness" /> carries on a
///         <see cref="Vixen.Ui.DrawCommandKind.Shadow" />.</b> Two conventions for one word live a
///         few types apart: <c>box-shadow</c>'s third length is the total fade distance and the box
///         shader wants half of it, while <c>drop-shadow(x y r)</c> is a Gaussian of σ = r with no
///         halving. This is the second one — the same convention <see cref="UiLayer.Blur" /> uses,
///         and deliberately so, because the two are executed by the same kernel.
///     </para>
///     <para>
///         ⚠ <b>Straight colour, because the two halves are spent in different places and neither
///         wants the product.</b> The RGB becomes <see cref="Tint" />, a colour matrix that replaces
///         whatever the surface holds; the alpha becomes the shadow quad's own vertex alpha, which
///         both executors apply to all four channels of an already-premultiplied sample. Storing the
///         premultiplied colour would make the first half wrong — a shadow at 25% opacity would be
///         painted in a colour a quarter as bright <i>and then</i> faded to a quarter — and the
///         mistake is a shadow that is sixteen times too faint rather than one that is missing, which
///         is the harder of the two to notice.
///     </para>
///     <para>
///         ⚠ <b>It does not commute with the group's own blur and the order is fixed rather than
///         carried.</b> A Gaussian of the shadow of a picture is not the shadow of the Gaussian of it
///         — the alpha channel is blurred twice one way round and once the other — so unlike
///         <see cref="UiLayer.Blur" /> and <see cref="UiLayer.Filter" /> there is no arithmetic here
///         that lets an executor choose. Both executors run this <i>after</i> the group's own blur,
///         over the finished surface, which is the order <c>UtilityComposition.Filter</c> assembles
///         and the order CSS gives for <c>blur(σ) drop-shadow(…)</c>. A stylesheet that writes the
///         two the other way round gets this one; see <c>DrawListBuilder.One</c>, which says so.
///     </para>
///     <para>
///         ⚠ <b>The colour matrix does not commute with the group's <see cref="UiLayer.Filter" />
///         either, and here it does not have to.</b> A <c>grayscale</c> before a
///         <c>drop-shadow</c> greys the element and leaves the shadow the colour it was written in;
///         after it, CSS would grey the shadow too. This runs the shadow's own tint over the
///         silhouette and nothing else, which is the first reading — and it is the only one the
///         fixed order can produce, because <see cref="Tint" /> discards the sampled colour
///         entirely. Anything the group's matrix did to the RGB is multiplied by zero.
///     </para>
/// </remarks>
public readonly record struct UiDropShadow(Vector2 Offset, float Blur, Color4 Colour) {
    /// <summary>Whether this shadow would paint nothing, whatever the silhouette is.</summary>
    /// <remarks>
    ///     ⚠ <b>The identity a <c>filter</c> list needs, and the reason it is spelt as a transparent
    ///     colour rather than as an absent function.</b> <c>UtilityComposition.Filter</c> assembles
    ///     all nine functions into every <c>filter</c> it emits and lets the seven or eight nobody
    ///     wrote resolve to their identities — see that method, which argues why a per-class
    ///     generator cannot do otherwise. Every other function has a number that means "unchanged";
    ///     <c>drop-shadow</c> has no such length, so its identity is a shadow painted in
    ///     <c>transparent</c>. Dropped here rather than drawn, because a transparent shadow costs a
    ///     surface and two passes to composite nothing.
    /// </remarks>
    public bool IsInvisible => Colour.A <= 0f;

    /// <summary>The matrix that turns a group's surface into its silhouette, painted in this colour.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero coefficients and the colour in the offsets, which is the one shape of
    ///         <see cref="UiColorMatrix" /> that reads alpha and writes colour.</b>
    ///         <see cref="UiColorMatrix.Apply" /> evaluates <c>c' = M·c + o·a</c> on premultiplied
    ///         colour, so <c>M = 0</c> leaves <c>c' = o·a</c> — the shadow's colour at exactly the
    ///         coverage the surface had, which <i>is</i> the tinted silhouette. Nothing new had to be
    ///         built for this and nothing about the seven colour functions had to change: the same
    ///         forty-eight bytes of push constant and the same fragment stage draw it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The alpha is deliberately not here.</b> A three-row matrix cannot scale alpha —
    ///         see <see cref="UiColorMatrix" />, which explains why there are three rows and not five
    ///         — so a translucent shadow colour could not be expressed this way at all. It rides the
    ///         quad instead, where <c>UiLayer.Alpha</c> already rides, and the two multiply.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clamping makes this exact rather than approximate for every colour a stylesheet
    ///         can write.</b> <c>Apply</c> clamps to <c>[0, a]</c>, and <c>o·a ≤ a</c> exactly when
    ///         <c>o ≤ 1</c> — which every channel of a parsed CSS colour is. A colour outside the
    ///         unit range would be clamped to the silhouette's own coverage, which is the same answer
    ///         the device's <c>Rgba8UNorm</c> target would give.
    ///     </para>
    /// </remarks>
    public UiColorMatrix Tint =>
        new(
            new Vector4(0f, 0f, 0f, Colour.R),
            new Vector4(0f, 0f, 0f, Colour.G),
            new Vector4(0f, 0f, 0f, Colour.B)
        );
}
