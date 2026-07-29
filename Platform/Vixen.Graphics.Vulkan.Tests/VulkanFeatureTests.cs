// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Capability translation, asserted in both directions on hand-built Vulkan structs. A
///     capability claimed wrongly crashes at the first frame; one denied wrongly runs the fallback
///     path forever and looks identical, so "off" is tested as carefully as "on".
/// </summary>
public sealed class VulkanFeatureTests {
    const uint Version11 = AdapterSelection.MinimumApiVersion;

    static readonly QueueFamilyPlan OneFamily = new(0, 0, 0, 0);
    static readonly QueueFamilyPlan ThreeFamilies = new(0, 1, 2, 0);

    /// <summary>A plausible mid-range desktop driver, as a starting point to vary one field at a time.</summary>
    static PhysicalDeviceLimits Limits() {
        var limits = new PhysicalDeviceLimits {
            MaxImageDimension2D = 16384,
            MaxImageArrayLayers = 2048,
            MaxColorAttachments = 8,
            MaxVertexInputBindings = 32,
            MaxBoundDescriptorSets = 8,
            MaxPushConstantsSize = 256,
            MaxSamplerAnisotropy = 16f,
            FramebufferColorSampleCounts = SampleCountFlags.Count1Bit
                | SampleCountFlags.Count2Bit
                | SampleCountFlags.Count4Bit
                | SampleCountFlags.Count8Bit,
            FramebufferDepthSampleCounts = SampleCountFlags.Count1Bit
                | SampleCountFlags.Count2Bit
                | SampleCountFlags.Count4Bit
        };

        SetWorkgroupSize(ref limits, 1024, 1024, 64);
        return limits;
    }

    static unsafe void SetWorkgroupSize(ref PhysicalDeviceLimits limits, uint x, uint y, uint z) {
        limits.MaxComputeWorkGroupSize[0] = x;
        limits.MaxComputeWorkGroupSize[1] = y;
        limits.MaxComputeWorkGroupSize[2] = z;
    }

    static GraphicsDeviceFeatures Translate(
        PhysicalDeviceFeatures features = default,
        PhysicalDeviceLimits? limits = null,
        IReadOnlySet<string>? extensions = null,
        uint apiVersion = Version11,
        QueueFamilyPlan? queues = null,
        bool unifiedMemory = false,
        PhysicalDeviceDescriptorIndexingFeatures indexing = default,
        PhysicalDeviceDescriptorIndexingProperties? indexingLimits = null
    ) =>
        VulkanFeatures.Translate(
            features,
            limits ?? Limits(),
            extensions ?? new HashSet<string>(),
            apiVersion,
            queues ?? OneFamily,
            unifiedMemory,
            indexing,
            indexingLimits ?? IndexingLimits()
        );

    /// <summary>The four bits an unbounded texture array needs, all on.</summary>
    static PhysicalDeviceDescriptorIndexingFeatures Indexing() => new() {
        RuntimeDescriptorArray = true,
        DescriptorBindingPartiallyBound = true,
        ShaderSampledImageArrayNonUniformIndexing = true,
        DescriptorBindingSampledImageUpdateAfterBind = true
    };

    /// <summary>A plausible desktop driver's update-after-bind ceilings, unequal on purpose.</summary>
    static PhysicalDeviceDescriptorIndexingProperties IndexingLimits() => new() {
        MaxDescriptorSetUpdateAfterBindSampledImages = 1_048_576,
        MaxPerStageDescriptorUpdateAfterBindSampledImages = 65_536
    };

    [Fact]
    public void LimitsAreCarriedAcross() {
        var features = Translate();

        Assert.Equal(16384, features.MaxTextureSize);
        Assert.Equal(2048, features.MaxTextureArrayLayers);
        Assert.Equal(8, features.MaxColourAttachments);
        Assert.Equal(32, features.MaxVertexBuffers);
        Assert.Equal(8, features.MaxDescriptorSets);
        Assert.Equal(256, features.MaxPushConstantSize);
        Assert.Equal((1024, 1024, 64), features.MaxComputeWorkgroupSize);
    }

    /// <summary>
    ///     Vulkan reports limits as <c>uint32</c> and "no limit" is <c>0xFFFFFFFF</c>, which cast
    ///     straight to <c>int</c> is −1 — and every <c>requested &lt;= Max</c> check then rejects
    ///     everything.
    /// </summary>
    [Fact]
    public void AnUnlimitedLimitDoesNotBecomeNegative() {
        var limits = Limits();
        limits.MaxImageArrayLayers = uint.MaxValue;

        Assert.Equal(int.MaxValue, Translate(limits: limits).MaxTextureArrayLayers);
    }

