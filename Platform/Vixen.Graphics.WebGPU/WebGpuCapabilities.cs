// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>What a WebGPU device can do, in the RHI's terms.</summary>
/// <remarks>
///     <para>
///         A pure function of the limits and the enabled feature set, so a test can ask what the
///         engine would do on a phone browser's numbers without owning a phone. That matters more
///         here than for any other backend: WebGPU's guaranteed floor is what a large share of real
///         devices report verbatim, and the fallback paths it forces are the ones nobody exercises
///         by accident.
///     </para>
///     <para>
///         Everything absent is absent for a reason worth stating once, since a reader's first
///         question is whether it was forgotten:
///     </para>
///     <list type="bullet">
///         <item><description>
///             <b>Geometry, tessellation and mesh shaders.</b> WebGPU has vertex, fragment and
///             compute, and no plan for more.
///         </description></item>
///         <item><description>
///             <b>Bindless.</b> No unbounded binding arrays, so GPU-driven culling and material
///             batching take their non-bindless path — the same one MoltenVK forces (ADR-011).
///         </description></item>
///         <item><description>
///             <b>Multi-draw indirect.</b> One indirect call issues one draw.
///             <c>DrawIndexedIndirect</c> with a count above one is refused rather than looped, so
///             the cost is visible at the call site.
///         </description></item>
///         <item><description>
///             <b>Async compute and async transfer.</b> WebGPU has exactly one queue, so all three
///             of the RHI's submitters are it.
///         </description></item>
///         <item><description>
///             <b>Timeline semaphores, sparse resources, pipeline statistics, wireframe, float64,
///             subgroups.</b> None exist in WebGPU.
///         </description></item>
///     </list>
///     <para>
///         And two are present that a reader might not expect. <b>Compute is always there</b> —
///         unlike WebGL2, which is the whole reason this backend is the better long-term web story.
///         <b>Dynamic rendering is always there</b> too: WebGPU has no render-pass or framebuffer
///         objects to fall back to, which is exactly what that capability reports.
///     </para>
/// </remarks>
public static class WebGpuCapabilities {
    /// <summary>The largest push-constant block the emulation supports, in bytes.</summary>
    /// <remarks>
    ///     WebGPU has no push constants. <see cref="PushConstantRing" /> emulates them with a
    ///     dynamic uniform buffer, and 128 bytes is the RHI's stated floor
    ///     (<see cref="GraphicsDeviceFeatures.Minimum" />) — enough for a transform and an index,
    ///     which is what push constants are for.
    /// </remarks>
    public const int PushConstantSize = 128;

    /// <summary>What to report for a device with these limits and features.</summary>
    /// <param name="reported">What the device reported, unnormalised.</param>
    /// <param name="adapter">What kind of adapter it runs on.</param>
    /// <param name="hasFeature">Whether an optional feature is enabled.</param>
    public static GraphicsDeviceFeatures Describe(
        in WebGpuLimits reported,
        WgpuAdapterType adapter,
        Func<WgpuFeatureName, bool> hasFeature
    ) {
        ArgumentNullException.ThrowIfNull(hasFeature);

        // Normalised first, and every backend that skipped this step has shipped a device that
        // claimed it could not do something the specification guarantees. See
        // WebGpuLimits.OrGuaranteed for the one that made the point.
        var limits = reported.OrGuaranteed();

        return GraphicsDeviceFeatures.Minimum with {
            HasCompute = true,
            HasDynamicRendering = true,
            HasIndependentBlend = true,
            HasAnisotropicFiltering = true,
            HasDepthClamp = hasFeature(WgpuFeatureName.DepthClipControl),

            // Optional here in a way it is not on the explicit APIs: WebGPU's core validation rejects
            // a non-zero `firstInstance` in an indirect command outright, and this feature is the
            // permission. `Wanted` has always asked for it; until this line nothing read the answer,
            // so a pass that patches the field had no way to find out it could not.
            HasDrawIndirectFirstInstance = hasFeature(WgpuFeatureName.IndirectFirstInstance),

            // An integrated GPU shares the CPU's memory pool, so staging through an upload buffer is
            // pure overhead there. A browser reports Unknown for everything — fingerprinting — so the
            // web answer is "assume not", which costs a copy and is never wrong in the unsafe
            // direction.
            HasUnifiedMemory = adapter == WgpuAdapterType.IntegratedGpu,

            MaxTextureSize = limits.MaxTextureDimension2D,
            MaxTextureArrayLayers = limits.MaxTextureArrayLayers,
            MaxColourAttachments = limits.MaxColorAttachments,
            MaxVertexBuffers = limits.MaxVertexBuffers,
            MaxDescriptorSets = limits.MaxBindGroups,
            MaxPushConstantSize = PushConstantSize,
            MaxAnisotropy = 16f,
            MaxComputeWorkgroupSize = (
                limits.MaxComputeWorkgroupSizeX,
                limits.MaxComputeWorkgroupSizeY,
                limits.MaxComputeWorkgroupSizeZ
            ),
            SupportedSampleCounts = WebGpuFormats.SupportedSampleCounts
        };
    }

    /// <summary>The optional features the backend asks a device for.</summary>
    /// <param name="available">Whether the adapter offers a feature.</param>
    /// <returns>The ones to enable, which is the intersection of wanted and offered.</returns>
    /// <remarks>
    ///     Asking for a feature an adapter lacks fails device creation outright on every
    ///     implementation, so the request has to be an intersection rather than a wish list. The
    ///     three texture-compression families are asked for together and taken separately: a desktop
    ///     browser offers BC and not ASTC, a phone the reverse, and content ships in whichever the
    ///     device has.
    /// </remarks>
    public static WgpuFeatureName[] Wanted(Func<WgpuFeatureName, bool> available) {
        ArgumentNullException.ThrowIfNull(available);

        WgpuFeatureName[] wanted = [
            WgpuFeatureName.DepthClipControl,
            WgpuFeatureName.Depth32FloatStencil8,
            WgpuFeatureName.TextureCompressionBc,
            WgpuFeatureName.TextureCompressionEtc2,
            WgpuFeatureName.TextureCompressionAstc,
            WgpuFeatureName.IndirectFirstInstance,
            WgpuFeatureName.Rg11B10UfloatRenderable,
            WgpuFeatureName.Float32Filterable,
            WgpuFeatureName.TimestampQuery
        ];

        return [.. wanted.Where(available)];
    }
}
