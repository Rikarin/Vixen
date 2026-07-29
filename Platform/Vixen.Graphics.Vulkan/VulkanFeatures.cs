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
    const string MeshShaderExt = "VK_EXT_mesh_shader";
    const string MeshShaderNv = "VK_NV_mesh_shader";

    /// <summary>Descriptor indexing, core in 1.2 and an extension below it.</summary>
    /// <remarks>
    ///     Internal rather than private because device creation has to name it in the enabled
    ///     extension list on a 1.1 device, and two spellings of an extension name is one typo away
    ///     from a capability that reports present and a device that was never asked for it.
    /// </remarks>
    internal const string DescriptorIndexing = "VK_EXT_descriptor_indexing";

    /// <summary>The count-buffer draw, core in 1.2 and an extension at every version.</summary>
    /// <remarks>
    ///     Named here rather than taken from <c>KhrDrawIndirectCount.ExtensionName</c> for the same
    ///     reason <see cref="DescriptorIndexing" /> is: the translation and device creation have to
    ///     name the same string, and two spellings is one typo away from a capability that reports
    ///     present on a device that was never asked for it.
    /// </remarks>
    internal const string DrawIndirectCount = "VK_KHR_draw_indirect_count";

    /// <summary>How many sets a pipeline binds once one of them is a bindless table.</summary>
    /// <remarks>
    ///     The engine's four, plus <c>DescriptorSetSlot.Bindless</c>. Vulkan's floor for
    ///     <c>maxBoundDescriptorSets</c> is four, so this is a real requirement rather than a
    ///     formality — see the comment where it is applied.
    /// </remarks>
    const uint BindlessSetCount = 5;

    /// <summary>Translates a device's report into the RHI's vocabulary.</summary>
    /// <param name="features">What <c>vkGetPhysicalDeviceFeatures</c> said.</param>
    /// <param name="limits">What <c>vkGetPhysicalDeviceProperties</c> said.</param>
    /// <param name="extensions">The device extensions it offers.</param>
    /// <param name="apiVersion">The Vulkan version it supports, packed as Vulkan packs it.</param>
    /// <param name="queues">Which family does what, which is where the async flags come from.</param>
    /// <param name="unifiedMemory">Whether the CPU and GPU share one pool.</param>
    /// <param name="indexing">
    ///     What <c>VkPhysicalDeviceDescriptorIndexingFeatures</c> said, or all-false where the device
    ///     was never asked — which is the right answer for a device that has no descriptor indexing
    ///     to ask about.
    /// </param>
    /// <param name="timestampValidBits">
    ///     How many meaningful bits the <i>graphics</i> family writes into a timestamp query. Zero
    ///     means the queue a frame is recorded on cannot be timed, whatever the rest of the device
    ///     can do.
    /// </param>
    /// <param name="indexingLimits">What <c>VkPhysicalDeviceDescriptorIndexingProperties</c> said.</param>
    public static GraphicsDeviceFeatures Translate(
        in PhysicalDeviceFeatures features,
        in PhysicalDeviceLimits limits,
        IReadOnlySet<string> extensions,
        uint apiVersion,
        in QueueFamilyPlan queues,
        bool unifiedMemory,
        uint timestampValidBits = 0,
        in PhysicalDeviceDescriptorIndexingFeatures indexing = default,
        in PhysicalDeviceDescriptorIndexingProperties indexingLimits = default
    ) {
        // ⚠ And a fifth bindable set, which is not part of descriptor indexing and is checked here
        // because nothing else would check it. The engine's table is its own descriptor set — see
        // DescriptorSetSlot.Bindless for why it cannot share one — so a shader that indexes a table
        // binds five sets, and Vulkan guarantees four. Every device with descriptor indexing has
        // reported eight or more so far; a device that reports four would build every layout
        // successfully and fail at vkCreatePipelineLayout, which is a long way from the cause.
        var bindless = HasDescriptorIndexing(extensions, apiVersion)
            && Bindless(indexing)
            && limits.MaxBoundDescriptorSets >= BindlessSetCount;

        return GraphicsDeviceFeatures.Minimum with {
            // Vulkan has no device without compute — unlike WebGL2, which is what the flag exists
            // for. Stated rather than inferred, so this reads as a decision and not an omission.
            HasCompute = true,

            HasGeometryShaders = features.GeometryShader,
            HasTessellation = features.TessellationShader,
            HasMeshShaders = extensions.Contains(MeshShaderExt) || extensions.Contains(MeshShaderNv),

            // The extension or 1.2 says descriptor indexing is *reachable*; Bindless below says the
            // four bits the engine's table actually needs are there. Both, because the two come
            // apart on real hardware: MoltenVK offers the extension on every device and gates the
            // bits behind Metal argument-buffer tier 2 (ADR-011), so an Apple device that answered
            // this on the extension alone would report a capability and then fail at
            // vkCreateDescriptorSetLayout.
            HasBindless = bindless,

            HasMultiDrawIndirect = features.MultiDrawIndirect,

            // The extension, at every version. It is core from 1.2 and gated there behind
            // VkPhysicalDeviceVulkan12Features::drawIndirectCount, which this backend does not query
            // — and every driver that promoted it still advertises it, so asking for the extension
            // is both sufficient and the same question device creation enables.
            HasDrawIndirectCount = extensions.Contains(DrawIndirectCount),
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

            // ⚠ Both halves, and the period is the half that is easy to forget. A device may report
            // valid bits on the graphics family and a period of zero, which happens on a small
            // number of software implementations; a profiler that trusted the bits alone would
            // multiply every duration by nothing and draw a timeline of zero-width passes.
            HasTimestampQueries = timestampValidBits > 0 && limits.TimestampPeriod > 0f,
            TimestampPeriod = timestampValidBits > 0 ? limits.TimestampPeriod : 0f,

            HasUnifiedMemory = unifiedMemory,

            MaxTextureSize = Clamp(limits.MaxImageDimension2D),
            MaxTextureArrayLayers = Clamp(limits.MaxImageArrayLayers),
            MaxColourAttachments = Clamp(limits.MaxColorAttachments),
            MaxVertexBuffers = Clamp(limits.MaxVertexInputBindings),
            MaxDescriptorSets = Clamp(limits.MaxBoundDescriptorSets),
            MaxPushConstantSize = Clamp(limits.MaxPushConstantsSize),

            // The lesser of the two, because a table has to satisfy both: the set limit bounds every
            // update-after-bind sampled image across the whole set, and the per-stage limit bounds
            // what one stage can see — and a fragment shader indexing the table is one stage seeing
            // all of it. Desktop drivers report a million or more for the first and often the same
            // for the second; the devices where they differ are exactly the ones where guessing
            // wrong is a layout the driver refuses.
            MaxBindlessDescriptors = bindless
                ? Math.Min(
                    Clamp(indexingLimits.MaxDescriptorSetUpdateAfterBindSampledImages),
                    Clamp(indexingLimits.MaxPerStageDescriptorUpdateAfterBindSampledImages)
                )
                : 0,

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
    }

    /// <summary>Whether the descriptor-indexing structures are worth asking the device about.</summary>
    /// <param name="extensions">The device extensions it offers.</param>
    /// <param name="apiVersion">The version it supports.</param>
    /// <remarks>
    ///     Chaining a structure a device does not recognise is not an error — the loader leaves it as
    ///     it found it — so this is not about safety. It is about the answer: an all-zero structure
    ///     back from a 1.1 device means "no", and an all-zero structure that was never written means
    ///     the same thing, and telling those apart is impossible after the fact. Asking only where
    ///     the question exists keeps the two from being confused.
    /// </remarks>
    public static bool HasDescriptorIndexing(IReadOnlySet<string> extensions, uint apiVersion) {
        ArgumentNullException.ThrowIfNull(extensions);
        return apiVersion >= Version12 || extensions.Contains(DescriptorIndexing);
    }

    /// <summary>The four bits an unbounded texture array needs, all of them.</summary>
    /// <param name="indexing">What the device reported.</param>
    /// <remarks>
    ///     <para>
    ///         Every one is load-bearing and none of them is implied by the others:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>runtimeDescriptorArray</c> — the array may be declared without a length. Without
    ///             it the shader is a different shader.
    ///         </item>
    ///         <item>
    ///             <c>descriptorBindingPartiallyBound</c> — a slot nobody has written may exist. A
    ///             table is almost entirely empty almost all of the time, and without this every slot
    ///             would have to hold something before the set could be used at all.
    ///         </item>
    ///         <item>
    ///             <c>shaderSampledImageArrayNonUniformIndexing</c> — the index may differ across a
    ///             subgroup. This is the one that makes the whole idea work: a merged draw is
    ///             fragments of different materials in one dispatch, so a uniform index would mean
    ///             the merge was pointless.
    ///         </item>
    ///         <item>
    ///             <c>descriptorBindingSampledImageUpdateAfterBind</c> — the set may be written while
    ///             bound. This is what lets a texture streamed in mid-frame take a slot without the
    ///             host waiting for the device.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static bool Bindless(in PhysicalDeviceDescriptorIndexingFeatures indexing) =>
        indexing.RuntimeDescriptorArray
        && indexing.DescriptorBindingPartiallyBound
        && indexing.ShaderSampledImageArrayNonUniformIndexing
        && indexing.DescriptorBindingSampledImageUpdateAfterBind;

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
