// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Geometry.Uv.Packing;

/// <summary>What one pack produced.</summary>
/// <param name="Placements">Where every island went, in the order they were handed in.</param>
/// <param name="Report">The packing half of the report — the other two stages leave their fields alone.</param>
readonly record struct PackOutcome(IReadOnlyList<UvPlacement> Placements, UvReport Report);

/// <summary>The four rungs, the texel margin, the even spacing and the efficiency pair.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D7 and § D8, and § Part 5's U4. This is the standalone packer: islands in,
///         transforms out, nothing re-cut. It is blocked on nothing the other two stages produce,
///         because islands can come from this library's charter, from the remesher's patch layout,
///         from an artist or from a file.
///     </para>
///     <para>
///         ⚠ <b>Everything below happens in texels.</b> The conversion from island coordinates to
///         texels is made once, at the top, and the conversion back to the unit square is made once,
///         at the bottom. A margin that lived in UV units in between would look right at the
///         resolution somebody tuned it at and bleed at half of it — docs/plan/42 § B4, and the
///         two-year fuse it describes.
///     </para>
/// </remarks>
static class Packer {
    /// <summary>How many packs the scale search may run in total.</summary>
    const int ScaleAttempts = 16;

    /// <summary>What the scale is multiplied by when a pack did not fit.</summary>
    const float Shrink = 0.7f;

    /// <summary>What it is multiplied by when one did, and the atlas has room to spare.</summary>
    const float Grow = 1.1f;

    /// <summary>How many halvings close the bracket once one scale fits and a larger one does not.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the geometric step <i>is</i> the error bar.</b> Stopping at the last
    ///     scale that fitted leaves up to a <see cref="Grow" /> factor of linear scale — a fifth of the
    ///     atlas's area — unclaimed, which reads as a packing failure and is an arithmetic one.
    /// </remarks>
    const int Bisections = 5;

    /// <summary>How much of the usable atlas the first guess aims to fill.</summary>
    const float Aim = 0.82f;

    /// <summary>The most tiles a spill may open before it is a runaway rather than a UDIM set.</summary>
    const int TileLimit = 64;

    /// <summary>Fill ratio at or above which an island is near-rectangular enough to join a super-patch.</summary>
    const float Rectangular = 0.8f;

    /// <summary>The most islands one super-patch groups.</summary>
    const int PatchWidth = 8;

    /// <summary>FNV-1a's offset basis, for <see cref="Contents" />.</summary>
    const ulong Seed = 14695981039346656037UL;

    /// <summary>FNV-1a's prime.</summary>
    const ulong Prime = 1099511628211UL;

