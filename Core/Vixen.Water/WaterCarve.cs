// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

// ⚠ `Terrain` names both a namespace and the type inside it, and inside `Vixen.Water` the namespace
// wins. Aliased rather than fully qualified at each use, so a reader sees the same word the terrain's
// own files do.
using TerrainAsset = Vixen.Terrain.Terrain;

namespace Vixen.Water;

/// <summary>How much of a body's bed the terrain actually takes, and what it paints along it.</summary>
/// <remarks>
///     <para>
///         <b>Three numbers, where the reference has twenty</b>
///         ([35 § D5](../../docs/plan/35-water.md#d5-carving-is-a-reserved-edit-layer-and-the-machinery-exists)).
///         The channel's depth, the ramp from the channel to the shoreline and the falloff outward are
///         <em>the body's</em> — <see cref="WaterProfilePoint.Depth" />,
///         <see cref="WaterBody.BedRamp" /> and <see cref="WaterBody.ShoreFalloff" /> — because the
///         shape the terrain is cut to and the shape the water is drawn in have to be the same shape.
///         What is left over is what belongs to the <em>carve</em>.
///     </para>
///     <para>
///         What is deliberately not taken from Unreal's brush: the terracing, the two octaves of curl
///         and the shape blur. They are a procedural shoreline generator living inside a water body,
///         they are the reason that property list has twenty entries, and every one of them is a
///         terrain brush an author runs on a layer above — which survives the body being moved,
///         because it is in a different layer. That is
///         [31 § D4](../../docs/plan/31-terrain-grass-and-trees.md)'s whole promise, and it is the gate
///         § D5 says to check this decision against.
///     </para>
/// </remarks>
public readonly record struct WaterCarveProfile {
    /// <summary>How much of the bed the ground takes, 0…1.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is Unreal's <c>Affects Landscape</c>, and it is a value rather than a flag.</b> A
    ///     body that carves nothing is one an author wants floating over ground they sculpted by hand
    ///     — a canal in a stone aqueduct — and a separate boolean would be a second way to say the
    ///     same thing that can disagree with this one.
    /// </remarks>
    public float Strength { get; init; }

    /// <summary>Which paint layer the bed is, or −1 for none.</summary>
    /// <remarks>
    ///     A riverbed of gravel and a lake floor of silt, which § D5 names as the optional half of a
    ///     carve. Painted through <see cref="TerrainWeights" />' own path, so the sum-to-one invariant
    ///     is maintained in one place.
    /// </remarks>
    public int BedLayer { get; init; }

    /// <summary>How much of that layer to lay down where the body is fully covering, 0…1.</summary>
    public float BedLayerStrength { get; init; }

    /// <summary>Carve fully, paint nothing.</summary>
    public static WaterCarveProfile Default =>
        new() { Strength = 1f, BedLayer = -1, BedLayerStrength = 1f };

    /// <summary>Carve nothing — a body that floats over ground somebody else owns.</summary>
    public static WaterCarveProfile None => Default with { Strength = 0f };
}

/// <summary>
///     Water bodies cutting their beds into the terrain's reserved <c>Water</c> layer.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D5](../../docs/plan/35-water.md#d5-carving-is-a-reserved-edit-layer-and-the-machinery-exists),
///         and the machinery existed.</b> A third reserved layer alongside Splines and Scatter, on
///         [31 § D4](../../docs/plan/31-terrain-grass-and-trees.md)'s contract and with no change to
///         it — regenerated wholesale whenever a body changes, which is what makes it
///         non-destructive. Unreal's water brush requires opting the landscape into edit layers, five
///         years after Landscape shipped, which is why it is a setup step people miss; here they
///         <em>are</em> the storage model, and water is the third consumer of a contract designed
///         without it in mind.
///     </para>
///     <para>
///         <b>The bed a body carves is the bed the field rasterises, by construction.</b> Both read
///         <see cref="WaterBody.Sample" /> — the surface height minus the coverage-weighted bed depth —
///         so the shoreline the terrain is cut to and the shoreline the water is drawn at cannot
///         disagree. That is § D2's argument applied to the ground rather than to the surface, and it
///         is why this takes a <see cref="WaterBody" /> instead of a
///         <see cref="TerrainSplineProfile" />.
///     </para>
///     <para>
///         ⚠ <b>A river's channel is a band about its centreline and a lake's is its own polygon</b>,
///         and both come out of one call because <see cref="WaterBody.Sample" /> already knows which
///         it is. Deforming a lake through <c>TerrainSpline.Deform</c> would cut a moat: that carves
///         along a curve's <em>width</em>, and a closed curve's width is its shoreline, not its
///         interior.
///     </para>
///     <para>
///         <b>An island raises instead of lowering</b> — Unreal's <c>Invert Shape</c> promoted to the
///         thing it actually is, and the same sign flip <see cref="WaterField.Rasterize" /> makes.
///     </para>
/// </remarks>
public static class WaterCarve {
    /// <summary>What the reserved layer is called.</summary>
    public const string LayerName = "Water";

