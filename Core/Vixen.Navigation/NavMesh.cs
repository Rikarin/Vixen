// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation;

/// <summary>Where a mesh's tile grid is and how big its tiles are.</summary>
/// <param name="Origin">The corner of tile (0, 0) — its minimum in X and Z.</param>
/// <param name="TileWidth">A tile's extent along X.</param>
/// <param name="TileDepth">A tile's extent along Z.</param>
/// <param name="BorderTolerance">
///     How far apart two tiles' border vertices may be, horizontally, and still be the same point.
///     Tiles are baked on a shared voxel grid, so in practice this catches float error rather than
///     real disagreement — a cell fraction is the right order of magnitude.
/// </param>
/// <param name="ClimbTolerance">
///     How far apart two tiles' border vertices may be vertically. This is the agent's step height:
///     the same reason a single tile connects a polygon to the one a step above it.
/// </param>
public readonly record struct NavMeshParams(
    Vector3 Origin,
    float TileWidth,
    float TileDepth,
    float BorderTolerance = 0.05f,
    float ClimbTolerance = 0.9f
) {
    /// <summary>A single-tile mesh, for a level small enough not to stream.</summary>
    /// <remarks>
    ///     The tile is arbitrarily large rather than infinite, because the grid arithmetic has to
    ///     produce a finite coordinate, and it is centred on the origin so that a level built around
    ///     it — which is most of them — does not fall into tile (-1, -1). Nothing is allocated per
    ///     tile-grid cell, so the size costs nothing; what it costs is that a mesh built this way
    ///     cannot later grow tiles beside it.
    /// </remarks>
    public static NavMeshParams Single => new(new(-5e6f, 0f, -5e6f), 1e7f, 1e7f);
}

/// <summary>A polygon, as the mesh holds it.</summary>
internal struct NavPoly {
    /// <summary>Where its vertex indices start.</summary>
    public int FirstVertex;

    /// <summary>The head of its chain of links to other tiles, or -1.</summary>
    public int FirstLink;

    /// <summary>How many vertices — and therefore edges — it has.</summary>
    public byte VertexCount;

    /// <summary>Its area id.</summary>
    public byte Area;

    /// <summary>What it may be used for.</summary>
    public NavPolyFlags Flags;
}

/// <summary>One polygon's connection to a polygon in another tile.</summary>
/// <remarks>
///     <para>
///         Interior adjacency is an index, held in an array parallel to the vertices, because inside
///         a tile an edge has exactly one neighbour and both polygons agree about where it is. Across
///         a tile border neither is true: the two tiles were simplified independently, so one tile's
///         edge can span two of its neighbour's, and the connection therefore has to carry the
///         <i>part</i> of the edge it covers.
///     </para>
///     <para>
///         <see cref="Min" /> and <see cref="Max" /> are that part, as parameters along the owning
///         polygon's edge. A funnel that used the whole edge would walk an agent through a wall at
///         every border where the two tiles disagreed.
///     </para>
/// </remarks>
internal struct NavLink {
    /// <summary>The polygon on the far side.</summary>
    public NavPolyRef Neighbour;

    /// <summary>The next link of the same polygon, or -1.</summary>
    public int Next;

    /// <summary>Which edge of the owning polygon this crosses.</summary>
    public byte Edge;

    /// <summary>Where the shared part of the edge starts, as a parameter along it.</summary>
    public float Min;

    /// <summary>Where it ends.</summary>
    public float Max;
}