    /// <summary>Packs islands into an atlas.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="settings">The settings, whose resolution is required because the margin is in texels.</param>
    /// <param name="scheduler">A scheduler to scan columns on, or <c>null</c> for the calling thread.</param>
    /// <param name="batch">How many slices one work item covers, or zero to let the scheduler choose.</param>
    /// <returns>The placements and the packing half of the report.</returns>
    public static PackOutcome Run(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        JobScheduler? scheduler,
        int batch
    ) {
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();
        var resolution = settings.Resolution;
        var margin = settings.Margin;
        var turns = Math.Clamp(settings.Rotations, 1, 4);

        if (islands.Count == 0) {
            return new([], Report(0, 0f, 0f, default, clock.Elapsed, warnings));
        }

        var factors = Factors(islands, settings, warnings, out var uniform);
        var contents = Contents(islands);
        var tiles = settings.Overflow == PackOverflow.NextTile && uniform ? TileLimit : 1;
        var scale = uniform ? 1f : Estimate(islands, factors, resolution, margin);
        var attempts = 0;
        var fits = 0f;
        var fails = float.PositiveInfinity;
        var run = default(Attempted?);

        // ⚠ Deterministic bracketing and nothing else. docs/plan/42 § B6 rules out the search the
        // irregular-packing literature reaches for — anneal the layout, restart from a new seed, keep
        // the best — so the only thing being searched here is a single scalar, by halving, from a
        // starting point that is arithmetic rather than lucky.
        while (attempts < ScaleAttempts) {
            attempts++;
            run = Attempt(islands, settings, factors, contents, scale, turns, tiles, scheduler, batch);

            if (run.Value.Fitted) {
                fits = scale;

                break;
            }

            if (settings.Overflow == PackOverflow.Refuse) {
                throw new InvalidOperationException(
                    $"{islands.Count} islands do not fit a {resolution}\u00b2 atlas at a {margin}-texel margin: "
                    + $"{run.Value.Unplaced} of them had nowhere to go. docs/plan/42 \u00a7 D11 \u2014 raise the "
                    + "resolution, lower the density, or choose another PackOverflow."
                );
            }

            run = null;
            fails = scale;
            scale *= Shrink;
        }

        if (run is null) {
            throw new InvalidOperationException(
                $"{islands.Count} islands still did not fit a {resolution}\u00b2 atlas after {ScaleAttempts} "
                + $"rescales down to {scale:0.####}\u00d7 \u2014 an island is larger than the atlas can ever hold, "
                + "or the margin has consumed it."
            );
        }

        // Only the fitting mode may grow past what it was asked for: a density the caller stated is a
        // constraint, and a packer that quietly exceeded it would be the same bug as one that quietly
        // shrank it.
        while (!uniform && attempts < ScaleAttempts && float.IsPositiveInfinity(fails)) {
            attempts++;

            var probe = fits * Grow;
            var larger = Attempt(islands, settings, factors, contents, probe, turns, tiles, scheduler, batch);

            if (larger.Fitted) {
                run = larger;
                fits = probe;
            } else {
                fails = probe;
            }
        }

        for (var step = 0; step < Bisections && attempts < ScaleAttempts && !float.IsPositiveInfinity(fails); step++) {
            var probe = 0.5f * (fits + fails);

            if (!(probe > fits) || !(probe < fails)) {
                break;
            }

            attempts++;

            var closer = Attempt(islands, settings, factors, contents, probe, turns, tiles, scheduler, batch);

            if (closer.Fitted) {
                run = closer;
                fits = probe;
            } else {
                fails = probe;
            }
        }

        var scaled = fits;
        var final = run.Value;

        if (uniform && scaled < 1f) {
            warnings.Add(
                $"The islands did not fit at the requested texel density, so every one of them was "
                + $"scaled to {scaled:0.###}× of it. docs/plan/42 § D9 — the achieved density is in the report."
            );
        }

        if (final.Tiles > 1) {
            warnings.Add($"The islands spilled into {final.Tiles} UDIM tiles. No island straddles a boundary.");
        }

        if (final.Tail > 0) {
            warnings.Add(
                $"{final.Tail} islands past the {settings.CoreLimit}-island core took the tail sweep, "
                + "which scans the skyline's steps rather than every column."
            );
        }

        var atlas = (double)resolution * resolution * final.Tiles;

        return new(
            final.Placements,
            Report(
                islands.Count,
                (float)(final.Covered / atlas),
                (float)(final.Consumed / atlas),
                Density(islands, factors, scaled),
                clock.Elapsed,
                warnings
            )
        );
    }

    /// <summary>A key per island, taken from its coordinates and from nothing else.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tie-break below <see cref="PackUnit.Signature" />, and the reason it has to
    ///         exist is that a signature is a hash of the <i>mask</i>.</b> A mask is the island rounded
    ///         to whole texels, and two islands that round the same way are not the same island — but
    ///         the atlas is baked from the coordinates rather than from the mask, so exchanging them
    ///         moves texels. docs/plan/42 § D7 asks for the same islands in a different order to pack
    ///         the same way; ending the comparison at the input index left that untrue for every pair
    ///         small enough that a few texels cannot tell them apart, which on a real atlas is most of
    ///         the long tail. See <see cref="PackUnit.Content" /> for the measurement.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bit patterns rather than values, so that <c>-0f</c> and <c>NaN</c> hash to
    ///         something rather than to a comparison that is false against itself.</b> The key is only
    ///         ever compared for order, never for meaning, so an exotic float needs to be
    ///         <i>stable</i> and does not need to be sensible.
    ///     </para>
    /// </remarks>
    static ulong[] Contents(IReadOnlyList<UvIsland> islands) {
        var contents = new ulong[islands.Count];

        for (var index = 0; index < islands.Count; index++) {
            var island = islands[index];
            var content = Mix(Seed, island.Minimum.X);

            content = Mix(content, island.Minimum.Y);
            content = Mix(content, island.Maximum.X);
            content = Mix(content, island.Maximum.Y);

            var coordinates = island.Coordinates;

            if (coordinates is not null) {
                for (var corner = 0; corner < coordinates.Count; corner++) {
                    content = Mix(Mix(content, coordinates[corner].X), coordinates[corner].Y);
                }
            }

            contents[index] = Mix(content, island.Scale);
        }

        return contents;
    }

