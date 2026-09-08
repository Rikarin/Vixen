// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>A single application of a paint brush, after jitter has been applied to it.</summary>
/// <param name="Centre">Where it lands, in texels of the atlas.</param>
/// <param name="Rotation">Its angle in radians.</param>
/// <param name="Radius">Its radius in texels — per stamp, because size jitter varies it.</param>
/// <param name="Flow">How much this stamp deposits, 0…1.</param>
/// <param name="Aspect">
///     How much longer the ellipse's long axis is than its short one. One — and zero, see the remarks
///     — is a disc.
/// </param>
/// <param name="AspectAngle">Which way that long axis points in the atlas, in radians.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The radius is on the stamp and not only on the brush.</b> Size jitter is the one
///         brush setting that changes the footprint, so a stamp that carried only a centre and an
///         angle would make the footprint a function of the brush rather than of the stamp — and the
///         undo record, the dirty rectangle and the dilation are all sized from the footprint.
///     </para>
///     <para>
///         ⚠ <b>The shape is on the stamp for exactly that reason too</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>. Everything sized from a
///         footprint has to be sized from the same ellipse the weight function evaluates, and a shape
///         read off the brush at one of those three places and off the stamp at another is how a
///         stamp writes outside the rectangle recorded for it.
///     </para>
///     <para>
///         ⚠ <b>Zero is a disc and not a degenerate ellipse, because <c>default</c> exists.</b> This
///         is a struct: <c>default(PaintStamp)</c> and every <c>new(centre, angle, radius, flow)</c>
///         written before this parameter did leave it zero, and an aspect of zero read literally is
///         an ellipse with no width — a brush that paints nothing. <see cref="Stretch" /> is what
///         every consumer reads, and it is what makes the old shape mean the old thing.
///     </para>
/// </remarks>
readonly record struct PaintStamp(
    Vector2 Centre,
    float Rotation,
    float Radius,
    float Flow,
    float Aspect = 1f,
    float AspectAngle = 0f
) {
    /// <summary>The aspect as the kernel may use it: at least one, finite, and capped.</summary>
    /// <remarks>
    ///     ⚠ <b>Capped at <see cref="PaintBrush.MaximumAspect" /> because the cost is linear in it
    ///     while the painted area is not.</b> The footprint rectangle is conservative and
    ///     rotation-independent, so its half-extent is the long semi-axis in <em>both</em> directions
    ///     — an aspect of <i>n</i> is <i>n</i> times the texels scanned for the same amount of paint.
    ///     A chart whose Jacobian is nearly singular reports an anisotropy in the thousands, and an
    ///     uncapped stamp there stops being local, which is the one property doc 48's exit criterion
    ///     8 is about. Past the cap the stamp is wrong in the old way, on charts that are already
    ///     unusable; below it, it is right.
    /// </remarks>
    public float Stretch =>
        Aspect > 1f && float.IsFinite(Aspect) ? MathF.Min(Aspect, PaintBrush.MaximumAspect) : 1f;
}

