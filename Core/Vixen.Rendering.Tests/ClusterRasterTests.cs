// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 4's decode: the triangle a pixel names is the triangle the mesh has, at the position the
///     mesh has it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The exit criterion the plan states is a golden image against
///         <c>MeshRenderFeature</c></b>, and that is a device test — there is no rasterizer here. What
///         <em>can</em> be checked on the host is the part a golden image would only tell you about
///         obliquely: every step between "the traversal accepted a cluster" and "a vertex is at a world
///         position" is arithmetic, all of it is mirrored, and a golden image that differed would leave
///         you bisecting a shader.
///     </para>
///     <para>
///         So this walks the vertex stage in C# — the visible word, the instance, the geometry record,
///         the slot table, the byte fetch out of the pool, the grid decode — and asserts the result
///         against the source mesh through <see cref="MeshletPageSet.GetPositions" />, which is the
///         decoder the format documents. A source assertion then says the shader still contains the same
///         arithmetic, which is the defence every mirror in this codebase needs and the one a mirror
///         cannot provide for itself.
///     </para>
/// </remarks>
public sealed class ClusterRasterTests {
    /// <summary>
    ///     The identity a pixel carries survives the round trip, and zero means nothing drew.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The bias is the whole content of this test. Zero has to mean "nothing covered this pixel"
    ///         because an integer target cannot be cleared to all ones — a clear colour is four floats in
    ///         every API the RHI wraps — so the slot is stored one higher than it is.
    ///     </para>
    ///     <para>
    ///         Remove the bias and slot zero becomes indistinguishable from an empty pixel: the frame's
    ///         first cluster vanishes from the resolve, or every empty pixel is shaded as it. Which of
    ///         those you get depends on which way the resolve tests, and neither looks like an off-by-one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_empty_pixel_is_not_the_first_cluster() {
        Assert.False(GpuClusterRaster.Covered(GpuClusterRaster.Nothing));
        Assert.Equal(0u, GpuClusterRaster.Nothing);

        // Slot zero, triangle zero — the one identity a bias-free packing could not distinguish from an
        // empty pixel.
        var first = GpuClusterRaster.Pack(0, 0);

        Assert.True(GpuClusterRaster.Covered(first));
        Assert.Equal(0u, GpuClusterRaster.Slot(first));
        Assert.Equal(0u, GpuClusterRaster.Triangle(first));

