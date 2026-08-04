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
    ///     <para>
    ///         What GPU-driven culling and material batching are built on. Limited on MoltenVK to
    ///         Metal argument-buffer tier 1 (ADR-011), so the non-bindless path is not merely a legacy
    ///         concession — it is what runs on Apple hardware and on WebGL2.
    ///     </para>
    ///     <para>
    ///         <strong>This is four questions, not one</strong>, and a backend must answer all four
    ///         before it says yes: the array has to be runtime-sized, a slot nobody wrote has to be
    ///         allowed to stay unwritten, an index that varies across a subgroup has to be legal, and
    ///         the set has to be writable after it is bound. Vulkan offers the first three as separate
    ///         opt-in bits under one extension, and a device that has the extension and not the bits
    ///         is a device where <see cref="BindlessTable" /> would fail at
    ///         <c>vkAllocateDescriptorSets</c> rather than at a capability check.
    ///     </para>
    /// </remarks>
    public bool HasBindless { get; init; }

    /// <summary>One indirect call may issue many draws.</summary>
    public bool HasMultiDrawIndirect { get; init; }

    /// <summary>
    ///     And the number of them may come from a buffer rather than from the host.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The difference between a compacted draw list and a padded one.
    ///         <see cref="ICommandList.DrawIndexedIndirect" /> takes its count as a host integer, so
    ///         a run the GPU compacted has to be issued at its <em>maximum</em> length with the tail
    ///         zeroed — every culled object still costing a command the front end reads and discards.
    ///         With this, one command covers exactly the survivors and the host never learns how many
    ///         there were.
    ///     </para>
    ///     <para>
    ///         ⚠ Strictly stronger than <see cref="HasMultiDrawIndirect" /> and not implied by it.
    ///         Vulkan spells it as a separate extension promoted to core in 1.2; GL wants 4.6; and
    ///         WebGPU and Metal have no equivalent, so the padded form is what runs there and is not
    ///         going away.
    ///     </para>
    /// </remarks>
    public bool HasDrawIndirectCount { get; init; }

    /// <summary>Timeline semaphores, rather than binary ones.</summary>
    public bool HasTimelineSemaphores { get; init; }

    /// <summary>A compute queue that runs in parallel with graphics.</summary>
    public bool HasAsyncCompute { get; init; }

    /// <summary>A transfer queue that runs in parallel with both.</summary>
    public bool HasAsyncTransfer { get; init; }

    /// <summary>Sparse (partially resident) resources.</summary>
    public bool HasSparseResources { get; init; }

    /// <summary>Acceleration structures and ray queries — doc 19 § L6's genuinely new RHI row.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Three promises, not one</strong>, and a backend must hold all of them before it
    ///         says yes: acceleration structures can be built and refitted
    ///         (<see cref="IGraphicsDevice.CreateAccelerationStructure" /> and
    ///         <see cref="ICommandList.BuildAccelerationStructure" />), a shader can open a ray query
    ///         against one, and buffer device addresses work — a build addresses its geometry by GPU
    ///         address, so ray tracing without addressing is not a configuration that exists. On
    ///         Vulkan that is <c>VK_KHR_acceleration_structure</c> + <c>VK_KHR_ray_query</c> with
    ///         their feature bits actually enabled, not merely their extensions listed.
    ///     </para>
    ///     <para>
    ///         False on MoltenVK, which exposes neither extension (ADR-011's family of absences), so
    ///         macOS and iOS run the distance-field tracer — which is not a degraded mode but the
    ///         default one: doc 19 § L6 is an <em>alternative</em> tracer behind L1's interface, and
    ///         nothing above it changes either way.
    ///     </para>
    /// </remarks>
    public bool HasRayTracing { get; init; }

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

    /// <summary>Timestamp queries, which is what a GPU timeline is made of.</summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="HasPipelineStatistics" /> because the two are separate
    ///         promises and MoltenVK is exactly the case that proves it: it has timestamps and it does
    ///         not have statistics. One flag for both would take the GPU profiler off macOS to no
    ///         purpose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a claim about the queue as well as the device.</b> Vulkan reports validity
    ///         bits per queue family, and a transfer queue that cannot time is a real configuration —
    ///         a backend reporting this true is promising the <i>graphics</i> queue can, which is the
    ///         one a frame's passes are recorded on.
    ///     </para>
    /// </remarks>
    public bool HasTimestampQueries { get; init; }

    /// <summary>Nanoseconds per <see cref="QueryKind.Timestamp" /> tick. Zero without
    /// <see cref="HasTimestampQueries" />.</summary>
    /// <remarks>A float rather than an integer because on several vendors it is not one — see
    /// <see cref="GpuTimestamps" />, which is the only thing that should be doing the arithmetic.</remarks>
    public float TimestampPeriod { get; init; }

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

    /// <summary>How many descriptors one unbounded binding may hold. Zero without
    /// <see cref="HasBindless" />.</summary>
    /// <remarks>
    ///     <para>
    ///         "Unbounded" is the shader's word and not the driver's. A shader indexes the array with
    ///         a number it was handed and never asks how long it is; the <em>set</em> is still a fixed
    ///         allocation, and this is how long it is. So a table is sized once, at creation, out of
    ///         this — which is also the number that decides how much descriptor memory a bindless
    ///         renderer costs before it has bound anything.
    ///     </para>
    ///     <para>
    ///         Reported rather than assumed because the spread is enormous and the failure is not
    ///         graceful: a desktop driver offers a million or more, and a mobile one under the same
    ///         extension can offer a few thousand. A table sized to the first on the second does not
    ///         fall back — <c>vkCreateDescriptorSetLayout</c> refuses it.
    ///     </para>
    /// </remarks>
    public int MaxBindlessDescriptors { get; init; }

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