    static ulong Mix(ulong state, float value) => Mix(state, BitConverter.SingleToUInt32Bits(value));

    static ulong Mix(ulong state, ulong value) => (state ^ value) * Prime;

    /// <summary>Texels per island coordinate, before the global scale.</summary>
    /// <remarks>
    ///     ⚠ Uniform density is all or nothing. An island whose own scale is unknown cannot be brought
    ///     to a texels-per-metre figure at all, and honouring the constraint for the rest would leave
    ///     one island in different units from its neighbours — which is docs/plan/42 § D9's failure
    ///     wearing the costume of the fix for it.
    /// </remarks>
    static float[] Factors(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        List<string> warnings,
        out bool uniform
    ) {
        var factors = new float[islands.Count];

        uniform = settings.TexelDensity > 0f;

        if (uniform) {
            for (var index = 0; index < islands.Count; index++) {
                if (!(islands[index].Scale > 0f) || !float.IsFinite(islands[index].Scale)) {
                    warnings.Add(
                        $"Island {index} carries no usable scale, so the requested texel density of "
                        + $"{settings.TexelDensity} could not be applied to any island. The islands were "
                        + "packed at the scale they arrived in."
                    );

                    uniform = false;

                    break;
                }
            }
        }

        for (var index = 0; index < islands.Count; index++) {
            factors[index] = uniform ? settings.TexelDensity / islands[index].Scale : 1f;
        }

        return factors;
    }

    /// <summary>A first guess at the global scale, from area with the margin's share counted in.</summary>
    static float Estimate(IReadOnlyList<UvIsland> islands, float[] factors, int resolution, int margin) {
        var usable = (double)Math.Max(1, resolution - (2 * margin));
        var target = Aim * usable * usable;
        var quadratic = 0d;
        var linear = 0d;
        var constant = (double)islands.Count * margin * margin;

        for (var index = 0; index < islands.Count; index++) {
            var size = islands[index].Size;
            var width = Math.Max(0f, size.X) * factors[index];
            var height = Math.Max(0f, size.Y) * factors[index];

            quadratic += (double)width * height;
            linear += margin * ((double)width + height);
        }

        if (quadratic <= 0d) {
            return linear > 0d ? (float)Math.Max(1e-6d, (target - constant) / linear) : 1f;
        }

        var room = Math.Max(0d, target - constant);
        var scale = (-linear + Math.Sqrt((linear * linear) + (4d * quadratic * room))) / (2d * quadratic);

        return (float)Math.Clamp(scale, 1e-9d, 1e9d);
    }

    static UvTexelDensity Density(IReadOnlyList<UvIsland> islands, float[] factors, float scale) {
        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        var sum = 0d;
        var count = 0;

        for (var index = 0; index < islands.Count; index++) {
            var islandScale = islands[index].Scale;

            if (!(islandScale > 0f) || !float.IsFinite(islandScale)) {
                continue;
            }

            var density = factors[index] * scale * islandScale;

            minimum = MathF.Min(minimum, density);
            maximum = MathF.Max(maximum, density);
            sum += density;
            count++;
        }

        if (count == 0) {
            return default;
        }

        var mean = sum / count;
        var variance = 0d;

        for (var index = 0; index < islands.Count; index++) {
            var islandScale = islands[index].Scale;

            if (islandScale > 0f && float.IsFinite(islandScale)) {
                var difference = (factors[index] * scale * islandScale) - mean;

                variance += difference * difference;
            }
        }

        return new(minimum, (float)mean, maximum, (float)(variance / count));
    }

    static UvReport Report(
        int count,
        float efficiency,
        float effective,
        UvTexelDensity density,
        TimeSpan elapsed,
        List<string> warnings
    ) =>
        // Compactness, convexity, seam length, jaggedness and the four distortion measures belong to
        // the two stages this one is deliberately independent of. A packer that guessed at them would
        // be reporting a chart's quality from its silhouette, which is docs/plan/42 § D6's warning
        // about single-number metrics with an extra step.
        new(
            count,
            0f,
            0f,
            0f,
            0f,
            0f,
            default,
            efficiency,
            effective,
            density,
            [new(UvStage.Pack, elapsed, count)],
            warnings
        );

