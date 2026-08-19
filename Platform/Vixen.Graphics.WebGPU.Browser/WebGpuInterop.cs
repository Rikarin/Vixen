// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Vixen.Graphics.WebGPU.Browser;

/// <summary>The calls across to <c>vixen-webgpu.js</c>.</summary>
/// <remarks>
///     <para>
///         Generated marshalling, not <c>eval</c>: <c>[JSImport]</c> emits a direct call through the
///         runtime's interop table, which is both faster and the only form that survives trimming and
///         ahead-of-time compilation — a browser build is published with both.
///     </para>
///     <para>
///         Objects cross as integers, because a browser-side WebGPU object is a slot in a JavaScript
///         array and there is nothing else it could sensibly be. Descriptors cross as a byte view
///         written by <see cref="WebGpuPacker" />, for the reason that class gives.
///     </para>
///     <para>
///         <b>Every method here is one interop crossing, and that is the cost this surface pays.</b>
///         A frame of a few thousand draws is a few thousand crossings, which is measurable and is
///         not free. The recorded command stream a layer up is a flat array of blittable structs
///         precisely so a bulk path — hand the frame over once, replay it in JavaScript — can be
///         added without disturbing anything above the binding. It is not here yet, and the README
///         says so.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class WebGpuInterop {
    /// <summary>What the module is called once imported.</summary>
    public const string ModuleName = "vixen-webgpu";

    /// <summary>Where it is fetched from when the caller does not say.</summary>
    /// <remarks>
    ///     ⚠ <c>../</c>, for the reason set out on <c>WebInterop.DefaultModuleUrl</c>:
    ///     <see cref="JSHost.ImportAsync" /> resolves against the runtime's module in
    ///     <c>_framework/</c>, and this file is a content file at the site root.
    /// </remarks>
    public const string DefaultModuleUrl = "../vixen-webgpu.js";

    /// <summary>Loads the module. Must complete before anything else here is called.</summary>
    /// <param name="url">Where the module is.</param>
    public static Task ImportAsync(string url) => JSHost.ImportAsync(ModuleName, url);

    // ── Bring-up ────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether this browser has WebGPU at all.</summary>
    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();

    /// <summary>Requests an adapter and a device, and configures the canvas if there is one.</summary>
    /// <param name="canvasSelector">A CSS selector for the canvas, or an empty string for
    /// offscreen.</param>
    /// <param name="powerPreference">"low-power", "high-performance", or an empty string.</param>
    /// <returns>An empty string on success, or why it failed.</returns>
    [JSImport("initialise", ModuleName)]
    public static partial Task<string> InitialiseAsync(string canvasSelector, string powerPreference);

    /// <summary>The device's limits, written into a caller-owned buffer.</summary>
    /// <param name="destination">Fourteen doubles, in <c>WebGpuLimits</c>'s declaration order.</param>
    [JSImport("readLimits", ModuleName)]
    public static partial void ReadLimits([JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    /// <summary>The names of the features the device was created with.</summary>
    [JSImport("readFeatures", ModuleName)]
    public static partial string[] ReadFeatures();

    /// <summary>What the browser will say about the adapter, which is not much.</summary>
    [JSImport("adapterName", ModuleName)]
    public static partial string AdapterName();

    /// <summary>Whether a canvas context was configured.</summary>
    [JSImport("hasSurface", ModuleName)]
    public static partial bool HasSurface();

    /// <summary>The format the canvas prefers, as a <c>webgpu.h</c> enum value.</summary>
    [JSImport("preferredFormat", ModuleName)]
    public static partial int PreferredFormat();

    /// <summary>Releases the device and everything still in the object table.</summary>
    [JSImport("shutdown", ModuleName)]
    public static partial void Shutdown();

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    [JSImport("createBuffer", ModuleName)]
    public static partial int CreateBuffer(double size, int usage, string label);

    [JSImport("createTexture", ModuleName)]
    public static partial int CreateTexture([JSMarshalAs<JSType.MemoryView>] Span<byte> descriptor, string label);

    [JSImport("createTextureView", ModuleName)]
    public static partial int CreateTextureView(
        int texture,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> descriptor,
        string label
    );

    [JSImport("createSampler", ModuleName)]
    public static partial int CreateSampler([JSMarshalAs<JSType.MemoryView>] Span<byte> descriptor, string label);

    [JSImport("createShaderModule", ModuleName)]
    public static partial int CreateShaderModule(string code, string label);

    [JSImport("createBindGroupLayout", ModuleName)]
    public static partial int CreateBindGroupLayout(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> entries,
        string label
    );

    [JSImport("createPipelineLayout", ModuleName)]
    public static partial int CreatePipelineLayout(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> groups,
        string label
    );

    [JSImport("createBindGroup", ModuleName)]
    public static partial int CreateBindGroup(
        int layout,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> entries,
        string label
    );

    [JSImport("createRenderPipeline", ModuleName)]
    public static partial int CreateRenderPipeline(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> descriptor,
        string vertexEntryPoint,
        string fragmentEntryPoint,
        string label
    );

    [JSImport("createComputePipeline", ModuleName)]
    public static partial int CreateComputePipeline(int layout, int module, string entryPoint, string label);

    [JSImport("release", ModuleName)]
    public static partial void Release(int handle);

    // ── Queue ───────────────────────────────────────────────────────────────────────────────

    [JSImport("writeBuffer", ModuleName)]
    public static partial void WriteBuffer(
        int buffer,
        double offset,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> data
    );

    [JSImport("submit", ModuleName)]
    public static partial void Submit(int commandBuffer);

    // ── Encoding ────────────────────────────────────────────────────────────────────────────

    [JSImport("createCommandEncoder", ModuleName)]
    public static partial int CreateCommandEncoder(string label);

    [JSImport("finishCommandEncoder", ModuleName)]
    public static partial int FinishCommandEncoder(int encoder, string label);

    [JSImport("copyBufferToBuffer", ModuleName)]
    public static partial void CopyBufferToBuffer(
        int encoder,
        int source,
        double sourceOffset,
        int destination,
        double destinationOffset,
        double size
    );

    /// <summary>Any of the three texture copies, told apart by <paramref name="kind" />.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="kind">0 buffer-to-texture, 1 texture-to-buffer, 2 texture-to-texture.</param>
    /// <param name="arguments">The packed copy.</param>
    [JSImport("copyTexture", ModuleName)]
    public static partial void CopyTexture(
        int encoder,
        int kind,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> arguments
    );

    /// <summary>A debug group or marker, on whichever encoder is named.</summary>
    /// <param name="target">The encoder or pass.</param>
    /// <param name="action">0 push, 1 pop, 2 marker.</param>
    /// <param name="name">The name, ignored for a pop.</param>
    [JSImport("debugGroup", ModuleName)]
    public static partial void DebugGroup(int target, int action, string name);

    // ── Passes ──────────────────────────────────────────────────────────────────────────────

    [JSImport("beginRenderPass", ModuleName)]
    public static partial int BeginRenderPass(
        int encoder,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> descriptor,
        string label
    );

    [JSImport("beginComputePass", ModuleName)]
    public static partial int BeginComputePass(int encoder, string label);

    /// <summary>Ends and releases a pass encoder of either kind.</summary>
    [JSImport("endPass", ModuleName)]
    public static partial void EndPass(int pass);

    [JSImport("setPipeline", ModuleName)]
    public static partial void SetPipeline(int pass, int pipeline);

    [JSImport("setBindGroup", ModuleName)]
    public static partial void SetBindGroup(
        int pass,
        int group,
        int bindGroup,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> dynamicOffsets
    );

    [JSImport("setVertexBuffer", ModuleName)]
    public static partial void SetVertexBuffer(int pass, int slot, int buffer, double offset, double size);

    [JSImport("setIndexBuffer", ModuleName)]
    public static partial void SetIndexBuffer(int pass, int buffer, int format, double offset, double size);

    [JSImport("setViewport", ModuleName)]
    public static partial void SetViewport(
        int pass,
        double x,
        double y,
        double width,
        double height,
        double minDepth,
        double maxDepth
    );

    [JSImport("setScissorRect", ModuleName)]
    public static partial void SetScissorRect(int pass, int x, int y, int width, int height);

    [JSImport("setBlendConstant", ModuleName)]
    public static partial void SetBlendConstant(int pass, double r, double g, double b, double a);

    [JSImport("setStencilReference", ModuleName)]
    public static partial void SetStencilReference(int pass, double reference);

    [JSImport("draw", ModuleName)]
    public static partial void Draw(
        int pass,
        double vertexCount,
        double instanceCount,
        double firstVertex,
        double firstInstance
    );

    [JSImport("drawIndexed", ModuleName)]
    public static partial void DrawIndexed(
        int pass,
        double indexCount,
        double instanceCount,
        double firstIndex,
        double baseVertex,
        double firstInstance
    );

    [JSImport("drawIndexedIndirect", ModuleName)]
    public static partial void DrawIndexedIndirect(int pass, int arguments, double offset);

    [JSImport("dispatch", ModuleName)]
    public static partial void Dispatch(int pass, double x, double y, double z);

    [JSImport("dispatchIndirect", ModuleName)]
    public static partial void DispatchIndirect(int pass, int arguments, double offset);

    // ── Surface ─────────────────────────────────────────────────────────────────────────────

    [JSImport("configureSurface", ModuleName)]
    public static partial void ConfigureSurface(int format, int usage, int width, int height, int alphaMode);

    /// <summary>Takes the canvas's texture for this frame.</summary>
    /// <returns>Its handle, or <c>0</c> if the context had none to give.</returns>
    [JSImport("acquireSurfaceTexture", ModuleName)]
    public static partial int AcquireSurfaceTexture();
}
