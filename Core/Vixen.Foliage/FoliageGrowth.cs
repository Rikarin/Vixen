// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>What a simulation is asked to grow, and where.</summary>
/// <remarks>
///     ⚠ <b>The seed is a setting rather than a default, because it is the one number an author
///     re-rolls.</b> "Grow me a different forest with the same rules" is the operation, and it is a
///     different operation from changing the rules — which is why the seed is beside the region and
///     not hidden inside the simulation.
/// </remarks>
public readonly record struct FoliageGrowthSettings {
    /// <summary>The low corner of the region, in world XZ.</summary>
    public Vector2 Origin { get; init; }

    /// <summary>How far it reaches, in metres.</summary>
    public Vector2 Size { get; init; }

    /// <summary>What every random draw derives from.</summary>
    public uint Seed { get; init; }

    /// <summary>How many steps to run.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed count and not a convergence test.</b> A simulation that ran until it settled
    ///     would take a different number of steps on a different seed, which makes "the same rules,
    ///     a different forest" produce two forests of different maturity — and it makes the cost
    ///     unpredictable in the one place an author is waiting for it.
    /// </remarks>
    public int Steps { get; init; }

    /// <summary>How many plants the region may hold before the simulation stops sowing.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a hope.</b> Spread is exponential until shade catches up with it, and
    ///     a region an author made ten times too large is ten thousand times the plants. When it
    ///     bites, <see cref="FoliageGrowthResult.Capped" /> says so — a simulation that silently
    ///     stopped growing would read as a rule that stopped working.
    /// </remarks>
    public int MaxPlants { get; init; }

    /// <summary>The settings a simulation over a region starts from.</summary>
    /// <param name="origin">The low corner, in world XZ.</param>
    /// <param name="size">How far it reaches.</param>
    /// <param name="seed">What to re-roll.</param>
    /// <returns>The settings.</returns>
    public static FoliageGrowthSettings Over(Vector2 origin, Vector2 size, uint seed = 0x9E3779B9u) =>
        new() {
            Origin = origin,
            Size = size,
            Seed = seed,
            Steps = 8,
            MaxPlants = 100_000
        };

    /// <summary>How many square metres the region is.</summary>
    public float Area => MathF.Max(Size.X, 0f) * MathF.Max(Size.Y, 0f);

    /// <summary>Whether a position is inside the region.</summary>
    /// <param name="at">Where, in world XZ.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(Vector2 at) =>
        at.X >= Origin.X && at.Y >= Origin.Y && at.X <= Origin.X + Size.X && at.Y <= Origin.Y + Size.Y;

    /// <summary>Why this cannot be simulated, or <see langword="null" /> if it can.</summary>
    public string? Validate() {
        if (!(Size.X > 0f) || !(Size.Y > 0f)) {
            return $"A region of {Size.X} × {Size.Y} m has no area, so nothing would be sown.";
        }

        if (Steps < 0) {
            return $"{Steps} steps is not a number of steps; zero is a sowing and no growth.";
        }

        if (MaxPlants <= 0) {
            return $"A cap of {MaxPlants} plants would refuse the first seed.";
        }

        return null;
    }
}

/// <summary>What a simulation did, and what refused each seed that did not take.</summary>
/// <param name="Steps">How many steps ran.</param>
/// <param name="Sown">How many seeds the first step scattered.</param>
/// <param name="Sprouted">How many seeds mature plants dropped over every step after it.</param>
/// <param name="Placed">How many plants were standing at the end.</param>
/// <param name="NoSurface">Seeds with no ground, ground too steep, or the wrong layer under them.</param>
/// <param name="Blocked">Seeds inside a blocking volume.</param>
/// <param name="Crowded">Seeds too close to a plant they could not displace.</param>
/// <param name="Shaded">Seeds under more canopy than they tolerate.</param>
/// <param name="Displaced">Plants a higher-priority seed removed.</param>
/// <param name="Capped">Seeds refused because the region was full.</param>
/// <remarks>
///     ⚠ <b>A refusal per reason, for <see cref="FoliageScatter.Consider" />'s reason.</b> "The
///     simulation grew nothing" is the report; "eleven thousand seeds, nine thousand of them shaded
///     out" is a shade tolerance somebody changes. A simulation an author cannot read is one they run
///     twice and abandon.
/// </remarks>
public readonly record struct FoliageGrowthResult(
    int Steps,
    int Sown,
    int Sprouted,
    int Placed,
    int NoSurface,
    int Blocked,
    int Crowded,
    int Shaded,
    int Displaced,
    int Capped
) {
    /// <summary>How many seeds the simulation considered in total.</summary>
    public int Considered => Sown + Sprouted;

    /// <summary>How many were refused.</summary>
    public int Refused => NoSurface + Blocked + Crowded + Shaded + Capped;
}

