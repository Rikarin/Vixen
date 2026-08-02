// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>How a paint layer combines with the ones beside it.</summary>
public enum TerrainBlend {
    /// <summary>
    ///     Shares one budget with every other weight-blended layer: painting it lowers the rest.
    /// </summary>
    Weight,

    /// <summary>
    ///     Has its own channel and takes from nobody. Snow over everything, and puddles.
    /// </summary>
    NonWeight
}

/// <summary>
///     The paint channels of a terrain, and the invariant that keeps them meaning something.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D5].</b> Weight-blended layers at a sample sum to one — stored as
///         <see cref="byte" /> summing to 255 — and painting one raises it while lowering the others
///         <em>proportionally</em>. Proportionally rather than uniformly is Unreal's rule and it is
///         the only one that does not produce a layer which can never be removed: subtracting the
///         same amount from every other layer drives the small ones to zero first and then has
///         nowhere left to take from, so the layer being painted stops being able to reach one.
///     </para>
///     <para>
///         <b>Four channels per weightmap on the device, but not here.</b> A layer is added and
///         removed on its own, so it gets its own array; the packing into RGBA textures is the
///         renderer's, and it is why the layer <em>count</em> is what the generated material
///         permutes on.
///     </para>
///     <para>
///         ⚠ <b>The invariant is asserted with the offending layer named.</b> A weight-sum drift is a
///         rounding bug that presents as a barely-visible tint and is otherwise unattributable — the
///         terrain looks very slightly wrong and nothing says where. <see cref="Verify" /> is the
///         gate, and it reports which layer is carrying the excess.
///     </para>
/// </remarks>
public sealed class TerrainWeights {
    /// <summary>What the weight-blended layers sum to at every sample.</summary>
    public const int Total = 255;

    readonly List<byte[]> channels = [];
    readonly List<TerrainBlend> blends = [];
    readonly List<TerrainLayerDescription> layers = [];
    readonly int samples;

    /// <summary>Creates the paint channels of a terrain, with no layers in them.</summary>
    /// <param name="description">The terrain's shape.</param>
    public TerrainWeights(TerrainDescription description) {
        Description = description;
        samples = (int)description.SampleCount;
    }

    /// <summary>The terrain's shape.</summary>
    public TerrainDescription Description { get; }

    /// <summary>How many paint layers there are.</summary>
    public int LayerCount => channels.Count;

    /// <summary>What each layer is called, in order.</summary>
    public IReadOnlyList<string> Names => layers.Select(layer => layer.Name).ToArray();

    /// <summary>What ground each layer is, in order — the <c>.vxlayer</c> each one names.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the channel rather than in a parallel list somewhere else.</b> A layer's
    ///     weights and its material are added and removed together, always; keeping them in two
    ///     containers is how a terrain ends up with six channels and five materials, which draws as
    ///     the last layer painted in the second-to-last layer's ground.
    /// </remarks>
    public IReadOnlyList<TerrainLayerDescription> Layers => layers;

    /// <summary>How many weightmap textures the device needs, at four channels each.</summary>
    public int WeightmapCount => (LayerCount + 3) / 4;

    /// <summary>Which layer-count permutation the generated material compiles for.</summary>
    /// <remarks>
    ///     Quantised to 4, 8, 12 or 16 so that adding a seventh layer does not compile a new shader —
    ///     [docs/plan/31 § D6]. Above sixteen the answer is a virtual texture or two terrains.
    /// </remarks>
    public int MaterialLayerSlots => Math.Min(16, ((LayerCount + 3) / 4) * 4);

    /// <summary>Adds a paint layer.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="blend">How it combines with the layers beside it.</param>
    /// <returns>Its index.</returns>
    /// <remarks>
    ///     <b>The first weight-blended layer starts at full coverage and the rest start at none.</b>
    ///     A terrain whose layers all start at zero has no valid weights anywhere — the invariant is
    ///     broken from the moment the second layer exists — and the first thing every artist does is
    ///     paint the base layer over the entire terrain, which is what the quick-start guides call a
    ///     troubleshooting step and what this makes unnecessary.
    /// </remarks>
    public int AddLayer(string name, TerrainBlend blend = TerrainBlend.Weight) =>
        AddLayer(TerrainLayerDescription.Of(name), blend);

