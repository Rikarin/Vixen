// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>One blade of grass, as the scatter produces it.</summary>
/// <param name="Instance">Where it stands, which way it faces and how big it is.</param>
/// <param name="Tint">A per-blade variation the material maps how it likes, 0…1.</param>
/// <param name="WindPhase">
///     A per-blade phase offset in radians, so a clump does not move as one object.
/// </param>
/// <remarks>
///     ⚠ <b>A blade is not a <see cref="FoliageInstance" /> plus nothing.</b> The two extra numbers
///     are exactly the ones a derived type cannot recover later: an instance an artist placed can be
///     re-hashed from its address, and a blade has no address — it exists between one cell entering
///     range and leaving it. So they ride with it, and they are the fields of
///     <c>InstanceParameters</c> a renderer copies straight across.
/// </remarks>
public readonly record struct GrassBlade(FoliageInstance Instance, float Tint, float WindPhase);

/// <summary>
///     Scattering one cell of one grass type — the CPU reference the compute pass mirrors.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D8], and it is the half a test can run.</b> A cell entering range is
///         scattered over a fixed grid of candidates; each one hashes its cell and its slot, jitters
///         itself off the grid, asks the surface what is underneath, and survives if the ground is
///         gentle enough and the layer is painted enough. Nothing about the result is written
///         anywhere: a cell leaving range drops its blades and re-scatters to the identical ones when
///         it comes back.
///     </para>
///     <para>
///         ⚠ <b>Deterministic from the cell coordinate and the slot, never from an iteration
///         order.</b> That is what makes a cell re-entering range produce the grass it had, what makes
///         two machines agree, and what makes this comparable to the dispatch at all — the seam test
///         [§ Part 4] asks for is impossible against a counter. <see cref="Hash" /> is the whole of
///         that contract and <c>GrassScatter.rvn</c> contains it transliterated.
///     </para>
///     <para>
///         ⚠ <b>The grid is fixed and the weight thins it.</b> The device runs one invocation per
///         slot, so the candidate count cannot depend on anything sampled; what the painted weight
///         decides is what fraction of the candidates survive. See
///         <see cref="GrassType.DensityAt" />.
///     </para>
///     <para>
///         ⚠ <b>No spacing check, and its absence is the difference from
///         <see cref="FoliageScatter" />.</b> A minimum spacing is a query against what is already
///         placed, which makes a candidate's fate depend on the order the others were tested in — and
///         an order is what a dispatch of sixty-five thousand parallel invocations does not have. The
///         grid <em>is</em> the spacing: a candidate cannot leave its own slot.
///     </para>
/// </remarks>
public static class GrassScatter {
    /// <summary>What a cell's X coordinate is mixed by.</summary>
    /// <remarks>The first two of Knuth's, which is what every hash in this engine starts from.</remarks>
    public const uint CellSeedX = 0x9E3779B1u;

    /// <summary>And its Z coordinate.</summary>
    public const uint CellSeedZ = 0x85EBCA77u;

    /// <summary>The stream a candidate's jitter along X is drawn from.</summary>
    /// <remarks>
    ///     Six, because <see cref="FoliageScatter" /> owns one to five — yaw, pitch, scale and the two
    ///     the disc takes. Two scatters drawing the same stream for different things is not wrong, but
    ///     it makes a blade's heading and a tree's heading the same number wherever their hashes
    ///     collide, which is a correlation nobody would think to look for.
    /// </remarks>
    public const int JitterXStream = 6;

    /// <summary>And along Z.</summary>
    public const int JitterZStream = 7;

    /// <summary>The stream the density test draws its verdict from.</summary>
    public const int DensityStream = 8;

    /// <summary>The stream a blade's tint comes from.</summary>
    public const int TintStream = 9;

    /// <summary>And its wind phase.</summary>
    public const int PhaseStream = 10;

    /// <summary>Why a candidate did not become a blade.</summary>
    public enum Refusal {
        /// <summary>It did.</summary>
        Placed,

        /// <summary>There was no ground under it.</summary>
        NoSurface,

        /// <summary>The ground was too steep.</summary>
        Slope,

        /// <summary>The layer is not painted densely enough there for this candidate.</summary>
        Density
    }

    /// <summary>The hash a candidate's every random draw comes from.</summary>
    /// <param name="cell">Which cell.</param>
    /// <param name="index">Which of its candidate slots, row-major.</param>
    /// <returns>The hash.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The seam.</b> <c>GrassScatter.rvn</c> contains this expression and
    ///         <c>GrassScatterParityTests</c> holds the two against each other, because a shader
    ///         cannot be asked whether it agrees with a function and a transliteration can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cast of a negative coordinate is a reinterpretation of its bits and has to
    ///         be on both sides.</b> C#'s <c>(uint)(-1)</c> and GLSL's <c>uint(-1)</c> are both
    ///         0xFFFFFFFF, which is what makes the four cells around the origin agree — and a shader
    ///         that took an absolute value or added a bias instead would produce a different field on
    ///         exactly the quadrant nobody tests.
    ///     </para>
    /// </remarks>
    public static uint Hash(FoliageCellKey cell, int index) =>
        FoliageScatter.Hash(((uint)cell.X * CellSeedX) ^ ((uint)cell.Z * CellSeedZ), index);