/// <summary>A tile that has been added to a mesh.</summary>
/// <remarks>
///     The data is the caller's <see cref="NavMeshTileData" />, unchanged and shared. What this adds
///     is everything that is true only while it is loaded: the slot, the salt, per-polygon bounds for
///     the nearest-polygon search, and the links across its borders.
/// </remarks>
public sealed class NavMeshTile {
    internal NavMeshTile(NavMeshTileData data, int slot, uint salt) {
        Data = data;
        Slot = slot;
        Salt = salt;

        var source = data.Polys;
        Polys = new NavPoly[source.Length];
        PolyBounds = new BoundingBox[source.Length];

        for (var index = 0; index < source.Length; index++) {
            Polys[index] = new() {
                FirstVertex = source[index].FirstVertex,
                VertexCount = source[index].VertexCount,
                Area = source[index].Area,
                Flags = source[index].Flags,
                FirstLink = -1
            };

            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);

            for (var corner = 0; corner < source[index].VertexCount; corner++) {
                var vertex = data.Vertices[data.PolyVertices[source[index].FirstVertex + corner]];
                minimum = Vector3.Min(minimum, vertex);
                maximum = Vector3.Max(maximum, vertex);
            }

            PolyBounds[index] = new(minimum, maximum);
        }
    }

    /// <summary>The baked data this tile was made from.</summary>
    public NavMeshTileData Data { get; }

    /// <summary>The slot it occupies in its mesh.</summary>
    public int Slot { get; }

    /// <summary>The salt its references carry.</summary>
    public uint Salt { get; }

    /// <summary>How many polygons it holds.</summary>
    public int PolyCount => Polys.Length;

    internal NavPoly[] Polys { get; }

    internal BoundingBox[] PolyBounds { get; }

    internal NavLink[] Links { get; private set; } = [];

    internal void SetLinks(NavLink[] links) => Links = links;
}

/// <summary>
///     The walkable surface, as convex polygons in tiles that can be added and removed while agents
///     are standing on them.
/// </summary>
/// <remarks>
///     <para>
///         Everything that answers a question about the mesh — nearest polygon, path, raycast — is in
///         <see cref="NavMeshQuery" />, which holds the search state a query needs and which is
///         therefore one per thread. This type is the data, and reading it is thread-safe as long as
///         no tile is being added or removed at the same time.
///     </para>
///     <para>
///         <b>Tiles are the unit of everything.</b> A bake produces one, streaming loads one, a
///         rebuild after a building is destroyed replaces one. The links across their borders are
///         recomputed from the tiles present, on every add and every remove, rather than stored — a
///         stored link is a thing that can outlive what it points at, and this mesh is expected to
///         have tiles removed underneath live paths.
///     </para>
/// </remarks>
public sealed class NavMesh {
    /// <summary>
    ///     The most vertices a polygon may have. Six is Recast's default and the reason the query
    ///     layer can put a polygon's vertices on the stack.
    /// </summary>
    public const int MaxVerticesPerPoly = 6;

    /// <summary>The four sides of a tile, as offsets in the tile grid.</summary>
    static readonly (int X, int Z)[] SideOffsets = [(-1, 0), (0, 1), (1, 0), (0, -1)];

    readonly List<NavMeshTile?> tiles = [];
    readonly Dictionary<long, int> slotsByCoordinate = [];
    readonly List<int> freeSlots = [];
    readonly List<NavLink> linkScratch = [];
    uint nextSalt = 1;

    /// <summary>Creates an empty mesh.</summary>
    /// <param name="parameters">Where its tile grid is and how big its tiles are.</param>
    public NavMesh(NavMeshParams parameters) {
        if (parameters.TileWidth <= 0 || parameters.TileDepth <= 0) {
            throw new ArgumentOutOfRangeException(nameof(parameters), "A tile has to have a positive extent in X and Z.");
        }

        Params = parameters;
    }

    /// <summary>Where the tile grid is and how big its tiles are.</summary>
    public NavMeshParams Params { get; }

    /// <summary>How many tiles are loaded.</summary>
    public int TileCount { get; private set; }

    /// <summary>Every loaded tile, in slot order.</summary>
    /// <remarks>Slots of removed tiles are skipped, so this is not an index-by-slot list.</remarks>
    public IEnumerable<NavMeshTile> Tiles {
        get {
            foreach (var tile in tiles) {
                if (tile is not null) {
                    yield return tile;
                }
            }
        }
    }

    /// <summary>Which tile a world position falls in, whether or not one is loaded there.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The tile's column and row.</returns>
    public (int X, int Z) TileCoordinates(Vector3 position) => (
        (int)MathF.Floor((position.X - Params.Origin.X) / Params.TileWidth),
        (int)MathF.Floor((position.Z - Params.Origin.Z) / Params.TileDepth)
    );

    /// <summary>The tile at a grid position, if one is loaded.</summary>
    /// <param name="x">The column.</param>
    /// <param name="z">The row.</param>
    /// <returns>The tile, or <see langword="null" />.</returns>
    public NavMeshTile? TileAt(int x, int z) =>
        slotsByCoordinate.TryGetValue(Key(x, z), out var slot) ? tiles[slot] : null;

