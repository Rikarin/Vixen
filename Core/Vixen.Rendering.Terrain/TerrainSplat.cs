// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     What the generated splat material compiles as, derived from a terrain's layer list.
/// </summary>
/// <param name="LayerSlots">
///     How many layer slots the shader loops over. 4, 8, 12 or 16.
/// </param>
/// <param name="HeightBlend">Whether any layer wants the height path.</param>
/// <param name="WeightMaps">How many RGBA weightmaps the layers pack into.</param>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D6]: the material is generated from the layer list, and nobody wires a
///         graph.</b> Every Unreal project rebuilds the same <c>LandscapeLayerBlend</c> material and
///         every one of them rebuilds it slightly differently, which is why "why is my landscape
///         black" is the most-asked landscape question. A configuration that cannot be miswired does
///         not need a troubleshooting step.
///     </para>
///     <para>
///         ⚠ <b>Two permutation axes and no more.</b> The layer <em>count</em> is quantised to
///         4/8/12/16 so a terrain gaining a seventh layer does not compile a new shader, and the
///         height path is one bool so a terrain that blends only by weight pays for none of it. What
///         is deliberately <em>not</em> a permutation is which mode each layer uses: that is per
///         layer and the permutation is per material, so eight layers with three modes between them
///         is one shader rather than eight.
///     </para>
/// </remarks>
public readonly record struct TerrainSplat(int LayerSlots, bool HeightBlend, int WeightMaps) {
    /// <summary>The most layers a generated material will loop over.</summary>
    /// <remarks>
    ///     Sixteen. Above it the answer is a virtual texture or two terrains —
    ///     [§ D7](../../docs/plan/31-terrain-grass-and-trees.md) — and neither is a bigger loop.
    /// </remarks>
    public const int MaxLayerSlots = 16;

    /// <summary>How many layers one RGBA weightmap holds.</summary>
    public const int LayersPerWeightMap = 4;

    /// <summary>What a terrain's layers compile to.</summary>
    /// <param name="weights">The terrain's paint channels.</param>
    /// <returns>The permutation.</returns>
    /// <exception cref="ArgumentException">There are more layers than a material can loop over.</exception>
    public static TerrainSplat Of(TerrainWeights weights) {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.LayerCount > MaxLayerSlots) {
            throw new ArgumentException(
                $"A generated terrain material loops over at most {MaxLayerSlots} layers and this "
                + $"terrain has {weights.LayerCount}. Above that the answer is a virtual texture or "
                + "two terrains — see docs/plan/31 § D7 — rather than a longer loop.",
                nameof(weights)
            );
        }

        var height = false;

        for (var layer = 0; layer < weights.LayerCount; layer++) {
            height |= weights.LayerOf(layer).NeedsHeight;
        }

        return new(SlotsFor(weights.LayerCount), height, (weights.LayerCount + 3) / LayersPerWeightMap);
    }

    /// <summary>Which slot count a layer count compiles for.</summary>
    /// <param name="layers">How many layers there are.</param>
    /// <returns>4, 8, 12 or 16 — and 4 for a terrain with none, because a loop of zero is not one.</returns>
    public static int SlotsFor(int layers) =>
        Math.Clamp(((Math.Max(1, layers) + 3) / LayersPerWeightMap) * LayersPerWeightMap, LayersPerWeightMap, MaxLayerSlots);

    /// <summary>How many slots the loop runs and never reads a layer for.</summary>
    /// <remarks>
    ///     ⚠ <b>Wasted iterations are the price of not recompiling, and they cost nothing.</b> A slot
    ///     with no layer has a weight of zero at every sample, so the loop's early-out skips it before
    ///     it samples anything — which is the same arithmetic that makes an eight-layer terrain touch
    ///     two or three textures per pixel rather than eight.
    /// </remarks>
    public int UnusedSlots(int layers) => LayerSlots - Math.Min(layers, LayerSlots);

    /// <summary>The permutation keys the effect is compiled with.</summary>
    /// <returns>The name and value of each.</returns>
    /// <remarks>
    ///     Names rather than an effect object, because this assembly decides <em>what</em> to compile
    ///     and the shader system decides how — the same split every other generated material makes.
    /// </remarks>
    public IReadOnlyList<(string Key, object Value)> Permutations() => [
        ("Vixen.Shaders.Terrain.Terrain.LayerSlots", LayerSlots),
        ("Vixen.Shaders.Terrain.Terrain.HeightBlend", HeightBlend)
    ];

    /// <summary>Fills the per-layer tiling scales the fragment stage divides its world XZ by.</summary>
    /// <param name="weights">The terrain's paint channels.</param>
    /// <param name="scales">Where to put them; at least <see cref="LayerSlots" /> long.</param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     ⚠ <b>A slot with no layer gets a positive scale, not zero.</b> The shader divides by it,
    ///     and although the early-out means it should never reach a slot with no weight, a divisor of
    ///     zero in a branch a compiler decided to flatten is a NaN across the whole terrain. One metre
    ///     is a number that cannot hurt.
    /// </remarks>
    public int FillScales(TerrainWeights weights, Span<float> scales) {
        ArgumentNullException.ThrowIfNull(weights);

        if (scales.Length < LayerSlots) {
            throw new ArgumentException(
                $"This material loops over {LayerSlots} slots and {scales.Length} were given.",
                nameof(scales)
            );
        }

        for (var slot = 0; slot < LayerSlots; slot++) {
            scales[slot] = slot < weights.LayerCount ? weights.LayerOf(slot).Tiling : 1f;
        }

        return LayerSlots;
    }

    /// <summary>Fills the per-layer blend mode and contrast the fragment stage reads.</summary>
    /// <param name="weights">The terrain's paint channels.</param>
    /// <param name="blends">Where to put them; at least <see cref="LayerSlots" /> long.</param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     ⚠ <b>The mode is a float because the shader reads a <c>float2</c> buffer</b>, and the two
    ///     halves travel together because they are always read together — a separate integer buffer
    ///     would be a second binding, a second upload and a second thing to get out of step with the
    ///     layer list.
    /// </remarks>
    public int FillBlends(TerrainWeights weights, Span<Vector2> blends) {
        ArgumentNullException.ThrowIfNull(weights);

        if (blends.Length < LayerSlots) {
            throw new ArgumentException(
                $"This material loops over {LayerSlots} slots and {blends.Length} were given.",
                nameof(blends)
            );
        }

        for (var slot = 0; slot < LayerSlots; slot++) {
            if (slot >= weights.LayerCount) {
                blends[slot] = new(0f, 1f);
                continue;
            }

            var layer = weights.LayerOf(slot);

            blends[slot] = new((float)(int)layer.Blend, MathF.Max(layer.HeightContrast, 0.001f));
        }

        return LayerSlots;
    }

    /// <summary>Packs the layer weights into RGBA texels, four layers to a texture.</summary>
    /// <param name="weights">The terrain's paint channels.</param>
    /// <param name="map">Which weightmap, 0-based.</param>
    /// <param name="rect">Which samples to pack.</param>
    /// <param name="texels">Where to put them, four bytes per sample, row-major.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     ⚠ <b>A channel with no layer is written zero rather than left alone.</b> The buffer is
    ///     reused between uploads, so a terrain that loses its fourth layer would keep drawing it out
    ///     of whatever the last pack left in the alpha channel — a ground that disappears from the
    ///     panel and stays on the terrain.
    /// </remarks>
    public static int Pack(TerrainWeights weights, int map, TerrainRect rect, Span<byte> texels) {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegative(map);

        var required = rect.Count * LayersPerWeightMap;

        if (texels.Length < required) {
            throw new ArgumentException(
                $"A {rect.Width}×{rect.Height} weightmap region is {required} bytes, not {texels.Length}.",
                nameof(texels)
            );
        }

        var at = 0;

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                for (var channel = 0; channel < LayersPerWeightMap; channel++) {
                    var layer = (map * LayersPerWeightMap) + channel;

                    texels[at++] = layer < weights.LayerCount ? weights.WeightAt(layer, x, z) : (byte)0;
                }
            }
        }

        return required;
    }
}