/// <summary>
///     The offline ecology: seeds sown, aged, spread, shaded out and displaced.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T9], which is Unreal's procedural foliage tool.</b> A region is sown at
///         each species' seed density; every step, plants age, mature ones drop seeds within their
///         spread distance, and each new seed is tested against the ground, the blocking volumes, the
///         plants already there and the canopy over it. What is left reads as a forest — clumped
///         where a parent stood, thinned under shade, cleared where a volume says.
///     </para>
///     <para>
///         ⚠ <b>The output is a volume of its own, and that is [§ D4]'s reserved layer in this
///         kernel's vocabulary.</b> A simulation is re-runnable, which means it regenerates its
///         instances <em>wholesale</em> — so it cannot share a container with the ones an artist
///         placed by hand, or re-rolling the seed would delete an afternoon's work. The destination is
///         cleared and refilled; the scene's own volume is never touched.
///     </para>
///     <para>
///         ⚠ <b>Deterministic from the stated seed, and never from an iteration order.</b> A plant's
///         identity is hashed at birth from its parent's identity and the seed index, so it does not
///         move when the plants around it change; and each step's candidates are <em>resolved in hash
///         order</em> rather than in the order they were generated, because which of two overlapping
///         seeds wins must not depend on which parent was walked first.
///     </para>
///     <para>
///         ⚠ <b>Shade is cast by the canopy, which grows.</b> A seed under a sapling survives and the
///         same seed under the grown tree does not — see <see cref="FoliageEcology.ShadeAt" />. A
///         simulation that shaded from the mature radius produces one tree per shade radius, evenly
///         spaced, everywhere, which is the failure that makes a procedural forest read as procedural.
///     </para>
/// </remarks>
public static class FoliageGrowth {
    /// <summary>The stream a sown seed's position along X is drawn from.</summary>
    /// <remarks>
    ///     Eleven, because <see cref="FoliageScatter" /> owns one to five and
    ///     <see cref="GrassScatter" /> six to ten. Three scatters drawing the same stream for
    ///     different things is a correlation nobody would think to look for.
    /// </remarks>
    public const int SowXStream = 11;

    /// <summary>And along Z.</summary>
    public const int SowZStream = 12;

    /// <summary>The stream a dropped seed's bearing from its parent is drawn from.</summary>
    public const int BearingStream = 13;

    /// <summary>And its distance.</summary>
    public const int SpreadStream = 14;

    /// <summary>One plant in flight: where it stands, how old it is, and who it is.</summary>
    /// <remarks>
    ///     A class rather than a struct in a list, because a plant is killed by being marked rather
    ///     than removed — <see cref="FoliageVolume" />'s own lesson, one level down. Removing from
    ///     the working set as the simulation walks it shifts every index after it, and the spatial
    ///     index holds indices.
    /// </remarks>
    sealed class Plant {
        public int Type;
        public Vector2 At;
        public Vector3 Ground;
        public Vector3 Normal;
        public float Age;
        public uint Identity;
        public bool Dead;
    }

