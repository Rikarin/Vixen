// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 5's host half: the tiling arithmetic, the per-cluster material records, and the constants
///     both sides of the binning have to agree on.
/// </summary>
/// <remarks>
///     Whether the resolve <em>shades</em> correctly is two other things' business —
///     <c>ClusterAttributeTests</c> for the reconstruction and <c>LibraryTreeTests</c> for the material
///     tree composing through it. What is here is the plumbing in between, where the failures are
///     off-by-ones in an index a shader and a host each compute independently.
/// </remarks>
public sealed class VisibilityResolveTests {
    /// <summary>
    ///     The host's tiling constants are the shader's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four numbers that appear in two files and index the same buffers. A tile size that
    ///         disagreed would put a workgroup over the wrong pixels; a capacity that disagreed would have
    ///         the host sizing a buffer the shader writes past the end of; a material ceiling that
    ///         disagreed would leave a bin the host never dispatches. None of the three is a compile
    ///         error, and none produces anything as legible as a crash.
    ///     </para>
    ///     <para>
    ///         Against the shader source rather than against a generated constant, because Raven publishes
    ///         bindings and permutations in its reflection and not the <c>const val</c>s inside a struct.
    ///         The same defence <c>GpuClusterCulling</c>'s queue capacity has.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_tiling_constants_match_the_shader() {
        var source = Source("Pipeline", "VisibilityTiles.rvn");

        Assert.Contains($"const val Size = {GpuVisibilityTiles.TileSize}", source, StringComparison.Ordinal);
        Assert.Contains($"const val Capacity = {GpuVisibilityTiles.DefaultTileCapacity}", source, StringComparison.Ordinal);

        // And the capacity the shader actually indexes with is a uniform, so a frame that overflowed
        // can be answered rather than only reported.
        Assert.Contains("var tileCapacity: uint", source, StringComparison.Ordinal);
        Assert.Contains("if (at < tileCapacity) {", source, StringComparison.Ordinal);
        Assert.Contains($"const val MaxMaterials = {GpuVisibilityTiles.MaxMaterials}", source, StringComparison.Ordinal);

        Assert.Contains(
            $"const val ArgumentWords = {GpuVisibilityTiles.ArgumentWords}",
            source,
            StringComparison.Ordinal
        );

        // A tile is one workgroup, and its pixels are its lanes. The resolve indexes a lane into a tile by
        // dividing by the size, so a workgroup that was not exactly the tile's pixel count would leave
        // pixels unshaded at one edge of every tile.
        Assert.Equal(64, GpuVisibilityTiles.TileSize * GpuVisibilityTiles.TileSize);
        Assert.Contains("[ComputeShader(64)]", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A tile's index and a tile's position are inverses, including on a screen that does not divide.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The binner writes an index and the resolve reads it back as a position, so the two functions
    ///         are a round trip across a buffer. What makes it worth a test is the ragged edge: a screen
    ///         whose width is not a multiple of the tile size has a final column of tiles that hang over
    ///         it, and a count computed by truncation instead of by rounding up drops them — which is a
    ///         strip of unshaded pixels down one side, at some resolutions and not others.
    ///     </para>
    ///     <para>
    ///         The host mirror is asserted here and the shader's is asserted by source, because they are
    ///         four lines each and a mirror of four lines is cheaper than a device test of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tiles_index_and_its_position_are_inverses() {
        foreach (var size in (Int2[])[new(1920, 1080), new(1280, 720), new(1, 1), new(1913, 1077), new(7, 9)]) {
            var count = GpuVisibilityTiles.TilesFor(size);

            // Every pixel is in some tile, which is what rounding up rather than truncating buys.
            Assert.True(count.X * GpuVisibilityTiles.TileSize >= size.X, $"{size} loses a column.");
            Assert.True(count.Y * GpuVisibilityTiles.TileSize >= size.Y, $"{size} loses a row.");

            // And no more tiles than that: a spare column is a workgroup that shades nothing.
            Assert.True((count.X - 1) * GpuVisibilityTiles.TileSize < size.X, $"{size} has a spare column.");
            Assert.True((count.Y - 1) * GpuVisibilityTiles.TileSize < size.Y, $"{size} has a spare row.");

            for (var y = 0; y < count.Y; y++) {
                for (var x = 0; x < count.X; x++) {
                    var index = (y * count.X) + x;

                    Assert.Equal(x, index % count.X);
                    Assert.Equal(y, index / count.X);
                }
            }
        }
    }

