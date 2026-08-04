// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     A terrain: a base heightfield, a stack of edit layers over it, paint channels and holes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The composite is derived, always, and never stored as the truth.</b>
///         [docs/plan/31 § D4]. <see cref="Base" /> plus each visible layer's delta times its alpha
///         is what the world is; <see cref="Composite" /> is a cache of that answer and
///         <see cref="Invalidate" /> is how it stops being trusted. Nothing may write to the
///         composite, because the next invalidation would discard the write and the tool that made it
///         would appear to work until somebody toggled a layer.
///     </para>
///     <para>
///         <b>Per tile, because that is the unit of everything else.</b> A stroke marks the tiles it
///         touched, and only those tiles recompute, re-upload and rebuild their collider — [§ D2].
///         Invalidating the whole terrain on every stamp would make a 4 km² terrain unusable at the
///         first drag.
///     </para>
/// </remarks>
public sealed class Terrain {
    readonly List<TerrainEditLayer> layers = [];
    readonly bool[] tileDirty;
    readonly ushort[] tileMinimum;
    readonly ushort[] tileMaximum;

    /// <summary>Creates a flat terrain at a height.</summary>
    /// <param name="description">Its shape.</param>
    /// <param name="height">What every base sample starts at, in metres.</param>
    /// <exception cref="ArgumentException">The description is not one a terrain can be built from.</exception>
    public Terrain(TerrainDescription description, float height = 0f) {
        if (description.Validate() is { } reason) {
            throw new ArgumentException(reason, nameof(description));
        }

        Description = description;
        Base = new(description, description.StoreHeight(height));
        Composite = new(description, description.StoreHeight(height));
        Weights = new(description);
        Holes = new(description);

        tileDirty = new bool[description.TileCount];
        tileMinimum = new ushort[description.TileCount];
        tileMaximum = new ushort[description.TileCount];

        InvalidateAll();
        Resolve();
    }

    /// <summary>Its shape.</summary>
    public TerrainDescription Description { get; }

    /// <summary>The heights before any layer touches them.</summary>
    /// <remarks>
    ///     Written by the create dialog and by a heightmap import, and by nothing else — a brush
    ///     writes to a layer. An import writes here or to a layer depending on which the artist
    ///     chose; both are supported and the difference is whether the import can be undone by
    ///     hiding something.
    /// </remarks>
    public TerrainSamples Base { get; }

    /// <summary>The heights the world actually has. Derived; do not write.</summary>
    public TerrainSamples Composite { get; }

    /// <summary>The paint channels.</summary>
    public TerrainWeights Weights { get; }

    /// <summary>Which samples are not there.</summary>
    public TerrainHoles Holes { get; }

    /// <summary>The edit layers, bottom first.</summary>
    public IReadOnlyList<TerrainEditLayer> Layers => layers;