    /// <summary>Loads a tile, replacing whatever was at its grid position.</summary>
    /// <param name="data">The baked tile.</param>
    /// <returns>The loaded tile.</returns>
    /// <remarks>
    ///     Replacing is the streaming case and the rebake case, and it is deliberately not an error:
    ///     the previous occupant is removed first, which invalidates every reference into it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The mesh is full, or the tile has too many polygons.</exception>
    public NavMeshTile AddTile(NavMeshTileData data) {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Polys.Length > NavPolyRef.MaxPolysPerTile) {
            throw new InvalidOperationException(
                $"The tile has {data.Polys.Length} polygons; a reference can only name {NavPolyRef.MaxPolysPerTile}."
            );
        }

        RemoveTile(data.X, data.Z);

        int slot;

        if (freeSlots.Count > 0) {
            slot = freeSlots[^1];
            freeSlots.RemoveAt(freeSlots.Count - 1);
        } else {
            slot = tiles.Count;

            if (slot >= NavPolyRef.MaxTiles) {
                throw new InvalidOperationException($"The mesh already holds {NavPolyRef.MaxTiles} tiles, which is as many as a reference can name.");
            }

            tiles.Add(null);
        }

        var tile = new NavMeshTile(data, slot, nextSalt++ & 0xffff);

        // Salt zero would make the reference to polygon zero of tile zero equal to NavPolyRef.Null.
        if (tile.Salt == 0) {
            tile = new(data, slot, nextSalt++);
        }

        tiles[slot] = tile;
        slotsByCoordinate[Key(data.X, data.Z)] = slot;
        TileCount++;

        RebuildLinks(tile);

        foreach (var (offsetX, offsetZ) in SideOffsets) {
            var neighbour = TileAt(data.X + offsetX, data.Z + offsetZ);

            if (neighbour is not null) {
                RebuildLinks(neighbour);
            }
        }

