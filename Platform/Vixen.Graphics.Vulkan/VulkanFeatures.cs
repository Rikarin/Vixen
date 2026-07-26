// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>What a physical device reported, turned into what the RHI asks about.</summary>
/// <remarks>
///     <para>
///         Pure, like <see cref="VulkanFormats" /> and <see cref="AdapterSelection" />, and for the
///         sharpest version of the same reason: a capability reported <em>wrongly</em> does not fail.
///         Claiming dynamic rendering on a driver that lacks it crashes at the first frame; claiming
///         it is absent when it is there silently runs the fallback path forever, and nobody
///         notices because the picture is identical. Both directions are asserted below by tests
///         that construct the Vulkan structs by hand.
///     </para>
///     <para>
///         <b>Absent unless proven present.</b> Every mapping starts from
///         <see cref="GraphicsDeviceFeatures.Minimum" />, so a capability nobody remembered to
///         translate reports false and the engine takes its fallback — which is the failure mode
///         worth having.
///     </para>
/// </remarks>
static class VulkanFeatures {
    /// <summary>Vulkan 1.2, where timeline semaphores and descriptor indexing became core.</summary>
    public const uint Version12 = (1u << 22) | (2u << 12);

    /// <summary>Vulkan 1.3, where dynamic rendering and synchronisation2 became core.</summary>
    public const uint Version13 = (1u << 22) | (3u << 12);

    /// <summary>What <c>VK_KHR_dynamic_rendering</c> depends on below Vulkan 1.2.</summary>
    /// <remarks>
    ///     Both became core in 1.2, so only a 1.1 device needs them named. Enabling dynamic rendering
    ///     without them is invalid usage that MoltenVK accepts silently and the validation layers
    ///     reject — which is how it was found.
    /// </remarks>
    public const string CreateRenderPass2 = "VK_KHR_create_renderpass2";

    /// <summary>The other half of that dependency, likewise core in 1.2.</summary>
    public const string DepthStencilResolve = "VK_KHR_depth_stencil_resolve";

    const string DynamicRendering = "VK_KHR_dynamic_rendering";
    const string TimelineSemaphore = "VK_KHR_timeline_semaphore";
    const string DescriptorIndexing = "VK_EXT_descriptor_indexing";
    const string MeshShaderExt = "VK_EXT_mesh_shader";
    const string MeshShaderNv = "VK_NV_mesh_shader";

    /// <summary>Translates a device's report into the RHI's vocabulary.</summary>
    /// <param name="features">What <c>vkGetPhysicalDeviceFeatures</c> said.</param>
    /// <param name="limits">What <c>vkGetPhysicalDeviceProperties</c> said.</param>
    /// <param name="extensions">The device extensions it offers.</param>
    /// <param name="apiVersion">The Vulkan version it supports, packed as Vulkan packs it.</param>
    /// <param name="queues">Which family does what, which is where the async flags come from.</param>
    /// <param name="unifiedMemory">Whether the CPU and GPU share one pool.</param>
    public static GraphicsDeviceFeatures Translate(
        in PhysicalDeviceFeatures features,
        in PhysicalDeviceLimits limits,
        IReadOnlySet<string> extensions,
        uint apiVersion,
        in QueueFamilyPlan queues,
        bool unifiedMemory
    ) =>
        GraphicsDeviceFeatures.Minimum with {
            // Vulkan has no device without compute — unlike WebGL2, which is what the flag exists
            // for. Stated rather than inferred, so this reads as a decision and not an omission.
            HasCompute = true,

            HasGeometryShaders = features.GeometryShader,
            HasTessellation = features.TessellationShader,
            HasMeshShaders = extensions.Contains(MeshShaderExt) || extensions.Contains(MeshShaderNv),

            // The extension, or 1.2 where it is core. This says the device *can* do descriptor
            // indexing, not that the runtime-descriptor-array and partially-bound features are on —
            // those are opt-in through PhysicalDeviceDescriptorIndexingFeatures at device creation,
            // and MoltenVK gates them behind Metal argument-buffer tier 2 (ADR-011). Device creation
            // narrows this; nothing widens it.
            HasBindless = apiVersion >= Version12 || extensions.Contains(DescriptorIndexing),

            HasMultiDrawIndirect = features.MultiDrawIndirect,
            HasTimelineSemaphores = apiVersion >= Version12 || extensions.Contains(TimelineSemaphore),
            HasAsyncCompute = queues.HasAsyncCompute,
            HasAsyncTransfer = queues.HasAsyncTransfer,
            HasSparseResources = features.SparseBinding,
            HasFloat64 = features.ShaderFloat64,

            // Core since 1.1, which AdapterSelection already made the floor.
            HasSubgroupOperations = true,

            // The dependencies too, not just the extension: a 1.1 device that offers dynamic
            // rendering without create-renderpass2 cannot legally enable it, and reporting the
            // capability would send the renderer down a path device creation had already declined.
            HasDynamicRendering = apiVersion >= Version13
                || (extensions.Contains(DynamicRendering)
                    && (apiVersion >= Version12
                        || (extensions.Contains(CreateRenderPass2)
                            && extensions.Contains(DepthStencilResolve)))),
            HasDepthClamp = features.DepthClamp,
            HasWireframe = features.FillModeNonSolid,
            HasAnisotropicFiltering = features.SamplerAnisotropy,
            HasIndependentBlend = features.IndependentBlend,
            HasPipelineStatistics = features.PipelineStatisticsQuery,
            HasUnifiedMemory = unifiedMemory,

            MaxTextureSize = Clamp(limits.MaxImageDimension2D),
            MaxTextureArrayLayers = Clamp(limits.MaxImageArrayLayers),
            MaxColourAttachments = Clamp(limits.MaxColorAttachments),
            MaxVertexBuffers = Clamp(limits.MaxVertexInputBindings),
            MaxDescriptorSets = Clamp(limits.MaxBoundDescriptorSets),
            MaxPushConstantSize = Clamp(limits.MaxPushConstantsSize),

            // A driver may report a large maximum anisotropy and still not support the feature, in
            // which case asking for more than 1 is a validation error rather than a slow path.
            MaxAnisotropy = features.SamplerAnisotropy ? limits.MaxSamplerAnisotropy : 1f,

            MaxComputeWorkgroupSize = WorkgroupSize(limits),

            // The intersection, not the colour counts alone: an MSAA pass needs a depth buffer at the
            // same sample count, and a device that offers 16x colour and 8x depth cannot render 16x.
            SupportedSampleCounts = VulkanFormats.FromSampleCounts(
                limits.FramebufferColorSampleCounts & limits.FramebufferDepthSampleCounts
            )
        };

