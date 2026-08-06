// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv;

/// <summary>Texel density as a constraint: uniform by default, with an override and a multiplier.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42 § D9.</b> Islands are scaled to a uniform texels-per-world-unit, and this is
///         where "except this material" and "except this chart" are said. ⚠ <b>The default has to be
///         uniform, because non-uniform density is invisible in the atlas and glaring in the game</b> —
///         the classic symptom being a character's face at half the resolution of their boots, which
///         nobody notices until the asset is in a scene next to something correct.
///     </para>
///     <para>
///         ⚠ <b>Everything here is a change to an island's <see cref="UvIsland.Scale" /> and never to
///         its coordinates.</b> <c>Scale</c> is coordinates per world unit and it is the only thing
///         <see cref="PackSettings.TexelDensity" /> divides by, so telling the packer an island is
///         twice the size it said it was is exactly "give this island twice the texels per metre" — and
///         it is the one way to say it that leaves the island's own shape untouched, which is
///         docs/plan/42's exit criterion 7.
///     </para>
///     <para>
///         ⚠ <b>Measured: uniform mode holds every chart to <c>0.0000 %</c> of the mean, and the
///         default does not.</b> Criterion 5 asks for 2 %. With <see cref="PackSettings.TexelDensity" />
///         set, an independent recomputation off the placements and the mesh — atlas texels over world
///         area, per island — comes out identical across every chart on every fixture. With it left at
///         its default of zero, which keeps each island at whatever scale the flattener gave it, the
///         same measurement spreads by <b>22.9 %</b> on a hemisphere and <b>12.9 %</b> on a saddle. The
///         zero is not a density; it is the absence of one, and § D9's default is the other value.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// var uniform = new PackSettings { Resolution = 2048, Margin = 4, TexelDensity = UvDensity.Reference(islands) };
/// var placements = UvUnwrap.Pack(islands, uniform, out var report);
///
/// var achieved = UvDensity.Measure(islands, placements, uniform.Resolution);
/// var spread = UvDensity.Spread(achieved);        // criterion 5 asks for 0.02
///     </code>
/// </example>
public static class UvDensity {
    /// <summary>A texels-per-world-unit that keeps the islands about the size they arrived at.</summary>
    /// <param name="islands">The islands.</param>
    /// <returns>The density, or zero when no island carries a usable scale or a usable area.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="islands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>For the caller who wants uniform density and has no number in mind</b>, which is most
    ///         of them: a texels-per-metre figure is a decision about a project, and the packer's
    ///         overflow search rescales everything uniformly to fit anyway — so what this value has to
    ///         be right about is the <i>ratio</i> between islands, not the absolute.
    ///     </para>
    ///     <para>
    ///         The definition is the one that conserves texture: the single density whose total
    ///         parameter area equals the total the islands already have. An island's world area is its
    ///         parameter area over its scale squared, so that is
    ///         <c>√(Σ paramArea ÷ Σ paramArea/scale²)</c> — an area-weighted mean that a handful of
    ///         tiny islands cannot drag around, which a plain average of the scales can.
    ///     </para>
    /// </remarks>
    public static float Reference(IReadOnlyList<UvIsland> islands) {
        ArgumentNullException.ThrowIfNull(islands);

        var parameter = 0d;
        var world = 0d;

        for (var index = 0; index < islands.Count; index++) {
            var island = islands[index];

            if (!(island.Scale > 0f) || !float.IsFinite(island.Scale)) {
                continue;
            }

            var area = ParameterArea(island);

            if (!(area > 0d)) {
                continue;
            }

            parameter += area;
            world += area / ((double)island.Scale * island.Scale);
        }

        return world > 0d ? (float)Math.Sqrt(parameter / world) : 0f;
    }

