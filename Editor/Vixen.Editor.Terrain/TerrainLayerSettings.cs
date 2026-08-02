// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     One row of the target-layer panel: what the layer is, and how much of the terrain it covers.
/// </summary>
/// <param name="Index">Which paint channel it is.</param>
/// <param name="Layer">The ground it paints.</param>
/// <param name="Blend">Whether it takes part in the sum-to-one budget.</param>
/// <param name="Coverage">How much of the terrain it covers, 0…1.</param>
/// <remarks>
///     ⚠ <b>The coverage is the histogram [§ The terrain panel] asks for, reduced to one
///     number.</b> A per-layer histogram of a four-million-sample terrain is a bar nobody reads; what
///     an artist actually needs from that section is "this layer is at zero and I do not know why" —
///     which is the state you get into by painting over your base layer — and one number answers it.
/// </remarks>
public readonly record struct TerrainTargetRow(
    int Index,
    TerrainLayerDescription Layer,
    TerrainBlend Blend,
    float Coverage
) {
    /// <summary>What the row says, beside its name.</summary>
    /// <returns>The coverage as a percentage, and the blend where it is not the usual one.</returns>
    public string Caption {
        get {
            var coverage = string.Create(CultureInfo.InvariantCulture, $"{Coverage * 100f:N1}% coverage");

            return Blend == TerrainBlend.NonWeight ? coverage + " · takes from nobody" : coverage;
        }
    }
}

/// <summary>
///     The target-layer section of the terrain panel.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § The terrain panel]: the paint channels the terrain's layer list
///         declares, each with its coverage, an assign control for the <c>.vxlayer</c>, and a
///         weight-blended toggle.</b> Selecting one makes it the paint target — which is
///         <see cref="TerrainEdit.Target" />, and the tools are the strip.
///     </para>
///     <para>
///         ⚠ <b>The layer being painted changes far more often than the tool, so the list is above
///         the strip and not in it.</b> That is Unreal's layout and it is correct: an artist paints
///         grass, then rock, then grass again with the same tool, and a design that made the layer a
///         mode would make that three mode switches.
///     </para>
/// </remarks>
[DataContract("TerrainLayerSettings")]
public sealed class TerrainLayerSettings {
    /// <summary>What the layer is called.</summary>
    [Inspector]
    public string Name { get; set; } = "Layer";

    /// <summary>Its base colour texture.</summary>
    [Inspector]
    [Tooltip("The ground's colour. A layer with none draws flat, which is a fine placeholder.")]
    public string Albedo { get; set; } = "";

    /// <summary>Its normal map.</summary>
    [Inspector]
    public string Normal { get; set; } = "";

    /// <summary>Its packed occlusion / roughness / metalness, with the blend height in alpha.</summary>
    [Inspector]
    [Tooltip("Occlusion, roughness and metalness — and the height a height blend reads, in alpha.")]
    public string Surface { get; set; } = "";

    /// <summary>How many metres of world one repeat of the textures covers.</summary>
    /// <remarks>
    ///     ⚠ <b>Metres of world, not repeats per terrain.</b> Repeats-per-terrain is the spelling
    ///     that makes a layer stop being reusable, and getting it wrong is the mistake in Unreal's own
    ///     quick-start troubleshooting section.
    /// </remarks>
    [Inspector(Name = "Tiling (m)")]
    [Range(TerrainLayerDescription.MinimumTiling, 512f)]
    public float TilingMetres { get; set; } = 4f;

    /// <summary>Where the tiling starts, in metres.</summary>
    [Inspector]
    public Vector2 Offset { get; set; }

    /// <summary>How its texels combine with the layers under it.</summary>
    [Inspector]
    public TerrainLayerBlend Blend { get; set; } = TerrainLayerBlend.Weight;

    /// <summary>How sharply a height blend transitions.</summary>
    [Inspector]
    [Range(0.01f, 1f)]
    [ShowIf(nameof(IsHeightBlended))]
    [Tooltip("Narrow is a hard edge that follows the texture; wide is closer to a cross-fade.")]
    public float HeightContrast { get; set; } = 0.5f;

    /// <summary>Whether it shares the sum-to-one budget with the other layers.</summary>
    /// <remarks>
    ///     Off is the snow case: its own channel, taking from nobody, so it lies over whatever is
    ///     underneath rather than replacing it.
    /// </remarks>
    [Inspector(Name = "Weight-blended")]
    [Tooltip("Off gives the layer its own channel, so it lies over the others instead of taking from them.")]
    public bool IsWeightBlended { get; set; } = true;

    /// <summary>What the ground is made of, so a footstep knows it is on gravel.</summary>
    [Inspector]
    public string PhysicsMaterial { get; set; } = "";

    /// <summary>Whether the height-blend rows apply.</summary>
    public bool IsHeightBlended => Blend == TerrainLayerBlend.Height;

    /// <summary>Which budget the layer is in.</summary>
    public TerrainBlend BlendBudget => IsWeightBlended ? TerrainBlend.Weight : TerrainBlend.NonWeight;

    /// <summary>What the form describes.</summary>
    public TerrainLayerDescription Description =>
        new(Name, Albedo, Normal, Surface, TilingMetres, Offset, Blend, HeightContrast, PhysicsMaterial);

    /// <summary>Why the layer cannot be used, or <see langword="null" /> if it can.</summary>
    public string? Validate() => Description.Validate();

    /// <summary>Whether it can.</summary>
    public bool IsValid => Validate() is null;

    /// <summary>Fills the form in from a layer that already exists.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="blend">Which budget it is in.</param>
    /// <returns>The form.</returns>
    public static TerrainLayerSettings Of(TerrainLayerDescription layer, TerrainBlend blend = TerrainBlend.Weight) =>
        new() {
            Name = layer.Name,
            Albedo = layer.Albedo,
            Normal = layer.Normal,
            Surface = layer.Surface,
            TilingMetres = layer.TilingMetres,
            Offset = layer.Offset,
            Blend = layer.Blend,
            HeightContrast = layer.HeightContrast,
            PhysicsMaterial = layer.PhysicsMaterial,
            IsWeightBlended = blend == TerrainBlend.Weight
        };

    /// <summary>The rows the target-layer section draws.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <returns>One per paint channel, in order.</returns>
    /// <remarks>
    ///     Rebuilt on demand rather than kept: the coverage is a reduction over every sample, and a
    ///     cached one is a number that is right until somebody paints. The panel asks when it draws,
    ///     which is the only moment it matters.
    /// </remarks>
    public static IReadOnlyList<TerrainTargetRow> Rows(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var weights = terrain.Weights;
        var rows = new TerrainTargetRow[weights.LayerCount];

        for (var index = 0; index < rows.Length; index++) {
            rows[index] = new(index, weights.LayerOf(index), weights.BlendOf(index), weights.CoverageOf(index));
        }

        return rows;
    }
}