    /// <summary>Grows a forest into a volume, replacing whatever was in it.</summary>
    /// <param name="destination">Where the plants go. Cleared first.</param>
    /// <param name="surface">What answers "what is the ground here".</param>
    /// <param name="settings">The region, the seed and the step count.</param>
    /// <param name="blockers">Volumes nothing grows inside, or none.</param>
    /// <returns>What happened, and what refused each seed that did not take.</returns>
    /// <exception cref="ArgumentNullException">There is no volume or no surface.</exception>
    /// <exception cref="ArgumentException">The settings describe nothing to simulate.</exception>
    public static FoliageGrowthResult Simulate(
        FoliageVolume destination,
        IFoliageSurface surface,
        in FoliageGrowthSettings settings,
        IReadOnlyList<FoliageBlocker>? blockers = null
    ) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(surface);

        if (settings.Validate() is { } problem) {
            throw new ArgumentException(problem, nameof(settings));
        }

        // Wholesale, because that is what makes a simulation re-runnable. See the class remarks.
        destination.Clear();

        var palette = destination.Palette;
        var plants = new List<Plant>();
        var index = new Index(Reach(palette));
        var tally = new Tally();

        Sow(destination, surface, in settings, blockers, plants, index, tally);

        for (var step = 1; step <= settings.Steps; step++) {
            foreach (var plant in plants) {
                if (!plant.Dead) {
                    plant.Age += 1f;
                }
            }

            Spread(destination, surface, in settings, blockers, plants, index, tally, step);
        }

        var placed = 0;

        foreach (var plant in plants) {
            if (plant.Dead) {
                continue;
            }

            destination.Add(plant.Type, Grown(palette[plant.Type], plant));
            placed++;
        }

