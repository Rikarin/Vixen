// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>What footprint a stamp has.</summary>
public enum BrushShape {
    /// <summary>A disc. Rotation means nothing to it, and it is the default for every tool.</summary>
    Circle,

    /// <summary>
    ///     A square footprint whose weight comes from a mask, so an artist can stamp a shape.
    /// </summary>
    Alpha,

    /// <summary>
    ///     Like <see cref="Alpha" />, but the mask tiles in world space rather than fitting the
    ///     stamp — so a stroke reveals one continuous texture instead of repeating the brush.
    /// </summary>
    Pattern
}

/// <summary>Which way a stamp is turned.</summary>
public enum BrushRotation {
    /// <summary>Always the brush's own angle. Predictable, and visibly repetitive with a mask.</summary>
    Fixed,

    /// <summary>A new angle per stamp, from the stroke's seed.</summary>
    Random,

    /// <summary>Turned to face along the stroke, which is what a road or a scar wants.</summary>
    AlongStroke
}

/// <summary>A single application of a brush: where it landed, and how it was turned.</summary>
/// <param name="Centre">Where it landed, in world XZ.</param>
/// <param name="Rotation">Its angle in radians, counter-clockwise about +Y.</param>
/// <param name="Flow">
///     How much of the brush's strength this stamp carries, 0…1. Below one for the partial stamp a
///     stroke ends on, and for a pressure-sensitive stylus.
/// </param>
public readonly record struct BrushStamp(Vector2 Centre, float Rotation = 0f, float Flow = 1f);

/// <summary>Where a masked brush reads its weights.</summary>
/// <remarks>
///     The seam that keeps this assembly free of an image type. A mask is a function from a unit
///     square to a number; whether that is a texture, a procedural noise or a test's lambda is not
///     something a falloff curve needs to know. See [docs/plan/31 § D12].
/// </remarks>
public interface IBrushMask {
    /// <summary>The mask's value at a point of the unit square.</summary>
    /// <param name="uv">Where, with <c>(0, 0)</c> at one corner and <c>(1, 1)</c> at the other.</param>
    /// <returns>The weight, 0…1. Outside the square, 0.</returns>
    float Sample(Vector2 uv);
}