    /// <summary>Whether the memory heaps describe a shared pool.</summary>
    /// <param name="memory">What <c>vkGetPhysicalDeviceMemoryProperties</c> said.</param>
    /// <param name="kind">What kind of adapter it is.</param>
    /// <remarks>
    ///     Asked of the heaps rather than assumed from the device type. Integrated GPUs are the usual
    ///     case, but a discrete card with resizable BAR also exposes a device-local host-visible heap,
    ///     and Apple silicon reports itself as integrated with genuinely unified memory. The test is
    ///     the one that matters to an upload path: is there a heap that is both device-local and
    ///     host-visible, so that staging would be pure overhead?
    /// </remarks>
    public static bool HasUnifiedMemory(in PhysicalDeviceMemoryProperties memory, AdapterKind kind) {
        const MemoryPropertyFlags Shared = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit;

        for (var index = 0u; index < memory.MemoryTypeCount; index++) {
            if ((memory.MemoryTypes[(int)index].PropertyFlags & Shared) == Shared) {
                // A discrete card's resizable-BAR window is device-local and host-visible too, and it
                // is a few hundred megabytes rather than all of memory — so the heap test alone would
                // tell an upload path to stop staging on exactly the hardware where staging pays.
                return kind != AdapterKind.Discrete;
            }
        }

        return false;
    }

    /// <summary>The three compute workgroup limits.</summary>
    /// <param name="limits">What the driver reported.</param>
    /// <remarks>
    ///     Unsafe because Vulkan declares this as a C array and Silk surfaces it as a fixed buffer.
    ///     Three reads at known indices out of a struct the caller owns — the narrowest possible use
    ///     of the keyword, kept to one method so nothing else has to be marked.
    /// </remarks>
    static unsafe (int X, int Y, int Z) WorkgroupSize(in PhysicalDeviceLimits limits) =>
        (Clamp(limits.MaxComputeWorkGroupSize[0]),
            Clamp(limits.MaxComputeWorkGroupSize[1]),
            Clamp(limits.MaxComputeWorkGroupSize[2]));

    /// <summary>A Vulkan limit as an <c>int</c>.</summary>
    /// <param name="value">What the driver reported.</param>
    /// <remarks>
    ///     Vulkan reports limits as <c>uint32</c> and several of them are <c>0xFFFFFFFF</c> meaning
    ///     "no limit" — <c>maxImageArrayLayers</c> on some drivers, <c>maxPushConstantsSize</c> on
    ///     none but nothing forbids it. Casting that straight to <c>int</c> yields −1, and a
    ///     capability check written as <c>requested &lt;= Max</c> then rejects everything.
    /// </remarks>
    static int Clamp(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;
}