    readonly record struct Attempted(
        bool Fitted,
        UvPlacement[] Placements,
        int Tiles,
        int Tail,
        int Unplaced,
        long Covered,
        long Consumed
    );

    static Attempted Attempt(
        IReadOnlyList<UvIsland> islands,
        PackSettings settings,
        float[] factors,
        ulong[] contents,
        float scale,
        int turns,
        int tileLimit,
        JobScheduler? scheduler,
        int batch
    ) {
        var resolution = settings.Resolution;
        var margin = settings.Margin;
        var solid = settings.Quality == PackQuality.Rectangle;
        var masks = new IslandMask[islands.Count][];
        var truncated = new bool[islands.Count];

        // ⚠ The island's own area, taken before the rectangle rung fills its bounding box in. Charging
        // a bounding-box packer for the empty corners of its boxes is what makes the comparison in the
        // tests mean anything: without it the rung that wastes the most reports the most used.
        var area = new int[islands.Count];
        var rasterize = new Rasterizer(
            islands,
            factors,
            masks,
            truncated,
            area,
            scale,
            margin,
            resolution,
            turns,
            solid
        );

        if (scheduler is not null && islands.Count > 1) {
            scheduler.ParallelFor(rasterize, islands.Count);
        } else {
            for (var index = 0; index < islands.Count; index++) {
                rasterize.Execute(index);
            }
        }

        foreach (var cut in truncated) {
            if (cut) {
                // An island the mask had to truncate is larger than the atlas can ever hold at this
                // scale, and a placement for it would describe a shape smaller than its coordinates.
                return new(false, [], 1, 0, 1, 0L, 0L);
            }
        }

        var units = settings.Quality == PackQuality.SuperPatch
            ? SuperPatches(masks, contents, margin, resolution, turns)
            : Singles(masks, contents);

        // ⚠ Descending area, then the mask, then the island's own coordinates, then the input index.
        // docs/plan/42 § D7: the same islands in a different order have to pack the same way, and a
        // comparison that can return zero is where that stops being true. Area alone ties often
        // enough to matter; the mask ties whenever two islands round to the same texels, which for
        // the long tail of a real atlas is most of it; and the index alone is exactly the thing the
        // caller is allowed to change. See Contents.
        Array.Sort(
            units,
            (left, right) => {
                if (left.Area != right.Area) {
                    return right.Area.CompareTo(left.Area);
                }

                if (left.Signature != right.Signature) {
                    return left.Signature.CompareTo(right.Signature);
                }

                return left.Content != right.Content
                    ? left.Content.CompareTo(right.Content)
                    : left.Order.CompareTo(right.Order);
            }
        );

        var grids = new List<AtlasGrid> { new(resolution, margin) };
        var placements = new UvPlacement[islands.Count];
        var frame = new Frame[islands.Count];
        var scan = new PlacementScan();
        var covered = 0L;
        var tail = 0;

        for (var index = 0; index < islands.Count; index++) {
            var texels = factors[index] * scale;
            var size = islands[index].Size;

            // The one conversion back out of texels, made once — and the slack a ceiling left behind.
            // ⚠ A mask's edge is `ceil(size × texels)` and a quarter turn reflects the *mask*, so a
            // turned island lands a fraction of a texel from where reflecting its own extent would put
            // it. Carrying that fraction here is what keeps `UvPlacement.Apply` an exact statement
            // about coordinates while the packer works in whole texels.
            frame[index] = new(
                texels / resolution,
                masks[index][0].Width - (size.X * texels),
                masks[index][0].Height - (size.Y * texels)
            );
        }

        for (var index = 0; index < units.Length; index++) {
            var unit = units[index];
            var sweep = index >= settings.CoreLimit;
            var placed = false;

            for (var tile = 0; tile < grids.Count && !placed; tile++) {
                placed = Place(scan, grids[tile], unit, turns, sweep, tile, resolution, placements, frame, scheduler, batch);
            }

            if (!placed && grids.Count < tileLimit) {
                grids.Add(new(resolution, margin));
                placed = Place(
                    scan,
                    grids[^1],
                    unit,
                    turns,
                    sweep,
                    grids.Count - 1,
                    resolution,
                    placements,
                    frame,
                    scheduler,
                    batch
                );
            }

            if (!placed) {
                return new(false, [], grids.Count, tail, units.Length - index, 0L, 0L);
            }

            foreach (var member in unit.Members) {
                covered += area[member.Island];
            }

            if (sweep) {
                tail++;
            }
        }

        var consumed = 0L;

        foreach (var grid in grids) {
            consumed += grid.Consumed;
        }

        return new(true, placements, grids.Count, tail, 0, covered, consumed);
    }

