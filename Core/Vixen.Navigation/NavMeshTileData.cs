// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Navigation;

/// <summary>One polygon, as the bake wrote it.</summary>
/// <remarks>
///     Vertices are a range in <see cref="NavMeshTileData.PolyVertices" /> rather than an array of
///     their own: a tile holds thousands of polygons of three to six vertices each, and an array per
///     polygon would be thousands of objects for a level that has one shape.
/// </remarks>
[DataContract("NavMeshPoly")]
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

/// <summary>Where one polygon's height detail is, in the tile's detail arrays.</summary>
/// <remarks>
///     <para>
///         The triangles index the polygon's own vertices and the detail vertices at once: an index
///         below the polygon's vertex count names one of its corners, and anything at or above it
///         names <see cref="NavMeshTileData.DetailVertices" /> offset by <see cref="FirstVertex" />.
///     </para>
///     <para>
///         The corners are not repeated because a repeated corner is a corner that can disagree with
///         itself. Two polygons meeting along an edge take its endpoints from the same place, so their
///         detail surfaces meet there exactly; a detail mesh with its own copies would meet there only
///         as accurately as the copies were made.
///     </para>
/// </remarks>
[DataContract("NavMeshDetail")]
public struct NavMeshDetailData {
    /// <summary>Where this polygon's added vertices start in <see cref="NavMeshTileData.DetailVertices" />.</summary>
    public int FirstVertex;

    /// <summary>How many it added, over and above its own corners.</summary>
    public int VertexCount;

    /// <summary>Where its triangles start in <see cref="NavMeshTileData.DetailTriangles" />, in triangles.</summary>
    public int FirstTriangle;

    /// <summary>How many it has.</summary>
    public int TriangleCount;
}

/// <summary>A way from one point of the mesh to another that is not a walk.</summary>
/// <remarks>
///     <para>
///         A ladder, a jump down a ledge, a zip line, a door that teleports. What they have in common
///         is that the two ends are not adjacent on the surface and no amount of baking will make them
///         so — the connection is authored, and the bake's job is only to work out which polygon each
///         end lands on.
///     </para>
///     <para>
///         <see cref="UserId" /> is the game's, and is the point of the whole feature: a connection is
///         useless unless something can recognise it and play the right animation, and the navmesh has
///         no opinion about what a jump looks like.
///     </para>
/// </remarks>
[DataContract("NavOffMeshConnection")]
public struct NavOffMeshConnectionData {
    /// <summary>Where it starts, in world space.</summary>
    public Vector3 Start;

    /// <summary>Where it ends.</summary>
    public Vector3 End;

    /// <summary>How far from an end the mesh is searched for the polygon to attach it to.</summary>
    public float Radius;

    /// <summary>Whether it can be used in both directions, or only from start to end.</summary>
    public bool Bidirectional;

    /// <summary>Its area id, so that using one can be made expensive.</summary>
    public byte Area;

    /// <summary>What it may be used for, so that a jump can be refused to an agent that cannot jump.</summary>
    public NavPolyFlags Flags;

    /// <summary>Whatever the game wants back when an agent uses it.</summary>
    public uint UserId;
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
[DataContract("NavMeshTile")]
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

        X = x;
        Z = z;
        Vertices = vertices;
        Polys = polys;
        PolyVertices = polyVertices;
        PolyNeighbours = polyNeighbours;

        Validate();
    }

    /// <summary>Creates an empty tile, for an object initialiser or a deserialiser to fill.</summary>
    /// <remarks>
    ///     The serializer reaches the <c>init</c> setters directly, so the constructor above — and the
    ///     check it makes — is not on the path a tile takes when it is read from disk.
    ///     <see cref="NavMesh.AddTile" /> calls <see cref="Validate" /> for that reason: a tile is
    ///     checked once, wherever it came from.
    /// </remarks>
    public NavMeshTileData() { }

    /// <summary>The tile's column in the mesh's tile grid.</summary>
    public int X { get; init; }

    /// <summary>The tile's row in the mesh's tile grid.</summary>
    public int Z { get; init; }

    /// <summary>Everything the tile's polygons are made of, in world space.</summary>
    public Vector3[] Vertices { get; init; } = [];

    /// <summary>The polygons.</summary>
    public NavMeshPolyData[] Polys { get; init; } = [];

    /// <summary>Vertex indices, referenced by range from <see cref="Polys" />.</summary>
    public int[] PolyVertices { get; init; } = [];

    /// <summary>Interior adjacency, parallel to <see cref="PolyVertices" />. -1 where there is none.</summary>
    public int[] PolyNeighbours { get; init; } = [];

    /// <summary>One entry per polygon, or empty if the bake did not sample the ground.</summary>
    /// <remarks>
    ///     Empty is a complete answer rather than a missing one: a polygon with no detail is exactly
    ///     its own plane, which is what a flat floor is, and the query falls back to that.
    /// </remarks>
    public NavMeshDetailData[] Detail { get; init; } = [];

    /// <summary>The vertices the detail added, in world space. The polygons' own are not repeated here.</summary>
    public Vector3[] DetailVertices { get; init; } = [];

    /// <summary>Three indices per detail triangle, resolved through <see cref="NavMeshDetailData" />.</summary>
    public int[] DetailTriangles { get; init; } = [];

    /// <summary>The authored ways off the surface that start in this tile.</summary>
    /// <remarks>
    ///     Held by the tile whose grid cell contains the connection's <i>start</i>, so that unloading
    ///     a tile takes its connections with it. The far end may be in another tile, and is resolved
    ///     when the tile is added — which is also when a connection whose far end is not loaded
    ///     quietly becomes a one-ended stub that nothing can use.
    /// </remarks>
    public NavOffMeshConnectionData[] OffMeshConnections { get; init; } = [];

    /// <summary>What the tile's geometry spans.</summary>
    /// <remarks>
    ///     Derived and cached rather than stored, so it is not in the serialised form. It is a
    ///     function of the vertices, and a stored copy is a thing that can disagree with them — for
    ///     twenty-four bytes saved on a file that holds thousands of vertices.
    /// </remarks>
    public BoundingBox Bounds {
        get {
            if (bounds is null) {
                bounds = Vertices.Length == 0 ? BoundingBox.Empty : BoundingBox.FromPoints(Vertices);
            }

            return bounds.Value;
        }
    }

    BoundingBox? bounds;

    /// <summary>Throws if the arrays do not describe a tile.</summary>
    /// <exception cref="ArgumentException">The neighbour array does not match the vertex array.</exception>
    public void Validate() {
        if (PolyNeighbours.Length != PolyVertices.Length) {
            throw new ArgumentException(
                $"There are {PolyVertices.Length} polygon vertices and {PolyNeighbours.Length} neighbours; an edge starts at every vertex, so they are the same length."
            );
        }

        if (Detail.Length != 0 && Detail.Length != Polys.Length) {
            throw new ArgumentException(
                $"There are {Polys.Length} polygons and {Detail.Length} detail entries. Detail is per polygon, so it is either absent or one each."
            );
        }
    }
}
