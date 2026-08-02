// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>What the ground is at a candidate position.</summary>
/// <param name="Position">Where the surface is, in world space.</param>
/// <param name="Normal">Which way it faces.</param>
/// <param name="Weight">
///     How much of the type's filtered layer is painted there, 0…1, or 1 where nothing filters.
/// </param>
/// <param name="Hit">Whether there was a surface at all.</param>
/// <remarks>
///     ⚠ <b>A record rather than an interface call per field, because a probe is a raycast.</b>
///     Asking separately for the height, then the normal, then the weight would be three casts down
///     the same ray, and the middle one is the expensive part.
/// </remarks>
public readonly record struct FoliageSurface(Vector3 Position, Vector3 Normal, float Weight, bool Hit) {
    /// <summary>Nothing under the ray.</summary>
    public static FoliageSurface Missed => new(default, Vector3.UnitY, 0f, false);

    /// <summary>The slope, in radians from flat.</summary>
    public float Slope {
        get {
            var up = Vector3.Dot(Vector3.Normalize(Normal), Vector3.UnitY);

            return MathF.Acos(Math.Clamp(up, -1f, 1f));
        }
    }
}

/// <summary>Where a scatter asks what the ground is.</summary>
/// <remarks>
///     An interface for <c>ISurfaceProbe</c>'s reason: the scatter is testable with no scene, no
///     terrain and no physics world, and a project can answer with whatever it has. It is also what
///     makes [§ The foliage tools]'s filters possible without foliage-specific code — a probe that
///     answers for blockout meshes makes painting onto a wall work on the day they are probeable.
/// </remarks>
public interface IFoliageSurface {
    /// <summary>What the ground is under a world XZ position.</summary>
    /// <param name="position">Where, in world XZ.</param>
    /// <param name="layer">The layer the type filters on, or empty for none.</param>
    /// <returns>The surface, or <see cref="FoliageSurface.Missed" />.</returns>
    FoliageSurface SampleAt(Vector2 position, string layer);
}

/// <summary>
///     Turning a brush stamp into instances, and the six rules that refuse one.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § The palette]'s placement rules, in one pass.</b> A candidate is
///         generated, the surface under it is sampled once, and then: is there ground, is the slope in
///         range, is the altitude in range, is the filtered layer painted enough, and is there already
///         an instance within the type's spacing. What survives is turned by the alignment and the
///         random ranges.
///     </para>
///     <para>
///         ⚠ <b>Deterministic from the stamp and the candidate index, never from an iteration
///         order</b> — [§ D8]'s requirement, and it is what makes an undone-and-redone stroke produce
///         the same forest. A counter-based identity would also make the CPU reference and the GPU
///         scatter impossible to compare, which is the seam test the grass phase needs.
///     </para>
///     <para>
///         ⚠ <b>The spacing check is against what is already placed, including this stamp's own
///         earlier candidates.</b> Checking only the volume would let one stamp drop forty trees on
///         one spot, because none of them was there when the others were tested.
///     </para>
/// </remarks>
public static class FoliageScatter {
    /// <summary>Why a candidate was refused.</summary>
    public enum Refusal {
        /// <summary>It was not.</summary>
        Placed,

        /// <summary>There was no ground under it.</summary>
        NoSurface,

        /// <summary>The ground was too steep or too flat.</summary>
        Slope,

        /// <summary>It was too high or too low.</summary>
        Altitude,

        /// <summary>The layer the type filters on is not painted enough there.</summary>
        Layer,

        /// <summary>Something of this type is already within the spacing radius.</summary>
        Spacing,

        /// <summary>The brush's own falloff did not reach it.</summary>
        Brush
    }

    /// <summary>What one stamp of a type produced.</summary>
    /// <param name="Placed">How many instances were added.</param>
    /// <param name="Considered">How many candidates were generated.</param>
    /// <remarks>
    ///     The counts a tool reports and a test asserts. A stroke that places nothing over forty
    ///     candidates is a filter doing its job or a filter set wrong, and the difference is visible
    ///     only if somebody counted.
    /// </remarks>
    public readonly record struct Result(int Placed, int Considered);

