// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.DistanceFields;

/// <summary>What a bake is being asked for: how finely to sample, and how hard to work at the sign.</summary>
/// <remarks>
///     <para>
///         <b>Resolution is the quality dial and the cost dial at once.</b>
///         <see cref="Resolution" /> is the count along the volume's <i>longest</i> axis and the
///         others follow from the bounds, so voxels stay near-cubic whatever shape the mesh is — a
///         door frame sampled 32×32×32 would be coarse along its length and absurdly fine across its
///         thickness, and the thin axis is the one that decides whether the field leaks. Doubling it
///         is eight times the voxels and eight times the bake.
///     </para>
///     <para>
///         <b>Thin geometry is the failure everybody hits.</b> A wall thinner than a voxel cannot be
///         represented: no sample lands inside it, so the field reads as though it were not there and
///         light passes through. That is a property of the representation and not of this bake — it
///         is why <c>docs/plan/19</c> lists leaks as risk G3 and why the remedy lives at the sampling
///         end rather than here.
///     </para>
/// </remarks>
public readonly record struct DistanceFieldBuildSettings {
    /// <summary>The default settings: a 32-voxel longest axis and thirty-two sign rays.</summary>
    public DistanceFieldBuildSettings() { }

    /// <summary>How many samples along the volume's longest axis.</summary>
    /// <remarks>
    ///     The other two axes are scaled from the bounds so voxels are near-cubic, and every axis is
    ///     clamped to at least two — a field with one sample along an axis has nothing to interpolate
    ///     between and is not a field.
    /// </remarks>
    public int Resolution { get; init; } = 32;

    /// <summary>How far the volume is grown past the mesh, as a fraction of the mesh's size.</summary>
    /// <remarks>
    ///     A field whose bounds are the mesh's own has the surface lying exactly on its boundary,
    ///     where a trilinear sample has nothing on one side and a gradient is one-sided. The margin
    ///     buys the outside of the surface somewhere to be, which is what a ray approaching the mesh
    ///     needs in order to slow down before it arrives rather than at it.
    /// </remarks>
    public float BoundsExpansion { get; init; } = 0.2f;

    /// <summary>How many rays each sample casts to decide whether it is inside.</summary>
    /// <remarks>
    ///     The dominant cost of the bake, and the reason the sign is robust rather than merely
    ///     plausible — see <see cref="MeshDistanceFieldBaker" />. Below about sixteen the vote is
    ///     noisy on concave geometry; above about sixty-four it stops changing.
    /// </remarks>
    public int SignRayCount { get; init; } = 32;

    /// <summary>What fraction of rays must strike a backface before a sample counts as inside.</summary>
    /// <remarks>
    ///     A half is the natural reading of the vote and is right for a closed mesh. Lowering it
    ///     makes an open mesh — a facade, a plane, a shell exported without a back — read as solid
    ///     more readily, which is usually what a field over it is wanted for.
    /// </remarks>
    public float BackfaceThreshold { get; init; } = 0.5f;

    /// <summary>Whether the bake may use more than one thread.</summary>
    /// <remarks>
    ///     Samples do not read each other, so the result does not depend on how the work is split and
    ///     a parallel bake is byte-identical to a serial one — which is asserted rather than assumed.
    ///     The switch exists so a profile or a debugger sees one thread, not because the answer
    ///     changes.
    /// </remarks>
    public bool Parallel { get; init; } = true;

    /// <summary>Throws if these settings cannot produce a field.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfLessThan(Resolution, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(BoundsExpansion);
        ArgumentOutOfRangeException.ThrowIfLessThan(SignRayCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BackfaceThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BackfaceThreshold, 1f);
    }
}