    /// <summary>Scatters one cell of one type.</summary>
    /// <param name="type">Which grass.</param>
    /// <param name="cell">Which cell.</param>
    /// <param name="grid">The cell grid it belongs to.</param>
    /// <param name="surface">What answers "what is the ground here".</param>
    /// <param name="blades">Where the survivors go. Appended, not cleared.</param>
    /// <param name="densityScale">A runtime scalar over the whole field, 0…1.</param>
    /// <returns>How many blades were produced.</returns>
    /// <exception cref="ArgumentNullException">There is no surface or nowhere to put the blades.</exception>
    public static int Scatter(
        in GrassType type,
        FoliageCellKey cell,
        FoliageCellGrid grid,
        IFoliageSurface surface,
        ICollection<GrassBlade> blades,
        float densityScale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(blades);

        var side = type.GridOf(grid.CellSize);
        var placed = 0;

        for (var index = 0; index < side * side; index++) {
            if (Consider(in type, cell, grid, surface, index, densityScale, out var blade) != Refusal.Placed) {
                continue;
            }

            blades.Add(blade);
            placed++;
        }

        return placed;
    }

    /// <summary>Where one candidate slot's blade would stand, before anything is sampled.</summary>
    /// <param name="type">Which grass.</param>
    /// <param name="cell">Which cell.</param>
    /// <param name="grid">The cell grid.</param>
    /// <param name="index">Which slot, row-major.</param>
    /// <returns>The world XZ position.</returns>
    /// <remarks>
    ///     Separate from <see cref="Consider" /> because it is the half that needs no surface, and
    ///     because it is what the dispatch computes before it decides whether to read a texture at
    ///     all.
    /// </remarks>
    public static Vector2 CandidateAt(in GrassType type, FoliageCellKey cell, FoliageCellGrid grid, int index) {
        var side = type.GridOf(grid.CellSize);
        var step = grid.CellSize / side;

        var slotX = index % side;
        var slotZ = index / side;

        var origin = grid.OriginOf(cell);
        var hash = Hash(cell, index);

        // Half a step at a jitter of one — see GrassType.Jitter. The draws are centred on zero, so a
        // jitter of zero is the grid exactly rather than the grid shifted by half of it.
        var reach = Math.Clamp(type.Jitter, 0f, 1f) * step * 0.5f;

        var x = origin.X + ((slotX + 0.5f) * step) + (((FoliageScatter.Unit(hash, JitterXStream) * 2f) - 1f) * reach);
        var z = origin.Z + ((slotZ + 0.5f) * step) + (((FoliageScatter.Unit(hash, JitterZStream) * 2f) - 1f) * reach);

        return new(x, z);
    }

    /// <summary>Whether one candidate becomes a blade, and what it would be.</summary>
    /// <param name="type">Which grass.</param>
    /// <param name="cell">Which cell.</param>
    /// <param name="grid">The cell grid.</param>
    /// <param name="surface">What answers for the ground.</param>
    /// <param name="index">Which candidate slot, row-major.</param>
    /// <param name="densityScale">A runtime scalar over the whole field, 0…1.</param>
    /// <param name="blade">What it would be.</param>
    /// <returns>Why not, or <see cref="Refusal.Placed" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The density scalar multiplies the threshold, and every blade keeps its own
    ///         draw.</b> So lowering the setting removes a subset of the blades that were there and
    ///         moves none of the rest — the field thins rather than rearranging itself, which is what
    ///         stops a quality slider from looking like a different level. It is
    ///         <c>InstanceCullSettings.DensityScale</c>'s rule, applied one stage earlier so the
    ///         instances are never produced at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refused with a reason, for <see cref="FoliageScatter.Consider" />'s reason.</b> A
    ///         field that grows nothing and does not say whether the layer is unpainted or the ground
    ///         is a cliff is the version reported as broken.
    ///     </para>
    /// </remarks>
    public static Refusal Consider(
        in GrassType type,
        FoliageCellKey cell,
        FoliageCellGrid grid,
        IFoliageSurface surface,
        int index,
        float densityScale,
        out GrassBlade blade
    ) {
        ArgumentNullException.ThrowIfNull(surface);

        blade = default;

        var at = CandidateAt(in type, cell, grid, index);
        var hash = Hash(cell, index);
        var ground = surface.SampleAt(at, type.Layer);

        if (!ground.Hit) {
            return Refusal.NoSurface;
        }

        if (ground.Slope > type.MaxSlope) {
            return Refusal.Slope;
        }

        var keep = type.DensityAt(ground.Weight) * Math.Clamp(densityScale, 0f, 1f);

        if (FoliageScatter.Unit(hash, DensityStream) >= keep) {
            return Refusal.Density;
        }

        var instance = FoliageScatter.Place(
            in ground,
            hash,
            type.RandomYaw,
            0f,
            type.MinScale,
            type.MaxScale,
            type.AlignToNormal
        );

        blade = new(
            instance,
            FoliageScatter.Unit(hash, TintStream),
            FoliageScatter.Unit(hash, PhaseStream) * MathF.Tau
        );

        return Refusal.Placed;
    }
}
