// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Graphics;

/// <summary>Which level of the two-level hierarchy a structure is.</summary>
public enum AccelerationStructureKind : byte {
    /// <summary>Geometry — triangles, in one object's space.</summary>
    BottomLevel = 0,

    /// <summary>Instances — placed references to bottom-level structures, which is what a ray
    ///     query actually opens against.</summary>
    TopLevel = 1
}

/// <summary>What to create an acceleration structure as.</summary>
/// <param name="Kind">Which level it is.</param>
/// <param name="Size">
///     Its size in bytes — <see cref="AccelerationStructureSizes.Structure" />, from
///     <see cref="IGraphicsDevice.GetAccelerationStructureSizes" /> over the same input the build
///     will use. The device decides this number; a caller inventing it gets a refusal at creation
///     on a good day and corruption on a bad one.
/// </param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct AccelerationStructureDescription(
    AccelerationStructureKind Kind,
    long Size,
    string Name = ""
);

/// <summary>Triangle geometry for a bottom-level build.</summary>
/// <param name="VertexBuffer">Positions — three floats each, <paramref name="VertexStride" /> apart.</param>
/// <param name="VertexOffset">Where the first position starts, in bytes.</param>
/// <param name="VertexCount">How many vertices the indices may name.</param>
/// <param name="VertexStride">Bytes from one position to the next.</param>
/// <param name="IndexBuffer">Indices — unsigned 32-bit, three per triangle.</param>
/// <param name="IndexOffset">Where the first index starts, in bytes.</param>
/// <param name="IndexCount">How many indices — three times the triangle count.</param>
/// <remarks>
///     One vertex format and one index width, deliberately: <c>float3</c> positions and
///     <c>uint32</c> indices are what every mesh here already holds, and a format enum grows the
///     day a second format has a caller. ⚠ Both buffers must carry
///     <see cref="BufferUsage.AccelerationStructureInput" /> and
///     <see cref="BufferUsage.ShaderDeviceAddress" /> — a build addresses them by GPU address.
/// </remarks>
public readonly record struct AccelerationStructureTriangles(
    BufferHandle VertexBuffer,
    long VertexOffset,
    int VertexCount,
    int VertexStride,
    BufferHandle IndexBuffer,
    long IndexOffset,
    int IndexCount
);

/// <summary>Instance geometry for a top-level build.</summary>
/// <param name="Buffer">Packed <see cref="AccelerationStructureInstance" /> records.</param>
/// <param name="Offset">Where the first starts, in bytes.</param>
/// <param name="Count">How many.</param>
/// <remarks>⚠ The buffer must carry <see cref="BufferUsage.AccelerationStructureInput" /> and
///     <see cref="BufferUsage.ShaderDeviceAddress" />, like a bottom-level build's geometry.</remarks>
public readonly record struct AccelerationStructureInstances(
    BufferHandle Buffer,
    long Offset,
    int Count
);

/// <summary>Everything one build reads — the same record sizes the structure and then builds it.</summary>
/// <param name="Kind">Which level is being built.</param>
/// <param name="Triangles">The geometry, for a bottom-level build.</param>
/// <param name="Instances">The instances, for a top-level build.</param>
/// <remarks>
///     One record for both levels rather than an overload per level, because sizing and building
///     must describe the <em>same</em> input — Vulkan's own rule — and two types would let them
///     drift. The counts are what sizing reads; the buffers may be filled right up to the build.
/// </remarks>
public readonly record struct AccelerationStructureBuildInput(
    AccelerationStructureKind Kind,
    AccelerationStructureTriangles Triangles = default,
    AccelerationStructureInstances Instances = default
);

/// <summary>How much memory one build needs, asked of the device before anything is created.</summary>
/// <param name="Structure">The structure's own size — <see cref="AccelerationStructureDescription.Size" />.</param>
/// <param name="Scratch">The build's working memory — a <see cref="BufferUsage.Storage" /> +
///     <see cref="BufferUsage.ShaderDeviceAddress" /> buffer the caller provides to
///     <see cref="ICommandList.BuildAccelerationStructure" />.</param>
public readonly record struct AccelerationStructureSizes(long Structure, long Scratch);

/// <summary>One placed reference to a bottom-level structure, in the layout every API shares.</summary>
/// <remarks>
///     <para>
///         Sixty-four bytes, bit for bit <c>VkAccelerationStructureInstanceKHR</c> (and D3D12's
///         <c>D3D12_RAYTRACING_INSTANCE_DESC</c>, which is the same layout — the vendors agreed for
///         once): a 3×4 row-major world transform, a 24-bit custom index with an 8-bit visibility
///         mask, a 24-bit hit-group offset with 8 flag bits, and the bottom-level structure's GPU
///         address from <see cref="IGraphicsDevice.GetAccelerationStructureAddress" />.
///     </para>
///     <para>
///         The transform's rows are the <em>upper three rows of a column-vector matrix</em> — each
///         row is (basis.X, basis.Y, basis.Z, translation) for one world axis. Row-vector
///         <c>Matrix4x4</c> users transpose on the way in; <see cref="FromRows" /> spells the
///         layout so the transposition happens in exactly one place.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct AccelerationStructureInstance {
    /// <summary>Row zero of the transform: world X from object space, then X translation.</summary>
    public float R0X, R0Y, R0Z, R0W;

    /// <summary>Row one: world Y.</summary>
    public float R1X, R1Y, R1Z, R1W;

    /// <summary>Row two: world Z.</summary>
    public float R2X, R2Y, R2Z, R2W;

    /// <summary>Low 24 bits: the custom index a shader reads back. High 8: the visibility mask —
    ///     zero is invisible to every ray.</summary>
    public uint IndexAndMask;

    /// <summary>Low 24 bits: the hit-group offset (zero for ray queries). High 8: instance flags —
    ///     zero is the default, and the queries here disable culling per query anyway.</summary>
    public uint OffsetAndFlags;

    /// <summary>The bottom-level structure's GPU address.</summary>
    public ulong BottomLevel;

    /// <summary>An identity-placed, fully visible instance of one bottom-level structure.</summary>
    /// <param name="bottomLevel">Its GPU address.</param>
    /// <param name="index">The custom index, below 2^24.</param>
    public static AccelerationStructureInstance Identity(ulong bottomLevel, int index = 0) => new() {
        R0X = 1f,
        R1Y = 1f,
        R2Z = 1f,
        IndexAndMask = (uint)(index & 0xFFFFFF) | 0xFF000000u,
        BottomLevel = bottomLevel
    };

    /// <summary>The transform from three explicit rows, each (basis, translation) for one world axis.</summary>
    public static AccelerationStructureInstance FromRows(
        ulong bottomLevel,
        ReadOnlySpan<float> rows,
        int index = 0
    ) {
        if (rows.Length != 12) {
            throw new ArgumentException($"an instance transform is 12 floats, not {rows.Length}", nameof(rows));
        }

        var instance = Identity(bottomLevel, index);

        instance.R0X = rows[0];
        instance.R0Y = rows[1];
        instance.R0Z = rows[2];
        instance.R0W = rows[3];
        instance.R1X = rows[4];
        instance.R1Y = rows[5];
        instance.R1Z = rows[6];
        instance.R1W = rows[7];
        instance.R2X = rows[8];
        instance.R2Y = rows[9];
        instance.R2Z = rows[10];
        instance.R2W = rows[11];

        return instance;
    }
}
