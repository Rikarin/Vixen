// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Rendering.VirtualGeometry;

namespace Vixen.Rendering;

/// <summary>One mesh's virtualized geometry, as a build wrote it and a frame reads it.</summary>
/// <param name="Hierarchy">The cluster DAG: bounds, cones, errors and the group links.</param>
/// <param name="Pages">Where each cluster's geometry is, with no geometry in it.</param>
/// <remarks>
///     <b>Two artefacts and no bytes, which is the whole point of the split.</b> The hierarchy is what a
///     traversal walks and has to stay resident; the page records say where a cluster's geometry is in a
///     blob that is read one page at a time. Deserialising the geometry here would read every page of
///     every mesh at load, which is the one thing paging exists to avoid — see
///     <see cref="MeshletPageSet.WithoutData" />.
/// </remarks>
/// <remarks>
///     ⚠ <b>Serialisable, and written as one chunk while the blob is written as another.</b> An address
///     names exactly one chunk, so the three artefacts a build used to write under one sub-asset id could
///     not be addressed at all — a content build refuses "two chunks for one sub-asset", which is how the
///     collision was found. The records travel together because they are read together; the blob travels
///     alone because it is seeked into rather than deserialised.
/// </remarks>
/// <remarks>
///     ⚠ A class and not a struct, which the content manager decides rather than this: an
///     <c>AssetHandle&lt;T&gt;</c> holds a reference type, because a claim is shared between everything
///     that asked for the same address and a copy per asker would not be shared at all.
/// </remarks>
[DataContract("VirtualGeometryAsset")]
public sealed record VirtualGeometryAsset(MeshletMesh Hierarchy, MeshletPageSet Pages) {
    /// <summary>For the serializer, which builds a value and then fills it.</summary>
    public VirtualGeometryAsset()
        : this(new(), new()) { }

    /// <summary>The cluster DAG: bounds, cones, errors and the group links.</summary>
    public MeshletMesh Hierarchy { get; init; } = Hierarchy;

    /// <summary>Where each cluster's geometry is, with no geometry in it.</summary>
    public MeshletPageSet Pages { get; init; } = Pages;
}

/// <summary>
///     Reads what <c>ModelImporter</c> wrote, and registers it so a frame can draw it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The caller <see cref="Features.VirtualGeometryRenderFeature.Register" /> was written
///         for.</b> A build produces two chunks per clustered mesh — the records under
///         <see cref="ClusterArtifact" /> and the page blob under <see cref="ClusterPageArtifact" /> —
///         and until this existed nothing outside the importer's own tests read either of them back. The
///         system was complete from import to shaded pixel with no way to get from one to the other.
///     </para>
///     <para>
///         <b>Bytes in, not an address.</b> What produces the bytes is the caller's — <c>AssetManager</c>
///         in an application, a file in a sample, an array in a test — and putting a content pipeline in
///         this signature would mean the renderer knew what a bundle was. What it does know is the two
///         formats, which it owns.
///     </para>
///     <para>
///         <b>The blob is a stream and stays one.</b> <see cref="StreamMeshletPageSource" /> seeks into
///         it per page, which is what makes residency demand-driven; handing it an array would load the
///         mesh whole and leave the streaming machinery reading out of memory it had already paid for.
///     </para>
/// </remarks>
public static class VirtualGeometryContent {
    /// <summary>The artefact name a build writes the hierarchy and page records under, together.</summary>
    public const string ClusterArtifact = "Clusters";

    /// <summary>The artefact name the page blob is written under.</summary>
    public const string ClusterPageArtifact = "ClusterPages";

    /// <summary>Registers an already-read mesh, and makes its geometry reachable.</summary>
    /// <param name="feature">The feature the objects will draw through.</param>
    /// <param name="source">The page source the pool reads from, which the blob is added to.</param>
    /// <param name="id">The id this mesh's pages carry in a <see cref="PageKey" />.</param>
    /// <param name="asset">The records, as one chunk holds them.</param>
    /// <param name="data">The blob. Owned by the source from here.</param>
    /// <returns>The index to put in <see cref="Features.VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static int Load(
        Features.VirtualGeometryRenderFeature feature,
        StreamMeshletPageSource source,
        int id,
        VirtualGeometryAsset asset,
        Stream data
    ) {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);

        source.Add(id, asset.Pages, data);

        return feature.Register(asset.Hierarchy, asset.Pages, id);
    }

    /// <summary>Reads the two record artefacts, as separate blobs.</summary>
    /// <param name="hierarchy">The <c>Meshlets</c> artefact's bytes.</param>
    /// <param name="pages">The <c>MeshletPages</c> artefact's bytes.</param>
    /// <returns>The mesh, ready to register.</returns>
    /// <exception cref="ArgumentException">Either artefact is empty, or the two do not describe one mesh.</exception>
    public static VirtualGeometryAsset Read(ReadOnlySpan<byte> hierarchy, ReadOnlySpan<byte> pages) {
        if (hierarchy.IsEmpty) {
            throw new ArgumentException("The cluster hierarchy is empty.", nameof(hierarchy));
        }

        if (pages.IsEmpty) {
            throw new ArgumentException("The page records are empty.", nameof(pages));
        }

        var mesh = Serializer.Read<MeshletMesh>(hierarchy.ToArray());
        var placement = Serializer.Read<MeshletPageSet>(pages.ToArray());

        // The two are written together by one import and are meaningless apart: a placement per cluster,
        // indexed as the hierarchy's clusters are. A pair from two different builds of the same mesh
        // decodes geometry out of the wrong offsets, which draws a mesh rather than failing.
        if (placement.Clusters.Length != mesh.Meshlets.Length) {
            throw new ArgumentException(
                $"The page records place {placement.Clusters.Length} clusters and the hierarchy has "
                + $"{mesh.Meshlets.Length}. The two artefacts are from different builds.",
                nameof(pages)
            );
        }

        return new(mesh, placement);
    }

    /// <summary>
    ///     Reads a mesh and makes it drawable: registered with the feature, streaming from the blob.
    /// </summary>
    /// <param name="feature">The feature the objects will draw through.</param>
    /// <param name="source">The page source the pool reads from, which the blob is added to.</param>
    /// <param name="id">The id this mesh's pages carry in a <see cref="PageKey" />.</param>
    /// <param name="hierarchy">The <c>Meshlets</c> artefact's bytes.</param>
    /// <param name="pages">The <c>MeshletPages</c> artefact's bytes.</param>
    /// <param name="data">The <c>MeshletPageData</c> blob. Owned by the source from here.</param>
    /// <returns>The index to put in <see cref="Features.VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The source id is the caller's and is used twice here</b>, which is the point: the blob
    ///         goes into the page source under it and the registration records it, so the two cannot
    ///         disagree. Doing them separately is how a mesh comes to be registered against a blob
    ///         nobody added, which draws its root page and nothing below it — a coarse mesh, not an
    ///         error.
    ///     </para>
    ///     <para>
    ///         Nothing is uploaded. Registration pins the root page and everything else arrives because
    ///         a traversal asked for it, which is the whole of phase 2.
    ///     </para>
    /// </remarks>
    public static int Load(
        Features.VirtualGeometryRenderFeature feature,
        StreamMeshletPageSource source,
        int id,
        ReadOnlySpan<byte> hierarchy,
        ReadOnlySpan<byte> pages,
        Stream data
    ) {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);

        var asset = Read(hierarchy, pages);

        source.Add(id, asset.Pages, data);

        return feature.Register(asset.Hierarchy, asset.Pages, id);
    }
}