/// <summary>
///     The brush a texture-paint stroke stamps with: doc 31's brush, in texels instead of metres.
/// </summary>
/// <remarks>
///     <para>
///         <b>⚠ The primitive was not rebuilt, and deciding that was the first question of doc 48
///         § M9.</b> Three homes were on the table — a new type here, a new <c>Core</c> assembly, or
///         <c>Core/Vixen.Terrain</c> beside the terrain one — and the answer is that the arithmetic
///         already has a home and it is not terrain-shaped. <c>TerrainBrush</c>'s own remarks say
///         "one service, three consumers … it does not know what the answer will be multiplied
///         into", and <c>Vixen.Terrain</c>'s package description calls it "the one brush every
///         sculpt, paint and foliage tool stamps with". Nothing in <c>WeightAt</c>, in
///         <see cref="BrushFalloff" /> or in <c>BrushStroke</c> reads a metre: they read a
///         <c>Vector2</c>, a radius and a spacing fraction. Feeding them texels gives a paint brush
///         whose falloff cannot drift from the sculpt brush's, because there is one implementation
///         and not two.
///     </para>
///     <para>
///         ⚠ <b>So the only thing this type adds is what a paint brush has and a terrain brush does
///         not.</b> Two things, and they are the two doc 48 § M9's scope line names beside the
///         shared five:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Flow against opacity.</b> A terrain brush has one <c>Strength</c>. A paint brush
///             has <see cref="Flow" />, which is how much one stamp deposits, and
///             <see cref="Opacity" />, which is the most the <em>whole stroke</em> may reach however
///             many stamps overlap. Collapsing them is the classic defect where a slow drag paints
///             darker than a fast one over the same ground. <c>PaintStroke</c> is where the cap
///             lives, because it is a property of the stroke and not of a stamp.
///         </item>
///         <item>
///             <b>Jitter.</b> Position, angle and size, per stamp, from a hash of the stamp's index —
///             the same determinism <c>BrushStroke</c> already gives its random rotation, and for the
///             same reason: a stroke whose randomness came from a shared generator could not be
///             replayed, so undo-then-redo would paint something else.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Jitter is applied outside the kernel and that is deliberate.</b> Doc 48 § D13 says
///         symmetry, curve strokes and smoothing are stroke-level and none of them touches the
///         kernel; jitter is the fourth of exactly that kind. It moves and resizes the stamp before
///         the weight function ever sees it, so the weight function stays the one terrain uses.
///     </para>
/// </remarks>
readonly record struct PaintBrush {
    /// <summary>The most anisotropy a stamp is stretched by before it is capped.</summary>
    /// <remarks>
    ///     See <see cref="PaintStamp.Stretch" /> for why there is a cap at all. Sixteen is a chart
    ///     stretched sixteen to one, which is past anything an ordinary unwrap produces and well past
    ///     where an artist can place a stroke accurately in the 2D view either.
    /// </remarks>
    public const float MaximumAspect = 16f;

    /// <summary>How far the brush reaches, in <b>texels of the atlas</b>. Positive.</summary>
    /// <remarks>
    ///     ⚠ <b>Texels, not a fraction of the atlas and not millimetres of surface.</b> A brush
    ///     measured in UV would change size when the texture set's resolution changed, which is the
    ///     setting most likely to change after the art is made — the mirror image of the argument
    ///     <c>TerrainBrush</c> makes for metres over samples. A 3D surface converts a screen-space
    ///     radius into texels through the hit's texel density; that conversion is the surface's job.
    /// </remarks>
    public float Radius { get; init; }

    /// <summary>How much longer the stamp is along one atlas direction than across it. One is a disc.</summary>
    /// <remarks>
    ///     ⚠ <b>Written by the 3D surface at pointer-down and by nothing else</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>. It is not a brush setting
    ///     an artist authors: it is what the hit triangle's layout does to a disc, measured by
    ///     <c>PaintFootprint.Ellipse</c>. The 2D view leaves it at one, and correctly — there a
    ///     radius is already in texels and there is nothing between the pointer and the atlas to
    ///     stretch it.
    /// </remarks>
    public float Aspect { get; init; }

    /// <summary>Which way <see cref="Aspect" /> stretches the stamp, in radians in the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Angle" />, which turns the alpha mask.</b> The two are
    ///     different rotations of different things: the mask turns with the stroke or with the brush,
    ///     and this turns with the chart under the pointer. Folding them into one number would make a
    ///     brush whose stamp rotation changed as the artist crossed a chart boundary.
    /// </remarks>
    public float AspectAngle { get; init; }

    /// <summary>How much one stamp deposits, 0…1.</summary>
    public float Flow { get; init; }

    /// <summary>The most the whole stroke may reach on any one texel, 0…1.</summary>
    public float Opacity { get; init; }

    /// <summary>How far apart stamps are, as a fraction of the radius. Positive.</summary>
    public float Spacing { get; init; }

    /// <summary>What fraction of the radius is falloff rather than plateau, 0…1.</summary>
    public float Falloff { get; init; }

    /// <summary>Which falloff curve. <c>Vixen.Terrain</c>'s, unwrapped and unrepeated.</summary>
    public BrushFalloffKind Curve { get; init; }

    /// <summary>How the stamps are turned.</summary>
    public BrushRotation Rotation { get; init; }

    /// <summary>The brush's own angle in radians, for <see cref="BrushRotation.Fixed" />.</summary>
    public float Angle { get; init; }

    /// <summary>How far a stamp may wander off the path, as a fraction of the radius. 0…1.</summary>
    public float PositionJitter { get; init; }

    /// <summary>How far a stamp's angle may turn, in radians, either way.</summary>
    public float AngleJitter { get; init; }

    /// <summary>How much a stamp's radius may shrink, as a fraction. 0…1.</summary>
    /// <remarks>
    ///     ⚠ <b>Shrink only, never grow.</b> A jitter that could enlarge the stamp would make the
    ///     footprint — and therefore the undo record and the dilation margin — depend on a random
    ///     number, so a conservative bound would have to be <c>(1 + jitter)</c> everywhere or a stamp
    ///     would occasionally write outside the rectangle recorded for it. One-sided keeps
    ///     <see cref="Radius" /> the true upper bound, which is what every consumer already assumes.
    /// </remarks>
    public float SizeJitter { get; init; }

    /// <summary>
    ///     The brush alpha, or <see langword="null" /> for a plain disc.
    /// </summary>
    /// <remarks>
    ///     <c>IBrushMask</c> is <c>Vixen.Terrain</c>'s seam for exactly this — "a mask is a function
    ///     from a unit square to a number; whether that is a texture, a procedural noise or a test's
    ///     lambda is not something a falloff curve needs to know". A paint brush's alpha is the same
    ///     function, so it is the same interface.
    /// </remarks>
    public IBrushMask? Alpha { get; init; }

    /// <summary>A soft round brush thirty-two texels across, depositing a quarter per stamp.</summary>
    public static PaintBrush Default =>
        new() {
            Radius = 32f,
            Aspect = 1f,
            AspectAngle = 0f,
            Flow = 0.25f,
            Opacity = 1f,
            Spacing = 0.15f,
            Falloff = 0.5f,
            Curve = BrushFalloffKind.Smooth,
            Rotation = BrushRotation.Fixed,
            Angle = 0f,
            PositionJitter = 0f,
            AngleJitter = 0f,
            SizeJitter = 0f,
            Alpha = null
        };

    /// <summary>The terrain brush this one evaluates through, for one stamp.</summary>
    /// <param name="stamp">The stamp, whose radius may have been jittered.</param>
    /// <returns>The kernel.</returns>
    /// <remarks>
    ///     ⚠ <b>Built once per stamp by the caller and never inside the texel loop.</b> It is a
    ///     struct with ten members and the loop runs over the whole footprint; constructing it per
    ///     texel is the difference between a stamp that costs its footprint and one that costs its
    ///     footprint times a constructor.
    ///     <para>
    ///         <see cref="Flow" /> is on the stamp rather than in <c>Strength</c> so that jitter and
    ///         a stylus can both scale it without rebuilding the brush; <c>Strength</c> is therefore
    ///         one, which is its neutral.
    ///     </para>
    /// </remarks>
    public TerrainBrush KernelFor(PaintStamp stamp) =>
        new() {
            Radius = stamp.Radius,
            Strength = 1f,
            Falloff = Falloff,
            Curve = Curve,
            Shape = Alpha is null ? BrushShape.Circle : BrushShape.Alpha,
            Spacing = Spacing > 0f ? Spacing : 0.15f,
            Rotation = Rotation,
            Angle = Angle,
            PatternScale = 1f
        };

    /// <summary>The brush a stroke's spacing and rotation come from, before any jitter.</summary>
    public TerrainBrush Kernel => KernelFor(new(Vector2.Zero, Angle, Radius, Flow));

    /// <summary>The weight this brush deposits at a texel centre, for one stamp.</summary>
    /// <param name="texel">The texel centre, in texels.</param>
    /// <param name="stamp">The stamp.</param>
    /// <returns>The weight, 0…<see cref="Flow" />.</returns>
    /// <remarks>The convenience form. A loop should hoist <see cref="KernelFor" /> instead.</remarks>
    public float WeightAt(Vector2 texel, PaintStamp stamp) => Weight(KernelFor(stamp), texel, stamp, Alpha);

    /// <summary>The weight a hoisted kernel deposits at a texel centre.</summary>
    /// <param name="kernel">The kernel, from <see cref="KernelFor" />.</param>
    /// <param name="texel">The texel centre, in texels.</param>
    /// <param name="stamp">The stamp the kernel was built for.</param>
    /// <param name="alpha">The brush alpha, or <see langword="null" />.</param>
    /// <returns>The weight.</returns>
    public static float Weight(in TerrainBrush kernel, Vector2 texel, PaintStamp stamp, IBrushMask? alpha) =>
        kernel.WeightAt(Circularised(texel, stamp), new(stamp.Centre, stamp.Rotation, stamp.Flow), alpha);

    /// <summary>Where a texel is, in the space the stamp's ellipse is a disc in.</summary>
    /// <param name="texel">The texel centre, in texels of the atlas.</param>
    /// <param name="stamp">The stamp, whose <see cref="PaintStamp.Stretch" /> is the ellipse.</param>
    /// <returns>The remapped position, which is the texel itself for a round stamp.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The transform goes in the caller's offset and never into the kernel</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>, and this type's own
    ///         remarks say why at length: <c>TerrainBrush.WeightAt</c> is the one falloff four
    ///         consumers share, and a second one here is the defect this workstream keeps finding.
    ///         Squeezing the offset before the kernel sees it costs the kernel nothing and makes the
    ///         ellipse exact rather than approximated by a curve of its own.
    ///     </para>
    ///     <para>
    ///         The two semi-axes are <c>r√a</c> and <c>r/√a</c>, so their product is <c>r²</c>: the
    ///         ellipse has the <em>same area</em> as the disc it replaces, whatever the aspect. That
    ///         is what keeps <see cref="Radius" /> meaning what <c>PaintDensity.Area</c> measured,
    ///         and it is why the stroke's spacing, flow and opacity need no compensating change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mask and the falloff are evaluated in this space rather than in the
    ///         atlas.</b> An alpha stamped on a chart stretched four to one is stretched with it,
    ///         which is what an artist means by painting a shape onto a surface — a mask kept round
    ///         in the atlas would be round in a place nobody looks at and wrong on the model.
    ///     </para>
    /// </remarks>
    public static Vector2 Circularised(Vector2 texel, PaintStamp stamp) {
        var stretch = stamp.Stretch;

        if (stretch <= 1f) {
            return texel;
        }

        var half = MathF.Sqrt(stretch);
        var (sin, cos) = MathF.SinCos(stamp.AspectAngle);
        var offset = texel - stamp.Centre;

        // Along the long axis and across it, each divided by its own semi-axis in units of the
        // radius — which is the ellipse's equation, written as a point rather than as a test.
        var along = ((offset.X * cos) + (offset.Y * sin)) / half;
        var across = ((offset.Y * cos) - (offset.X * sin)) * half;

        return stamp.Centre + new Vector2((along * cos) - (across * sin), (along * sin) + (across * cos));
    }

    /// <summary>Everything a stamp can touch, as whole texels.</summary>
    /// <param name="stamp">The stamp.</param>
    /// <param name="width">The atlas width, for clipping.</param>
    /// <param name="height">The atlas height, for clipping.</param>
    /// <returns>The rectangle, clipped to the atlas.</returns>
    /// <remarks>
    ///     <para>
    ///         Conservative and rotation-independent, for <c>TerrainBrush.FootprintOf</c>'s reason:
    ///         it sizes an undo record and clips a dirty rectangle, and both want the answer that is
    ///         never too small.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So an elliptical stamp is bounded by its <em>long</em> semi-axis in both
    ///         directions</b> — <c>r√a</c> — and not by the ellipse's own axis-aligned box. The tight
    ///         box would be right for the falloff and wrong for the alpha, whose square in the
    ///         circularised space is a parallelogram in the atlas; and a footprint that was ever one
    ///         texel too small is a stamp writing outside the rectangle its undo record was sized
    ///         from. ⚠ It is also what makes the cost linear in the aspect, which is the whole reason
    ///         <see cref="PaintStamp.Stretch" /> has a cap.
    ///     </para>
    /// </remarks>
    public PaintRect FootprintOf(PaintStamp stamp, int width, int height) {
        var reach = stamp with { Radius = stamp.Radius * MathF.Sqrt(stamp.Stretch) };
        var footprint = KernelFor(reach).FootprintOf(new(stamp.Centre, stamp.Rotation, stamp.Flow));

        return PaintRect.Covering(footprint.Minimum, footprint.Maximum).Clip(width, height);
    }
}