    static bool Place(
        PlacementScan scan,
        AtlasGrid grid,
        PackUnit unit,
        int turns,
        bool sweep,
        int tile,
        int resolution,
        UvPlacement[] placements,
        Frame[] frame,
        JobScheduler? scheduler,
        int batch
    ) {
        var spot = scan.Best(grid, unit.Orientations, turns, sweep ? grid.Breakpoints() : null, scheduler, batch);

        if (!spot.Exists) {
            return false;
        }

        grid.Commit(unit.Orientations[spot.Rotation], spot.X, spot.Y);

        foreach (var member in unit.Members) {
            var (x, y) = unit.Locate(member, spot.Rotation);
            var slack = frame[member.Island];

            // Turning the mask reflects it about the *rounded-up* edge; `Apply` reflects the island
            // about its own. The two differ by a constant, and a constant belongs in the offset.
            var shift = (spot.Rotation & 3) switch {
                1 => new Vector2(slack.PadY, 0f),
                2 => new Vector2(slack.PadX, slack.PadY),
                3 => new Vector2(0f, slack.PadX),
                _ => Vector2.Zero
            };

            placements[member.Island] = new(
                member.Island,
                new((spot.X + x + shift.X) / resolution, (spot.Y + y + shift.Y) / resolution),
                slack.Scale,
                spot.Rotation,
                new(tile, 0)
            );
        }

        return true;
    }

    /// <summary>What turning an island's mask costs its offset, and what its coordinates scale by.</summary>
    /// <param name="Scale">UV per island coordinate.</param>
    /// <param name="PadX">How much wider the mask is than the island, in texels. Always in <c>[0, 1)</c>.</param>
    /// <param name="PadY">The same for its height.</param>
    readonly record struct Frame(float Scale, float PadX, float PadY);

    static PackUnit[] Singles(IslandMask[][] masks, ulong[] contents) {
        var units = new PackUnit[masks.Length];

        for (var index = 0; index < masks.Length; index++) {
            var mask = masks[index][0];

            units[index] = new() {
                Orientations = masks[index],
                Members = [new(index, 0, 0, mask.Width, mask.Height)],
                Area = mask.Area,
                Content = contents[index],
                Order = index
            };
        }

        return units;
    }

