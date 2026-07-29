// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>What a page build is being asked for.</summary>
public readonly record struct MeshletPageSettings {
    /// <summary>The defaults: 128 KB pages, positions only.</summary>
    public MeshletPageSettings() { }

    /// <summary>
    ///     How many bytes one page occupies.
    /// </summary>
    /// <remarks>
    ///     A hundred and twenty-eight kilobytes, which is Nanite's and is a compromise between two
    ///     costs that pull in opposite directions. A page is the unit of I/O, so smaller pages mean
    ///     more requests for the same bytes and a request costs far more than a kilobyte; a page is
    ///     also the unit of <em>residency</em>, so larger pages mean more geometry resident that
    ///     nothing asked for. At this size a page holds a few dozen clusters, which is about one
    ///     spatial neighbourhood at one level — the granularity at which a camera actually changes
    ///     its mind.
    /// </remarks>
    public int PageSize { get; init; } = 128 * 1024;

    /// <summary>How many bytes of attributes accompany each vertex's position.</summary>
    /// <remarks>
    ///     Whatever the caller's vertex layout says, copied through verbatim. Only the position is
    ///     this format's business: it is the one attribute whose precision decides whether a locked
    ///     boundary survives, and the one whose full precision a cluster's small extent makes
    ///     wasteful. Normals, tangents and texture coordinates are the material's to pack, and
    ///     packing them here would put a second opinion about vertex layout below the one
    ///     <c>ModelCompiler</c> already has.
    /// </remarks>
    public int AttributeStride { get; init; }

    /// <summary>Refuses settings that cannot produce pages.</summary>
    /// <exception cref="ArgumentOutOfRangeException">One of them is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfLessThan(PageSize, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(AttributeStride);
    }
}

/// <summary>
///     Packs a cluster DAG's geometry into fixed-size pages, quantized against one grid.
/// </summary>
/// <remarks>
///     <para>
///         Phase 2's offline half. Two things happen here and they are independent: the geometry is
///         <em>quantized</em>, which is about bytes per vertex, and it is <em>paged</em>, which is
///         about what can be loaded without loading everything.
///     </para>
///     <para>
///         The quantization is the part that can go wrong silently, and
///         <see cref="MeshletPageSet.QuantizationStep" /> says how. The paging is the part that
///         decides whether streaming thrashes, and the policy is one line: clusters are packed
///         coarsest-first, so page zero holds the roots and a page holds one level of one
///         neighbourhood. A camera that moves closer wants the next page rather than a scattering of
///         clusters out of every page there is.
///     </para>
/// </remarks>
public static class MeshletPageBuilder {
    /// <summary>Six bytes of quantized position, three unsigned shorts.</summary>
    /// <remarks>
    ///     Sixteen bits an axis across the mesh's longest extent. Fewer would put the quantization
    ///     error above the finest level's own error, which is the point at which paging the mesh
    ///     changes what it looks like; more would not fit a cluster's local coordinates in a short,
    ///     since the coarsest cluster spans the whole grid.
    /// </remarks>
    public const int PositionSize = 6;

    /// <summary>How many grid steps span the mesh's longest extent.</summary>
    const int GridSteps = ushort.MaxValue;

