// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation;

/// <summary>One polygon, as the bake wrote it.</summary>
/// <remarks>
///     Vertices are a range in <see cref="NavMeshTileData.PolyVertices" /> rather than an array of
///     their own: a tile holds thousands of polygons of three to six vertices each, and an array per
///     polygon would be thousands of objects for a level that has one shape.
/// </remarks>
public struct NavMeshPolyData {
    /// <summary>Where this polygon's vertex indices start in <see cref="NavMeshTileData.PolyVertices" />.</summary>
    public int FirstVertex;

    /// <summary>How many vertices it has.</summary>
    public byte VertexCount;

    /// <summary>Its area id — see <see cref="NavArea" />.</summary>
    public byte Area;

    /// <summary>What it may be used for.</summary>
    public NavPolyFlags Flags;
}

/// <summary>
///     A baked tile: the polygons, their vertices, and which of them touch each other. No links to
///     any other tile, and nothing that depends on where it is loaded.
/// </summary>
/// <remarks>
///     <para>
///         This is the artefact — what a bake produces, what a content build would write to disk, and
///         what <see cref="NavMesh.AddTile" /> takes. It is deliberately inert: arrays of numbers, no
///         references out, and no notion of the mesh it will join. Everything that <i>is</i> about
///         the surrounding mesh — the salt, the slot, the links across the tile border — is computed
///         when it is added and thrown away when it is removed, so the same data can be loaded into
///         two meshes at once, or into the same slot twice.
///     </para>
///     <para>
///         Vertices are in world space, not tile-local. That costs nothing here — a tile is baked at
///         the position it will be loaded at — and it saves every query from a per-tile offset that
///         is right in all the ordinary cases and wrong in the one where a tile is loaded somewhere
///         else, which is a thing this design does not offer.
///     </para>
/// </remarks>
public sealed class NavMeshTileData {
    /// <summary>Creates a tile.</summary>
    /// <param name="x">The tile's column in the mesh's tile grid.</param>
    /// <param name="z">The tile's row in the mesh's tile grid.</param>
    /// <param name="vertices">The vertices, in world space.</param>
    /// <param name="polys">The polygons.</param>
    /// <param name="polyVertices">Vertex indices, in the ranges the polygons name.</param>
    /// <param name="polyNeighbours">
    ///     One entry per entry of <paramref name="polyVertices" />: the polygon on the far side of
    ///     the edge that starts at that vertex, or -1 for an edge that is a wall or a tile border.
    /// </param>
    public NavMeshTileData(
        int x,
        int z,
        Vector3[] vertices,
        NavMeshPolyData[] polys,
        int[] polyVertices,
        int[] polyNeighbours
    ) {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(polys);
        ArgumentNullException.ThrowIfNull(polyVertices);
        ArgumentNullException.ThrowIfNull(polyNeighbours);

        if (polyNeighbours.Length != polyVertices.Length) {
            throw new ArgumentException(
                $"There are {polyVertices.Length} polygon vertices and {polyNeighbours.Length} neighbours; an edge starts at every vertex, so they are the same length.",
                nameof(polyNeighbours)
            );
        }

        X = x;
        Z = z;
        Vertices = vertices;
        Polys = polys;
        PolyVertices = polyVertices;
        PolyNeighbours = polyNeighbours;
        Bounds = vertices.Length == 0 ? BoundingBox.Empty : BoundingBox.FromPoints(vertices);
    }

    /// <summary>The tile's column in the mesh's tile grid.</summary>
    public int X { get; }

    /// <summary>The tile's row in the mesh's tile grid.</summary>
    public int Z { get; }

    /// <summary>Everything the tile's polygons are made of, in world space.</summary>
    public Vector3[] Vertices { get; }

    /// <summary>The polygons.</summary>
    public NavMeshPolyData[] Polys { get; }

    /// <summary>Vertex indices, referenced by range from <see cref="Polys" />.</summary>
    public int[] PolyVertices { get; }

    /// <summary>Interior adjacency, parallel to <see cref="PolyVertices" />. -1 where there is none.</summary>
    public int[] PolyNeighbours { get; }

    /// <summary>What the tile's geometry spans.</summary>
    public BoundingBox Bounds { get; }
}
