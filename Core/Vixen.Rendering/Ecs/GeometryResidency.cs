// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Ecs;

/// <summary>Which geometry an entity draws, as something a cache can be keyed by.</summary>
/// <remarks>
///     ⚠ <b>Two key spaces in one struct, because an entity draws an asset <i>or</i> a primitive and the
///     cache should not care which.</b> A <see cref="MeshRenderable" /> names a mesh by
///     <see cref="AssetReference" />; a <see cref="PrimitiveShape" /> names one of eight shapes the
///     engine builds. Both resolve to a <see cref="MeshData" /> and both want one resident slice shared
///     by every entity drawing them, so both need to be one dictionary key. Encoding a primitive as a
///     synthetic asset id would work and would put eight made-up guids in every diagnostic that printed
///     one.
/// </remarks>
public readonly record struct GeometryKey {
    /// <summary>Which asset, or <see cref="AssetReference.Null" /> when this names a primitive.</summary>
    public AssetReference Asset { get; private init; }

    /// <summary>Which primitive, meaningful only when <see cref="IsPrimitive" />.</summary>
    public PrimitiveKind Primitive { get; private init; }

    /// <summary>Whether this names a built-in shape rather than an asset.</summary>
    public bool IsPrimitive { get; private init; }

    /// <summary>A key naming a mesh asset.</summary>
    /// <param name="asset">The mesh's reference.</param>
    /// <returns>The key.</returns>
    public static GeometryKey Of(AssetReference asset) => new() { Asset = asset };

    /// <summary>A key naming a built-in shape.</summary>
    /// <param name="primitive">The shape.</param>
    /// <returns>The key.</returns>
    public static GeometryKey Of(PrimitiveKind primitive) => new() { Primitive = primitive, IsPrimitive = true };

    /// <inheritdoc />
    public override string ToString() => IsPrimitive ? Primitive.ToString() : Asset.ToString();
}

/// <summary>What is resident in the shared geometry buffer, and how many entities want it.</summary>
/// <remarks>
///     <para>
///         <b>One slice per mesh, however many entities draw it.</b> A courtyard of forty identical
///         crates is one upload and one slice; without this it would be forty, and the vertex buffer
///         would run out on a scene a level designer would call small. That sharing is the whole reason
///         the cache exists — <see cref="GeometryBuffer" /> already handles suballocation, and it has no
///         notion of two callers wanting the same bytes.
///     </para>
///     <para>
///         <b>Reference counted, because an entity being destroyed is not a mesh being unloaded.</b>
///         Freeing on the first release would drop the crate the other thirty-nine are still drawing;
///         never freeing would make a level transition leak everything the old level held. The count is
///         entities, not loads.
///     </para>
///     <para>
///         ⚠ <b>The host flushes, and it must be outside a render pass.</b> <see cref="Acquire" />
///         stages an upload into the buffer's staging area; <see cref="Flush" /> is what records the
///         transfer, and Vulkan forbids a transfer inside a render pass — the same constraint
///         <c>DebugDrawRenderer.Upload</c> and <c>UiRenderer</c>'s atlas both have.
///     </para>
///     <para>
///         ⚠ <b>A mesh too large for the remaining space is refused rather than growing the
///         buffer.</b> Growing means recreating a buffer the GPU may still be reading from, and the
///         alternative to a refusal here is a stall of unbounded length in the middle of a frame. What
///         the caller does about it — evict, or draw nothing — is above this.
///     </para>
/// </remarks>
/// <param name="buffer">The buffer to suballocate from. Not owned; the host disposes it.</param>
public sealed class GeometryResidency(GeometryBuffer buffer) {
    readonly Dictionary<GeometryKey, Entry> resident = [];

    /// <summary>The buffer everything here lives in.</summary>
    public GeometryBuffer Buffer { get; } = buffer ?? throw new ArgumentNullException(nameof(buffer));

    /// <summary>How many distinct meshes are resident.</summary>
    public int Count => resident.Count;

    /// <summary>How many entities are holding the meshes that are resident, in total.</summary>
    /// <remarks>Diagnostic: it should equal the number of drawable entities, and a drift says a leak.</remarks>
    public int Claims {
        get {
            var total = 0;

            foreach (var entry in resident.Values) {
                total += entry.Claims;
            }

            return total;
        }
    }