    /// <summary>Adds a paint layer with the ground it paints.</summary>
    /// <param name="layer">What the layer is.</param>
    /// <param name="blend">How it combines with the layers beside it.</param>
    /// <returns>Its index.</returns>
    /// <remarks>See the other overload for why the first weight-blended layer starts at full
    ///     coverage.</remarks>
    public int AddLayer(TerrainLayerDescription layer, TerrainBlend blend = TerrainBlend.Weight) {
        var channel = new byte[samples];
        var isFirstWeighted = blend == TerrainBlend.Weight && !blends.Contains(TerrainBlend.Weight);

        if (isFirstWeighted) {
            Array.Fill(channel, (byte)Total);
        }

        channels.Add(channel);
        blends.Add(blend);
        layers.Add(layer);

        return channels.Count - 1;
    }

    /// <summary>Removes a paint layer, redistributing what it held.</summary>
    /// <param name="layer">Which layer.</param>
    /// <remarks>
    ///     ⚠ <b>Removing a weight-blended layer has to give its weight to somebody</b>, or every
    ///     sample it covered drops below the total and the material reads a hole. It goes to the
    ///     remaining weight-blended layers in proportion, and to the first of them where there is
    ///     nothing left to be in proportion to.
    /// </remarks>
    public void RemoveLayer(int layer) {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, channels.Count);

        var wasWeighted = blends[layer] == TerrainBlend.Weight;

        channels.RemoveAt(layer);
        blends.RemoveAt(layer);
        layers.RemoveAt(layer);