    /// <summary>
    ///     A material's tile list and its dispatch arguments do not overlap another material's.
    /// </summary>
    /// <remarks>
    ///     Two strides over two shared buffers, computed by the host to bind with and by the shader to
    ///     write with. An overlap is one material's tiles appearing in another's dispatch, which shades the
    ///     wrong pixels with the wrong material — a picture, and a plausible one.
    /// </remarks>
    /// <summary>
    ///     A frame that overflowed makes the lists larger, and the growth terminates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The difference between a diagnostic and a policy.</b> <c>Overflowed</c> reported that a
    ///         material wanted more tiles than its list held — which is a hole in the picture — and
    ///         nothing did anything about it, so the same hole appeared every frame for as long as the
    ///         camera stayed there.
    ///     </para>
    ///     <para>
    ///         The ceiling is the assertion that matters. A capacity that doubles forever is a buffer
    ///         that eventually fails to allocate; a material's list holds tiles that exist on the screen,
    ///         so past that count there is nothing left to drop and the growth is finished.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_overflowed_list_grows_and_stops_growing() {
        var screen = new Int2(240, 180);
        var tiles = screen.X * screen.Y;

        // One step, and it clears the count that overflowed rather than merely doubling toward it.
        var once = GpuVisibilityTiles.NextCapacity(GpuVisibilityTiles.DefaultTileCapacity, 40000, screen);

        Assert.True(once >= 40000, $"A capacity of {once} still drops a material that wanted 40000.");
        Assert.True(once <= tiles, $"A capacity of {once} is larger than the {tiles} tiles on the screen.");

        // And it converges: whatever it is asked for, it reaches the ceiling and stays there.
        var capacity = GpuVisibilityTiles.DefaultTileCapacity;

        for (var step = 0; step < 32; step++) {
            capacity = GpuVisibilityTiles.NextCapacity(capacity, int.MaxValue, screen);
        }

        Assert.Equal(tiles, capacity);
        Assert.Equal(tiles, GpuVisibilityTiles.NextCapacity(capacity, int.MaxValue, screen));