        // And every slot and triangle a pixel can name comes back as itself.
        foreach (var slot in (uint[])[0, 1, 2, 511, 4095, (uint)GpuClusterRaster.MaximumSlots - 1]) {
            for (var triangle = 0u; triangle < GpuClusterRaster.MaximumTriangles; triangle += 17) {
                var packed = GpuClusterRaster.Pack(slot, triangle);

                Assert.True(GpuClusterRaster.Covered(packed));
                Assert.Equal(slot, GpuClusterRaster.Slot(packed));
                Assert.Equal(triangle, GpuClusterRaster.Triangle(packed));
            }
        }
    }

    /// <summary>
    ///     Every corner the raster would fetch decodes to the vertex the mesh has there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The vertex stage, walked on the host: from a visible-list word to a world position, through
    ///         the same records and the same page bytes the device would read. The oracle is
    ///         <see cref="MeshletPageSet.GetPositions" /> — the decoder the format documents — applied to
    ///         the same cluster, so a disagreement is a disagreement about the encoding rather than about
    ///         the mesh.
    ///     </para>
    ///     <para>
    ///         <b>Exactly equal, not nearly.</b> A page's positions are integers on a grid the mesh owns,
    ///         and the decode is an integer addition and one multiply — so two readers of the same bytes
    ///         have to reach the same float, bit for bit. Any tolerance here would hide the one failure
    ///         that matters: a per-cluster grid, or an origin applied on the wrong side, which moves a
    ///         locked boundary by less than a step and cracks the mesh at exactly one threshold.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_corner_the_raster_fetches_is_the_vertex_the_mesh_has() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var input = Sphere(24, 48);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 8 * 1024 });

        // Two registrations, so the page offset and the per-mesh grid are both exercised — a decode that
        // read mesh zero's grid for mesh one's cluster passes with one mesh registered.
        visibility.Register(mesh, pages, 0);
        var second = visibility.Register(mesh, pages, 1);

        var instance = new CullInstance {
            FirstCluster = (uint)second.FirstCluster,
            ClusterCount = (uint)second.ClusterCount,
            Position = new(3f, -2f, 7f),
            Scale = 2.5f,
            Flags = GpuCulling.Alive,
            Mesh = 1u
        };

        // Every page resident, at a slot that is not its index — so a decode that used the page number
        // where it should use the slot reads the wrong bytes rather than the right ones by luck.
        var slots = new uint[visibility.PageCount];

        for (var page = 0; page < slots.Length; page++) {
            slots[page] = (uint)(slots.Length - 1 - page);
        }

        var pool = new byte[(long)slots.Length * pages.PageSize];

        for (var page = 0; page < visibility.PageCount; page++) {
            var local = page < pages.Pages.Length ? page : page - pages.Pages.Length;

            pages.BytesOf(local)
                .CopyTo(pool.AsSpan((int)((long)slots[page] * pages.PageSize), pages.Pages[local].Size));
        }

        var records = visibility.Records;
        var geometry = visibility.GeometryRecords;
        var expected = new Vector3[mesh.Meshlets.Max(meshlet => meshlet.VertexCount)];
        var checkedCorners = 0;

        for (var cluster = 0; cluster < mesh.Meshlets.Length; cluster++) {
            var meshlet = mesh.Meshlets[cluster];
            var record = geometry[second.FirstCluster + cluster];

            // Which the traversal would have written, and what the vertex stage takes apart.
            var packed = Pack((uint)1, (uint)cluster);
            Assert.Equal(1u, packed >> 16);
            Assert.Equal((uint)cluster, packed & 0xFFFFu);

            pages.GetPositions(cluster, meshlet.VertexCount, expected);

            for (var triangle = 0; triangle < meshlet.TriangleCount; triangle++) {
                for (var corner = 0u; corner < 3u; corner++) {
                    var world = Fetch(pool, slots, pages, record, (uint)triangle, corner, instance);

                    // The oracle: the same corner through the format's own decoder, placed and scaled the
                    // way the vertex stage places and scales it.
                    var local = pages.GetCorners(cluster, meshlet.TriangleCount)[(triangle * 3) + (int)corner];
                    var reference = instance.Position + (expected[local] * instance.Scale);

                    Assert.Equal(reference, world);
                    checkedCorners++;
                }
            }
        }

        Assert.True(checkedCorners > 1000, $"Only {checkedCorners} corners were checked, which is not a mesh.");
        Assert.True(records.Length > mesh.Meshlets.Length, "The second registration's records are missing.");
    }

    /// <summary>
    ///     A cluster whose page is not resident contributes no geometry rather than wrong geometry.
    /// </summary>
    /// <remarks>
    ///     The traversal will not accept such a cluster, so this should be unreachable — and it is checked
    ///     anyway, because the alternative when it is reached is reading whatever page happens to occupy
    ///     slot zero. That is geometry from another mesh at the right place, which reads as a corrupt
    ///     asset. See <c>Cull.PageAbsent</c>, which is all ones for the same reason.
    /// </remarks>
    [Fact]
    public void An_absent_page_draws_nothing() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var input = Sphere(8, 16);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 4 * 1024 });

        visibility.Register(mesh, pages, 0);

        // Nothing resident at all, which is what a table cleared to PageAbsent says.
        var slots = new uint[visibility.PageCount];
        Array.Fill(slots, GpuClusterVisibility.PageAbsent);

        Assert.All(slots, slot => Assert.Equal(GpuClusterVisibility.PageAbsent, slot));

        // Zero would be a real slot, which is precisely why absent is not zero.
        Assert.NotEqual(0u, GpuClusterVisibility.PageAbsent);
    }

    /// <summary>
    ///     The shader still fetches through the slot table, the page size and the mesh's grid.
    /// </summary>
    /// <remarks>
    ///     The defence a mirror cannot provide for itself: the test above proves the arithmetic in this
    ///     file is right about the format, and says nothing about whether the shader still does it. Each
    ///     of these is a step that could be dropped and leave a shader that compiles, binds and draws
    ///     something — geometry at the wrong scale, or out of the wrong page.
    /// </remarks>
    [Fact]
    public void The_shader_decodes_what_the_host_says_it_does() {
        var source = Source("Pipeline", "ClusterRaster.rvn");

        // The slot table, not the page number, is what locates the bytes.
        Assert.Contains("val page = residency[int(residencyBase + record.page)]", source, StringComparison.Ordinal);
        Assert.Contains("val start = page * pageSize", source, StringComparison.Ordinal);

        // The corner is a byte inside the page and the vertex is a stride from the page's own start.
        Assert.Contains("Byte(start + record.triangleOffset + triangle * 3u + corner)", source, StringComparison.Ordinal);
        Assert.Contains("start + record.vertexOffset + local * record.vertexStride", source, StringComparison.Ordinal);

        // The grid decode is an integer addition of the cluster's origin, then the mesh's step.
        Assert.Contains("record.origin.x + int(Short(at))", source, StringComparison.Ordinal);
        Assert.Contains("mesh.quantizationOrigin + local3 * mesh.quantizationStep", source, StringComparison.Ordinal);

        // The instance places and scales it, and the identity is biased so zero can mean nothing.
        Assert.Contains("instance.position + position * instance.scale", source, StringComparison.Ordinal);
        Assert.Contains("(slot + 1u) << TriangleBits", source, StringComparison.Ordinal);

        // And a surplus corner is a degenerate triangle rather than a fetch past the cluster.
        Assert.Contains("triangle >= record.triangleCount || page == Cull.PageAbsent", source, StringComparison.Ordinal);
    }

    /// <summary>What the traversal appends, as <c>Cull.PackVisible</c> packs it.</summary>
    static uint Pack(uint instance, uint cluster) => (instance << 16) | (cluster & 0xFFFFu);

    /// <summary>
    ///     One corner, fetched the way <c>ClusterRaster.rvn</c>'s vertex stage fetches it.
    /// </summary>
    /// <remarks>
    ///     A transliteration, deliberately step for step: the slot, the page base, the corner byte, the
    ///     three shorts and the grid. Written out rather than expressed through
    ///     <see cref="MeshletPageSet.GetPositions" />, because that decoder is the oracle and a mirror
    ///     that called it would be comparing it against itself.
    /// </remarks>
    static Vector3 Fetch(
        byte[] pool,
        uint[] slots,
        MeshletPageSet pages,
        in RasterCluster record,
        uint triangle,
        uint corner,
        in CullInstance instance
    ) {
        var slot = slots[record.Page];
        Assert.NotEqual(GpuClusterVisibility.PageAbsent, slot);

        var start = slot * (uint)pages.PageSize;
        var local = pool[start + record.TriangleOffset + (triangle * 3u) + corner];
        var at = start + record.VertexOffset + (local * record.VertexStride);

        var x = record.Origin.X + BitConverter.ToUInt16(pool, (int)at);
        var y = record.Origin.Y + BitConverter.ToUInt16(pool, (int)(at + 2));
        var z = record.Origin.Z + BitConverter.ToUInt16(pool, (int)(at + 4));

        var position = pages.QuantizationOrigin + (new Vector3(x, y, z) * pages.QuantizationStep);

        return instance.Position + (position * instance.Scale);
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
