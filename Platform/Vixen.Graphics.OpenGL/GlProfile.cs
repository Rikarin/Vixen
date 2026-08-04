// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Which dialect of GL a device is driving.</summary>
/// <remarks>
///     <para>
///         One project, three profiles (<c>docs/plan/05</c> § <c>Vixen.Graphics.OpenGL</c>). They
///         share the state shadowing, the pipeline emulation and the barrier elision, and differ in
///         extension availability and shader dialect — which is a difference of a few decisions
///         rather than of a code path, and is why the profile is a value and not a subclass.
///     </para>
///     <para>
///         The ordering is deliberate and load-bearing: a profile is a <em>floor</em>, and
///         <see cref="GlProfiles" /> asks "at least" questions against it rather than switching on
///         each member. A capability added for a new profile that forgot to update every switch is
///         the classic way a backend claims something it cannot do.
///     </para>
/// </remarks>
public enum GlProfile : byte {
    /// <summary>WebGL2 — GLES 3.0 minus buffer mapping, minus compute, in a browser.</summary>
    /// <remarks>
    ///     The floor, and the one that decides the shape of everything above it. No compute, no
    ///     storage buffers, no <c>glMapBufferRange</c>, no <c>layout(binding = …)</c> on uniform
    ///     blocks or samplers, and no clip control — so every concession this backend makes is
    ///     visible here first.
    /// </remarks>
    WebGl2 = 0,

    /// <summary>GLES 3.0 — the Android floor (<c>docs/plan/05</c> § Android).</summary>
    /// <remarks>Adds buffer mapping over WebGL2 and nothing else that matters here.</remarks>
    Es30 = 1,

    /// <summary>GLES 3.2 — compute, storage buffers, <c>layout(binding)</c>, geometry, debug output.</summary>
    Es32 = 2,

    /// <summary>GL 4.5 core — the desktop profile.</summary>
    /// <remarks>
    ///     The only profile with <c>glClipControl</c>, which is the one extension that matters most:
    ///     it makes GL's clip space Vulkan's exactly — upper-left origin, depth <c>0..1</c> — and
    ///     turns the vertex-shader fixup every other profile needs into nothing at all. See
    ///     <see cref="GlslTranslator" />.
    /// </remarks>
    Core45 = 3
}

