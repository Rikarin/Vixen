// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>How a type behaves in a growth simulation.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T9]'s four sliders, and they are Unreal's procedural foliage
///         parameters.</b> A type sows itself at some density, ages towards maturity, spreads seeds
///         a distance, casts shade over a radius, tolerates some amount of it, and wins or loses when
///         two of them land on the same ground.
///     </para>
///     <para>
///         ⚠ <b>Separate from the placement rules, and the separation is the point.</b>
///         <see cref="FoliageType" />'s slope, altitude and layer filters say where a plant
///         <em>may</em> stand; these say what happens between plants. A hand-painted stroke obeys the
///         first set and knows nothing about the second, which is what lets one type be painted and
///         simulated without two assets.
///     </para>
///     <para>
///         ⚠ <b>A zeroed ecology never sows, and that is the right way round.</b> A type an author
///         has not given a seed density to is one they paint by hand; a zero that meant "the default
///         density" would make every palette entry appear in a simulation somebody ran to grow one
///         species.
///     </para>
/// </remarks>
public readonly record struct FoliageEcology {
    /// <summary>How many seeds a square metre starts with.</summary>
    /// <remarks>
    ///     The simulation's first step and nothing else — what comes after is spread. A dense sowing
    ///     and few steps is a field; a sparse sowing and many steps is a forest that grew from a
    ///     handful of trees, and the second is what reads as a forest.
    /// </remarks>
    public float SeedDensity { get; init; }

    /// <summary>How far a seed lands from the plant that dropped it, in metres.</summary>
    public float SpreadDistance { get; init; }

    /// <summary>How many seeds a mature plant drops each step.</summary>
    public int SeedsPerStep { get; init; }

    /// <summary>How far a mature plant's canopy shades, in metres.</summary>
    public float ShadeRadius { get; init; }

    /// <summary>How much shade this survives, 0…1.</summary>
    /// <remarks>
    ///     One is a species that grows in full shade and zero is one that needs open ground. It is
    ///     what makes a canopy read as a canopy: the shade-intolerant species clears out underneath
    ///     and the tolerant one fills in.
    /// </remarks>
    public float ShadeTolerance { get; init; }

    /// <summary>Who wins when two plants want the same ground. Higher takes it.</summary>
    /// <remarks>
    ///     ⚠ <b>A displacement rather than a tie-break, and that is what makes it worth having.</b>
    ///     A higher-priority seed landing on a lower-priority plant <em>removes</em> it, which is how
    ///     an oak comes to stand in a clearing of the scrub that got there first. Equal priorities
    ///     fall back to age, and equal ages to the seed's own hash — never to an iteration order.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>How many steps a seed takes to reach full size.</summary>
    /// <remarks>
    ///     ⚠ <b>Also how long it takes to start spreading</b>, because a plant spreads once it is
    ///     mature. A max age above the step count is a species that never reproduces, which is a
    ///     legitimate thing to author and an easy thing to author by accident —
    ///     <see cref="FoliageGrowthResult.Sprouted" /> is what says it happened.
    /// </remarks>
    public float MaxAge { get; init; }

    /// <summary>A type that takes no part in a simulation.</summary>
    public static FoliageEcology None =>
        new() {
            SeedDensity = 0f,
            SpreadDistance = 0f,
            SeedsPerStep = 0,
            ShadeRadius = 0f,
            ShadeTolerance = 1f,
            Priority = 0,
            MaxAge = 1f
        };

    /// <summary>The settings a species that behaves like a tree starts from.</summary>
    public static FoliageEcology Tree =>
        new() {
            SeedDensity = 0.002f,
            SpreadDistance = 12f,
            SeedsPerStep = 2,
            ShadeRadius = 6f,
            ShadeTolerance = 0.2f,
            Priority = 10,
            MaxAge = 4f
        };

    /// <summary>Whether this type takes part in a simulation at all.</summary>
    public bool Sows => SeedDensity > 0f;

    /// <summary>Whether a mature plant of this type drops seeds.</summary>
    public bool Spreads => SeedsPerStep > 0 && SpreadDistance > 0f;

    /// <summary>How far through its life a plant of this age is, 0…1.</summary>
    /// <param name="age">Its age in steps.</param>
    /// <returns>The fraction.</returns>
    public float Maturity(float age) => Math.Clamp(age / MathF.Max(MaxAge, 1e-3f), 0f, 1f);

    /// <summary>How far a plant of this age shades, in metres.</summary>
    /// <param name="age">Its age in steps.</param>
    /// <returns>The radius.</returns>
    /// <remarks>
    ///     ⚠ <b>The canopy grows with the plant, and a simulation that shaded from the mature radius
    ///     would never fill in.</b> A seed under a sapling survives and the same seed under the grown
    ///     tree does not — which is the difference between a forest with an understorey and a forest
    ///     that is one tree per shade radius, evenly spaced, everywhere.
    /// </remarks>
    public float ShadeAt(float age) => ShadeRadius * Maturity(age);

    /// <summary>Why this ecology cannot be simulated, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (SeedDensity < 0f) {
            return $"A seed density of {SeedDensity} per square metre is not a density.";
        }

        if (!(MaxAge > 0f)) {
            return $"A maximum age of {MaxAge} steps means a seed is mature the moment it lands, "
                + "so nothing ever grows and everything spreads at once.";
        }

        if (ShadeTolerance is < 0f or > 1f) {
            return $"A shade tolerance of {ShadeTolerance} is outside 0…1; one survives full shade "
                + "and zero needs open ground.";
        }

        return null;
    }
}

/// <summary>A box that nothing grows inside.</summary>
/// <param name="Centre">Where it is.</param>
/// <param name="Extent">Half its size on each axis.</param>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T9]'s blocking volumes.</b> A road, a building footprint, a clearing an
///         author wants — anywhere the simulation must leave alone.
///     </para>
///     <para>
///         ⚠ <b>A box and not a shape, because this assembly has no physics world.</b> A caller with
///         one converts; a caller without still gets the feature. The same reason
///         <see cref="IFoliageSurface" /> is an interface rather than a terrain.
///     </para>
///     <para>
///         ⚠ <b>It blocks rather than removes.</b> A seed inside is refused, so re-running with the
///         volume moved regrows what it used to cover — which is the whole point of the simulation
///         being re-runnable. A blocker that deleted would make its own removal irreversible.
///     </para>
/// </remarks>
public readonly record struct FoliageBlocker(Vector3 Centre, Vector3 Extent) {
    /// <summary>A blocker covering a horizontal circle at any height.</summary>
    /// <param name="centre">Its centre, in world XZ.</param>
    /// <param name="radius">How far it reaches.</param>
    /// <returns>The blocker, as the square that contains the circle.</returns>
    /// <remarks>
    ///     The square rather than the circle, deliberately: a clearing an author asked for as a
    ///     radius is a clearing they will look at from above, and a box that is a little too large is
    ///     a clearing that is a little too large rather than a tree standing in the middle of one.
    /// </remarks>
    public static FoliageBlocker Around(Vector2 centre, float radius) =>
        new(
            new(centre.X, 0f, centre.Y),
            new(MathF.Abs(radius), float.PositiveInfinity, MathF.Abs(radius))
        );

    /// <summary>Whether a position is inside.</summary>
    /// <param name="position">Where.</param>
    /// <returns>Whether it is blocked.</returns>
    public bool Contains(Vector3 position) =>
        MathF.Abs(position.X - Centre.X) <= Extent.X
        && MathF.Abs(position.Y - Centre.Y) <= Extent.Y
        && MathF.Abs(position.Z - Centre.Z) <= Extent.Z;
}