    [Fact]
    public void BooleanFeaturesFollowTheDriver() {
        var reported = new PhysicalDeviceFeatures {
            GeometryShader = true,
            TessellationShader = true,
            MultiDrawIndirect = true,
            SparseBinding = true,
            ShaderFloat64 = true,
            DepthClamp = true,
            FillModeNonSolid = true,
            SamplerAnisotropy = true,
            IndependentBlend = true,
            PipelineStatisticsQuery = true
        };

        var features = Translate(reported);

        Assert.True(features.HasGeometryShaders);
        Assert.True(features.HasTessellation);
        Assert.True(features.HasMultiDrawIndirect);
        Assert.True(features.HasSparseResources);
        Assert.True(features.HasFloat64);
        Assert.True(features.HasDepthClamp);
        Assert.True(features.HasWireframe);
        Assert.True(features.HasAnisotropicFiltering);
        Assert.True(features.HasIndependentBlend);
        Assert.True(features.HasPipelineStatistics);
    }

    /// <summary>
    ///     The half that matters more. MoltenVK reports no pipeline-statistics queries (ADR-011) and
    ///     a profiler that believed otherwise would show plausible nonsense.
    /// </summary>
    [Fact]
    public void ADriverThatReportsNothingGetsNothing() {
        var features = Translate();

        Assert.False(features.HasGeometryShaders);
        Assert.False(features.HasTessellation);
        Assert.False(features.HasMultiDrawIndirect);
        Assert.False(features.HasSparseResources);
        Assert.False(features.HasFloat64);
        Assert.False(features.HasDepthClamp);
        Assert.False(features.HasWireframe);
        Assert.False(features.HasAnisotropicFiltering);
        Assert.False(features.HasIndependentBlend);
        Assert.False(features.HasPipelineStatistics);
        Assert.False(features.HasMeshShaders);
        Assert.False(features.HasUnifiedMemory);
    }

    /// <summary>Vulkan always has compute; the flag exists for WebGL2, which does not.</summary>
    [Fact]
    public void ComputeIsAlwaysOn() {
        Assert.True(Translate().HasCompute);
    }

    [Fact]
    public void DynamicRenderingComesFromTheExtensionOrFromVersion13() {
        Assert.False(Translate().HasDynamicRendering);
        Assert.True(Translate(extensions: DynamicRenderingSet()).HasDynamicRendering);
        Assert.True(Translate(apiVersion: VulkanFeatures.Version13).HasDynamicRendering);

        // On 1.2 the dependencies are core, so the extension alone is enough.
        Assert.True(
            Translate(
                extensions: new HashSet<string> { "VK_KHR_dynamic_rendering" },
                apiVersion: VulkanFeatures.Version12
            ).HasDynamicRendering
        );
    }

    /// <summary>
    ///     Below 1.2, <c>VK_KHR_dynamic_rendering</c> requires <c>VK_KHR_depth_stencil_resolve</c>,
    ///     which requires <c>VK_KHR_create_renderpass2</c>. Reporting the capability without them
    ///     sends the renderer down a path device creation has to decline — which is not a hypothetical:
    ///     MoltenVK accepted the incomplete extension list and the validation layers rejected it.
    /// </summary>
    [Fact]
    public void DynamicRenderingNeedsItsDependenciesBelowVersion12() {
        Assert.False(
            Translate(extensions: new HashSet<string> { "VK_KHR_dynamic_rendering" }).HasDynamicRendering
        );

        Assert.False(
            Translate(
                extensions: new HashSet<string> {
                    "VK_KHR_dynamic_rendering",
                    VulkanFeatures.CreateRenderPass2
                }
            ).HasDynamicRendering
        );
    }

    static HashSet<string> DynamicRenderingSet() => [
        "VK_KHR_dynamic_rendering",
        VulkanFeatures.CreateRenderPass2,
        VulkanFeatures.DepthStencilResolve
    ];

    [Fact]
    public void TimelineSemaphoresComeFromTheExtensionOrFromVersion12() {
        Assert.False(Translate().HasTimelineSemaphores);

        Assert.True(
            Translate(extensions: new HashSet<string> { "VK_KHR_timeline_semaphore" }).HasTimelineSemaphores
        );

        Assert.True(Translate(apiVersion: VulkanFeatures.Version12).HasTimelineSemaphores);
    }

