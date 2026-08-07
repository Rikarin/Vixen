// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>What `vixen uv pack` reads before it repacks anything: the atlas a file already has.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect these exist for reads a connected component over <i>vertex indices</i> as an
///         island.</b> That is true only of a file that shares an index wherever the position and the
///         coordinate agree. A generated GLB shares nothing: measured on sixteen image-to-3D outputs,
///         every one carries exactly three drawing vertices per triangle, so components over raw indices
///         came out at one island per triangle — 25 427 on a 25 439-triangle mesh whose atlas has 422.
///         Both numbers were reproduced exactly, and the packer duly packed 25 427 single triangles.
///     </para>
///     <para>
///         ⚠ <b>The fix may not weld a seam away, which is what the second half of this suite holds.</b>
///         docs/plan/42's seventh exit criterion is a mesh unwrapped elsewhere, imported and repacked
///         with "seams untouched, island shapes untouched" — so two corners at one position that
///         disagree about the coordinate have to stay two, and the reasoning the old code was right
///         about has to keep working.
///     </para>
/// </remarks>
public class ModelGeometryIslandTests {
    /// <summary>A mesh that shares no index at all is still the one island its atlas describes.</summary>
    /// <remarks>
    ///     ⚠ <b>The fixture is the finding.</b> Every triangle carries its own three vertices, which is
    ///     ordinary for anything that came out of a generator, and the whole grid is laid out as one
    ///     continuous run of the atlas. Read over indices this is 32 islands; read over the atlas it is
    ///     one, and one is what the file means.
    /// </remarks>
    [Fact]
    public void A_mesh_that_shares_no_index_is_still_one_island() {
        var mesh = Unwelded(4, 4);

        Assert.Equal(mesh.Indices.Length, mesh.Positions.Length);
        Assert.Equal(32, mesh.TriangleCount);

        var islands = ModelGeometry.Islands(mesh);

        Assert.Single(islands);
        Assert.Equal(mesh.Indices.Length, islands[0].Corners.Count);
    }

    /// <summary>A seam the exporter really did split stays a seam, and its two sides stay two islands.</summary>
    /// <remarks>
    ///     ⚠ <b>The case the old reading was right about, and the one a weld would destroy.</b> The two
    ///     halves meet along a shared row of positions and disagree about the coordinate there, which is
    ///     what a seam <i>is</i>. Welding by position alone — the obvious repair — puts them back into
    ///     one island and silently repacks somebody's atlas into a different shape.
    /// </remarks>
    [Fact]
    public void A_split_seam_stays_two_islands() {
        var mesh = Seamed();
        var islands = ModelGeometry.Islands(mesh);

        Assert.Equal(2, islands.Count);
        Assert.Equal(mesh.Indices.Length, islands[0].Corners.Count + islands[1].Corners.Count);
    }

    /// <summary>Two surfaces that touch nowhere are two islands however the file indexes them.</summary>
    [Fact]
    public void Two_disjoint_surfaces_are_two_islands() {
        var one = Unwelded(2, 2);
        var two = Unwelded(2, 2);

        for (var vertex = 0; vertex < two.Positions.Length; vertex++) {
            two.Positions[vertex] += new Vector3(10f, 0f, 0f);
            two.TexCoords[vertex] += new Vector2(4f, 0f);
        }

        Assert.Equal(2, ModelGeometry.Islands(Join(one, two)).Count);
    }

    /// <summary>Every corner of every island lands on the corner of the mesh it came from.</summary>
    /// <remarks>
    ///     A <c>UvIsland.Corners</c> entry is a slot in <c>MeshData.Indices</c>, and `vixen uv pack`
    ///     writes the packed coordinate back through it — so an entry that pointed anywhere else would
    ///     move a coordinate belonging to a different triangle.
    /// </remarks>
    [Fact]
    public void Every_corner_indexes_the_slot_it_came_from() {
        var mesh = Seamed();
        var seen = new HashSet<int>();

        foreach (var island in ModelGeometry.Islands(mesh)) {
            Assert.Equal(island.Corners.Count, island.Coordinates.Count);

            for (var corner = 0; corner < island.Corners.Count; corner++) {
                var slot = island.Corners[corner];

                Assert.InRange(slot, 0, mesh.Indices.Length - 1);
                Assert.True(seen.Add(slot), $"Slot {slot} is in two islands.");
                Assert.Equal(mesh.TexCoords[mesh.Indices[slot]], island.Coordinates[corner]);
            }
        }

        Assert.Equal(mesh.Indices.Length, seen.Count);
    }

    /// <summary>A grid with one vertex per corner and nothing shared, laid out as one atlas run.</summary>
    static MeshData Unwelded(int across, int along) {
        var positions = new List<Vector3>();
        var coordinates = new List<Vector2>();
        var indices = new List<int>();

        void Corner(int column, int row) {
            indices.Add(positions.Count);
            positions.Add(new((float) column / across, 0f, (float) row / along));
            coordinates.Add(new((float) column / across, (float) row / along));
        }

        for (var row = 0; row < along; row++) {
            for (var column = 0; column < across; column++) {
                Corner(column, row);
                Corner(column, row + 1);
                Corner(column + 1, row + 1);

                Corner(column, row);
                Corner(column + 1, row + 1);
                Corner(column + 1, row);
            }
        }

        return Built(positions, coordinates, indices);
    }

    /// <summary>Two halves of one surface, split down the middle the way an exporter splits a seam.</summary>
    static MeshData Seamed() {
        var left = Unwelded(2, 2);
        var right = Unwelded(2, 2);

        // The right half sits against the left along x = 1 and its atlas is somewhere else entirely,
        // which is a seam: one position, two coordinates.
        for (var vertex = 0; vertex < right.Positions.Length; vertex++) {
            right.Positions[vertex] += new Vector3(1f, 0f, 0f);
            right.TexCoords[vertex] += new Vector2(0f, 4f);
        }

        return Join(left, right);
    }

    /// <summary>Two meshes end to end, with the second's indices shifted past the first's vertices.</summary>
    static MeshData Join(MeshData one, MeshData two) {
        var indices = new List<int>(one.Indices);

        foreach (var index in two.Indices) {
            indices.Add(index + one.Positions.Length);
        }

        return Built(
            [.. one.Positions, .. two.Positions],
            [.. one.TexCoords, .. two.TexCoords],
            indices
        );
    }

    static MeshData Built(List<Vector3> positions, List<Vector2> coordinates, List<int> indices) {
        var low = positions[0];
        var high = positions[0];

        foreach (var position in positions) {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        return new() {
            Name = "island-fixture",
            Positions = [.. positions],
            TexCoords = [.. coordinates],
            Indices = [.. indices],
            Bounds = new(low, high)
        };
    }
}
