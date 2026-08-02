// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>How a paint layer's texels combine with the layers under it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A different axis from <see cref="TerrainBlend" />, and the design calls both of them
///         "blend mode".</b> <see cref="TerrainBlend" /> is a <em>storage</em> question — whether the
///         layer takes part in the sum-to-one budget — and this is a <em>shading</em> one: given the
///         weight the storage produced, how does the material combine this layer's albedo with the
///         rest. A layer can be weight-blended in storage and height-blended in shading, and the
///         common case is exactly that.
///     </para>
///     <para>
///         The three are [docs/plan/31 § D6]'s, and they are Unity's names because they are the ones
///         an artist already knows.
///     </para>
/// </remarks>
public enum TerrainLayerBlend {
    /// <summary>The weight is the mix. Linear, and what a ground layer wants.</summary>
    Weight,

    /// <summary>
    ///     The layer's own height map biases the mix, so gravel shows through mud in its cracks.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one that makes a terrain stop looking like a cross-fade.</b> A linear blend of
    ///     two ground textures over a metre of transition reads as neither of them; a height blend
    ///     puts the coarser one in the low spots of the finer one, which is what the boundary between
    ///     two grounds actually looks like. <see cref="TerrainLayerDescription.HeightContrast" /> is
    ///     how sharp it gets.
    /// </remarks>
    Height,

    /// <summary>The layer's alpha channel gates it, so a decal-like layer can have holes.</summary>
    Alpha
}

/// <summary>
///     One reusable ground material: what a <c>.vxlayer</c> asset holds.
/// </summary>
/// <param name="Name">What the layer is called, which is what the paint panel lists.</param>
/// <param name="Albedo">The base colour texture, by asset name.</param>
/// <param name="Normal">Its normal map, or empty for a flat one.</param>
/// <param name="Surface">
///     Its packed occlusion / roughness / metalness texture, or empty for the defaults.
/// </param>
/// <param name="TilingMetres">How many metres of world one repeat of the textures covers.</param>
/// <param name="Offset">Where the tiling starts, in metres.</param>
/// <param name="Blend">How its texels combine with the layers under it.</param>
/// <param name="HeightContrast">
///     How sharply a height blend transitions, in the height map's own units.
/// </param>
/// <param name="PhysicsMaterial">
///     What the ground it represents is made of, so a footstep knows it is on gravel.
/// </param>
/// <remarks>
///     <para>
///         <b>Unity's reusable layer asset, and it is reusable for the reason theirs is</b> — the
///         same gravel is the third layer of one terrain and the first layer of another, and an
///         author who has to re-enter its tiling twice will enter it differently twice.
///     </para>
///     <para>
///         ⚠ <b>Textures are named, not handled.</b> This assembly has no device and no asset
///         database — [§ D1] — and a handle names a slot in a world that issued it, while a
///         <c>.vxlayer</c> is read by a world that has not run yet. It is the same choice
///         <c>TerrainComponent.Terrain</c> makes, for the same reason.
///     </para>
///     <para>
///         ⚠ <b>The tiling is in metres of world, not in repeats per terrain.</b> Repeats-per-terrain
///         is the spelling that makes a layer stop being reusable — the same number means a different
///         texel density on a terrain of another size — and getting it wrong is the mistake in
///         Unreal's own quick-start guide's troubleshooting section.
///     </para>
/// </remarks>
public readonly record struct TerrainLayerDescription(
    string Name,
    string Albedo = "",
    string Normal = "",
    string Surface = "",
    float TilingMetres = 4f,
    Vector2 Offset = default,
    TerrainLayerBlend Blend = TerrainLayerBlend.Weight,
    float HeightContrast = 0.5f,
    string PhysicsMaterial = ""
) {
    /// <summary>The smallest tiling that is a tiling rather than a divide by zero.</summary>
    public const float MinimumTiling = 0.001f;

    /// <summary>A layer with nothing but a name, which is what "create layer" makes.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The layer.</returns>
    public static TerrainLayerDescription Of(string name) => new(name);

    /// <summary>How many metres one repeat covers, guarded against zero.</summary>
    public float Tiling => TilingMetres > MinimumTiling ? TilingMetres : MinimumTiling;

    /// <summary>Whether the material has to sample a height map for this layer.</summary>
    public bool NeedsHeight => Blend == TerrainLayerBlend.Height;

    /// <summary>Why this layer cannot be used, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     ⚠ <b>A missing albedo is a warning rather than a refusal, and a missing height map for a
    ///     height blend is a refusal.</b> A layer with no albedo draws as its own flat colour, which
    ///     is a reasonable placeholder while somebody is still painting; a height blend with nothing
    ///     to read the height from silently degrades to a weight blend, which is the class of failure
    ///     that gets reported as "the height blending does not work".
    /// </remarks>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Name)) {
            return "A layer needs a name; it is what the paint panel lists it as.";
        }

        if (TilingMetres <= MinimumTiling) {
            return $"'{Name}' tiles every {TilingMetres} m, which is not a distance. "
                + "The tiling is how many metres of world one repeat covers.";
        }

        if (Blend == TerrainLayerBlend.Height && string.IsNullOrWhiteSpace(Surface)) {
            return $"'{Name}' blends by height and has no surface texture to read the height from. "
                + "Assign one, or blend it by weight.";
        }

        return null;
    }
}