        return new(
            settings.Steps,
            tally.Sown,
            tally.Sprouted,
            placed,
            tally.NoSurface,
            tally.Blocked,
            tally.Crowded,
            tally.Shaded,
            tally.Displaced,
            tally.Capped
        );
    }

    /// <summary>The first step: every sowing species scattered over the whole region.</summary>
    static void Sow(
        FoliageVolume destination,
        IFoliageSurface surface,
        in FoliageGrowthSettings settings,
        IReadOnlyList<FoliageBlocker>? blockers,
        List<Plant> plants,
        Index index,
        Tally tally
    ) {
        var candidates = new List<Plant>();

        for (var type = 0; type < destination.Palette.Count; type++) {
            var ecology = destination.Palette[type].Ecology;

            if (!ecology.Sows) {
                continue;
            }

            var count = (int)MathF.Round(ecology.SeedDensity * settings.Area);

            for (var seed = 0; seed < count; seed++) {
                // The type is mixed into the seed so two species sown over one region do not land on
                // the same points — which would make every overlap a priority contest and nothing
                // else.
                var identity = FoliageScatter.Hash(settings.Seed ^ ((uint)type * 0x85EBCA77u), seed);

                var at = settings.Origin
                    + new Vector2(
                        FoliageScatter.Unit(identity, SowXStream) * settings.Size.X,
                        FoliageScatter.Unit(identity, SowZStream) * settings.Size.Y
                    );

                candidates.Add(new() { Type = type, At = at, Identity = identity });
                tally.Sown++;
            }
        }

        Resolve(destination, surface, in settings, blockers, plants, index, tally, candidates);
    }

    /// <summary>One step: every mature plant drops its seeds.</summary>
    static void Spread(
        FoliageVolume destination,
        IFoliageSurface surface,
        in FoliageGrowthSettings settings,
        IReadOnlyList<FoliageBlocker>? blockers,
        List<Plant> plants,
        Index index,
        Tally tally,
        int step
    ) {
        var candidates = new List<Plant>();

        // A snapshot of the count, so seeds dropped this step do not themselves drop seeds inside it
        // — which would make one step's yield depend on the order the parents were walked.
        var parents = plants.Count;

        for (var at = 0; at < parents; at++) {
            var parent = plants[at];

            if (parent.Dead) {
                continue;
            }

            var ecology = destination.Palette[parent.Type].Ecology;

            if (!ecology.Spreads || ecology.Maturity(parent.Age) < 1f) {
                continue;
            }

            for (var seed = 0; seed < ecology.SeedsPerStep; seed++) {
                var identity = FoliageScatter.Hash(
                    parent.Identity ^ ((uint)step * 0x9E3779B1u),
                    seed
                );

                var bearing = FoliageScatter.Unit(identity, BearingStream) * MathF.Tau;

                // The square root is what spreads seeds evenly over the disc rather than packing
                // them at the parent's feet — FoliageScatter.Disc's reason, and here it is the
                // difference between a clump and a thicket around every trunk.
                var distance = ecology.SpreadDistance * MathF.Sqrt(FoliageScatter.Unit(identity, SpreadStream));

                var landed = parent.At + new Vector2(MathF.Cos(bearing) * distance, MathF.Sin(bearing) * distance);

                candidates.Add(new() { Type = parent.Type, At = landed, Identity = identity });
                tally.Sprouted++;
            }
        }

        Resolve(destination, surface, in settings, blockers, plants, index, tally, candidates);
    }

    /// <summary>Tests a step's seeds and admits the ones that take.</summary>
    /// <remarks>
    ///     ⚠ <b>In hash order, not in the order they were generated.</b> Which of two overlapping
    ///     seeds wins must not depend on which parent was walked first, or a simulation would produce
    ///     a different forest whenever anything upstream changed the order of the working set — and
    ///     "the same seed grew a different forest" is a bug report nobody can act on.
    /// </remarks>
    static void Resolve(
        FoliageVolume destination,
        IFoliageSurface surface,
        in FoliageGrowthSettings settings,
        IReadOnlyList<FoliageBlocker>? blockers,
        List<Plant> plants,
        Index index,
        Tally tally,
        List<Plant> candidates
    ) {
        candidates.Sort(static (left, right) => left.Identity.CompareTo(right.Identity));

        var palette = destination.Palette;

        foreach (var candidate in candidates) {
            if (!settings.Contains(candidate.At)) {
                tally.NoSurface++;
                continue;
            }

            var type = palette[candidate.Type];
            var ground = surface.SampleAt(candidate.At, type.LayerFilter);

            if (!ground.Hit) {
                tally.NoSurface++;
                continue;
            }

            var slope = ground.Slope;

            if (slope < type.MinSlope
                || slope > type.MaxSlope
                || ground.Position.Y < type.MinAltitude
                || ground.Position.Y > type.MaxAltitude
                || (type.NeedsSurfaceWeight && ground.Weight < type.LayerThreshold)) {
                tally.NoSurface++;
                continue;
            }

            if (IsBlocked(blockers, ground.Position)) {
                tally.Blocked++;
                continue;
            }

            candidate.Ground = ground.Position;
            candidate.Normal = ground.Normal;

            if (!Contest(palette, plants, index, tally, candidate)) {
                continue;
            }

            if (plants.Count - tally.Dead >= settings.MaxPlants) {
                tally.Capped++;
                continue;
            }

            index.Add(plants.Count, candidate.At);
            plants.Add(candidate);
        }
    }

    /// <summary>
    ///     Whether a seed beats what is already standing where it landed, and shades and spacing
    ///     permitting.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Priority displaces rather than ties.</b> A higher-priority seed landing on a
    ///     lower-priority plant removes it, which is how an oak comes to stand in a clearing of the
    ///     scrub that got there first. Equal priorities fall to age — the established plant keeps its
    ///     ground — and equal ages to the seed's identity, never to an iteration order.
    /// </remarks>
    static bool Contest(
        IReadOnlyList<FoliageType> palette,
        List<Plant> plants,
        Index index,
        Tally tally,
        Plant candidate
    ) {
        var mine = palette[candidate.Type];
        var doomed = new List<int>();
        var shade = 0f;

        foreach (var slot in index.Near(candidate.At)) {
            var other = plants[slot];

            if (other.Dead) {
                continue;
            }

            var theirs = palette[other.Type];
            var distance = Vector2.Distance(other.At, candidate.At);

            var spacing = MathF.Max(MathF.Max(mine.Radius, theirs.Radius), 0.01f);

            if (distance < spacing) {
                if (mine.Ecology.Priority <= theirs.Ecology.Priority) {
                    tally.Crowded++;
                    return false;
                }

                doomed.Add(slot);
                continue;
            }

            var canopy = theirs.Ecology.ShadeAt(other.Age);

            if (canopy > 0f && distance < canopy) {
                shade += 1f - (distance / canopy);
            }
        }

        // Saturated, so a tolerance of one survives any canopy and a tolerance of zero survives none.
        // An unbounded sum would make tolerance mean "how many neighbours", which is a number nobody
        // can reason about from the panel.
        if (Math.Clamp(shade, 0f, 1f) > mine.Ecology.ShadeTolerance) {
            tally.Shaded++;
            return false;
        }

        // Marked rather than removed, and only once the seed has survived everything else: killing as
        // the loop walked would leave a plant dead for a seed the shade then refused.
        foreach (var slot in doomed) {
            plants[slot].Dead = true;
            tally.Dead++;
            tally.Displaced++;
        }

        return true;
    }

    static bool IsBlocked(IReadOnlyList<FoliageBlocker>? blockers, Vector3 position) {
        if (blockers is null) {
            return false;
        }

        foreach (var blocker in blockers) {
            if (blocker.Contains(position)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>A plant as an instance, sized by how far through its life it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Age scales the instance rather than choosing a mesh.</b> A sapling is the tree at a
    ///     third of its size, which is wrong botanically and right for a tool whose output an artist
    ///     then edits by hand — a second mesh per species would double the palette for a distinction
    ///     the simulation cannot make well anyway.
    /// </remarks>
    static FoliageInstance Grown(in FoliageType type, Plant plant) {
        var maturity = type.Ecology.Maturity(plant.Age);

        var instance = FoliageScatter.Place(
            new(plant.Ground, plant.Normal, 1f, true),
            plant.Identity,
            type.RandomYaw,
            type.MaxPitch,
            type.MinScale,
            type.MaxScale,
            type.AlignToNormal
        );

        return instance with { Scale = instance.Scale * MathF.Max(maturity, 0.05f) };
    }

    /// <summary>How far apart two plants can be and still interact.</summary>
    static float Reach(IReadOnlyList<FoliageType> palette) {
        var reach = 1f;

        foreach (var type in palette) {
            reach = MathF.Max(reach, MathF.Max(type.Radius, type.Ecology.ShadeRadius));
        }

        return reach;
    }

    /// <summary>Which plants are near a point, without asking all of them.</summary>
    /// <remarks>
    ///     A grid of the interaction reach, so a query looks at nine cells. The working set is tens
    ///     of thousands of plants and every seed asks — quadratic here is the difference between a
    ///     simulation an author waits a second for and one they cancel.
    /// </remarks>
    sealed class Index(float reach) {
        readonly Dictionary<(int X, int Z), List<int>> cells = [];
        readonly float size = MathF.Max(reach, 0.5f);

        public void Add(int slot, Vector2 at) {
            var key = KeyOf(at);

            if (!cells.TryGetValue(key, out var list)) {
                cells[key] = list = [];
            }

            list.Add(slot);
        }

        public IEnumerable<int> Near(Vector2 at) {
            var centre = KeyOf(at);

            for (var z = centre.Z - 1; z <= centre.Z + 1; z++) {
                for (var x = centre.X - 1; x <= centre.X + 1; x++) {
                    if (cells.TryGetValue((x, z), out var list)) {
                        foreach (var slot in list) {
                            yield return slot;
                        }
                    }
                }
            }
        }

        (int X, int Z) KeyOf(Vector2 at) =>
            ((int)MathF.Floor(at.X / size), (int)MathF.Floor(at.Y / size));
    }

    /// <summary>The counters, mutable because they are accumulated across every step.</summary>
    sealed class Tally {
        public int Sown;
        public int Sprouted;
        public int NoSurface;
        public int Blocked;
        public int Crowded;
        public int Shaded;
        public int Displaced;
        public int Capped;
        public int Dead;
    }
}