    /// <summary>Scatters one stamp of one type into a volume.</summary>
    /// <param name="volume">Where the instances go.</param>
    /// <param name="type">Which palette entry.</param>
    /// <param name="surface">What answers "what is the ground here".</param>
    /// <param name="centre">Where the stamp landed, in world XZ.</param>
    /// <param name="radius">How far it reaches, in metres.</param>
    /// <param name="strength">How much of the type's density it places, 0…1.</param>
    /// <param name="seed">What the candidate positions derive from.</param>
    /// <param name="placed">Appended the address of everything added, for an undo record.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such type.</exception>
    public static Result Stamp(
        FoliageVolume volume,
        int type,
        IFoliageSurface surface,
        Vector2 centre,
        float radius,
        float strength = 1f,
        uint seed = 0x9E3779B9u,
        ICollection<FoliageAddress>? placed = null
    ) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, volume.Palette.Count);

        var settings = volume.Palette[type];
        var candidates = (int)MathF.Round(settings.CandidatesFor(radius) * Math.Clamp(strength, 0f, 1f));

        if (candidates <= 0 || !(radius > 0f)) {
            return new(0, 0);
        }

        var added = 0;

        // ⚠ This stamp's own placements, kept beside the volume's. A spacing check that only asked
        // the volume would pass every candidate, because none of them is in it yet.
        var mine = new List<Vector2>(candidates);

        for (var index = 0; index < candidates; index++) {
            var hash = Hash(seed, index);
            var at = Disc(hash, centre, radius);

            if (Consider(volume, type, settings, surface, at, mine, hash, out var instance) != Refusal.Placed) {
                continue;
            }

            var address = volume.Add(type, instance);

            mine.Add(new(instance.Position.X, instance.Position.Z));
            placed?.Add(address);
            added++;
        }

        return new(added, candidates);
    }

    /// <summary>Whether one candidate would be placed, and what it would be.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="type">Which palette entry.</param>
    /// <param name="settings">Its settings.</param>
    /// <param name="surface">What answers for the ground.</param>
    /// <param name="at">Where the candidate is, in world XZ.</param>
    /// <param name="pending">What this stamp has already placed.</param>
    /// <param name="hash">The candidate's own hash, which its randomness derives from.</param>
    /// <param name="instance">What it would be.</param>
    /// <returns>Why not, or <see cref="Refusal.Placed" />.</returns>
    /// <remarks>
    ///     Public and returning a reason, because "the brush places nothing and does not say why" is
    ///     the single most reported problem with every foliage tool ever shipped. A panel that can
    ///     say "forty candidates, thirty-one too steep" turns that into a setting somebody changes.
    /// </remarks>
    public static Refusal Consider(
        FoliageVolume volume,
        int type,
        in FoliageType settings,
        IFoliageSurface surface,
        Vector2 at,
        IReadOnlyList<Vector2> pending,
        uint hash,
        out FoliageInstance instance
    ) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(pending);

        instance = default;

        var ground = surface.SampleAt(at, settings.LayerFilter);

        if (!ground.Hit) {
            return Refusal.NoSurface;
        }

        var slope = ground.Slope;

        if (slope < settings.MinSlope || slope > settings.MaxSlope) {
            return Refusal.Slope;
        }

        if (ground.Position.Y < settings.MinAltitude || ground.Position.Y > settings.MaxAltitude) {
            return Refusal.Altitude;
        }

        if (settings.NeedsSurfaceWeight && ground.Weight < settings.LayerThreshold) {
            return Refusal.Layer;
        }

        var spacing = MathF.Max(settings.Radius, 0.01f);
        var squared = spacing * spacing;

        foreach (var earlier in pending) {
            if (Vector2.DistanceSquared(earlier, at) < squared) {
                return Refusal.Spacing;
            }
        }

        foreach (var _ in volume.Within(at, spacing, new HashSet<int> { type })) {
            return Refusal.Spacing;
        }

        instance = Place(settings, ground, hash);
        return Refusal.Placed;
    }

    /// <summary>Turns a surface hit into an instance, with the type's randomness applied.</summary>
    /// <param name="settings">The type.</param>
    /// <param name="ground">What the surface is.</param>
    /// <param name="hash">What the randomness derives from.</param>
    /// <returns>The instance.</returns>
    /// <remarks>
    ///     ⚠ <b>Alignment is a <em>fraction</em> of the way to the surface normal, not a flag.</b> A
    ///     tree leaning ten per cent into a hill reads as growth; a tree lying flat on it reads as
    ///     felled. Slerping from upright to the normal is what makes the setting continuous, and it
    ///     is why <see cref="FoliageType.AlignToNormal" /> is a number.
    /// </remarks>
    public static FoliageInstance Place(in FoliageType settings, in FoliageSurface ground, uint hash) =>
        Place(
            in ground,
            hash,
            settings.RandomYaw,
            settings.MaxPitch,
            settings.MinScale,
            settings.MaxScale,
            settings.AlignToNormal
        );

    /// <summary>Turns a surface hit into an instance, from the ranges rather than from a type.</summary>
    /// <param name="ground">What the surface is.</param>
    /// <param name="hash">What the randomness derives from.</param>
    /// <param name="randomYaw">Whether to turn it to a random heading.</param>
    /// <param name="maxPitch">How far it may be tilted at random, in radians.</param>
    /// <param name="minScale">The smallest uniform scale.</param>
    /// <param name="maxScale">And the largest.</param>
    /// <param name="alignToNormal">How much it turns to face the normal, 0…1.</param>
    /// <returns>The instance.</returns>
    /// <remarks>
    ///     ⚠ <b>The one definition of "turn a hit into a thing standing on it", and it takes the
    ///     ranges rather than a <see cref="FoliageType" /> so that grass can reach it.</b> A
    ///     <see cref="GrassType" /> is not a foliage type — it has no spacing, no collision and no
    ///     identity — and a second copy of this arithmetic is a second answer to "which way does a
    ///     blade lean", which is exactly the drift the CPU/GPU seam test exists to forbid.
    /// </remarks>
    public static FoliageInstance Place(
        in FoliageSurface ground,
        uint hash,
        bool randomYaw,
        float maxPitch,
        float minScale,
        float maxScale,
        float alignToNormal
    ) {
        var yaw = randomYaw ? Unit(hash, 1) * MathF.Tau : 0f;
        var pitch = maxPitch > 0f ? ((Unit(hash, 2) * 2f) - 1f) * maxPitch : 0f;
        var scale = float.Lerp(minScale, maxScale, Unit(hash, 3));

        var upright = Quaternion.FromAxisAngle(Vector3.UnitY, yaw);
        var rotation = upright;

        var align = Math.Clamp(alignToNormal, 0f, 1f);

        if (align > 0f) {
            var normal = Vector3.Normalize(ground.Normal);

            if (!normal.IsZero) {
                var toNormal = FromUpTo(normal);

                rotation = Quaternion.Slerp(Quaternion.Identity, toNormal, align) * upright;
            }
        }

        if (pitch != 0f) {
            rotation *= Quaternion.FromAxisAngle(Vector3.UnitX, pitch);
        }

        return new(ground.Position, Quaternion.Normalize(rotation), scale);
    }

    /// <summary>The rotation that takes world up onto a normal.</summary>
    static Quaternion FromUpTo(Vector3 normal) {
        var dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, normal), -1f, 1f);

        if (dot > 0.9999f) {
            return Quaternion.Identity;
        }

        // Upside down: any axis in the horizontal plane will do, and X is one.
        if (dot < -0.9999f) {
            return Quaternion.FromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));

        return Quaternion.FromAxisAngle(axis, MathF.Acos(dot));
    }

    /// <summary>A point in a disc, from a hash.</summary>
    /// <remarks>
    ///     ⚠ <b>The square root is what makes it uniform.</b> Taking the radius straight from a
    ///     uniform number packs candidates towards the centre, because a ring at radius r has
    ///     circumference proportional to r — which draws as a painted clump with a bald rim.
    /// </remarks>
    static Vector2 Disc(uint hash, Vector2 centre, float radius) {
        var angle = Unit(hash, 4) * MathF.Tau;
        var distance = radius * MathF.Sqrt(Unit(hash, 5));

        return centre + new Vector2(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);
    }

    /// <summary>A hash of a stamp's seed and a candidate's index.</summary>
    /// <remarks>
    ///     A hash rather than a sequence, so candidate N depends only on N — which is what lets a
    ///     redo produce the same forest after an undo that discarded the intermediate state. It is
    ///     `BrushStroke.RandomAngle`'s finalizer, for the same reason.
    /// </remarks>
    public static uint Hash(uint seed, int index) {
        var hash = seed ^ (uint)index;

        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return hash;
    }

    /// <summary>One of a hash's several independent 0…1 draws.</summary>
    /// <param name="hash">The candidate's hash.</param>
    /// <param name="stream">Which of its independent draws, from 1 up.</param>
    /// <returns>A number in 0…1.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Re-hashed per stream rather than sliced out of the bits.</b> Slicing gives the yaw
    ///         and the scale correlated low bits, which shows up as every large tree facing the same
    ///         way — a pattern an artist sees immediately and cannot describe.
    ///     </para>
    ///     <para>
    ///         Public because <see cref="GrassScatter" /> draws from the same streams and because the
    ///         compute pass in <c>GrassScatter.rvn</c> mirrors this expression exactly — see
    ///         <see cref="GrassScatter.Hash" />. Streams 1…5 are this file's; grass takes 6 upward.
    ///     </para>
    /// </remarks>
    public static float Unit(uint hash, int stream) {
        var mixed = Hash(hash ^ (uint)(stream * 0x27D4EB2), stream);

        return mixed / (float)uint.MaxValue;
    }
}
