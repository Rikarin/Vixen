// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Graphics;

/// <summary>What a device can do, asked once at creation and never again.</summary>
/// <remarks>
///     <para>
///         A flat record struct rather than a query API, because every one of these is fixed for the
///         life of the device and a call per question is a call per frame in code that asks in a
///         loop. Copied by value, compared by value, and cheap enough to keep in a hot struct.
///     </para>
///     <para>
///         <b>Everything here is capability-gated with a documented fallback, never a hard
///         requirement</b> — except the floor below, which is stated once and enforced at device
///         selection with a readable error rather than discovered as a crash.
///     </para>
///     <para>
///         <b>Minimum spec:</b> Vulkan 1.1, D3D12 feature level 11_0, GLES 3.0, or WebGL2. Below
///         that Vixen does not run.
///     </para>
/// </remarks>
public readonly record struct GraphicsDeviceFeatures {
    /// <summary>Compute shaders and dispatches.</summary>
    /// <remarks>
    ///     False on WebGL2, and that absence cascades: clustered light binning, GPU particle
    ///     simulation, GTAO, compute post-processing and GPU culling all need a fullscreen-fragment
    ///     or CPU path. <c>docs/plan/06</c> requires every post effect to declare a non-compute
    ///     variant for exactly this reason.
    /// </remarks>
    public bool HasCompute { get; init; }

    /// <summary>Geometry shaders.</summary>
    public bool HasGeometryShaders { get; init; }

    /// <summary>Tessellation control and evaluation shaders.</summary>
    public bool HasTessellation { get; init; }

    /// <summary>Task and mesh shaders.</summary>
    public bool HasMeshShaders { get; init; }

    /// <summary>A shader may index an unbounded descriptor array.</summary>
    /// <remarks>
    ///     What GPU-driven culling and material batching are built on. Limited on MoltenVK to Metal
    ///     argument-buffer tier 1 (ADR-011), so the non-bindless path is not merely a legacy
    ///     concession — it is what runs on Apple hardware and on WebGL2.
    /// </remarks>
    public bool HasBindless { get; init; }

    /// <summary>One indirect call may issue many draws.</summary>
    public bool HasMultiDrawIndirect { get; init; }

    /// <summary>Timeline semaphores, rather than binary ones.</summary>
    public bool HasTimelineSemaphores { get; init; }

    /// <summary>A compute queue that runs in parallel with graphics.</summary>
    public bool HasAsyncCompute { get; init; }

    /// <summary>A transfer queue that runs in parallel with both.</summary>
    public bool HasAsyncTransfer { get; init; }

    /// <summary>Sparse (partially resident) resources.</summary>
    public bool HasSparseResources { get; init; }

    /// <summary>Double-precision floats in shaders.</summary>
    public bool HasFloat64 { get; init; }

    /// <summary>Subgroup (wave) operations.</summary>
    public bool HasSubgroupOperations { get; init; }

    /// <summary>Rendering without render-pass and framebuffer objects.</summary>
    /// <remarks>
    ///     Core in Vulkan 1.3 and absent on a meaningful slice of Android devices still on 1.1,
    ///     which is why <c>docs/plan/05</c> makes the real-render-pass fallback mandatory rather
    ///     than optional.
    /// </remarks>
    public bool HasDynamicRendering { get; init; }

    /// <summary>Depth values may be clamped rather than clipped.</summary>
    /// <remarks>What a shadow pass wants, so a caster in front of the near plane still casts.</remarks>
    public bool HasDepthClamp { get; init; }

    /// <summary>Triangles may be drawn as wireframe.</summary>
    public bool HasWireframe { get; init; }

    /// <summary>Anisotropic sampling.</summary>
    public bool HasAnisotropicFiltering { get; init; }

    /// <summary>Each colour attachment may have its own blend state.</summary>
    public bool HasIndependentBlend { get; init; }

    /// <summary>Pipeline-statistics queries.</summary>
    /// <remarks>Unsupported by MoltenVK, so a profiler that requires them shows nothing on macOS
    /// and iOS (ADR-011).</remarks>
    public bool HasPipelineStatistics { get; init; }

    /// <summary>The CPU and GPU share one memory pool.</summary>
    /// <remarks>
    ///     True on integrated and mobile GPUs. Where it holds, staging copies are pure overhead and
    ///     an upload path that always stages is measurably slower than one that asks.
    /// </remarks>
    public bool HasUnifiedMemory { get; init; }

    /// <summary>The largest 2D texture edge, in texels.</summary>
    public int MaxTextureSize { get; init; }

    /// <summary>The largest array texture layer count.</summary>
    public int MaxTextureArrayLayers { get; init; }

    /// <summary>The largest colour attachment count in one pass.</summary>
    public int MaxColourAttachments { get; init; }

    /// <summary>The largest bound vertex buffer count.</summary>
    public int MaxVertexBuffers { get; init; }

    /// <summary>The largest bound descriptor set count.</summary>
    /// <remarks>Four is the engine's convention — per-frame, per-view, per-material, per-draw — and
    /// is the floor every target meets.</remarks>
    public int MaxDescriptorSets { get; init; }

    /// <summary>The largest push-constant block, in bytes.</summary>
    public int MaxPushConstantSize { get; init; }

    /// <summary>The largest anisotropy a sampler may ask for.</summary>
    public float MaxAnisotropy { get; init; }

    /// <summary>The largest compute workgroup, per dimension.</summary>
    public (int X, int Y, int Z) MaxComputeWorkgroupSize { get; init; }

    /// <summary>Sample counts the device supports for colour attachments, as a bit mask of counts.</summary>
    /// <remarks>Bit <c>n</c> set means <c>2^n</c> samples, so <c>0b10101</c> is 1, 4 and 16.</remarks>
    public int SupportedSampleCounts { get; init; }

    /// <summary>Whether a sample count is supported.</summary>
    /// <param name="samples">A power of two.</param>
    public bool SupportsSampleCount(int samples) =>
        samples > 0
        && (samples & (samples - 1)) == 0
        && (SupportedSampleCounts & (1 << BitOperations.Log2((uint)samples))) != 0;

    /// <summary>
    ///     What a backend with nothing but the floor reports: the minimum spec and no more.
    /// </summary>
    /// <remarks>
    ///     A starting point for a backend to modify rather than a set of defaults to inherit
    ///     silently — a capability left unset here is reported absent, which makes the failure mode
    ///     of a forgotten line "the fallback path runs" rather than "the device claims something it
    ///     cannot do".
    /// </remarks>
    public static GraphicsDeviceFeatures Minimum => new() {
        MaxTextureSize = 4096,
        MaxTextureArrayLayers = 256,
        MaxColourAttachments = 4,
        MaxVertexBuffers = 4,
        MaxDescriptorSets = 4,
        MaxPushConstantSize = 128,
        MaxAnisotropy = 1f,
        MaxComputeWorkgroupSize = (128, 128, 64),
        SupportedSampleCounts = 0b1
    };
}