    /// <summary>How many tiles are waiting to be recomposited.</summary>
    public int DirtyTileCount {
        get {
            var count = 0;

            foreach (var dirty in tileDirty) {
                if (dirty) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Adds an edit layer on top of the stack.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="kind">Who owns its contents.</param>
    /// <returns>The layer.</returns>
    public TerrainEditLayer AddLayer(string name, TerrainLayerKind kind = TerrainLayerKind.Manual) {
        var layer = new TerrainEditLayer(Description, name, kind);
        layers.Add(layer);
        return layer;
    }

    /// <summary>The layer reserved for a generator, adding it if there is not one.</summary>
    /// <param name="kind">Which generator owns it.</param>
    /// <param name="name">What to call it, if it has to be made.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind" /> is <see cref="TerrainLayerKind.Manual" />.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One layer per generator, not one per thing generated.</b> Two roads crossing have
    ///         to agree about the height at the junction, and two layers would give the answer to
    ///         whichever composited last. The same is true of a river running into a lake, which is why
    ///         water is one reserved layer and not one per body — and it is what makes "regenerate the
    ///         water" a single operation.
    ///     </para>
    ///     <para>
    ///         Refuses <see cref="TerrainLayerKind.Manual" />: an author's layers are many, are named
    ///         by the author, and asking for "the manual layer" would silently return the first one
    ///         somebody happened to make.
    ///     </para>
    /// </remarks>
    public TerrainEditLayer ReservedLayer(TerrainLayerKind kind, string? name = null) {
        if (kind == TerrainLayerKind.Manual) {
            throw new ArgumentException(
                "Manual layers are the author's and there may be any number of them, so there is no "
                + "such thing as the manual layer. Name one, or ask for a reserved kind.",
                nameof(kind)
            );
        }

        foreach (var layer in layers) {
            if (layer.Kind == kind) {
                return layer;
            }
        }

        return AddLayer(name ?? kind.ToString(), kind);
    }

    /// <summary>Where a layer sits in the stack.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>Its index, or −1 if it is not in this terrain.</returns>
    public int IndexOf(TerrainEditLayer layer) => layers.IndexOf(layer);

    /// <summary>Puts a layer back into the stack at a given height.</summary>
    /// <param name="index">Where it goes, 0 being the bottom.</param>
    /// <param name="layer">The layer.</param>
    /// <exception cref="ArgumentException">The layer was made for a differently shaped terrain.</exception>
    /// <remarks>
    ///     ⚠ <b>The reason removing a layer is undoable at all.</b> <see cref="AddLayer" /> makes a
    ///     new empty layer, so an undo built on it would put back a layer with the right name and
    ///     none of the ground — and a reorder afterwards would be undoing to a stack in a different
    ///     order. This takes the object that was removed, with its deltas, at the height it was.
    /// </remarks>
    public void InsertLayer(int index, TerrainEditLayer layer) {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, layers.Count);

        if (layer.Description != Description) {
            throw new ArgumentException(
                "The layer was made for a terrain of a different shape, so its deltas do not "
                + "address this one's samples.",
                nameof(layer)
            );
        }

        layers.Insert(index, layer);
        InvalidateAll();
    }

    /// <summary>Removes an edit layer and everything it held.</summary>
    /// <param name="layer">Which layer.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveLayer(TerrainEditLayer layer) {
        if (!layers.Remove(layer)) {
            return false;
        }

        InvalidateAll();
        return true;
    }

    /// <summary>Moves a layer up or down the stack.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it goes.</param>
    /// <remarks>
    ///     ⚠ <b>Reordering changes the result even though addition commutes.</b> The deltas add in
    ///     any order, but the composite <em>clamps</em> to the height range, and a clamp is not
    ///     commutative — a layer that pushes past the ceiling loses what it pushed past, and which
    ///     layer that is depends on the order. That is Unreal's behaviour too, and it is why
    ///     reordering invalidates everything rather than being a no-op on a stack of pure sums.
    /// </remarks>
    public void MoveLayer(int from, int to) {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, layers.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, layers.Count);

        if (from == to) {
            return;
        }

        var layer = layers[from];
        layers.RemoveAt(from);
        layers.Insert(to, layer);

        InvalidateAll();
    }

    /// <summary>
    ///     Adds a layer's deltas into the one below it and drops the layer.
    /// </summary>
    /// <param name="index">Which layer. Must not be the bottom one.</param>
    /// <remarks>
    ///     ⚠ <b>Destructive, and the panel has to say so.</b> The two layers become one and the
    ///     separation is gone; past the end of the undo stack there is no way back. It is the correct
    ///     semantic — it is what "collapse" means everywhere — and the wrong thing to discover.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">There is nothing below it.</exception>
    public void Collapse(int index) {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, layers.Count);

        var upper = layers[index];
        var lower = layers[index - 1];

        // Scaled by the upper layer's alpha, because that is what it was contributing. Collapsing a
        // layer at half alpha and then discovering the mountain doubled is the obvious bug here.
        for (var z = 0; z < Description.SamplesZ; z++) {
            for (var x = 0; x < Description.SamplesX; x++) {
                var delta = upper.DeltaAt(x, z);

                if (delta != 0) {
                    lower.AddDelta(x, z, (int)MathF.Round(delta * upper.HeightAlpha));
                }
            }
        }