    /// <summary>
    ///     A 1.1 device without <c>VK_KHR_dynamic_rendering</c> is the case that makes the
    ///     real-render-pass fallback mandatory rather than optional ([05](../../docs/plan/05-graphics-rhi.md)),
    ///     and a large slice of Android is exactly that.
    /// </summary>
    [Fact]
    public void AnElevenOnlyDeviceReportsNeitherOfTheModernPaths() {
        var features = Translate(apiVersion: Version11);

        Assert.False(features.HasDynamicRendering);
        Assert.False(features.HasTimelineSemaphores);
        Assert.False(features.HasBindless);
    }

    /// <summary>
    ///     The extension alone is not bindless, and this is the case that made it a rule.
    /// </summary>
    /// <remarks>
    ///     MoltenVK offers <c>VK_EXT_descriptor_indexing</c> on every device it runs on and gates the
    ///     features behind Metal argument-buffer tier 2 (ADR-011). A translation that answered on the
    ///     extension string would report bindless on every Mac and every iPhone, and the discovery
    ///     would be <c>vkCreateDescriptorSetLayout</c> refusing the table — long after the renderer
    ///     had chosen its path.
    /// </remarks>
    [Fact]
    public void DescriptorIndexingReachableIsNotDescriptorIndexingEnabled() {
        var reachable = new HashSet<string> { "VK_EXT_descriptor_indexing" };

        Assert.False(Translate(extensions: reachable).HasBindless);
        Assert.False(Translate(apiVersion: VulkanFeatures.Version12).HasBindless);

        Assert.True(Translate(extensions: reachable, indexing: Indexing()).HasBindless);
        Assert.True(Translate(apiVersion: VulkanFeatures.Version12, indexing: Indexing()).HasBindless);
    }

    /// <summary>
    ///     The features without a route to them are not bindless either.
    /// </summary>
    /// <remarks>
    ///     The mirror of the case above, and it is not hypothetical: the structure is only ever
    ///     filled where the device was asked, so features reported against a 1.1 device with no
    ///     extension are features nobody could have obtained. Answering yes there would enable an
    ///     extension device creation never asked for.
    /// </remarks>
    [Fact]
    public void FeaturesWithoutTheExtensionOrTheVersionAreNotBindless() =>
        Assert.False(Translate(apiVersion: Version11, indexing: Indexing()).HasBindless);

    /// <summary>
    ///     Each of the four bits is load-bearing, so dropping any one of them turns bindless off.
    /// </summary>
    /// <remarks>
    ///     Written out one at a time rather than as a single all-on assertion, because the failure
    ///     this guards against is a translation that checks three and forgets the fourth — which
    ///     reports a capability the device has three quarters of, and fails at whichever call needed
    ///     the missing quarter.
    /// </remarks>
    [Fact]
    public void EveryOneOfTheFourBitsIsRequired() {
        var full = Indexing();

        Assert.False(Translate(apiVersion: VulkanFeatures.Version12, indexing: full with {
            RuntimeDescriptorArray = false
        }).HasBindless);

        Assert.False(Translate(apiVersion: VulkanFeatures.Version12, indexing: full with {
            DescriptorBindingPartiallyBound = false
        }).HasBindless);

        Assert.False(Translate(apiVersion: VulkanFeatures.Version12, indexing: full with {
            ShaderSampledImageArrayNonUniformIndexing = false
        }).HasBindless);

        Assert.False(Translate(apiVersion: VulkanFeatures.Version12, indexing: full with {
            DescriptorBindingSampledImageUpdateAfterBind = false
        }).HasBindless);
    }

    /// <summary>
    ///     A table is sized by the lesser of the two update-after-bind ceilings.
    /// </summary>
    /// <remarks>
    ///     Both bound it. The set limit covers every update-after-bind sampled image in the set, and
    ///     the per-stage limit covers what one stage may see — and the fragment stage indexing the
    ///     table sees all of it. Taking the larger produces a layout the driver refuses, on exactly
    ///     the mobile parts where the two differ most.
    /// </remarks>
    [Fact]
    public void TheTableIsSizedByTheLesserCeiling() {
        var features = Translate(apiVersion: VulkanFeatures.Version12, indexing: Indexing());

        Assert.Equal(65_536, features.MaxBindlessDescriptors);
    }

    /// <summary>A device without the capability reports no descriptors, whatever its limits say.</summary>
    /// <remarks>
    ///     So that a host cannot reach a non-zero capacity through a device that would refuse the
    ///     layout. <c>BindlessTable.IsSupportedBy</c> asks both questions for the same reason.
    /// </remarks>
    [Fact]
    public void WithoutTheCapabilityThereAreNoBindlessDescriptors() =>
        Assert.Equal(0, Translate(apiVersion: VulkanFeatures.Version12).MaxBindlessDescriptors);

