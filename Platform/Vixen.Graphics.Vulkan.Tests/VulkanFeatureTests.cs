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

            // What an NVIDIA desktop part reports. A number rather than zero because the interesting
            // assertion is that a device with valid bits and no period is still reported untimeable.
            TimestampPeriod = 1f,

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
        uint timestampValidBits = 0,
        PhysicalDeviceDescriptorIndexingFeatures indexing = default,
        PhysicalDeviceDescriptorIndexingProperties? indexingLimits = null,
        PhysicalDeviceAccelerationStructureFeaturesKHR acceleration = default,
        PhysicalDeviceRayQueryFeaturesKHR rayQuery = default,
        PhysicalDeviceBufferDeviceAddressFeatures addressing = default
    ) =>
        VulkanFeatures.Translate(
            features,
            limits ?? Limits(),
            extensions ?? new HashSet<string>(),
            apiVersion,
            queues ?? OneFamily,
            unifiedMemory,
            timestampValidBits,
            indexing,
            indexingLimits ?? IndexingLimits(),
            acceleration,
            rayQuery,
            addressing
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

    /// <summary>The three extensions ray tracing enables, spelled as a driver spells them.</summary>
    static HashSet<string> RayTracingSet() => [
        "VK_KHR_acceleration_structure",
        "VK_KHR_ray_query",
        "VK_KHR_deferred_host_operations"
    ];

    /// <summary>The three feature bits ray tracing needs, all on.</summary>
    static (
        PhysicalDeviceAccelerationStructureFeaturesKHR Acceleration,
        PhysicalDeviceRayQueryFeaturesKHR RayQuery,
        PhysicalDeviceBufferDeviceAddressFeatures Addressing
        ) RayTracingBits() => (
        new() { AccelerationStructure = true },
        new() { RayQuery = true },
        new() { BufferDeviceAddress = true }
    );

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
    ///     The count-buffer draw is the extension, and multi-draw does not imply it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two separate things on every API that has both, and a host that read
    ///         <c>HasMultiDrawIndirect</c> and issued a count-buffer draw would find out on whichever
    ///         driver it reached first. <c>multiDrawIndirect</c> on and the extension absent is what
    ///         MoltenVK reports today, so this is the combination that actually ships rather than one
    ///         invented for the assertion.
    ///     </para>
    ///     <para>
    ///         Asked as the extension at every version. It is core from 1.2, where it is gated behind
    ///         <c>VkPhysicalDeviceVulkan12Features::drawIndirectCount</c> — a structure this backend
    ///         never queries — and every driver that promoted it still advertises it. Answering from
    ///         the version instead would report a capability on a 1.2 device whose feature bit is off,
    ///         and the failure is a validation error at the draw.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCountBufferDrawIsItsOwnExtension() {
        var multi = new PhysicalDeviceFeatures { MultiDrawIndirect = true };

        Assert.True(Translate(features: multi).HasMultiDrawIndirect);
        Assert.False(Translate(features: multi).HasDrawIndirectCount);
        Assert.False(Translate(features: multi, apiVersion: VulkanFeatures.Version13).HasDrawIndirectCount);

        var offered = new HashSet<string> { "VK_KHR_draw_indirect_count" };

        Assert.True(Translate(extensions: offered).HasDrawIndirectCount);
        Assert.False(Translate(extensions: offered).HasMultiDrawIndirect);
    }

    /// <summary>
    ///     And a fifth bindable set, which descriptor indexing does not imply.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The claim a wrong answer would hide.</strong> A table is its own descriptor set
    ///         — see <c>DescriptorSetSlot.Bindless</c> — so a shader that indexes one binds five, and
    ///         Vulkan's floor for <c>maxBoundDescriptorSets</c> is four. A device that answered this
    ///         on the indexing bits alone would create every descriptor-set layout successfully, and
    ///         then fail at <c>vkCreatePipelineLayout</c> — a long way from the cause, and in a call
    ///         that says nothing about descriptor indexing.
    ///     </para>
    ///     <para>
    ///         Four is not a hypothetical floor: it is the number the specification guarantees, so it
    ///         is exactly what a conformant minimum device reports.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(4u, false)]
    [InlineData(5u, true)]
    [InlineData(8u, true)]
    public void BindlessNeedsAFifthBindableSet(uint sets, bool expected) {
        var limits = Limits();
        limits.MaxBoundDescriptorSets = sets;

        Assert.Equal(
            expected,
            Translate(limits: limits, apiVersion: VulkanFeatures.Version12, indexing: Indexing()).HasBindless
        );
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

    /// <summary>
    ///     The graphics family's valid bits are what decides it, and the period comes across with
    ///     them.
    /// </summary>
    [Fact]
    public void TimestampsFollowTheGraphicsFamily() {
        var features = Translate(timestampValidBits: 64);

        Assert.True(features.HasTimestampQueries);
        Assert.Equal(1f, features.TimestampPeriod);
    }

    /// <summary>
    ///     A transfer-only queue reporting zero valid bits is an ordinary configuration, and a
    ///     profiler told it could time the device would record readings that never change.
    /// </summary>
    [Fact]
    public void NoValidBitsIsNoTimeline() {
        var features = Translate();

        Assert.False(features.HasTimestampQueries);
        Assert.Equal(0f, features.TimestampPeriod);
    }

    /// <summary>
    ///     Valid bits and a zero period is a real combination on software implementations, and it is
    ///     the one where trusting the bits alone draws a timeline of zero-width passes.
    /// </summary>
    [Fact]
    public void AZeroPeriodIsNotATimeline() {
        var limits = Limits();
        limits.TimestampPeriod = 0f;

        Assert.False(Translate(limits: limits, timestampValidBits: 64).HasTimestampQueries);
    }

    /// <summary>Everything ray tracing asks for, together, answers yes.</summary>
    /// <remarks>
    ///     The one configuration that ships it: 1.2 or better, all three extensions, all three
    ///     feature bits. MoltenVK offers none of this (ADR-011's family of absences), so this
    ///     hand-built struct is the only place the "on" direction runs on a Mac — which is exactly
    ///     why it is asserted here rather than left to a device test.
    /// </remarks>
    [Fact]
    public void RayTracingNeedsEverythingAndEverythingIsEnough() {
        var (acceleration, rayQuery, addressing) = RayTracingBits();

        Assert.True(
            Translate(
                extensions: RayTracingSet(),
                apiVersion: VulkanFeatures.Version12,
                acceleration: acceleration,
                rayQuery: rayQuery,
                addressing: addressing
            ).HasRayTracing
        );
    }

    /// <summary>Dropping any one of the three extensions turns ray tracing off.</summary>
    /// <remarks>
    ///     Including deferred host operations, which this backend never calls: the specification
    ///     makes enabling acceleration structures without it invalid usage, so reporting the
    ///     capability without it would promise a device creation that the validation layers reject.
    /// </remarks>
    [Theory]
    [InlineData("VK_KHR_acceleration_structure")]
    [InlineData("VK_KHR_ray_query")]
    [InlineData("VK_KHR_deferred_host_operations")]
    public void EveryRayTracingExtensionIsRequired(string dropped) {
        var (acceleration, rayQuery, addressing) = RayTracingBits();
        var extensions = RayTracingSet();
        extensions.Remove(dropped);

        Assert.False(
            Translate(
                extensions: extensions,
                apiVersion: VulkanFeatures.Version12,
                acceleration: acceleration,
                rayQuery: rayQuery,
                addressing: addressing
            ).HasRayTracing
        );
    }

    /// <summary>
    ///     The extensions alone are not ray tracing — each of the three feature bits is load-bearing.
    /// </summary>
    /// <remarks>
    ///     The <see cref="EveryOneOfTheFourBitsIsRequired" /> discipline for the other family: a
    ///     driver may list an extension whose feature bit it declines, and a translation that checked
    ///     two bits and forgot the third would report a capability the device has two thirds of.
    ///     Buffer device address is the bit worth singling out — a build addresses its geometry by
    ///     GPU address, so ray tracing without addressing is not a configuration that exists.
    /// </remarks>
    [Fact]
    public void EveryOneOfTheThreeRayTracingBitsIsRequired() {
        var (acceleration, rayQuery, addressing) = RayTracingBits();

        Assert.False(
            Translate(
                extensions: RayTracingSet(),
                apiVersion: VulkanFeatures.Version12,
                rayQuery: rayQuery,
                addressing: addressing
            ).HasRayTracing
        );

        Assert.False(
            Translate(
                extensions: RayTracingSet(),
                apiVersion: VulkanFeatures.Version12,
                acceleration: acceleration,
                addressing: addressing
            ).HasRayTracing
        );

        Assert.False(
            Translate(
                extensions: RayTracingSet(),
                apiVersion: VulkanFeatures.Version12,
                acceleration: acceleration,
                rayQuery: rayQuery
            ).HasRayTracing
        );
    }

    /// <summary>Below Vulkan 1.2 there is no ray tracing, whatever else the device says.</summary>
    /// <remarks>
    ///     A deliberate floor rather than an observation: every device shipping the extensions ships
    ///     1.2, and requiring it makes <c>vkGetBufferDeviceAddress</c> core — one spelling of the
    ///     entry point, no extension-function fallback that no hardware would ever run.
    /// </remarks>
    [Fact]
    public void RayTracingRequiresVersion12() {
        var (acceleration, rayQuery, addressing) = RayTracingBits();

        Assert.False(
            Translate(
                extensions: RayTracingSet(),
                apiVersion: Version11,
                acceleration: acceleration,
                rayQuery: rayQuery,
                addressing: addressing
            ).HasRayTracing
        );
    }

    /// <summary>The bits without the extensions are not ray tracing either.</summary>
    /// <remarks>
    ///     The <see cref="FeaturesWithoutTheExtensionOrTheVersionAreNotBindless" /> mirror: the
    ///     structures are only filled where the device was asked, so bits against a device with no
    ///     extensions are bits nobody could have obtained, and answering yes would enable extensions
    ///     device creation never named.
    /// </remarks>
    [Fact]
    public void RayTracingBitsWithoutTheExtensionsAreNotRayTracing() {
        var (acceleration, rayQuery, addressing) = RayTracingBits();

        Assert.False(
            Translate(
                apiVersion: VulkanFeatures.Version12,
                acceleration: acceleration,
                rayQuery: rayQuery,
                addressing: addressing
            ).HasRayTracing
        );
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