/// <summary>What each profile can do.</summary>
/// <remarks>
///     Asked as "at least this profile", never as a switch over every member. A backend that
///     switched would silently report a new profile's capabilities as absent — or, worse, as
///     whatever the default arm said.
/// </remarks>
public static class GlProfiles {
    /// <summary>Whether the profile has compute shaders and dispatches.</summary>
    /// <remarks>
    ///     False on WebGL2 and on GLES 3.0, and that absence cascades all the way up: clustered
    ///     light binning, GPU particles, GTAO and compute post-processing all need the
    ///     fullscreen-fragment fallback <c>docs/plan/06</c> requires every effect to declare.
    /// </remarks>
    public static bool HasCompute(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether the profile has shader storage buffers.</summary>
    public static bool HasStorageBuffers(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether the profile has storage (image load/store) textures.</summary>
    public static bool HasStorageTextures(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether <c>glClipControl</c> is available.</summary>
    /// <remarks>
    ///     GL 4.5 only. Everywhere else the vertex shader has to flip <c>y</c> and remap <c>z</c>
    ///     from <c>[0, 1]</c> into <c>[-1, 1]</c> itself — see <see cref="GlslTranslator" /> — which
    ///     is the single largest semantic difference between this backend and the reference one.
    /// </remarks>
    public static bool HasClipControl(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>Whether <c>layout(binding = …)</c> may appear on uniform blocks and samplers.</summary>
    /// <remarks>
    ///     GL 4.2 and GLES 3.1 introduced it. Below that the binding has to be assigned after the
    ///     link, by name, which is why <see cref="GlslTranslator" /> keeps the names it strips.
    /// </remarks>
    public static bool HasExplicitBindings(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether buffers may be mapped.</summary>
    /// <remarks>
    ///     WebGL2 has no <c>glMapBufferRange</c> at all — a readback there is
    ///     <c>glGetBufferSubData</c> on the JS side and a staging copy on ours.
    /// </remarks>
    public static bool HasBufferMapping(this GlProfile profile) => profile >= GlProfile.Es30;

    /// <summary>Whether indirect draws and dispatches are available.</summary>
    public static bool HasIndirect(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether <c>KHR_debug</c> groups and object labels are available.</summary>
    public static bool HasDebugOutput(this GlProfile profile) => profile >= GlProfile.Es32;

    /// <summary>Whether a draw may start at a non-zero instance.</summary>
    /// <remarks>
    ///     <c>glDrawElementsInstancedBaseInstance</c> is GL 4.2 and has no GLES equivalent at all.
    ///     The RHI takes a first-instance on every draw because Vulkan and D3D12 both do; this is
    ///     one of the two places the GL backend has to refuse something rather than emulate it, and
    ///     refusing loudly is better than silently drawing instance zero.
    /// </remarks>
    public static bool HasBaseInstance(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>Whether the profile has anisotropic sampling in core.</summary>
    public static bool HasAnisotropy(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>Whether sRGB encoding on write can be switched on and off.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>GL_FRAMEBUFFER_SRGB</c> is desktop GL, and <c>EXT_sRGB_write_control</c> everywhere
    ///         else — which means <c>glEnable</c> of it on a GLES or WebGL2 context is
    ///         <c>GL_INVALID_ENUM</c> rather than a no-op.
    ///     </para>
    ///     <para>
    ///         Its absence costs nothing, and it is worth saying why: without the switch, GLES
    ///         encodes for any attachment whose format is sRGB and does not for any other, which is
    ///         precisely what the RHI means by a format. The switch is the odd one out — a global
    ///         that has to be turned on for desktop GL to honour a per-attachment property it already
    ///         knows.
    ///     </para>
    /// </remarks>
    public static bool HasFramebufferSrgbControl(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>Whether wireframe fill is available.</summary>
    /// <remarks><c>glPolygonMode</c> does not exist in GLES at any version.</remarks>
    public static bool HasWireframe(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>Whether a sampler may clamp to a border colour.</summary>
    /// <remarks>
    ///     Core in GL 4.5 and an extension in GLES. It matters more than it sounds: a shadow sampler
    ///     that falls back to clamp-to-edge shadows everything outside the map with whatever the
    ///     edge texel happened to be, which looks like a rendering bug rather than a missing feature.
    /// </remarks>
    public static bool HasBorderClamp(this GlProfile profile) => profile >= GlProfile.Core45;

    /// <summary>The GLSL version directive the profile wants.</summary>
    public static string ShaderVersion(this GlProfile profile) => profile switch {
        GlProfile.Core45 => "#version 450 core",
        GlProfile.Es32 => "#version 320 es",
        _ => "#version 300 es"
    };

    /// <summary>What a device on this profile reports it can do.</summary>
    /// <param name="profile">The profile.</param>
    /// <remarks>
    ///     Built from <see cref="GraphicsDeviceFeatures.Minimum" /> and added to, never from a fresh
    ///     record: a capability nobody remembered to set comes back absent, so the failure mode of
    ///     forgetting a line is "the fallback path runs" rather than "the device lied".
    /// </remarks>
    public static GraphicsDeviceFeatures Features(this GlProfile profile) => GraphicsDeviceFeatures.Minimum with {
        HasCompute = profile.HasCompute(),
        HasGeometryShaders = profile >= GlProfile.Es32,
        HasTessellation = profile >= GlProfile.Es32,

        // Never. Mesh shaders are a vendor extension on GL and are not in any core profile.
        HasMeshShaders = false,

        // Never. `GL_ARB_bindless_texture` exists but is not core anywhere and is absent on every
        // GLES driver — so the non-bindless path is not a legacy concession here, it is the path.
        HasBindless = false,
        HasMultiDrawIndirect = profile >= GlProfile.Core45,

        // Never, at any profile this backend targets. `glMultiDrawElementsIndirectCount` is core in
        // 4.6 and an ARB extension before it, and Core45 is this backend's ceiling — see GlProfile.
        // So a compacted run is issued at its maximum length with the culled arguments zeroed, which
        // is what GpuDrawArguments does without the capability and will keep doing here.
        HasDrawIndirectCount = false,

        // Never. GL has no semaphores of any kind: its execution model is one implicit in-order
        // stream, which is exactly why this backend elides barriers rather than translating them.
        HasTimelineSemaphores = false,
        HasAsyncCompute = false,
        HasAsyncTransfer = false,
        HasSparseResources = false,

        // Never. Hardware ray tracing arrived with the explicit APIs and was never back-ported to
        // GL — no core profile or widely shipped extension has acceleration structures — so the
        // distance-field tracer is the path on this backend, permanently.
        HasRayTracing = false,
        HasFloat64 = profile >= GlProfile.Core45,
        HasSubgroupOperations = false,

        // GL renders into framebuffer objects, which are attachment sets built at bind time and
        // thrown away — there is no render-pass object to be without. Reporting the capability
        // Vulkan uses to mean "no VkRenderPass needed" is the honest answer.
        HasDynamicRendering = true,
        HasDepthClamp = profile >= GlProfile.Core45,
        HasWireframe = profile.HasWireframe(),
        HasAnisotropicFiltering = profile.HasAnisotropy(),
        HasIndependentBlend = profile >= GlProfile.Es32,
        HasPipelineStatistics = false,

        // A GLES or WebGL2 context is on a phone or in a browser; both share memory with the host.
        HasUnifiedMemory = profile <= GlProfile.Es32,
        MaxTextureSize = profile >= GlProfile.Core45 ? 16384 : 4096,
        MaxTextureArrayLayers = 256,
        MaxColourAttachments = 4,
        MaxVertexBuffers = 4,
        MaxDescriptorSets = 4,

        // GL has no push constants. They are emulated as plain uniforms in a reserved block, and
        // 128 bytes is what the RHI's floor asks for — see `GlPipelineLayout`.
        MaxPushConstantSize = 128,
        MaxAnisotropy = profile.HasAnisotropy() ? 16f : 1f,
        MaxComputeWorkgroupSize = (128, 128, 64),
        SupportedSampleCounts = profile >= GlProfile.Es32 ? 0b101 : 0b1
    };
}