        layers.RemoveAt(index);
        InvalidateAll();
    }

    /// <summary>Marks a rectangle of samples as needing recompositing.</summary>
    /// <param name="rect">Which samples.</param>
    public void Invalidate(TerrainRect rect) {
        // A sample on a tile boundary belongs to two tiles, which is what TilesOf answers with —
        // TileOf answers with one of them and would leave the neighbour holding a stale composite
        // along the seam.
        var tiles = Description.TilesOf(rect);

        for (var tileZ = tiles.Z; tileZ < tiles.EndZ; tileZ++) {
            for (var tileX = tiles.X; tileX < tiles.EndX; tileX++) {
                tileDirty[(tileZ * Description.TilesX) + tileX] = true;
            }
        }
    }

    /// <summary>Marks every tile as needing recompositing.</summary>
    public void InvalidateAll() => Array.Fill(tileDirty, true);

    /// <summary>Whether a tile's composite is stale.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>Whether it is stale.</returns>
    public bool IsTileDirty(int tileX, int tileZ) => tileDirty[(tileZ * Description.TilesX) + tileX];

    /// <summary>Recomposites every stale tile.</summary>
    /// <returns>How many tiles were recomputed.</returns>
    /// <remarks>
    ///     Call once after a stroke, not once per stamp. A drag marks the same handful of tiles over
    ///     and over and this collapses that into one pass — which is why dirtiness is a flag rather
    ///     than a recompute at the point of the edit.
    /// </remarks>
    public int Resolve() {
        var resolved = 0;

        for (var tileZ = 0; tileZ < Description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < Description.TilesX; tileX++) {
                var index = (tileZ * Description.TilesX) + tileX;

                if (!tileDirty[index]) {
                    continue;
                }

                ResolveTile(tileX, tileZ, index);
                tileDirty[index] = false;
                resolved++;
            }
        }

        return resolved;
    }

    /// <summary>The lowest and highest composited sample of a tile.</summary>
    /// <param name="tileX">Its X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <returns>The range, in metres.</returns>
    /// <remarks>
    ///     Maintained by <see cref="Resolve" /> rather than measured on demand, because it is what a
    ///     tile's bounding box is and a frame asks every tile for it.
    /// </remarks>
    public (float Minimum, float Maximum) HeightRangeOf(int tileX, int tileZ) {
        var index = (tileZ * Description.TilesX) + tileX;
        return (Description.HeightOf(tileMinimum[index]), Description.HeightOf(tileMaximum[index]));
    }

    /// <summary>The lowest and highest composited sample over a rectangle of samples.</summary>
    /// <param name="rect">Which samples. Clipped to the terrain.</param>
    /// <returns>The range in metres, or the terrain's whole range for a rectangle outside it.</returns>
    /// <remarks>
    ///     <b>Reduced over whole tiles, not measured per sample.</b> It is what a LOD node's bounding
    ///     box is built from and a frame asks for a few hundred of them, so it reads the per-tile
    ///     ranges <see cref="Resolve" /> already maintains and takes the union. The answer is
    ///     therefore conservative — a node covering a corner of a tall tile gets that tile's range —
    ///     which is the right direction for a cull: too large draws something needlessly, too small
    ///     makes terrain disappear at the edge of the screen.
    /// </remarks>
    public (float Minimum, float Maximum) HeightRangeOf(TerrainRect rect) {
        var clipped = rect.Clip(new(0, 0, Description.SamplesX, Description.SamplesZ));

        if (clipped.IsEmpty) {
            return (Description.MinHeight, Description.MaxHeight);
        }

        var minTileX = Math.Clamp(clipped.X / Description.TileQuads, 0, Description.TilesX - 1);
        var maxTileX = Math.Clamp((clipped.EndX - 1) / Description.TileQuads, 0, Description.TilesX - 1);
        var minTileZ = Math.Clamp(clipped.Z / Description.TileQuads, 0, Description.TilesZ - 1);
        var maxTileZ = Math.Clamp((clipped.EndZ - 1) / Description.TileQuads, 0, Description.TilesZ - 1);

        var minimum = ushort.MaxValue;
        var maximum = ushort.MinValue;

        for (var tileZ = minTileZ; tileZ <= maxTileZ; tileZ++) {
            for (var tileX = minTileX; tileX <= maxTileX; tileX++) {
                var index = (tileZ * Description.TilesX) + tileX;
                minimum = Math.Min(minimum, tileMinimum[index]);
                maximum = Math.Max(maximum, tileMaximum[index]);
            }
        }

        return minimum > maximum
            ? (Description.MinHeight, Description.MaxHeight)
            : (Description.HeightOf(minimum), Description.HeightOf(maximum));
    }

    /// <summary>What the composite would be at one sample, computed rather than read.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The stored height.</returns>
    /// <remarks>
    ///     The definition <see cref="Composite" /> caches. A test asserting that the cache is right
    ///     compares against this, which is the only way to catch an invalidation that was missed —
    ///     the cached value looks perfectly reasonable, it is just old.
    /// </remarks>
    public ushort CompositeAt(int x, int z) {
        var total = (int)Base[x, z];

        foreach (var layer in layers) {
            if (!layer.IsVisible || layer.HeightAlpha == 0f) {
                continue;
            }

            var delta = layer.DeltaAt(x, z);

            if (delta != 0) {
                total += (int)MathF.Round(delta * layer.HeightAlpha);
            }
        }

        return (ushort)Math.Clamp(total, 0, TerrainSamples.MaxHeight);
    }

    void ResolveTile(int tileX, int tileZ, int index) {
        var rect = Description.SamplesOf(tileX, tileZ).Clip(Composite.Bounds);

        var minimum = ushort.MaxValue;
        var maximum = ushort.MinValue;

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var value = CompositeAt(x, z);

                Composite[x, z] = value;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        tileMinimum[index] = minimum;
        tileMaximum[index] = maximum;
    }
}