/// <summary>
///     The one brush every terrain and foliage tool stamps with.
/// </summary>
/// <remarks>
///     <para>
///         <b>One service, three consumers, and that is the whole point.</b> Sculpt strength over a
///         falloff, paint weight over a falloff and foliage density over a falloff are the same
///         function applied to different targets. Unreal implements them three times, so a soft edge
///         sculpted at strength 0.3 and a soft edge painted at strength 0.3 are different shapes.
///         This answers one question — <em>what weight does this stamp have at this world-space
///         sample</em> — and does not know what the answer will be multiplied into. It is
///         [docs/plan/24 § D4]'s argument for <c>SnapContext</c>, applied again.
///     </para>
///     <para>
///         <b>Metres, not texels.</b> A radius is a distance in the world, so the same brush is the
///         same size on a terrain at one metre per quad and on one at four. A brush measured in
///         samples would change size when somebody changed the resolution, which is the setting most
///         likely to change after the art is made.
///     </para>
///     <para>
///         ⚠ <b><see cref="Falloff" /> is the fraction of the radius that falls off, not where the
///         falloff starts.</b> Zero is a hard-edged disc and one falls off from the centre. Reading it
///         the other way round gives a brush that is hardest where it should be softest, and the
///         result still looks like a brush — which is why the direction is stated here and asserted
///         in the tests.
///     </para>
/// </remarks>
public readonly record struct TerrainBrush {
    /// <summary>How far the brush reaches, in metres. Positive.</summary>
    public float Radius { get; init; }

    /// <summary>How hard it presses, 0…1. Scales every weight it returns.</summary>
    public float Strength { get; init; }

    /// <summary>What fraction of the radius is falloff rather than plateau, 0…1.</summary>
    public float Falloff { get; init; }

    /// <summary>Which falloff curve.</summary>
    public BrushFalloffKind Curve { get; init; }

    /// <summary>What footprint it has.</summary>
    public BrushShape Shape { get; init; }

    /// <summary>How far apart stamps are along a stroke, as a fraction of the radius. Positive.</summary>
    /// <remarks>
    ///     A fraction rather than a distance, so changing the radius does not change how dense a
    ///     stroke is. Unity and every paint application spell it the same way.
    /// </remarks>
    public float Spacing { get; init; }

    /// <summary>How the stamps are turned.</summary>
    public BrushRotation Rotation { get; init; }

    /// <summary>The brush's own angle in radians, used by <see cref="BrushRotation.Fixed" />.</summary>
    public float Angle { get; init; }

    /// <summary>
    ///     How many metres of world one tile of a <see cref="BrushShape.Pattern" /> mask covers.
    /// </summary>
    public float PatternScale { get; init; }

    /// <summary>The default: a soft circle a metre across, at half strength.</summary>
    public static TerrainBrush Default =>
        new() {
            Radius = 1f,
            Strength = 0.5f,
            Falloff = 0.5f,
            Curve = BrushFalloffKind.Smooth,
            Shape = BrushShape.Circle,
            Spacing = 0.25f,
            Rotation = BrushRotation.Fixed,
            Angle = 0f,
            PatternScale = 4f
        };

    /// <summary>The weight this brush applies at a world-space sample, for one stamp.</summary>
    /// <param name="sample">Where, in world XZ.</param>
    /// <param name="stamp">Which stamp.</param>
    /// <param name="mask">
    ///     The mask, for <see cref="BrushShape.Alpha" /> and <see cref="BrushShape.Pattern" />. A null
    ///     mask makes those shapes behave as <see cref="BrushShape.Circle" /> rather than throwing,
    ///     because a tool whose mask asset has not finished loading should paint rather than crash.
    /// </param>
    /// <returns>The weight, 0…<see cref="Strength" />.</returns>
    public float WeightAt(Vector2 sample, BrushStamp stamp, IBrushMask? mask = null) {
        if (!(Radius > 0f)) {
            return 0f;
        }

        var offset = sample - stamp.Centre;
        var distance = offset.Length();

        if (distance >= Radius) {
            return 0f;
        }

        var falloff = Math.Clamp(Falloff, 0f, 1f);
        var plateau = Radius * (1f - falloff);

        // A brush with no falloff band has no gradient to evaluate, and dividing by its width would
        // be a division by zero at exactly the setting an artist reaches for to get a hard edge.
        var radial = falloff <= 0f
            ? 1f
            : BrushFalloff.Evaluate(Curve, Math.Clamp((distance - plateau) / (Radius - plateau), 0f, 1f));

        var shaped = Shape switch {
            BrushShape.Alpha when mask is not null => radial * mask.Sample(AlphaUv(offset, stamp.Rotation)),
            BrushShape.Pattern when mask is not null => radial * mask.Sample(PatternUv(sample, stamp.Rotation)),
            _ => radial
        };

        return Math.Clamp(Strength, 0f, 1f) * Math.Clamp(stamp.Flow, 0f, 1f) * shaped;
    }

    /// <summary>Where a sample lands on a stamp-fitted mask.</summary>
    Vector2 AlphaUv(Vector2 offset, float rotation) {
        var local = Rotate(offset, -rotation);
        return new((local.X / (2f * Radius)) + 0.5f, (local.Y / (2f * Radius)) + 0.5f);
    }

    /// <summary>Where a sample lands on a world-tiled mask.</summary>
    /// <remarks>
    ///     The world position rather than the offset, which is the whole difference between a pattern
    ///     and an alpha: a stroke over a pattern reveals one continuous texture, and a stroke over an
    ///     alpha repeats the stamp. Wrapped here rather than by a sampler's address mode, because
    ///     <see cref="IBrushMask" /> is a unit square and does not have one.
    /// </remarks>
    Vector2 PatternUv(Vector2 sample, float rotation) {
        var scale = PatternScale > 0f ? PatternScale : 1f;
        var local = Rotate(sample, -rotation) / scale;
        return new(Wrap(local.X), Wrap(local.Y));
    }

    static float Wrap(float value) {
        var fraction = value - MathF.Floor(value);
        return fraction >= 1f ? 0f : fraction;
    }

    static Vector2 Rotate(Vector2 value, float radians) {
        if (radians == 0f) {
            return value;
        }

        var (sin, cos) = MathF.SinCos(radians);
        return new((value.X * cos) - (value.Y * sin), (value.X * sin) + (value.Y * cos));
    }

    /// <summary>How far apart this brush's stamps are, in metres.</summary>
    public float StampDistance {
        get {
            var spacing = Spacing > 0f ? Spacing : 0.25f;
            return Math.Max(Radius * spacing, 1e-4f);
        }
    }

    /// <summary>Everything a stamp can touch, in world XZ.</summary>
    /// <param name="stamp">The stamp.</param>
    /// <returns>The footprint, which is the region a tool has to mark dirty.</returns>
    /// <remarks>
    ///     A square rather than a disc, and rotation-independent, because it is used to clip a tile
    ///     rectangle and to size an undo record — both of which want the conservative answer. A
    ///     <see cref="BrushShape.Alpha" /> stamp turned 45° reaches <c>√2</c> radii into its corners,
    ///     which this deliberately does not try to be clever about: it would be a bound that is tight
    ///     for one rotation and wrong for the next.
    /// </remarks>
    public BrushFootprint FootprintOf(BrushStamp stamp) {
        var reach = Shape == BrushShape.Circle ? Radius : Radius * MathF.Sqrt(2f);
        return new(stamp.Centre - new Vector2(reach, reach), stamp.Centre + new Vector2(reach, reach));
    }
}

/// <summary>An axis-aligned region of world XZ.</summary>
/// <param name="Minimum">The low corner.</param>
/// <param name="Maximum">The high corner.</param>
[DataContract]
public readonly record struct BrushFootprint(Vector2 Minimum, Vector2 Maximum) {
    /// <summary>The smallest footprint containing both.</summary>
    /// <param name="other">The other.</param>
    /// <returns>The union.</returns>
    public BrushFootprint Union(BrushFootprint other) =>
        new(Vector2.Min(Minimum, other.Minimum), Vector2.Max(Maximum, other.Maximum));

    /// <summary>Whether a point is inside.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Whether it is inside.</returns>
    public bool Contains(Vector2 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X && point.Y >= Minimum.Y && point.Y <= Maximum.Y;
}