    /// <summary>Packs a DAG into pages.</summary>
    /// <param name="mesh">The DAG, from <see cref="MeshletBuilder" />.</param>
    /// <param name="positions">The source mesh's positions, which the DAG's vertex list indexes.</param>
    /// <param name="attributes">
    ///     The source mesh's other per-vertex bytes, at <see cref="MeshletPageSettings.AttributeStride" />
    ///     each, or empty.
    /// </param>
    /// <param name="settings">How big a page is, and how much of a vertex is attributes.</param>
    /// <returns>The packed pages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <exception cref="ArgumentException">The attributes are the wrong length, or a cluster does not fit a page.</exception>
    public static MeshletPageSet Build(
        MeshletMesh mesh,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<byte> attributes,
        MeshletPageSettings settings = default
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        settings.Validate();

        if (settings.AttributeStride > 0 && attributes.Length != positions.Length * settings.AttributeStride) {
            throw new ArgumentException(
                $"The attributes are {attributes.Length} bytes for {positions.Length} vertices at "
                + $"{settings.AttributeStride} each.",
                nameof(attributes)
            );
        }

        var stride = PositionSize + settings.AttributeStride;
        var (origin, step) = Grid(positions);

        // Coarsest first, so page zero is the roots. Stable within a level, because the builder
        // emits a level in the partitioner's order and that order is spatially coherent — which is
        // what makes a page a neighbourhood rather than a scattering.
        var order = Order(mesh);

        var pages = new List<MeshletPage>();
        var placements = new MeshletPageCluster[mesh.Meshlets.Length];
        var data = new List<byte>();

        var pageStart = 0;
        var pageFirst = 0;
        var pageClusters = 0;
        var pageCoarsest = 0;
        var used = 0;

        foreach (var index in order) {
            var meshlet = mesh.Meshlets[index];
            var size = Align(meshlet.VertexCount * stride) + Align(meshlet.TriangleCount * 3);

            if (size > settings.PageSize) {
                throw new ArgumentException(
                    $"Cluster {index} needs {size} bytes and a page is {settings.PageSize}.",
                    nameof(settings)
                );
            }

            if (pageClusters > 0 && used + size > settings.PageSize) {
                pages.Add(Close(pageStart, used, pageFirst, pageClusters, pageCoarsest, settings.PageSize, data));
                pageStart = data.Count;
                pageClusters = 0;
                used = 0;
            }

            if (pageClusters == 0) {
                pageFirst = index;
                pageCoarsest = meshlet.Level;
            }

            placements[index] = Write(
                mesh,
                meshlet,
                index,
                positions,
                attributes,
                settings,
                origin,
                step,
                pages.Count,
                used,
                data
            );

            used += size;
            pageClusters++;
            pageCoarsest = Math.Min(pageCoarsest, meshlet.Level);
        }

        if (pageClusters > 0) {
            pages.Add(Close(pageStart, used, pageFirst, pageClusters, pageCoarsest, settings.PageSize, data));
        }

        return new() {
            Pages = [.. pages],
            Clusters = placements,
            Data = [.. data],
            PageSize = settings.PageSize,
            VertexStride = stride,
            QuantizationOrigin = origin,
            QuantizationStep = step
        };
    }

    /// <summary>
    ///     The grid every position in the mesh is snapped to: its corner, and how far apart its
    ///     points are.
    /// </summary>
    /// <remarks>
    ///     Uniform, and derived from the longest extent rather than per axis — so a flat mesh does
    ///     not get a grid one step deep, and a rotated one picks up the same error as an axis-aligned
    ///     one. A degenerate mesh, everything at one point, gets a step of one: any positive number
    ///     works and zero would divide by nothing.
    /// </remarks>
    static (Vector3 Origin, float Step) Grid(ReadOnlySpan<Vector3> positions) {
        if (positions.IsEmpty) {
            return (Vector3.Zero, 1f);
        }

        var min = positions[0];
        var max = positions[0];

        foreach (var position in positions) {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var extent = max - min;
        var longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));

