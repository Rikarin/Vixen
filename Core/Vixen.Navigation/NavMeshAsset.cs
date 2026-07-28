// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Navigation.Baking;

namespace Vixen.Navigation;

/// <summary>
///     A whole baked navmesh as one value: the tile grid, and every tile on it.
/// </summary>
/// <remarks>
///     <para>
///         This is the artefact. A content build bakes one and writes it; a player reads it and calls
///         <see cref="ToNavMesh" />; nothing in between has to know how it was produced. Keeping it a
///         separate type from <see cref="NavMesh" /> is what makes that possible — the mesh holds
///         salts, slots and links, which are facts about a particular load rather than about the
///         level, and serialising them would be serialising a moment.
///     </para>
///     <para>
///         Serialised through <c>Vixen.Core.Serialization</c>, so the format is the engine's ordinary
///         one: positional, little-endian, floats by their bits — which is what makes two bakes of the
///         same level compare byte for byte and lets the content build's determinism check mean
///         something.
///     </para>
/// </remarks>
[DataContract("NavMesh")]
public sealed record NavMeshAsset {
    /// <summary>Where the tile grid is and how big its tiles are.</summary>
    public NavMeshParams Params { get; set; } = NavMeshParams.Single;

    /// <summary>The tiles. Empty positions in the grid are simply absent.</summary>
    public NavMeshTileData[] Tiles { get; set; } = [];

    /// <summary>Wraps the result of a tiled bake.</summary>
    /// <param name="result">What <see cref="NavMeshBaker.BakeTiles" /> produced.</param>
    /// <returns>The asset.</returns>
    public static NavMeshAsset FromBake(NavMeshBakeResult result) => new() {
        Params = result.Params,
        Tiles = [.. result.Tiles]
    };

    /// <summary>Wraps a single tile baked with <see cref="NavMeshParams.Single" />.</summary>
    /// <param name="tile">The tile.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile" /> is null.</exception>
    public static NavMeshAsset FromTile(NavMeshTileData tile) {
        ArgumentNullException.ThrowIfNull(tile);

        return new() { Params = NavMeshParams.Single, Tiles = [tile] };
    }

    /// <summary>Loads the asset into a mesh that can be queried.</summary>
    /// <returns>The mesh, with every tile added and linked.</returns>
    public NavMesh ToNavMesh() {
        var mesh = new NavMesh(Params);

        foreach (var tile in Tiles) {
            mesh.AddTile(tile);
        }

        return mesh;
    }

    /// <summary>How many polygons the whole mesh holds. Diagnostic, and what a build log wants.</summary>
    public int PolyCount {
        get {
            var total = 0;

            foreach (var tile in Tiles) {
                total += tile.Polys.Length;
            }

            return total;
        }
    }
}