    [Fact]
    public void MeshShadersComeFromEitherVendorsExtension() {
        Assert.True(Translate(extensions: new HashSet<string> { "VK_EXT_mesh_shader" }).HasMeshShaders);
        Assert.True(Translate(extensions: new HashSet<string> { "VK_NV_mesh_shader" }).HasMeshShaders);
    }

    [Fact]
    public void AsyncQueuesFollowTheQueuePlan() {
        var single = Translate(queues: OneFamily);
        Assert.False(single.HasAsyncCompute);
        Assert.False(single.HasAsyncTransfer);

        var many = Translate(queues: ThreeFamilies);
        Assert.True(many.HasAsyncCompute);
        Assert.True(many.HasAsyncTransfer);
    }

    /// <summary>
    ///     Reporting the driver's maximum anisotropy while the feature is off produces a sampler
    ///     description the validation layers reject — a limit and a feature are different questions.
    /// </summary>
    [Fact]
    public void MaxAnisotropyIsOneWhereTheFeatureIsOff() {
        Assert.Equal(1f, Translate().MaxAnisotropy);
        Assert.Equal(16f, Translate(new() { SamplerAnisotropy = true }).MaxAnisotropy);
    }

    /// <summary>
    ///     An MSAA pass needs depth at the same count as colour, so the answer is the intersection.
    ///     Reporting 8x from the colour mask alone would fail at framebuffer creation on a device
    ///     whose depth stops at 4x.
    /// </summary>
    [Fact]
    public void SampleCountsAreTheIntersectionOfColourAndDepth() {
        var features = Translate();

        Assert.True(features.SupportsSampleCount(1));
        Assert.True(features.SupportsSampleCount(2));
        Assert.True(features.SupportsSampleCount(4));
        Assert.False(features.SupportsSampleCount(8));
    }

    [Fact]
    public void UnifiedMemoryIsCarriedAcross() {
        Assert.True(Translate(unifiedMemory: true).HasUnifiedMemory);
    }

    /// <summary>
    ///     A discrete card with resizable BAR exposes a device-local host-visible heap too, and it is
    ///     a window of a few hundred megabytes rather than all of memory — so a staging path told to
    ///     stop staging there would be slower on exactly the hardware where staging pays.
    /// </summary>
    [Fact]
    public void ResizableBarOnADiscreteCardIsNotUnifiedMemory() {
        var memory = SharedHeap();

        Assert.True(VulkanFeatures.HasUnifiedMemory(memory, AdapterKind.Integrated));
        Assert.False(VulkanFeatures.HasUnifiedMemory(memory, AdapterKind.Discrete));
    }

    [Fact]
    public void ADeviceLocalOnlyHeapIsNotUnifiedMemory() {
        var memory = new PhysicalDeviceMemoryProperties { MemoryTypeCount = 1 };
        memory.MemoryTypes[0] = new() { PropertyFlags = MemoryPropertyFlags.DeviceLocalBit };

        Assert.False(VulkanFeatures.HasUnifiedMemory(memory, AdapterKind.Integrated));
    }

    /// <summary>
    ///     Types past <c>memoryTypeCount</c> are uninitialised, not merely empty. Reading the whole
    ///     fixed array would read whatever the driver left there.
    /// </summary>
    [Fact]
    public void MemoryTypesPastTheReportedCountAreNotRead() {
        var memory = new PhysicalDeviceMemoryProperties { MemoryTypeCount = 1 };
        memory.MemoryTypes[0] = new() { PropertyFlags = MemoryPropertyFlags.DeviceLocalBit };

        memory.MemoryTypes[1] = new() {
            PropertyFlags = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit
        };

        Assert.False(VulkanFeatures.HasUnifiedMemory(memory, AdapterKind.Integrated));
    }

    static PhysicalDeviceMemoryProperties SharedHeap() {
        var memory = new PhysicalDeviceMemoryProperties { MemoryTypeCount = 2 };
        memory.MemoryTypes[0] = new() { PropertyFlags = MemoryPropertyFlags.DeviceLocalBit };

        memory.MemoryTypes[1] = new() {
            PropertyFlags = MemoryPropertyFlags.DeviceLocalBit
                | MemoryPropertyFlags.HostVisibleBit
                | MemoryPropertyFlags.HostCoherentBit
        };

        return memory;
    }
}