        // It never gives capacity back, including for a screen smaller than the buffer already is.
        Assert.Equal(capacity, GpuVisibilityTiles.NextCapacity(capacity, 0, new(8, 8)));
        Assert.Equal(capacity, GpuVisibilityTiles.NextCapacity(capacity, 0, screen));
    }

    [Fact]
    public void Each_materials_lists_are_its_own() {
        using var device = new NullDevice();
        using var tiles = new GpuVisibilityTiles(device);

        for (var material = 1; material < GpuVisibilityTiles.MaxMaterials; material++) {
            Assert.Equal(
                tiles.TileBase(material - 1) + tiles.TileCapacity,
                tiles.TileBase(material)
            );

            Assert.Equal(
                GpuVisibilityTiles.ArgumentOffset(material - 1) + (GpuVisibilityTiles.ArgumentWords * sizeof(uint)),
                GpuVisibilityTiles.ArgumentOffset(material)
            );
        }

        // And element zero of each triple is the count the atomic increments, which is what makes the
        // counter and the dispatch argument one word.
        Assert.Equal(0, GpuVisibilityTiles.ArgumentOffset(0));
    }

    /// <summary>
    ///     Every cluster of a registered mesh carries that mesh's material, and two meshes do not share.
    /// </summary>
    /// <remarks>
    ///     The binning reads one word per cluster to decide which bin a pixel belongs in, so a cluster
    ///     whose material was left at zero would be binned as the first material and shaded with it. Per
    ///     cluster rather than per mesh because phase 8 of the plan routes clusters by whether their
    ///     material discards — nothing today depends on the values being equal within a mesh.
    /// </remarks>
    [Fact]
    public void Every_cluster_carries_its_meshs_material() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var (first, firstPages) = Scene(materialIndex: 0);
        var (second, secondPages) = Scene(materialIndex: 3);

        var a = visibility.Register(first, firstPages, 0);
        var b = visibility.Register(second, secondPages, 1);

        Assert.Equal(4, visibility.MaterialCount);

        // Registered but not uploaded, so this reads what Register built rather than what a device holds.
        Assert.Equal(a.ClusterCount + b.ClusterCount, visibility.ClusterCount);
    }

    /// <summary>The resolve reconstructs through the same arithmetic the raster rastered with.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The plan flags this as the one thing worth an assertion rather than a comment: the
    ///         vertex-side transform runs twice, once to rasterize and once to reconstruct, and a
    ///         disagreement lands attributes on the wrong surface. Not a crash, not a hole — a normal map
    ///         sliding across a mesh as the camera moves.
    ///     </para>
    ///     <para>
    ///         Asserted on the two sources, because what has to agree is the arithmetic and both files
    ///         contain it in full. A shared function would be better and is not possible here: one is a
    ///         vertex stage of a graphics shader and the other a compute stage of a shader that composes a
    ///         material, and Raven has no free functions outside a shader or a struct.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_resolve_decodes_a_vertex_the_way_the_raster_does() {
        var raster = Source("Pipeline", "ClusterRaster.rvn");
        var resolve = Source("Pipeline", "VisibilityResolve.rvn");

        // The page byte offset of a vertex: the slot's base, the cluster's offset, the local index times
        // the stride. Identical text in both, which is the strongest form available to a source assertion.
        const string address = "start + record.vertexOffset + local * record.vertexStride";
        Assert.Contains(address, raster, StringComparison.Ordinal);
        Assert.Contains(address, resolve, StringComparison.Ordinal);

        // The grid decode: an integer addition of the cluster's origin, then the mesh's step.
        Assert.Contains("record.origin.x + int(Short(at))", raster, StringComparison.Ordinal);
        Assert.Contains("record.origin.x + int(Short(at))", resolve, StringComparison.Ordinal);

        foreach (var source in (string[])[raster, resolve]) {
            Assert.Contains("mesh.quantizationOrigin", source, StringComparison.Ordinal);
            Assert.Contains("mesh.quantizationStep", source, StringComparison.Ordinal);
            Assert.Contains("instance.position", source, StringComparison.Ordinal);
            Assert.Contains("instance.scale", source, StringComparison.Ordinal);
        }

        // And the resolve reads the attributes at the offsets the build wrote them at: six bytes of
        // position, three halves of normal, two of coordinate.
        Assert.Contains("Half(at + 6u), Half(at + 8u), Half(at + 10u)", resolve, StringComparison.Ordinal);
        Assert.Contains("Half(at + 12u), Half(at + 14u)", resolve, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The resolve's attribute offsets are the ones the build packs.
    /// </summary>
    /// <remarks>
    ///     The other half of the layout, and the half a shader cannot see: the offsets above are literals
    ///     in the shader and the stride they are inside is a constant in the importer. A page vertex is
    ///     sixteen bytes — six of position, six of normal, four of coordinate — and the shader's <c>+ 14u</c>
    ///     reads the last two of them.
    /// </remarks>
    [Fact]
    public void The_page_vertex_layout_is_what_both_sides_assume() {
        // Position first, then the attributes, and the whole thing is a device word boundary per vertex.
        Assert.Equal(6, MeshletPageBuilder.PositionSize);

        var input = Sphere(8, 16);
        var mesh = MeshletBuilder.Build(input);

        var pages = MeshletPageBuilder.Build(
            mesh,
            input.Positions,
            new byte[input.Positions.Length * 10],
            new() { AttributeStride = 10, PageSize = 4 * 1024 }
        );

        Assert.Equal(16, pages.VertexStride);

        // The last attribute byte the shader reads is at 14 and 15, which is inside the stride and not
        // past it. One off here reads the next vertex's position as a texture coordinate.
        Assert.True(14 + 2 <= pages.VertexStride, "The coordinate runs off the end of a page vertex.");
    }

    static (MeshletMesh Mesh, MeshletPageSet Pages) Scene(int materialIndex) {
        var input = Sphere(10, 20) with { MaterialIndex = materialIndex };
        var mesh = MeshletBuilder.Build(input);

        return (mesh, MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 4 * 1024 }));
    }

    /// <summary>A closed UV sphere: one vertex per pole, and the seam welded.</summary>
    static MeshletBuildInput Sphere(int rings, int segments) {
        var positions = new List<Vector3> { new(0f, 1f, 0f) };

        for (var ring = 1; ring < rings; ring++) {
            var phi = MathF.PI * ring / rings;

            for (var segment = 0; segment < segments; segment++) {
                var theta = 2f * MathF.PI * segment / segments;

                positions.Add(
                    new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta))
                );
            }
        }

        positions.Add(new(0f, -1f, 0f));

        var indices = new List<int>();
        var last = positions.Count - 1;

        int At(int ring, int segment) => 1 + ((ring - 1) * segments) + (segment % segments);

        for (var segment = 0; segment < segments; segment++) {
            indices.AddRange([0, At(1, segment + 1), At(1, segment)]);
            indices.AddRange([last, At(rings - 1, segment), At(rings - 1, segment + 1)]);
        }

        for (var ring = 1; ring < rings - 1; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var a = At(ring, segment);
                var b = At(ring, segment + 1);
                var c = At(ring + 1, segment);
                var d = At(ring + 1, segment + 1);

                indices.AddRange([a, b, c]);
                indices.AddRange([b, d, c]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