        if (wasWeighted && blends.Contains(TerrainBlend.Weight)) {
            Renormalise();
        }
    }

    /// <summary>How much of a layer covers a sample, 0…<see cref="Total" />.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The weight.</returns>
    public byte WeightAt(int layer, int x, int z) {
        if ((uint)layer >= (uint)channels.Count) {
            return 0;
        }

        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return 0;
        }

        return channels[layer][(z * Description.SamplesX) + x];
    }

    /// <summary>How much of a layer covers a sample, as a fraction.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The weight, 0…1.</returns>
    public float FractionAt(int layer, int x, int z) => WeightAt(layer, x, z) / (float)Total;

    /// <summary>
    ///     Paints a layer at a sample, taking the difference from the other weight-blended layers.
    /// </summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <param name="amount">
    ///     How much to add, in weight units. Negative lowers it and gives the difference back.
    /// </param>
    /// <remarks>
    ///     A non-weight-blended layer is simply clamped and nothing else moves, which is what makes
    ///     snow able to lie over whatever is underneath rather than replacing it.
    /// </remarks>
    public void Paint(int layer, int x, int z, int amount) {
        if ((uint)layer >= (uint)channels.Count || amount == 0) {
            return;
        }

        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return;
        }

        var index = (z * Description.SamplesX) + x;

        if (blends[layer] == TerrainBlend.NonWeight) {
            channels[layer][index] = (byte)Math.Clamp(channels[layer][index] + amount, 0, Total);
            return;
        }

        var before = channels[layer][index];
        var after = (byte)Math.Clamp(before + amount, 0, Total);

        if (after == before) {
            return;
        }

        channels[layer][index] = after;
        Redistribute(layer, index, Total - after);
    }

    /// <summary>Sets a layer's weight at a sample, taking the difference from the others.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <param name="weight">What it becomes.</param>
    public void SetWeight(int layer, int x, int z, byte weight) =>
        Paint(layer, x, z, weight - WeightAt(layer, x, z));

    /// <summary>The sum of the weight-blended layers at a sample.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The sum, which should be <see cref="Total" />.</returns>
    public int SumAt(int x, int z) {
        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return 0;
        }

        var index = (z * Description.SamplesX) + x;
        var sum = 0;

        for (var layer = 0; layer < channels.Count; layer++) {
            if (blends[layer] == TerrainBlend.Weight) {
                sum += channels[layer][index];
            }
        }

        return sum;
    }

    /// <summary>
    ///     Checks the sum-to-one invariant everywhere, and says which layer broke it.
    /// </summary>
    /// <returns>Null if every sample is right, or a message naming the sample and the layers.</returns>
    /// <remarks>
    ///     The gate [docs/plan/31 § Part 4] asks for. It names the layer carrying the most weight at
    ///     the offending sample rather than only the sample, because "the weights at (2043, 991) sum
    ///     to 254" is a fact nobody can act on and "and Grass holds 138 of it" is a place to look.
    /// </remarks>
    public string? Verify() {
        if (!blends.Contains(TerrainBlend.Weight)) {
            return null;
        }

        for (var z = 0; z < Description.SamplesZ; z++) {
            for (var x = 0; x < Description.SamplesX; x++) {
                var sum = SumAt(x, z);

                if (sum == Total) {
                    continue;
                }

                var worst = 0;
                var worstWeight = -1;

                for (var layer = 0; layer < channels.Count; layer++) {
                    if (blends[layer] == TerrainBlend.Weight && WeightAt(layer, x, z) > worstWeight) {
                        worstWeight = WeightAt(layer, x, z);
                        worst = layer;
                    }
                }

                return $"The weight-blended layers at sample ({x}, {z}) sum to {sum} rather than "
                    + $"{Total}; '{layers[worst].Name}' holds {worstWeight} of it.";
            }
        }

        return null;
    }

    /// <summary>A layer's raw channel, for a renderer packing it into a texture.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>The weights, row-major in Z then X.</returns>
    public ReadOnlySpan<byte> ChannelOf(int layer) => channels[layer];

    /// <summary>How a layer combines with the ones beside it.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>Its blend mode.</returns>
    public TerrainBlend BlendOf(int layer) => blends[layer];

    /// <summary>What ground a layer paints.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>Its description.</returns>
    public TerrainLayerDescription LayerOf(int layer) => layers[layer];

    /// <summary>Changes what ground a layer paints, keeping everything painted with it.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="description">What it becomes.</param>
    /// <remarks>
    ///     ⚠ <b>Reassigning the material does not touch the weights.</b> Deciding that the third
    ///     layer is gravel rather than mud is a change of material, not a change of where it is
    ///     painted — and an implementation that cleared the channel would lose an hour of painting to
    ///     a dropdown.
    /// </remarks>
    public void SetLayer(int layer, TerrainLayerDescription description) {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, layers.Count);

        layers[layer] = description;
    }

    /// <summary>How much of the terrain a layer covers, as a fraction of its samples.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>The mean weight, 0…1.</returns>
    /// <remarks>
    ///     What the target-layer panel draws as a coverage bar. A layer at zero everywhere is the
    ///     state an artist gets into by painting over their base layer and then wondering where it
    ///     went, and a number beside the row is what answers that without a screenshot.
    /// </remarks>
    public float CoverageOf(int layer) {
        if ((uint)layer >= (uint)channels.Count || samples == 0) {
            return 0f;
        }

        var channel = channels[layer];
        var total = 0L;

        foreach (var weight in channel) {
            total += weight;
        }

        return total / (float)samples / Total;
    }

    /// <summary>Which layer covers a sample most, which is what the ground under a foot is.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The layer's index, or −1 if there are none.</returns>
    /// <remarks>
    ///     ⚠ <b>The dominant layer, not a blend, and a collision material has to be one of them.</b>
    ///     A footstep sound is a choice out of a set; there is no half-gravel sample to play. Ties go
    ///     to the lower index, so the answer does not depend on the order the layers were declared in
    ///     after somebody reorders them.
    /// </remarks>
    public int DominantAt(int x, int z) {
        var best = -1;
        var bestWeight = -1;

        for (var layer = 0; layer < channels.Count; layer++) {
            var weight = WeightAt(layer, x, z);

            if (weight > bestWeight) {
                bestWeight = weight;
                best = layer;
            }
        }

        return best;
    }

    /// <summary>
    ///     Fills a tile's per-quad ground materials, so a footstep knows what it is standing on.
    /// </summary>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="destination">
    ///     Where to put them, one per quad — <c>TileQuads²</c> — row-major in Z then X.
    /// </param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>[docs/plan/31 § T4]: the layer's physics material reaching the collider.</b> What
    ///         comes out is a layer index per quad; turning that into a physics material is the
    ///         caller's, because <see cref="TerrainLayerDescription.PhysicsMaterial" /> is a name and
    ///         this assembly has no asset database. It is the same seam
    ///         <see cref="TerrainSamples.FillCollisionSamples" /> uses for the heights.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per quad, not per sample, because a collision material is per triangle.</b> A
    ///         quad has four corner samples and they can disagree; what is written is the layer with
    ///         the most weight <em>summed over the four</em>, which is the majority answer rather
    ///         than the corner-nearest one. Taking one corner makes the material flip along a
    ///         boundary depending on which way the quad happens to be indexed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A tile with no layers writes −1 rather than 0.</b> Zero is a layer index and
    ///         would silently claim every quad is the first ground; the caller is expected to map a
    ///         negative to whatever "unspecified" means to it.
    ///     </para>
    /// </remarks>
    public int FillCollisionMaterials(int tileX, int tileZ, Span<sbyte> destination) {
        var quads = Description.TileQuads;
        var required = quads * quads;

        if (destination.Length < required) {
            throw new ArgumentException(
                $"A tile of {quads} quads needs {required} materials, not {destination.Length}.",
                nameof(destination)
            );
        }

        var originX = tileX * quads;
        var originZ = tileZ * quads;

        for (var row = 0; row < quads; row++) {
            for (var column = 0; column < quads; column++) {
                destination[(row * quads) + column] = DominantOverQuad(originX + column, originZ + row);
            }
        }

        return required;
    }

    /// <summary>Which layer holds the most weight over a quad's four corners.</summary>
    sbyte DominantOverQuad(int x, int z) {
        var best = -1;
        var bestWeight = -1;

        for (var layer = 0; layer < channels.Count; layer++) {
            var weight = WeightAt(layer, x, z)
                + WeightAt(layer, x + 1, z)
                + WeightAt(layer, x, z + 1)
                + WeightAt(layer, x + 1, z + 1);

            if (weight > bestWeight) {
                bestWeight = weight;
                best = layer;
            }
        }

        return (sbyte)best;
    }

    /// <summary>What ground a quad is, as the layer that claims it.</summary>
    /// <param name="x">The quad's low X sample.</param>
    /// <param name="z">Its low Z sample.</param>
    /// <returns>The layer's description, or null where there are no layers.</returns>
    /// <remarks>
    ///     The convenience over <see cref="FillCollisionMaterials" /> for a caller asking about one
    ///     place — a footstep, a decal, a tyre — rather than building a whole tile's shape.
    /// </remarks>
    public TerrainLayerDescription? GroundAt(int x, int z) {
        var layer = DominantOverQuad(x, z);

        return layer < 0 ? null : layers[layer];
    }

    /// <summary>Puts a whole sample's weights back, exactly as they were.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <param name="weights">One weight per layer, in layer order.</param>
    /// <exception cref="ArgumentException">The row is not one weight per layer.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only public way to write more than one layer at once, and it exists because
    ///         an undo cannot be spelled with <see cref="SetWeight" />.</b> Setting six layers one at
    ///         a time redistributes six times, so the first five are moved again by the sixth and the
    ///         result lands somewhere near where the stroke started rather than on it. A whole sample
    ///         is one assignment and is exact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not redistribute, so it can put back a state that breaks the
    ///         invariant.</b> That is deliberate and it is safe for its one caller: a row taken by
    ///         <see cref="TerrainWeightStroke" /> summed to <see cref="Total" /> when it was read, so
    ///         restoring it restores a valid state. Handing it anything else is handing
    ///         <see cref="Verify" /> a failure to find later.
    ///     </para>
    /// </remarks>
    public void Restore(int x, int z, ReadOnlySpan<byte> weights) {
        if (weights.Length != channels.Count) {
            throw new ArgumentException(
                $"The terrain has {channels.Count} paint layers and {weights.Length} weights were "
                + "given. A restore is a whole sample or it is not exact.",
                nameof(weights)
            );
        }

        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return;
        }

        var index = (z * Description.SamplesX) + x;

        for (var layer = 0; layer < channels.Count; layer++) {
            channels[layer][index] = weights[layer];
        }
    }

    /// <summary>Writes a raw weight, bypassing the redistribution that maintains the invariant.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists so <see cref="Verify" /> can be tested.</b> A checker with no
    ///     test is a checker nobody knows works, and the only way to test one is to hand it something
    ///     broken — which the public surface deliberately cannot produce. Nothing outside the tests
    ///     may call this.
    /// </remarks>
    internal void PokeRaw(int layer, int x, int z, byte weight) =>
        channels[layer][(z * Description.SamplesX) + x] = weight;

    /// <summary>Gives <paramref name="budget" /> back to every weight-blended layer but one.</summary>
    void Redistribute(int exclude, int index, int budget) {
        var others = 0;

        for (var layer = 0; layer < channels.Count; layer++) {
            if (layer != exclude && blends[layer] == TerrainBlend.Weight) {
                others += channels[layer][index];
            }
        }

        if (others == 0) {
            // Nothing to be in proportion to. The budget goes to the first other weight-blended
            // layer, because leaving it unassigned would break the invariant and there is no better
            // claim than "the first one an author declared".
            for (var layer = 0; layer < channels.Count; layer++) {
                if (layer != exclude && blends[layer] == TerrainBlend.Weight) {
                    channels[layer][index] = (byte)budget;
                    return;
                }
            }

            return;
        }

        // Largest-remainder: scale each layer down, then hand the rounding shortfall to whichever
        // layers were cut hardest. Rounding each independently loses up to one unit per layer, and
        // that shortfall is exactly the drift Verify would then report and nobody could explain.
        var assigned = 0;
        var remainders = new List<(int Layer, float Remainder)>();

        for (var layer = 0; layer < channels.Count; layer++) {
            if (layer == exclude || blends[layer] != TerrainBlend.Weight) {
                continue;
            }

            var exact = channels[layer][index] * (float)budget / others;
            var floor = (int)exact;

            channels[layer][index] = (byte)floor;
            assigned += floor;
            remainders.Add((layer, exact - floor));
        }

        remainders.Sort((left, right) => right.Remainder.CompareTo(left.Remainder));

        for (var i = 0; i < remainders.Count && assigned < budget; i++) {
            channels[remainders[i].Layer][index]++;
            assigned++;
        }
    }

    /// <summary>Rescales every sample so the weight-blended layers sum to the total again.</summary>
    void Renormalise() {
        for (var index = 0; index < samples; index++) {
            var sum = 0;

            for (var layer = 0; layer < channels.Count; layer++) {
                if (blends[layer] == TerrainBlend.Weight) {
                    sum += channels[layer][index];
                }
            }

            if (sum == Total) {
                continue;
            }

            if (sum == 0) {
                for (var layer = 0; layer < channels.Count; layer++) {
                    if (blends[layer] == TerrainBlend.Weight) {
                        channels[layer][index] = Total;
                        break;
                    }
                }

                continue;
            }

            // Rescale by pretending the first weight-blended layer is being repainted to what it
            // already is, which routes every case through one redistribution.
            var first = blends.IndexOf(TerrainBlend.Weight);
            var kept = (byte)Math.Min(Total, channels[first][index] * Total / sum);

            channels[first][index] = kept;
            Redistribute(first, index, Total - kept);
        }
    }
}