    /// <summary>Takes a claim on a mesh, uploading it if this is the first.</summary>
    /// <param name="key">Which mesh.</param>
    /// <param name="build">Produces it, called only on a miss.</param>
    /// <param name="slice">Where it lives in the buffer.</param>
    /// <param name="bounds">Its bounding sphere in its own space, for the culling loop.</param>
    /// <returns>Whether it is resident. <see langword="false" /> means it did not fit.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="build" /> is not called on a hit</b>, which is what makes a primitive
    ///     cheap: <c>MeshPrimitives.Create</c> builds a sphere's every vertex, and doing that per entity
    ///     per appearance would cost more than the drawing.
    /// </remarks>
    public bool Acquire(
        GeometryKey key,
        Func<MeshData> build,
        out GeometrySlice slice,
        out BoundingSphere bounds
    ) {
        ArgumentNullException.ThrowIfNull(build);

        if (resident.TryGetValue(key, out var entry)) {
            resident[key] = entry with { Claims = entry.Claims + 1 };
            slice = entry.Slice;
            bounds = entry.Bounds;

            return true;
        }

        var mesh = build();
        var vertices = SurfaceGeometry.Packed(mesh);

        // ⚠ The staging question is asked BEFORE the allocation, and both halves of that matter.
        // `GeometryBuffer.Stage` throws outright when the region is part-full and the write will not
        // fit — deliberately, because growing it would abandon the bytes a pending copy refers to —
        // and this method's contract already has the answer that case wants: false means "did not
        // fit", the caller counts it in `Dropped`, the entity keeps no handle, and `Appear` offers it
        // again next frame against an empty region. Asking after `TryAllocate` would instead leak the
        // range every deferred frame. `MeshInstanceRenderer` guards the same call the same way and
        // says the same thing: the answer cannot be an exception.
        //
        // Found by a scene whose first frame registered a morphed head and a sphere: the head's 42 KB
        // fit the fresh 64 KB region, the sphere behind it did not, and the frame loop threw on a
        // mesh that was merely second.
        var staging = ((long) vertices.Length * Buffer.VertexStride)
            + ((long) mesh.Indices.Length * Buffer.IndexStride);

        if (vertices.Length == 0
            || !Buffer.CanStage(staging)
            || !Buffer.TryAllocate(vertices.Length, mesh.Indices.Length, out slice)) {
            slice = default;
            bounds = default;

            return false;
        }

        Buffer.Write(slice, MemoryMarshal.AsBytes(vertices.AsSpan()), MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));

        bounds = SurfaceGeometry.BoundsOf(mesh);
        resident[key] = new(slice, bounds, 1);

        return true;
    }

    /// <summary>Gives up a claim, freeing the mesh when it was the last.</summary>
    /// <param name="key">Which mesh.</param>
    /// <returns>Whether it was freed, as opposed to still being wanted or never held.</returns>
    public bool Release(GeometryKey key) {
        if (!resident.TryGetValue(key, out var entry)) {
            return false;
        }

        if (entry.Claims > 1) {
            resident[key] = entry with { Claims = entry.Claims - 1 };
            return false;
        }

        resident.Remove(key);
        Buffer.Free(entry.Slice);

        return true;
    }

    /// <summary>Where a resident mesh lives, without taking a claim on it.</summary>
    /// <param name="key">Which mesh.</param>
    /// <param name="slice">Its slice.</param>
    /// <returns>Whether it is resident.</returns>
    public bool TryGet(GeometryKey key, out GeometrySlice slice) {
        if (resident.TryGetValue(key, out var entry)) {
            slice = entry.Slice;
            return true;
        }

        slice = default;
        return false;
    }

    /// <summary>How many entities are holding one mesh.</summary>
    /// <param name="key">Which mesh.</param>
    /// <returns>The count, or zero when it is not resident.</returns>
    public int ClaimsOn(GeometryKey key) => resident.TryGetValue(key, out var entry) ? entry.Claims : 0;

    /// <summary>Records the staged uploads into a command list.</summary>
    /// <param name="list">The list, outside any render pass.</param>
    /// <returns>How many copy regions were recorded — one per staged write, not a byte count.</returns>
    public int Flush(ICommandList list) => Buffer.Flush(list);

    [SuppressMessage(
        "Performance",
        "CA1815",
        Justification = "Never compared; it is a dictionary value, and GeometrySlice's own equality is what would be asked for."
    )]
    readonly record struct Entry(GeometrySlice Slice, BoundingSphere Bounds, int Claims);
}