        return tile;
    }

    /// <summary>Unloads the tile at a grid position.</summary>
    /// <param name="x">The column.</param>
    /// <param name="z">The row.</param>
    /// <returns><see langword="false" /> if no tile was there.</returns>
    /// <remarks>
    ///     Every reference into the removed tile stops resolving, which is the point of the salt. The
    ///     neighbours are relinked, so a path that crossed the border stops there rather than
    ///     stepping into a slot that has been emptied.
    /// </remarks>
    public bool RemoveTile(int x, int z) {
        if (!slotsByCoordinate.TryGetValue(Key(x, z), out var slot)) {
            return false;
        }

        tiles[slot] = null;
        slotsByCoordinate.Remove(Key(x, z));
        freeSlots.Add(slot);
        TileCount--;

        foreach (var (offsetX, offsetZ) in SideOffsets) {
            var neighbour = TileAt(x + offsetX, z + offsetZ);

            if (neighbour is not null) {
                RebuildLinks(neighbour);
            }
        }

        return true;
    }

    /// <summary>Resolves a reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="tile">The tile it names.</param>
    /// <param name="poly">Its polygon's index in that tile.</param>
    /// <returns><see langword="false" /> if the tile has been unloaded, replaced, or never existed.</returns>
    public bool TryGetPoly(NavPolyRef reference, out NavMeshTile tile, out int poly) {
        tile = null!;
        poly = 0;

        if (reference.IsNull) {
            return false;
        }

        var slot = reference.Tile;

        if ((uint)slot >= (uint)tiles.Count || tiles[slot] is not { } candidate || candidate.Salt != reference.Salt) {
            return false;
        }

        if ((uint)reference.Poly >= (uint)candidate.PolyCount) {
            return false;
        }

        tile = candidate;
        poly = reference.Poly;

        return true;
    }

    /// <summary>Whether a reference still names a live polygon.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns><see langword="true" /> if it resolves.</returns>
    public bool IsValid(NavPolyRef reference) => TryGetPoly(reference, out _, out _);

    /// <summary>The reference for a polygon of a loaded tile.</summary>
    /// <param name="tile">The tile.</param>
    /// <param name="poly">The polygon's index.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile" /> is null.</exception>
    public static NavPolyRef ReferenceOf(NavMeshTile tile, int poly) {
        ArgumentNullException.ThrowIfNull(tile);

        return NavPolyRef.Encode(tile.Salt, tile.Slot, poly);
    }

    /// <summary>Copies a polygon's vertices out.</summary>
    /// <param name="reference">The polygon.</param>
    /// <param name="destination">Where to write them. Six is enough for any polygon this engine bakes.</param>
    /// <returns>How many were written, or zero if the reference does not resolve.</returns>
    public int GetPolyVertices(NavPolyRef reference, Span<Vector3> destination) {
        if (!TryGetPoly(reference, out var tile, out var index)) {
            return 0;
        }

        ref var poly = ref tile.Polys[index];

        if (destination.Length < poly.VertexCount) {
            throw new ArgumentException($"The polygon has {poly.VertexCount} vertices and the destination holds {destination.Length}.", nameof(destination));
        }

        for (var corner = 0; corner < poly.VertexCount; corner++) {
            destination[corner] = tile.Data.Vertices[tile.Data.PolyVertices[poly.FirstVertex + corner]];
        }

        return poly.VertexCount;
    }

    /// <summary>A polygon's area id and flags.</summary>
    /// <param name="reference">The polygon.</param>
    /// <param name="area">Its area.</param>
    /// <param name="flags">Its flags.</param>
    /// <returns><see langword="false" /> if the reference does not resolve.</returns>
    public bool TryGetPolyAttributes(NavPolyRef reference, out byte area, out NavPolyFlags flags) {
        if (!TryGetPoly(reference, out var tile, out var index)) {
            area = NavArea.Null;
            flags = NavPolyFlags.None;

            return false;
        }

        area = tile.Polys[index].Area;
        flags = tile.Polys[index].Flags;

        return true;
    }

    /// <summary>Changes a polygon's flags — closing a door, disabling a bridge.</summary>
    /// <param name="reference">The polygon.</param>
    /// <param name="flags">Its new flags.</param>
    /// <returns><see langword="false" /> if the reference does not resolve.</returns>
    /// <remarks>
    ///     This is the cheap half of dynamic navigation and the reason flags exist as a separate word
    ///     from the area: nothing is rebaked, no reference is invalidated, and the next query simply
    ///     stops considering the polygon. What it cannot do is change the <i>shape</i> of the walkable
    ///     surface, which is a rebake of the tile.
    /// </remarks>
    public bool SetPolyFlags(NavPolyRef reference, NavPolyFlags flags) {
        if (!TryGetPoly(reference, out var tile, out var index)) {
            return false;
        }

        tile.Polys[index].Flags = flags;

        return true;
    }

    /// <summary>Walks a polygon's neighbours, inside its tile and across its borders.</summary>
    /// <param name="reference">The polygon.</param>
    /// <returns>The neighbours.</returns>
    public NeighbourEnumerator Neighbours(NavPolyRef reference) => new(this, reference);

    /// <summary>
    ///     The segment two adjacent polygons share, as the two points a funnel puts on its left and
    ///     right sides.
    /// </summary>
    /// <param name="from">The polygon being left.</param>
    /// <param name="to">The polygon being entered.</param>
    /// <param name="left">The end of the shared segment on the left of the direction of travel.</param>
    /// <param name="right">The end on the right.</param>
    /// <returns><see langword="false" /> if the two are not adjacent.</returns>
    /// <remarks>
    ///     Which end is which is decided by the polygons' winding, which the bake fixes: a polygon's
    ///     vertices run counter-clockwise seen from above, so walking out through edge <c>i</c> puts
    ///     vertex <c>i</c> on the right and vertex <c>i + 1</c> on the left. Deriving it from the
    ///     direction of travel instead would be one cross product per portal and would disagree with
    ///     itself whenever a path doubles back inside a polygon.
    /// </remarks>
    public bool GetPortalPoints(NavPolyRef from, NavPolyRef to, out Vector3 left, out Vector3 right) {
        left = default;
        right = default;

        if (!TryGetPoly(from, out var tile, out var index)) {
            return false;
        }

        ref var poly = ref tile.Polys[index];

        for (var edge = 0; edge < poly.VertexCount; edge++) {
            var neighbour = tile.Data.PolyNeighbours[poly.FirstVertex + edge];

            if (neighbour >= 0 && ReferenceOf(tile, neighbour) == to) {
                (right, left) = EdgePoints(tile, in poly, edge);

                return true;
            }
        }

        for (var link = poly.FirstLink; link >= 0; link = tile.Links[link].Next) {
            if (tile.Links[link].Neighbour != to) {
                continue;
            }

            var (start, end) = EdgePoints(tile, in poly, tile.Links[link].Edge);
            var direction = end - start;

            right = start + (direction * tile.Links[link].Min);
            left = start + (direction * tile.Links[link].Max);

            return true;
        }

        return false;
    }

    /// <summary>The midpoint of the segment two adjacent polygons share.</summary>
    /// <param name="from">The polygon being left.</param>
    /// <param name="to">The polygon being entered.</param>
    /// <param name="point">The midpoint.</param>
    /// <returns><see langword="false" /> if the two are not adjacent.</returns>
    public bool GetEdgeMidpoint(NavPolyRef from, NavPolyRef to, out Vector3 point) {
        if (!GetPortalPoints(from, to, out var left, out var right)) {
            point = default;

            return false;
        }

        point = (left + right) * 0.5f;

        return true;
    }

    internal static (Vector3 Start, Vector3 End) EdgePoints(NavMeshTile tile, ref readonly NavPoly poly, int edge) {
        var vertices = tile.Data.Vertices;
        var indices = tile.Data.PolyVertices;
        var start = vertices[indices[poly.FirstVertex + edge]];
        var end = vertices[indices[poly.FirstVertex + ((edge + 1) % poly.VertexCount)]];

        return (start, end);
    }

    static long Key(int x, int z) => ((long)x << 32) | (uint)z;

    /// <summary>Recomputes one tile's links to all four of its neighbours.</summary>
    /// <remarks>
    ///     Wholesale rather than incrementally. A tile has links only along its border, so the work is
    ///     proportional to the perimeter rather than the area, and doing it in one place means there
    ///     is no partial-update path that can leave a link pointing at an unloaded tile.
    /// </remarks>
    void RebuildLinks(NavMeshTile tile) {
        linkScratch.Clear();

        for (var index = 0; index < tile.Polys.Length; index++) {
            tile.Polys[index].FirstLink = -1;
        }

        for (var side = 0; side < SideOffsets.Length; side++) {
            var (offsetX, offsetZ) = SideOffsets[side];
            var neighbour = TileAt(tile.Data.X + offsetX, tile.Data.Z + offsetZ);

            if (neighbour is not null) {
                ConnectSide(tile, neighbour, side);
            }
        }

        tile.SetLinks([.. linkScratch]);
    }

    /// <summary>Links every border edge of one tile's side to whatever it overlaps in the neighbour.</summary>
    void ConnectSide(NavMeshTile tile, NavMeshTile neighbour, int side) {
        var border = BorderCoordinate(tile, side);
        var alongX = side is 1 or 3;
        var tolerance = Params.BorderTolerance;

        for (var index = 0; index < tile.Polys.Length; index++) {
            ref var poly = ref tile.Polys[index];

            for (var edge = 0; edge < poly.VertexCount; edge++) {
                if (tile.Data.PolyNeighbours[poly.FirstVertex + edge] >= 0) {
                    continue;
                }

                var (start, end) = EdgePoints(tile, in poly, edge);

                if (!OnBorder(start, end, border, alongX, tolerance)) {
                    continue;
                }

                var from = alongX ? start.X : start.Z;
                var to = alongX ? end.X : end.Z;

                ConnectEdge(tile, index, edge, from, to, (start.Y + end.Y) * 0.5f, neighbour, border, alongX);
            }
        }
    }

    /// <summary>Finds the parts of one border edge that a neighbour tile's border edges cover.</summary>
    void ConnectEdge(
        NavMeshTile tile,
        int polyIndex,
        int edge,
        float from,
        float to,
        float height,
        NavMeshTile neighbour,
        float border,
        bool alongX
    ) {
        var span = to - from;

        if (MathF.Abs(span) < 1e-6f) {
            return;
        }

        var low = MathF.Min(from, to);
        var high = MathF.Max(from, to);

        for (var other = 0; other < neighbour.Polys.Length; other++) {
            ref var otherPoly = ref neighbour.Polys[other];

            for (var otherEdge = 0; otherEdge < otherPoly.VertexCount; otherEdge++) {
                if (neighbour.Data.PolyNeighbours[otherPoly.FirstVertex + otherEdge] >= 0) {
                    continue;
                }

                var (otherStart, otherEnd) = EdgePoints(neighbour, in otherPoly, otherEdge);

                if (!OnBorder(otherStart, otherEnd, border, alongX, Params.BorderTolerance)) {
                    continue;
                }

                if (MathF.Abs(((otherStart.Y + otherEnd.Y) * 0.5f) - height) > Params.ClimbTolerance) {
                    continue;
                }

                var otherLow = MathF.Min(alongX ? otherStart.X : otherStart.Z, alongX ? otherEnd.X : otherEnd.Z);
                var otherHigh = MathF.Max(alongX ? otherStart.X : otherStart.Z, alongX ? otherEnd.X : otherEnd.Z);

                var overlapLow = MathF.Max(low, otherLow);
                var overlapHigh = MathF.Min(high, otherHigh);

                // A shared corner is an overlap of zero length and connects nothing an agent can walk
                // through, so the test is strictly greater than the tolerance rather than zero.
                if (overlapHigh - overlapLow <= Params.BorderTolerance) {
                    continue;
                }

                var first = (overlapLow - from) / span;
                var second = (overlapHigh - from) / span;

                linkScratch.Add(new() {
                    Neighbour = ReferenceOf(neighbour, other),
                    Next = tile.Polys[polyIndex].FirstLink,
                    Edge = (byte)edge,
                    Min = MathF.Min(first, second),
                    Max = MathF.Max(first, second)
                });

                tile.Polys[polyIndex].FirstLink = linkScratch.Count - 1;
            }
        }
    }

    /// <summary>The world coordinate of the plane a tile's side lies in.</summary>
    float BorderCoordinate(NavMeshTile tile, int side) => side switch {
        0 => Params.Origin.X + (tile.Data.X * Params.TileWidth),
        2 => Params.Origin.X + ((tile.Data.X + 1) * Params.TileWidth),
        1 => Params.Origin.Z + ((tile.Data.Z + 1) * Params.TileDepth),
        _ => Params.Origin.Z + (tile.Data.Z * Params.TileDepth)
    };

    static bool OnBorder(Vector3 start, Vector3 end, float border, bool alongX, float tolerance) {
        var startCoordinate = alongX ? start.Z : start.X;
        var endCoordinate = alongX ? end.Z : end.X;

        return MathF.Abs(startCoordinate - border) <= tolerance && MathF.Abs(endCoordinate - border) <= tolerance;
    }

    /// <summary>What a polygon is next to, and across which part of which of its edges.</summary>
    /// <param name="Reference">The neighbouring polygon.</param>
    /// <param name="Edge">The edge of the polygon being asked about.</param>
    /// <param name="Min">Where the shared part of that edge starts, as a parameter along it.</param>
    /// <param name="Max">Where it ends. An interior neighbour shares the whole edge, so 0 and 1.</param>
    public readonly record struct Neighbour(NavPolyRef Reference, int Edge, float Min, float Max);

    /// <summary>Walks a polygon's neighbours without allocating.</summary>
    /// <remarks>
    ///     Interior neighbours first, then the links across tile borders. A polygon has at most six of
    ///     the first and, in a mesh whose tiles disagree about where their border vertices are, a
    ///     handful of the second.
    /// </remarks>
    public struct NeighbourEnumerator {
        readonly NavMeshTile? tile;
        readonly int polyIndex;
        int edge;
        int link;

        internal NeighbourEnumerator(NavMesh mesh, NavPolyRef reference) {
            if (mesh.TryGetPoly(reference, out var resolved, out var index)) {
                tile = resolved;
                polyIndex = index;
                link = resolved.Polys[index].FirstLink;
            } else {
                tile = null;
                polyIndex = 0;
                link = -1;
            }

            edge = 0;
            Current = default;
        }

        /// <summary>The neighbour the enumerator is on.</summary>
        public Neighbour Current { get; private set; }

        /// <summary>So that this can be used in a <c>foreach</c>.</summary>
        /// <returns>Itself.</returns>
        public NeighbourEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next neighbour.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            if (tile is null) {
                return false;
            }

            ref var poly = ref tile.Polys[polyIndex];

            while (edge < poly.VertexCount) {
                var neighbour = tile.Data.PolyNeighbours[poly.FirstVertex + edge];
                var crossed = edge++;

                if (neighbour >= 0) {
                    Current = new(ReferenceOf(tile, neighbour), crossed, 0f, 1f);

                    return true;
                }
            }

            if (link < 0) {
                return false;
            }

            Current = new(tile.Links[link].Neighbour, tile.Links[link].Edge, tile.Links[link].Min, tile.Links[link].Max);
            link = tile.Links[link].Next;

            return true;
        }
    }
}