    /// <summary>The same islands, with a per-chart multiplier on the density each one will get.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="multipliers">One factor per island. One leaves it alone; two doubles its texels per world unit.</param>
    /// <returns>Islands whose coordinates are identical and whose <see cref="UvIsland.Scale" /> has moved.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The two lists are different lengths.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A multiplier is not finite and positive.</exception>
    /// <remarks>
    ///     ⚠ <b>A multiplier and not a density, because the two compose and only one of them survives a
    ///     rescale.</b> When the islands do not fit, the packer scales every one of them by the same
    ///     factor and says so — a chart asked for at twice its neighbours' density is still at twice
    ///     theirs afterwards, where a chart asked for at an absolute figure would silently stop being at
    ///     it. § D9's report answers <i>"did the packer quietly rescale something"</i>; this is what
    ///     makes the answer "yes, all of it, by the same amount" instead of "yes, some of it".
    /// </remarks>
    public static IReadOnlyList<UvIsland> Weight(IReadOnlyList<UvIsland> islands, IReadOnlyList<float> multipliers) {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(multipliers);

        if (islands.Count != multipliers.Count) {
            throw new ArgumentException(
                $"{multipliers.Count} multipliers for {islands.Count} islands. There is one per island, "
                + "because a per-chart density is a statement about a chart.",
                nameof(multipliers)
            );
        }

        var weighted = new UvIsland[islands.Count];

        for (var index = 0; index < islands.Count; index++) {
            var multiplier = multipliers[index];

            if (!(multiplier > 0f) || !float.IsFinite(multiplier)) {
                throw new ArgumentOutOfRangeException(
                    nameof(multipliers),
                    multiplier,
                    $"Island {index} was given a multiplier of {multiplier}. A density multiplier is a "
                    + "positive finite ratio; zero would ask for an island of no texels at all."
                );
            }

            var island = islands[index];

            // ⚠ Divided rather than multiplied. `Scale` is coordinates per world unit and the packer
            // computes `TexelDensity / Scale`, so an island that claims to be *larger* in the world is
            // the island that gets *more* texels for the same coordinates.
            weighted[index] = island with { Scale = island.Scale / multiplier };
        }

        return weighted;
    }