    /// <summary>docs/plan/42 § D7's third rung: near-rectangular neighbours become one composite rectangle.</summary>
    static PackUnit[] SuperPatches(
        IslandMask[][] masks,
        ulong[] contents,
        int margin,
        int resolution,
        int turns
    ) {
        var candidates = new List<int>();

        for (var index = 0; index < masks.Length; index++) {
            var mask = masks[index][0];

            if (mask.Area >= Rectangular * mask.Width * mask.Height && mask.Height > 1) {
                candidates.Add(index);
            }
        }

        // ⚠ The same tie-break chain as the unit sort below, and for a worse reason. There, a
        // comparison ending at the input index exchanges two units the packer cannot tell apart. Here
        // it decides *which islands are grouped together*, so an input index in the key does not move
        // one island — it builds different composites, and every placement in the atlas moves with
        // them. Measured before the content key existed: 214 of 300 permutations produced a different
        // atlas on this rung, by hundreds of texels, against none on the other two.
        candidates.Sort(
            (left, right) => {
                var byHeight = masks[right][0].Height.CompareTo(masks[left][0].Height);

                if (byHeight != 0) {
                    return byHeight;
                }

                var bySignature = masks[left][0].Signature.CompareTo(masks[right][0].Signature);

                if (bySignature != 0) {
                    return bySignature;
                }

                return contents[left] != contents[right]
                    ? contents[left].CompareTo(contents[right])
                    : left.CompareTo(right);
            }
        );

        var grouped = new bool[masks.Length];
        var units = new List<PackUnit>();
        var ceiling = resolution - (2 * margin);

        for (var seat = 0; seat < candidates.Count; seat++) {
            var seed = candidates[seat];

            if (grouped[seed]) {
                continue;
            }

            var height = masks[seed][0].Height;
            var members = new List<int> { seed };
            var width = masks[seed][0].Width;

            for (var next = seat + 1; next < candidates.Count && members.Count < PatchWidth; next++) {
                var other = candidates[next];

                if (grouped[other] || masks[other][0].Height < 0.7f * height) {
                    continue;
                }

                var widened = width + margin + masks[other][0].Width;

                if (widened > ceiling) {
                    continue;
                }

                width = widened;
                members.Add(other);
            }

            if (members.Count < 2 || height > ceiling) {
                continue;
            }

            foreach (var member in members) {
                grouped[member] = true;
            }

            units.Add(Composite(masks, contents, members, width, height, margin, turns));
        }

        for (var index = 0; index < masks.Length; index++) {
            if (grouped[index]) {
                continue;
            }

            var mask = masks[index][0];

            units.Add(
                new() {
                    Orientations = masks[index],
                    Members = [new(index, 0, 0, mask.Width, mask.Height)],
                    Area = mask.Area,
                    Content = contents[index],
                    Order = index
                }
            );
        }

        return [.. units];
    }

    static PackUnit Composite(
        IslandMask[][] masks,
        ulong[] contents,
        List<int> members,
        int width,
        int height,
        int margin,
        int turns
    ) {
        var coverage = new byte[width * height];
        var placed = new UnitMember[members.Count];
        var area = 0;
        var cursor = 0;
        var order = int.MaxValue;
        var content = Seed;

        for (var slot = 0; slot < members.Count; slot++) {
            var island = members[slot];
            var mask = masks[island][0];
            var pixels = mask.ToCoverage();

            for (var y = 0; y < mask.Height; y++) {
                for (var x = 0; x < mask.Width; x++) {
                    if (pixels[(y * mask.Width) + x] != 0) {
                        coverage[(y * width) + cursor + x] = 1;
                    }
                }
            }

            placed[slot] = new(island, cursor, 0, mask.Width, mask.Height);
            area += mask.Area;
            content = Mix(content, contents[island]);
            order = Math.Min(order, island);
            cursor += mask.Width + margin;
        }

        return new() {
            Orientations = Orientations(coverage, width, height, margin, turns),
            Members = placed,
            Area = area,
            Content = content,
            Order = order
        };
    }

    /// <summary>Builds a mask per quarter turn by turning the bitmap, which is exact.</summary>
    static IslandMask[] Orientations(byte[] coverage, int width, int height, int margin, int turns) {
        var orientations = new IslandMask[turns];
        var current = coverage;
        var currentWidth = width;
        var currentHeight = height;

        for (var turn = 0; turn < turns; turn++) {
            orientations[turn] = IslandMask.Build(current, currentWidth, currentHeight, margin);

            if (turn + 1 < turns) {
                current = IslandMask.Turn(current, currentWidth, currentHeight);
                (currentWidth, currentHeight) = (currentHeight, currentWidth);
            }
        }

        return orientations;
    }

    readonly struct Rasterizer(
        IReadOnlyList<UvIsland> islands,
        float[] factors,
        IslandMask[][] masks,
        bool[] truncated,
        int[] area,
        float scale,
        int margin,
        int resolution,
        int turns,
        bool solid
    ) : IJobParallelFor {
        public void Execute(int index) {
            var coverage = IslandMask.Rasterize(
                islands[index],
                factors[index] * scale,
                resolution,
                out var width,
                out var height,
                out var clamped
            );

            var covered = 0;

            foreach (var texel in coverage) {
                covered += texel;
            }

            area[index] = Math.Max(1, covered);

            if (solid) {
                // The rectangle rung is the same machinery over a bounding box, which is what makes
                // the comparison in the tests an honest one: one packer, one margin rule, one order.
                Array.Fill(coverage, (byte)1);
            }

            truncated[index] = clamped;
            masks[index] = Orientations(coverage, width, height, margin, turns);
        }
    }
}