        return (min, longest > 0f ? longest / GridSteps : 1f);
    }

    /// <summary>The clusters in packing order: coarsest level first, stable within a level.</summary>
    static int[] Order(MeshletMesh mesh) {
        var order = new int[mesh.Meshlets.Length];

        for (var i = 0; i < order.Length; i++) {
            order[i] = i;
        }

        // Descending level, because level zero is the original mesh and the roots are the highest
        // level there is. A stable sort, so the partitioner's spatial order survives inside a level.
        return [.. order.OrderByDescending(index => mesh.Meshlets[index].Level)];
    }

    /// <summary>Writes one cluster's vertices and corners, and says where they went.</summary>
    static MeshletPageCluster Write(
        MeshletMesh mesh,
        in Meshlet meshlet,
        int index,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<byte> attributes,
        in MeshletPageSettings settings,
        Vector3 origin,
        float step,
        int page,
        int offset,
        List<byte> data
    ) {
        var stride = PositionSize + settings.AttributeStride;

        // The cluster's own origin on the shared grid: the floor of its lowest corner, so every
        // local coordinate is non-negative and the arithmetic that decodes it is an addition.
        var lowest = new Int3(int.MaxValue, int.MaxValue, int.MaxValue);

        for (var i = 0; i < meshlet.VertexCount; i++) {
            var grid = Snap(positions[mesh.Vertices[meshlet.VertexOffset + i]], origin, step);
            lowest = new(Math.Min(lowest.X, grid.X), Math.Min(lowest.Y, grid.Y), Math.Min(lowest.Z, grid.Z));
        }

        if (meshlet.VertexCount == 0) {
            lowest = default;
        }

        for (var i = 0; i < meshlet.VertexCount; i++) {
            var source = mesh.Vertices[meshlet.VertexOffset + i];
            var grid = Snap(positions[source], origin, step);

            // Checked rather than masked. A local coordinate out of range means the grid was built
            // over a different set of positions than the DAG indexes, which is a caller error that
            // would otherwise arrive as geometry folded in on itself.
            data.AddRange(BitConverter.GetBytes(Local(grid.X - lowest.X, index)));
            data.AddRange(BitConverter.GetBytes(Local(grid.Y - lowest.Y, index)));
            data.AddRange(BitConverter.GetBytes(Local(grid.Z - lowest.Z, index)));

            if (settings.AttributeStride > 0) {
                data.AddRange(attributes.Slice(source * settings.AttributeStride, settings.AttributeStride));
            }
        }

        Pad(data, meshlet.VertexCount * stride);

        var triangles = offset + Align(meshlet.VertexCount * stride);
        data.AddRange(mesh.Triangles.AsSpan(meshlet.TriangleOffset * 3, meshlet.TriangleCount * 3));
        Pad(data, meshlet.TriangleCount * 3);

        return new() { Page = page, VertexOffset = offset, TriangleOffset = triangles, Origin = lowest };
    }

    /// <summary>Which grid point a position snaps to.</summary>
    /// <remarks>
    ///     Round-to-nearest, and the same rounding for every cluster that references the vertex —
    ///     which is the whole property. <c>MathF.Round</c> rather than a cast, because a cast
    ///     truncates and would put the error at a whole step instead of half of one.
    /// </remarks>
    static Int3 Snap(Vector3 position, Vector3 origin, float step) {
        var scaled = (position - origin) / step;

        return new((int)MathF.Round(scaled.X), (int)MathF.Round(scaled.Y), (int)MathF.Round(scaled.Z));
    }

    static ushort Local(int value, int cluster) {
        if (value is < 0 or > ushort.MaxValue) {
            throw new ArgumentException(
                $"Cluster {cluster} has a vertex {value} grid steps from its origin, which does not fit "
                + "sixteen bits — the positions do not belong to the mesh the grid was built over.",
                nameof(cluster)
            );
        }

        return (ushort)value;
    }

    /// <summary>Closes a page, padding its bytes out to the slot it will occupy.</summary>
    /// <remarks>
    ///     The padding is in the artefact and not in what is streamed: <see cref="MeshletPage.Size" />
    ///     is the used length, so a short last page reads short. What the padding buys is that a page's
    ///     bytes start at a page-sized boundary in <see cref="MeshletPageSet.Data" />, which makes the
    ///     file the same shape as the pool it is loaded into.
    /// </remarks>
    static MeshletPage Close(
        int start,
        int used,
        int first,
        int clusters,
        int coarsest,
        int pageSize,
        List<byte> data
    ) {
        while (data.Count < start + pageSize) {
            data.Add(0);
        }

        return new() { Offset = start, Size = used, FirstCluster = first, ClusterCount = clusters, CoarsestLevel = coarsest };
    }

    /// <summary>Four-byte alignment, so a cluster's corners start where a word does.</summary>
    static int Align(int bytes) => (bytes + 3) / 4 * 4;

    static void Pad(List<byte> data, int written) {
        for (var i = written; i < Align(written); i++) {
            data.Add(0);
        }
    }
}