    /// <summary>The same islands, with a density override per material rather than per chart.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="materials">Which material each island belongs to, as an index into <paramref name="densities" />.</param>
    /// <param name="densities">The texels per world unit each material wants, or zero to take the reference.</param>
    /// <param name="reference">The density the rest of the atlas is packed at — <see cref="Reference" />'s answer, usually.</param>
    /// <returns>Islands whose <see cref="UvIsland.Scale" /> carries the override.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An island names a material that is not in the list.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reference" /> is not finite and positive.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/42 § D9's override, expressed as § D9's multiplier.</b> A material that
    ///         wants 512 texels per metre in an atlas packed at 256 is a multiplier of two, so this is
    ///         <see cref="Weight" /> with the division done for the caller — which matters because
    ///         doing it the other way round is the mistake that halves a face's resolution.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Parallel arrays and no dictionary, deliberately.</b> A material-to-density map read
    ///         through a <see cref="Dictionary{TKey,TValue}" /> is a hash order, and a hash order that
    ///         reaches a greedy pass is an atlas that differs between runtimes. Nothing here enumerates
    ///         an unordered collection; the island order is the input order and the material order is
    ///         the caller's.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<UvIsland> Override(
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<int> materials,
        IReadOnlyList<float> densities,
        float reference
    ) {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(densities);

        if (!(reference > 0f) || !float.IsFinite(reference)) {
            throw new ArgumentOutOfRangeException(
                nameof(reference),
                reference,
                "The reference density is what every override is a ratio against, so it has to be a "
                + "positive finite texels-per-world-unit. UvDensity.Reference computes one."
            );
        }

        if (islands.Count != materials.Count) {
            throw new ArgumentException(
                $"{materials.Count} material assignments for {islands.Count} islands.",
                nameof(materials)
            );
        }

        var multipliers = new float[islands.Count];

        for (var index = 0; index < islands.Count; index++) {
            var material = materials[index];

            if (material < 0 || material >= densities.Count) {
                throw new ArgumentException(
                    $"Island {index} names material {material} and there are {densities.Count} of them.",
                    nameof(materials)
                );
            }

            var wanted = densities[material];

            multipliers[index] = wanted > 0f && float.IsFinite(wanted) ? wanted / reference : 1f;
        }

        return Weight(islands, multipliers);
    }

    /// <summary>What density each island actually got, measured off the placements rather than asked for.</summary>
    /// <param name="islands">
    ///     The islands, carrying their <i>true</i> coordinates-per-world-unit. ⚠ Not the output of
    ///     <see cref="Weight" /> — see the remarks.
    /// </param>
    /// <param name="placements">What came back from the packer.</param>
    /// <param name="resolution">The atlas's edge in texels, which is what turns unit-square coordinates into texels.</param>
    /// <returns>Texels per world unit, one per island, in the islands' own order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resolution" /> is not positive.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the number a texture artist sees, and it is not the same measurement
    ///         <see cref="UvReport.TexelDensity" /> makes.</b> The report's figure is computed from the
    ///         factors the packer applied; this one goes back through
    ///         <see cref="UvPlacement.Apply" /> and the island's own parameter area. The two agreeing
    ///         is the check worth having — a metric that agrees with a naive recomputation is one you
    ///         can trust — and computing it only one way is how a field that is structurally correct by
    ///         construction gets mistaken for a measurement.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Hand it the islands the flattener produced and never the ones
    ///         <see cref="Weight" /> returned.</b> A multiplier is expressed as a change to
    ///         <see cref="UvIsland.Scale" /> — the island tells the packer it is larger in the world
    ///         than it is — and this measurement divides by that same scale to recover the world area.
    ///         Given the weighted list it would believe the claim and report every island at the same
    ///         density, which is the one answer that makes a per-chart multiplier look like it did
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Density is measured before the margin and not after, and the two differ.</b> The
    ///         margin is empty space <i>between</i> islands: it costs atlas area, which is what
    ///         <see cref="UvReport.EffectiveEfficiency" /> is for, and it does not change how many
    ///         texels land on a square metre of surface. Charging an island for its margin band would
    ///         report a density no sampler ever sees.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<float> Measure(
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvPlacement> placements,
        int resolution
    ) {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);

        var measured = new float[islands.Count];

        foreach (var placement in placements) {
            if (placement.Island < 0 || placement.Island >= islands.Count) {
                continue;
            }

            var island = islands[placement.Island];
            var parameter = ParameterArea(island);

            if (!(parameter > 0d) || !(island.Scale > 0f) || !float.IsFinite(island.Scale)) {
                continue;
            }

            // The island's world area is its own parameter area over its own scale squared, and the
            // atlas area is the same parameter area under the placement's uniform scale, in texels.
            var world = parameter / ((double)island.Scale * island.Scale);
            var atlas = parameter * ((double)placement.Scale * resolution * placement.Scale * resolution);

            measured[placement.Island] = (float)Math.Sqrt(atlas / world);
        }

        return measured;
    }

    /// <summary>How far from uniform a set of measured densities is, as a fraction of its mean.</summary>
    /// <param name="densities">What <see cref="Measure" /> returned.</param>
    /// <returns>The full range over the mean, or zero when there is nothing to compare.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="densities" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The full range and not a standard deviation, because docs/plan/42's exit criterion 5 is
    ///     a statement about <i>every</i> chart.</b> "Within 2 % across every chart" is violated by one
    ///     chart at 10 % among four hundred at zero, and a variance over that set is a number small
    ///     enough to pass. Islands with no usable density are skipped rather than counted as zero,
    ///     which would make the spread 100 % for a reason that has nothing to do with the packer.
    /// </remarks>
    public static float Spread(IReadOnlyList<float> densities) {
        ArgumentNullException.ThrowIfNull(densities);

        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        var total = 0d;
        var count = 0;

        for (var index = 0; index < densities.Count; index++) {
            var density = densities[index];

            if (!(density > 0f) || !float.IsFinite(density)) {
                continue;
            }

            minimum = MathF.Min(minimum, density);
            maximum = MathF.Max(maximum, density);
            total += density;
            count++;
        }

        if (count == 0) {
            return 0f;
        }

        var mean = total / count;

        return mean > 0d ? (float)((maximum - minimum) / mean) : 0f;
    }

    /// <summary>The area an island covers in its own coordinates, summed over its triangles.</summary>
    static double ParameterArea(UvIsland island) {
        if (island.Coordinates is null || island.Corners is null) {
            return 0d;
        }

        var area = 0d;

        for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
            var a = island.Coordinates[(triangle * 3) + 0];
            var b = island.Coordinates[(triangle * 3) + 1];
            var c = island.Coordinates[(triangle * 3) + 2];

            area += 0.5d * Math.Abs((((double)b.X - a.X) * ((double)c.Y - a.Y))
                - (((double)b.Y - a.Y) * ((double)c.X - a.X)));
        }

        return area;
    }
}