    /// <summary>The layer a terrain reserves for its water, adding it if there is not one.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="terrain" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One layer for every body, not one per body</b>, on
    ///     <c>TerrainSpline.LayerOf</c>'s reasoning: a river running into a lake has to agree with it
    ///     about the ground at the mouth, and two layers would give the answer to whichever
    ///     composited last. It is also what makes "regenerate the water" one operation.
    /// </remarks>
    public static TerrainEditLayer LayerOf(TerrainAsset terrain, string name = LayerName) {
        ArgumentNullException.ThrowIfNull(terrain);

        return terrain.ReservedLayer(TerrainLayerKind.Water, name);
    }

    /// <summary>Cuts one body's bed into a layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">The layer to write. Cleared over the affected rect first.</param>
    /// <param name="body">The body, in the terrain's own space.</param>
    /// <param name="profile">How much of the bed the ground takes, and what it paints.</param>
    /// <returns>The rect that changed.</returns>
    /// <exception cref="ArgumentNullException">Something was not supplied.</exception>
    /// <remarks>
    ///     ⚠ <b>Only this body's own rect is cleared, which is not enough when a body <em>moves</em>.</b>
    ///     A lake dragged twenty metres leaves its old rect untouched, because the new one no longer
    ///     covers it — so what an editor calls is <see cref="Regenerate" />, which empties the layer and
    ///     lays every body down again. This is the operation for adding one body to a layer that is
    ///     otherwise already right, and it is <c>TerrainSpline.Deform</c>'s division for its reason.
    /// </remarks>
    public static TerrainRect Carve(
        TerrainAsset terrain,
        TerrainEditLayer layer,
        WaterBody body,
        in WaterCarveProfile profile
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(body);

        var strength = Math.Clamp(profile.Strength, 0f, 1f);

        if (strength <= 0f) {
            return TerrainRect.Empty;
        }

        var description = terrain.Description;
        var rect = RectOf(description, body);

        if (rect.IsEmpty) {
            return TerrainRect.Empty;
        }

        layer.Clear(rect);

        var scale = description.MetresPerQuad;
        var raises = body.IsSubtractive;

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var contribution = body.Sample(new(x * scale, z * scale));

                if (contribution.Coverage <= 0f) {
                    continue;
                }

                // ⚠ The same expression WaterField.Rasterize uses, sign included. A carve that
                // computed its own bed would be a second definition of where the ground under the
                // water is, and the frame the two disagree on is the one where the shoreline the
                // terrain has is not the shoreline the water is drawn at.
                var wanted = raises
                    ? contribution.SurfaceHeight + (contribution.BedDepth * contribution.Coverage)
                    : contribution.SurfaceHeight - (contribution.BedDepth * contribution.Coverage);

                var weight = contribution.Coverage * strength;

                // The composite *without* this layer, which is what clearing the rect first bought:
                // reading the base instead would put a lake cut into a hillside back at sea level
                // wherever an author's own sculpting had raised the ground under it.
                var current = description.HeightOf(terrain.CompositeAt(x, z));

                // ⚠ A carve only ever cuts, and a raise only ever raises. Without this a lake whose
                // surface sits above a valley floor would *fill the valley in* — the bed is where the
                // body wants the ground, not where it insists on it, and ground already deeper than
                // the bed is a trench the author dug on purpose.
                if (raises ? wanted <= current : wanted >= current) {
                    continue;
                }

                var moved = float.Lerp(current, wanted, weight);

                // A delta in stored steps, which is the layer's own unit. Rounding rather than
                // truncating, so a bed at exactly one step's depth does not sink by one every time
                // the layer is regenerated.
                var delta = (moved - current) / description.MetresPerStep;

                layer.SetDelta(x, z, (short)Math.Clamp(MathF.Round(delta), short.MinValue, short.MaxValue));
            }
        }

        terrain.Invalidate(rect);

        if (profile.BedLayer >= 0) {
            PaintBed(terrain, body, profile, rect);
        }

        return rect;
    }

    /// <summary>Empties the layer and lays every body down again.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">The reserved layer.</param>
    /// <param name="bodies">Every body that carves, with how much of its bed the ground takes.</param>
    /// <returns>The rect that changed, which covers where the bodies <em>were</em> as well.</returns>
    /// <exception cref="ArgumentNullException">Something was not supplied.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What "regenerated wholesale" means, and it is the whole of the non-destructiveness.</b>
    ///         Nothing is undone: the layer is deltas, the ground under it is untouched, and emptying
    ///         the layer restores exactly what an author sculpted. Moving a river therefore restores
    ///         the old bank and cuts the new one in one operation, and a shoreline sculpted by hand in
    ///         a layer <em>above</em> survives both — which is § D5's stated gate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The invalidated rect covers where the bodies were, from the chunks the layer had
    ///         already allocated.</b> Invalidating only the new rects would leave the cached composite
    ///         holding the old bed wherever the two do not overlap — which draws as a lake that has
    ///         been moved and also has not.
    ///     </para>
    /// </remarks>
    public static TerrainRect Regenerate(
        TerrainAsset terrain,
        TerrainEditLayer layer,
        IEnumerable<(WaterBody Body, WaterCarveProfile Profile)> bodies
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(bodies);

        var dirty = Occupied(layer);
        var description = terrain.Description;

        layer.Clear();

        var carving = new List<(WaterBody Body, WaterCarveProfile Profile)>();
        var target = TerrainRect.Empty;

        foreach (var (body, profile) in bodies) {
            ArgumentNullException.ThrowIfNull(body);

            if (!(profile.Strength > 0f)) {
                continue;
            }

            var rect = RectOf(description, body);

            if (rect.IsEmpty) {
                continue;
            }

            carving.Add((body, profile));
            target = target.IsEmpty ? rect : target.Union(rect);
        }

        // ⚠ Every body is resolved at each sample and combined by min and max, rather than each
        // carving in turn. Carving in turn is order-dependent twice over: a later body's Clear
        // erases an earlier one's bed wherever their rects overlap, and even without that the last
        // writer wins. A field that depended on the order a scene happened to walk its entities in
        // is one where moving an unrelated body changes the ground at a river mouth — which is
        // WaterField.Rasterize's own argument, and this is the same arithmetic.
        for (var z = target.Z; z < target.EndZ; z++) {
            for (var x = target.X; x < target.EndX; x++) {
                var ground = new Vector2(x * description.MetresPerQuad, z * description.MetresPerQuad);

                // The composite with the layer empty, which is what clearing it first bought: the
                // ground an author sculpted, without any previous carve folded into it.
                var current = description.HeightOf(terrain.CompositeAt(x, z));

                var cut = current;
                var raised = float.NegativeInfinity;

                foreach (var (body, profile) in carving) {
                    var contribution = body.Sample(ground);

                    if (contribution.Coverage <= 0f) {
                        continue;
                    }

                    var weight = contribution.Coverage * Math.Clamp(profile.Strength, 0f, 1f);
                    var bed = contribution.BedDepth * contribution.Coverage;

                    if (body.IsSubtractive) {
                        var wanted = contribution.SurfaceHeight + bed;

                        if (wanted > current) {
                            raised = MathF.Max(raised, float.Lerp(current, wanted, weight));
                        }
                    } else {
                        var wanted = contribution.SurfaceHeight - bed;

                        // ⚠ A carve only cuts. The bed is where a body *wants* the ground, not where
                        // it insists on it, and ground already deeper is a trench somebody dug on
                        // purpose — a carve that filled it would be a body silently undoing a sculpt.
                        if (wanted < current) {
                            cut = MathF.Min(cut, float.Lerp(current, wanted, weight));
                        }
                    }
                }

                // A raise wins over a cut where an island overlaps a lake, which is the same
                // max(bed, raised) WaterField.Rasterize takes for the ground.
                var moved = raised > float.NegativeInfinity ? MathF.Max(raised, cut) : cut;

                if (moved == current) {
                    continue;
                }

                var delta = (moved - current) / description.MetresPerStep;

                layer.SetDelta(x, z, (short)Math.Clamp(MathF.Round(delta), short.MinValue, short.MaxValue));
            }
        }

        dirty = dirty.IsEmpty ? target : target.IsEmpty ? dirty : dirty.Union(target);

        if (!dirty.IsEmpty) {
            terrain.Invalidate(dirty);
        }

        foreach (var (body, profile) in carving) {
            if (profile.BedLayer >= 0) {
                PaintBed(terrain, body, profile, RectOf(description, body));
            }
        }

        return dirty;
    }

    /// <summary>Lays the bed's own layer along the covered ground.</summary>
    /// <remarks>
    ///     Through <see cref="TerrainWeights.Paint" />, so the sum-to-one invariant is maintained in
    ///     one place. Writing the channel directly would leave the other layers where they were, and
    ///     a sample whose weights sum to more than one draws as a brighter patch no tool can find.
    /// </remarks>
    static void PaintBed(TerrainAsset terrain, WaterBody body, in WaterCarveProfile profile, in TerrainRect rect) {
        if (profile.BedLayer >= terrain.Weights.LayerCount) {
            return;
        }

        var scale = terrain.Description.MetresPerQuad;
        var strength = Math.Clamp(profile.BedLayerStrength, 0f, 1f);

        if (strength <= 0f) {
            return;
        }

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var coverage = body.Sample(new(x * scale, z * scale)).Coverage;

                if (coverage <= 0f) {
                    continue;
                }

                // In weight units, which is what the invariant is kept in — a fraction here would be
                // rounded to zero for everything below half a unit and the bed would paint nowhere.
                var amount = (int)MathF.Round(coverage * strength * TerrainWeights.Total);

                terrain.Weights.Paint(profile.BedLayer, x, z, amount);
            }
        }
    }

    /// <summary>The samples a body could touch, clipped to the terrain.</summary>
    static TerrainRect RectOf(in TerrainDescription description, WaterBody body) {
        var (low, high) = body.Bounds();

        if (!(high.X >= low.X)) {
            return TerrainRect.Empty;
        }

        var scale = description.MetresPerQuad;

        var x0 = (int)MathF.Floor(low.X / scale);
        var z0 = (int)MathF.Floor(low.Y / scale);
        var x1 = (int)MathF.Ceiling(high.X / scale);
        var z1 = (int)MathF.Ceiling(high.Y / scale);

        x0 = Math.Clamp(x0, 0, description.SamplesX - 1);
        z0 = Math.Clamp(z0, 0, description.SamplesZ - 1);
        x1 = Math.Clamp(x1 + 1, 0, description.SamplesX);
        z1 = Math.Clamp(z1 + 1, 0, description.SamplesZ);

        return x1 <= x0 || z1 <= z0 ? TerrainRect.Empty : new(x0, z0, x1 - x0, z1 - z0);
    }

    /// <summary>The samples a layer's allocated chunks cover.</summary>
    static TerrainRect Occupied(TerrainEditLayer layer) {
        var rect = TerrainRect.Empty;

        foreach (var (x, z) in layer.OccupiedChunks()) {
            var chunk = new TerrainRect(
                x * TerrainEditLayer.ChunkSize,
                z * TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize,
                TerrainEditLayer.ChunkSize
            );

            rect = rect.IsEmpty ? chunk : rect.Union(chunk);
        }

        return rect;
    }
}
