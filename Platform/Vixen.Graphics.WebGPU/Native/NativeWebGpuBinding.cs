// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Vixen.Core;

// Aliased rather than relied on, and this is a trap worth naming. A name used inside
// Vixen.Graphics.WebGPU.Native is looked up through the *enclosing namespaces* before any using
// directive — so an unqualified `BlendFactor` here resolves to Vixen.Graphics.BlendFactor, not
// Silk's, and `(BlendFactor)value` still compiles because both are enums. The result would be a
// cast that changes nothing and a pipeline built from the RHI's numbering. Vixen.Graphics.Vulkan
// aliases its clashes as `Vk…` for the same reason.
using NativeAddressMode = Silk.NET.WebGPU.AddressMode;
using NativeBufferUsage = Silk.NET.WebGPU.BufferUsage;
using NativeCompareFunction = Silk.NET.WebGPU.CompareFunction;
using NativeFilterMode = Silk.NET.WebGPU.FilterMode;
using NativePresentMode = Silk.NET.WebGPU.PresentMode;
using NativeShaderStage = Silk.NET.WebGPU.ShaderStage;
using NativeTextureDimension = Silk.NET.WebGPU.TextureDimension;
using NativeTextureUsage = Silk.NET.WebGPU.TextureUsage;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;
using WgpuTexture = Silk.NET.WebGPU.Texture;

namespace Vixen.Graphics.WebGPU.Native;

/// <summary>What to create a native WebGPU binding as.</summary>
public readonly record struct NativeWebGpuOptions() {
    /// <summary>A surface a swapchain will be created for.</summary>
    /// <remarks>
    ///     Which adapter can present is a property of a <em>surface</em>, so a device meant for a
    ///     window has to be told about the window before the adapter is chosen — the same ordering
    ///     Vulkan imposes. <see cref="SurfaceHandle.None" /> creates an offscreen device.
    /// </remarks>
    public SurfaceHandle Surface { get; init; } = SurfaceHandle.None;

    /// <summary>Which adapter to prefer.</summary>
    public WgpuPowerPreference PowerPreference { get; init; } = WgpuPowerPreference.HighPerformance;

    /// <summary>Where the backend logs.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>WebGPU through Dawn or wgpu-native.</summary>
/// <remarks>
///     <para>
///         One of the two surface implementations <c>docs/plan/05</c> asks for — the desktop one, and
///         therefore the one the tests, the golden-image suite and a developer's machine can reach.
///         The other is <c>Vixen.Graphics.WebGPU.Browser</c>.
///     </para>
///     <para>
///         <b>This class decides nothing.</b> It marshals what it is given and returns what the
///         implementation returned; every choice about what to create was made above
///         <see cref="IWebGpuBinding" />, in code the browser surface runs unchanged.
///     </para>
///     <para>
///         The callbacks are <c>[UnmanagedCallersOnly]</c> static functions writing through a
///         <c>userdata</c> pointer to a stack local, rather than delegates. Two reasons, and the
///         second is the load-bearing one: it keeps the AOT analyser quiet (R11), and a delegate
///         would have needed something to hold it alive for exactly as long as the implementation
///         might call it, which for <c>uncapturedError</c> is the life of the device.
///     </para>
/// </remarks>
public sealed unsafe partial class NativeWebGpuBinding : IWebGpuBinding {
    readonly Silk.NET.WebGPU.WebGPU api;
    readonly Instance* instance;
    readonly Adapter* adapter;
    readonly Device* device;
    readonly Queue* queue;
    readonly Surface* surface;
    readonly HashSet<WgpuFeatureName> features;

    /// <summary>
    ///     <c>wgpuDevicePoll</c>, or null on an implementation that does not have it.
    /// </summary>
    /// <remarks>
    ///     A function pointer rather than a Silk method, because it is in <c>wgpu.h</c> rather than
    ///     <c>webgpu.h</c> and Silk.NET binds only the latter. See <see cref="WebGpuLoader.DevicePoll" />
    ///     for why it is needed at all.
    /// </remarks>
    readonly delegate* unmanaged[Cdecl]<Device*, uint, void*, uint> devicePoll;

    Texture* acquired;
    bool disposed;

    NativeWebGpuBinding(
        Silk.NET.WebGPU.WebGPU api,
        Instance* instance,
        Adapter* adapter,
        Device* device,
        Surface* surface,
        HashSet<WgpuFeatureName> features,
        WebGpuLimits limits,
        WebGpuAdapterInfo info
    ) {
        this.api = api;
        this.instance = instance;
        this.adapter = adapter;
        this.device = device;
        this.surface = surface;
        this.features = features;

        queue = api.DeviceGetQueue(device);
        devicePoll = (delegate* unmanaged[Cdecl]<Device*, uint, void*, uint>)WebGpuLoader.DevicePoll;
        Limits = limits;
        AdapterInfo = info;

        PreferredSurfaceFormat = surface is null
            ? WgpuTextureFormat.Undefined
            : (WgpuTextureFormat)api.SurfaceGetPreferredFormat(surface, adapter);
    }

    /// <inheritdoc />
    public WebGpuAdapterInfo AdapterInfo { get; }

    /// <inheritdoc />
    public WebGpuLimits Limits { get; }

    /// <inheritdoc />
    public bool HasSurface => surface is not null;

    /// <inheritdoc />
    public WgpuTextureFormat PreferredSurfaceFormat { get; }

    /// <summary>Where the implementation was loaded from, for the boot log.</summary>
    public static string? LibraryPath => WebGpuLoader.ResolvedPath;

    /// <inheritdoc />
    public bool HasFeature(WgpuFeatureName feature) => features.Contains(feature);

    /// <summary>Creates a binding, reporting failure rather than throwing.</summary>
    /// <param name="options">What to create.</param>
    /// <param name="binding">The binding, when it was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    /// <remarks>
    ///     Reports rather than throws because the ordinary outcome on a machine nobody installed
    ///     Dawn on is failure, and that is backend selection working rather than an error — see
    ///     <see cref="WebGpuLoader" />.
    /// </remarks>
    public static bool TryCreate(
        NativeWebGpuOptions options,
        [NotNullWhen(true)] out NativeWebGpuBinding? binding,
        [NotNullWhen(false)] out string? reason
    ) {
        binding = null;

        if (!WebGpuLoader.TryLoad(out var api, out reason)) {
            return false;
        }

        var descriptor = new InstanceDescriptor();
        var instance = api.CreateInstance(&descriptor);

        if (instance is null) {
            reason = "wgpuCreateInstance returned nothing. The library loaded but would not "
                + "initialise, which usually means no usable graphics backend underneath it.";

            return false;
        }

        Surface* surface = null;

        if (options.Surface.CanPresent
            && !NativeWebGpuSurfaces.TryCreate(api, instance, options.Surface, out surface, out reason)) {
            api.InstanceRelease(instance);
            return false;
        }

        var adapter = RequestAdapter(api, instance, surface, options.PowerPreference, out var failure);

        if (adapter is null) {
            Release(api, instance, surface);
            reason = failure ?? "No WebGPU adapter.";
            return false;
        }

        var supported = ReadSupportedLimits(api, adapter);
        var limits = Describe(supported);
        var available = ReadFeatures(api, adapter);
        var wanted = WebGpuCapabilities.Wanted(available.Contains);
        var device = RequestDevice(api, adapter, supported, wanted, out failure);

        if (device is null) {
            api.AdapterRelease(adapter);
            Release(api, instance, surface);
            reason = failure ?? "wgpuAdapterRequestDevice returned nothing.";
            return false;
        }

        if (options.Logger is { } logger) {
            Errors.Attach(api, device, logger);
        }

        binding = new(api, instance, adapter, device, surface, [.. wanted], limits, ReadInfo(api, adapter));
        reason = null;

        return true;
    }

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateBuffer(in WgpuBufferDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);

        try {
            var native = new BufferDescriptor {
                Label = (byte*)label,
                Size = (ulong)descriptor.Size,
                Usage = (NativeBufferUsage)descriptor.Usage
            };

            return Wrap(api.DeviceCreateBuffer(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateTexture(in WgpuTextureDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);

        try {
            var native = new TextureDescriptor {
                Label = (byte*)label,
                Usage = (NativeTextureUsage)descriptor.Usage,
                Dimension = (NativeTextureDimension)descriptor.Dimension,
                Size = new(
                    (uint)descriptor.Width,
                    (uint)descriptor.Height,
                    (uint)Math.Max(1, descriptor.DepthOrArrayLayers)
                ),
                Format = (TextureFormat)descriptor.Format,
                MipLevelCount = (uint)Math.Max(1, descriptor.MipLevelCount),
                SampleCount = (uint)Math.Max(1, descriptor.SampleCount)
            };

            return Wrap(api.DeviceCreateTexture(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateTextureView(WebGpuObject texture, in WgpuTextureViewDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);

        try {
            var native = new TextureViewDescriptor {
                Label = (byte*)label,
                Format = (TextureFormat)descriptor.Format,
                Dimension = (TextureViewDimension)descriptor.Dimension,
                BaseMipLevel = (uint)descriptor.BaseMipLevel,
                MipLevelCount = (uint)Math.Max(1, descriptor.MipLevelCount),
                BaseArrayLayer = (uint)descriptor.BaseArrayLayer,
                ArrayLayerCount = (uint)Math.Max(1, descriptor.ArrayLayerCount),
                Aspect = (TextureAspect)descriptor.Aspect
            };

            return Wrap(api.TextureCreateView((WgpuTexture*)texture.Value, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateSampler(in WgpuSamplerDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);

        try {
            var native = new SamplerDescriptor {
                Label = (byte*)label,
                AddressModeU = (NativeAddressMode)descriptor.AddressU,
                AddressModeV = (NativeAddressMode)descriptor.AddressV,
                AddressModeW = (NativeAddressMode)descriptor.AddressW,
                MagFilter = (NativeFilterMode)descriptor.MagFilter,
                MinFilter = (NativeFilterMode)descriptor.MinFilter,
                MipmapFilter = (MipmapFilterMode)descriptor.MipmapFilter,
                LodMinClamp = descriptor.LodMinClamp,
                LodMaxClamp = descriptor.LodMaxClamp,
                Compare = (NativeCompareFunction)descriptor.Compare,
                MaxAnisotropy = Math.Max((ushort)1, descriptor.MaxAnisotropy)
            };

            return Wrap(api.DeviceCreateSampler(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateShaderModule(in WgpuShaderModuleDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);

        try {
            if (descriptor.Source == WgpuShaderSource.SpirV) {
                fixed (byte* code = descriptor.Code) {
                    var spirv = new ShaderModuleSPIRVDescriptor {
                        Chain = new() { SType = SType.ShaderModuleSpirvDescriptor },
                        CodeSize = (uint)(descriptor.Code.Length / sizeof(uint)),
                        Code = (uint*)code
                    };

                    var native = new ShaderModuleDescriptor { NextInChain = &spirv.Chain, Label = (byte*)label };
                    return Wrap(api.DeviceCreateShaderModule(device, &native));
                }
            }

            // WGSL is a null-terminated UTF-8 string, and the RHI hands bytecode in as a span that
            // need not be. Copying through SilkMarshal gets the terminator rather than trusting the
            // caller to have put one there.
            var source = SilkMarshal.StringToPtr(
                System.Text.Encoding.UTF8.GetString(descriptor.Code),
                NativeStringEncoding.UTF8
            );

            try {
                var wgsl = new ShaderModuleWGSLDescriptor {
                    Chain = new() { SType = SType.ShaderModuleWgslDescriptor },
                    Code = (byte*)source
                };

                var native = new ShaderModuleDescriptor { NextInChain = &wgsl.Chain, Label = (byte*)label };
                return Wrap(api.DeviceCreateShaderModule(device, &native));
            } finally {
                SilkMarshal.Free(source);
            }
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateBindGroupLayout(in WgpuBindGroupLayoutDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var entries = stackalloc BindGroupLayoutEntry[Math.Max(1, descriptor.Entries.Length)];

        try {
            for (var index = 0; index < descriptor.Entries.Length; index++) {
                var source = descriptor.Entries[index];

                entries[index] = new() {
                    Binding = source.Binding,
                    Visibility = (NativeShaderStage)source.Visibility,
                    Buffer = new() {
                        Type = (BufferBindingType)source.BufferType,
                        HasDynamicOffset = source.HasDynamicOffset
                    },
                    Sampler = new() { Type = (SamplerBindingType)source.SamplerType },
                    Texture = new() {
                        SampleType = (TextureSampleType)source.TextureSampleType,
                        ViewDimension = source.TextureSampleType == WgpuTextureSampleType.Undefined
                            ? TextureViewDimension.DimensionUndefined
                            : (TextureViewDimension)source.TextureViewDimension,
                        Multisampled = source.Multisampled
                    },
                    StorageTexture = new() {
                        Access = (StorageTextureAccess)source.StorageAccess,
                        Format = (TextureFormat)source.StorageFormat,
                        ViewDimension = source.StorageAccess == WgpuStorageTextureAccess.Undefined
                            ? TextureViewDimension.DimensionUndefined
                            : (TextureViewDimension)source.TextureViewDimension
                    }
                };
            }

            var native = new BindGroupLayoutDescriptor {
                Label = (byte*)label,
                EntryCount = (nuint)descriptor.Entries.Length,
                Entries = entries
            };

            return Wrap(api.DeviceCreateBindGroupLayout(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreatePipelineLayout(in WgpuPipelineLayoutDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var groups = stackalloc BindGroupLayout*[Math.Max(1, descriptor.BindGroupLayouts.Length)];

        try {
            for (var index = 0; index < descriptor.BindGroupLayouts.Length; index++) {
                groups[index] = (BindGroupLayout*)descriptor.BindGroupLayouts[index].Value;
            }

            var native = new PipelineLayoutDescriptor {
                Label = (byte*)label,
                BindGroupLayoutCount = (nuint)descriptor.BindGroupLayouts.Length,
                BindGroupLayouts = groups
            };

            return Wrap(api.DeviceCreatePipelineLayout(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateBindGroup(in WgpuBindGroupDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var entries = stackalloc BindGroupEntry[Math.Max(1, descriptor.Entries.Length)];

        try {
            for (var index = 0; index < descriptor.Entries.Length; index++) {
                var source = descriptor.Entries[index];

                entries[index] = new() {
                    Binding = source.Binding,
                    Buffer = (WgpuBuffer*)source.Buffer.Value,
                    Offset = (ulong)source.Offset,
                    Size = (ulong)source.Size,
                    Sampler = (Sampler*)source.Sampler.Value,
                    TextureView = (TextureView*)source.TextureView.Value
                };
            }

            var native = new BindGroupDescriptor {
                Label = (byte*)label,
                Layout = (BindGroupLayout*)descriptor.Layout.Value,
                EntryCount = (nuint)descriptor.Entries.Length,
                Entries = entries
            };

            return Wrap(api.DeviceCreateBindGroup(device, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public void Release(WebGpuObjectKind kind, WebGpuObject handle) {
        if (!handle.IsValid) {
            return;
        }

        var pointer = (void*)handle.Value;

        switch (kind) {
            case WebGpuObjectKind.Buffer:
                api.BufferRelease((WgpuBuffer*)pointer);
                break;
            case WebGpuObjectKind.Texture:
                api.TextureRelease((WgpuTexture*)pointer);
                break;
            case WebGpuObjectKind.TextureView:
                api.TextureViewRelease((TextureView*)pointer);
                break;
            case WebGpuObjectKind.Sampler:
                api.SamplerRelease((Sampler*)pointer);
                break;
            case WebGpuObjectKind.ShaderModule:
                api.ShaderModuleRelease((ShaderModule*)pointer);
                break;
            case WebGpuObjectKind.BindGroupLayout:
                api.BindGroupLayoutRelease((BindGroupLayout*)pointer);
                break;
            case WebGpuObjectKind.PipelineLayout:
                api.PipelineLayoutRelease((PipelineLayout*)pointer);
                break;
            case WebGpuObjectKind.BindGroup:
                api.BindGroupRelease((BindGroup*)pointer);
                break;
            case WebGpuObjectKind.RenderPipeline:
                api.RenderPipelineRelease((RenderPipeline*)pointer);
                break;
            case WebGpuObjectKind.ComputePipeline:
                api.ComputePipelineRelease((ComputePipeline*)pointer);
                break;
            case WebGpuObjectKind.CommandBuffer:
                api.CommandBufferRelease((CommandBuffer*)pointer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown WebGPU object kind.");
        }
    }

    // ── Queue ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void WriteBuffer(WebGpuObject buffer, long offset, ReadOnlySpan<byte> data) {
        fixed (byte* source = data) {
            api.QueueWriteBuffer(queue, (WgpuBuffer*)buffer.Value, (ulong)offset, source, (nuint)data.Length);
        }
    }

    /// <inheritdoc />
    public bool ReadBuffer(WebGpuObject buffer, long offset, Span<byte> destination) {
        var target = (WgpuBuffer*)buffer.Value;
        var status = BufferMapAsyncStatus.Unknown;

        api.BufferMapAsync(
            target,
            MapMode.Read,
            (nuint)offset,
            (nuint)destination.Length,
            (PfnBufferMapCallback)(nint)(delegate* unmanaged[Cdecl]<BufferMapAsyncStatus, void*, void>)&OnMapped,
            &status
        );

        // The callback fires from inside the poll, on this thread. Bounded rather than a bare loop:
        // an implementation that never completes the map would otherwise hang the process with no
        // clue why, and a readback that has not finished after this many turns is a bug somewhere
        // and not a slow GPU.
        for (var turn = 0; turn < 1024 && status == BufferMapAsyncStatus.Unknown; turn++) {
            Poll(true);
        }

        if (status != BufferMapAsyncStatus.Success) {
            return false;
        }

        var mapped = api.BufferGetConstMappedRange(target, (nuint)offset, (nuint)destination.Length);

        if (mapped is null) {
            api.BufferUnmap(target);
            return false;
        }

        new ReadOnlySpan<byte>(mapped, destination.Length).CopyTo(destination);
        api.BufferUnmap(target);

        return true;
    }

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<WebGpuObject> commands) {
        var buffers = stackalloc CommandBuffer*[Math.Max(1, commands.Length)];

        for (var index = 0; index < commands.Length; index++) {
            buffers[index] = (CommandBuffer*)commands[index].Value;
        }

        api.QueueSubmit(queue, (nuint)commands.Length, buffers);
    }

    /// <inheritdoc />
    public bool WaitIdle() {
        // wgpu-native's poll takes a "wait" flag and blocks until the queue is empty, which is
        // exactly what this method promises and is a great deal better than spinning on a callback.
        if (devicePoll is not null) {
            devicePoll(device, 1, null);
            return true;
        }

        var status = QueueWorkDoneStatus.Unknown;

        api.QueueOnSubmittedWorkDone(
            queue,
            (PfnQueueWorkDoneCallback)(nint)(delegate* unmanaged[Cdecl]<QueueWorkDoneStatus, void*, void>)&OnWorkDone,
            &status
        );

        for (var turn = 0; turn < 4096 && status == QueueWorkDoneStatus.Unknown; turn++) {
            api.InstanceProcessEvents(instance);
        }


        return status != QueueWorkDoneStatus.Unknown;
    }

    /// <inheritdoc />
    public void Tick() => Poll(false);

    /// <summary>Turns the crank, by whichever means this implementation offers.</summary>
    /// <param name="wait">Whether to block until the queue is empty.</param>
    /// <remarks>
    ///     <b>Only one of the two, never both.</b> wgpu-native 0.19 declares
    ///     <c>wgpuInstanceProcessEvents</c> and does not implement it — calling it panics the Rust
    ///     runtime with <c>not implemented</c>, which aborts the process rather than raising anything
    ///     .NET can catch. So the extension is used where it exists and the specification's own entry
    ///     point only where it does not.
    /// </remarks>
    void Poll(bool wait) {
        if (devicePoll is not null) {
            devicePoll(device, wait ? 1u : 0u, null);
            return;
        }

        api.InstanceProcessEvents(instance);
    }

    // ── Surface ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void ConfigureSurface(in WgpuSurfaceConfiguration configuration) {
        if (surface is null) {
            return;
        }

        var native = new SurfaceConfiguration {
            Device = device,
            Format = (TextureFormat)configuration.Format,
            Usage = (NativeTextureUsage)configuration.Usage,
            AlphaMode = (CompositeAlphaMode)configuration.AlphaMode,
            Width = (uint)Math.Max(1, configuration.Width),
            Height = (uint)Math.Max(1, configuration.Height),
            PresentMode = (NativePresentMode)configuration.PresentMode
        };

        api.SurfaceConfigure(surface, &native);
    }

    /// <inheritdoc />
    public WgpuSurfaceStatus AcquireSurfaceTexture(out WebGpuObject texture) {
        texture = WebGpuObject.Null;

        if (surface is null) {
            return WgpuSurfaceStatus.Lost;
        }

        // An image acquired and never presented — a frame the renderer skipped because the swapchain
        // was out of date. Its reference is still ours, and one leaked texture per skipped frame is
        // a leak a window resize produces by the hundred.
        if (acquired is not null) {
            api.TextureRelease(acquired);
            acquired = null;
        }

        SurfaceTexture result;
        api.SurfaceGetCurrentTexture(surface, &result);

        if (result.Status != SurfaceGetCurrentTextureStatus.Success || result.Texture is null) {
            return (WgpuSurfaceStatus)result.Status;
        }

        acquired = result.Texture;
        texture = new((ulong)result.Texture);

        // Suboptimal is folded into Success rather than reported: the RHI's Suboptimal means "the
        // frame is presentable, rebuild afterwards", which is a decision the swapchain makes from
        // its own size — and wgpu-native sets this bit on frames a resize is already handling.
        return WgpuSurfaceStatus.Success;
    }

    /// <inheritdoc />
    public void PresentSurface() {
        if (surface is null) {
            return;
        }

        api.SurfacePresent(surface);

        // The texture a surface hands out is invalidated by the present, and the reference the
        // implementation gave us is ours to drop. Not dropping it leaks one texture per frame, which
        // takes about a minute of running to become obvious and rather longer to attribute.
        if (acquired is not null) {
            api.TextureRelease(acquired);
            acquired = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (acquired is not null) {
            api.TextureRelease(acquired);
            acquired = null;
        }

        api.QueueRelease(queue);
        api.DeviceRelease(device);
        api.AdapterRelease(adapter);
        Release(api, instance, surface);
    }

    static WebGpuObject Wrap(void* pointer) {
        if (pointer is null) {
            throw new InvalidOperationException(
                "A WebGPU object could not be created. Dawn and wgpu-native report why through the "
                + "uncaptured-error callback rather than through a return value — pass a Logger to "
                + "NativeWebGpuOptions to see it."
            );
        }

        return new((ulong)pointer);
    }

    static void Release(Silk.NET.WebGPU.WebGPU api, Instance* instance, Surface* surface) {
        if (surface is not null) {
            api.SurfaceRelease(surface);
        }

        api.InstanceRelease(instance);
    }

    static Adapter* RequestAdapter(
        Silk.NET.WebGPU.WebGPU api,
        Instance* instance,
        Surface* surface,
        WgpuPowerPreference preference,
        out string? reason
    ) {
        var options = new RequestAdapterOptions {
            CompatibleSurface = surface,
            PowerPreference = (PowerPreference)preference
        };

        var result = default(AdapterRequest);

        api.InstanceRequestAdapter(
            instance,
            &options,
            (PfnRequestAdapterCallback)(nint)
            (delegate* unmanaged[Cdecl]<RequestAdapterStatus, Adapter*, byte*, void*, void>)&OnAdapter,
            &result
        );

        // wgpu-native answers before returning; Dawn needs its instance pumped. Guarded because
        // pumping wgpu-native is a panic rather than a no-op — see Poll.
        for (var turn = 0; turn < 1024 && !result.Completed && !WebGpuLoader.IsWgpuNative; turn++) {
            api.InstanceProcessEvents(instance);
        }

        if (!result.Completed) {
            reason = "wgpuInstanceRequestAdapter never called back. The implementation loaded but did "
                + "not answer, which on wgpu-native means no adapter matched the surface.";

            return null;
        }

        if (result.Status != RequestAdapterStatus.Success || result.Adapter is null) {
            reason = $"No WebGPU adapter: {result.Status}.";
            return null;
        }

        reason = null;
        return result.Adapter;
    }

    static Device* RequestDevice(
        Silk.NET.WebGPU.WebGPU api,
        Adapter* adapter,
        Limits supported,
        WgpuFeatureName[] wanted,
        out string? reason
    ) {
        // The adapter's own limits, handed straight back.
        //
        // Asked for explicitly rather than left to default, because a device created with no
        // required limits reports the specification's guaranteed floor whatever the hardware can do
        // — an 8192 texture limit on a card that manages 16384 — and every one of those numbers
        // reaches a renderer through GraphicsDeviceFeatures.
        //
        // Passed WHOLE rather than field by field, and that is not laziness. Most of WGPULimits is
        // maximums, where asking for less is always legal; two of them — the buffer offset
        // alignments — run the other way, where a *smaller* value is the stronger request and zero
        // is not a weak request but an invalid one. Filling in the interesting fields and leaving
        // the rest zeroed therefore fails device creation outright, with `Error` and not one word
        // about which limit, which is precisely what it did.
        var required = new RequiredLimits { Limits = supported };
        var features = stackalloc FeatureName[Math.Max(1, wanted.Length)];

        for (var index = 0; index < wanted.Length; index++) {
            features[index] = (FeatureName)wanted[index];
        }

        var label = SilkMarshal.StringToPtr("Vixen", NativeStringEncoding.UTF8);

        try {
            var descriptor = new DeviceDescriptor {
                Label = (byte*)label,
                RequiredFeatureCount = (nuint)wanted.Length,
                RequiredFeatures = features,
                RequiredLimits = &required
            };

            var result = default(DeviceRequest);

            api.AdapterRequestDevice(
                adapter,
                &descriptor,
                (PfnRequestDeviceCallback)(nint)
                (delegate* unmanaged[Cdecl]<RequestDeviceStatus, Device*, byte*, void*, void>)&OnDevice,
                &result
            );

            // No instance to pump here — wgpu-native calls this back before returning, and Dawn
            // completes it on the instance the adapter came from, which the caller ticks next frame.
            if (result.Status != RequestDeviceStatus.Success || result.Device is null) {
                reason = $"wgpuAdapterRequestDevice failed: {result.Status}.";
                return null;
            }

            reason = null;
            return result.Device;
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <summary>What the adapter says it can do, in WebGPU's own struct.</summary>
    /// <remarks>
    ///     Kept as the C struct rather than converted straight into <see cref="WebGpuLimits" />,
    ///     because it is handed back to <see cref="RequestDevice" /> unchanged — see the note there
    ///     for why that matters.
    /// </remarks>
    static Limits ReadSupportedLimits(Silk.NET.WebGPU.WebGPU api, Adapter* adapter) {
        SupportedLimits supported = default;

        if (api.AdapterGetLimits(adapter, &supported)) {
            return supported.Limits;
        }

        // The specification's floor, if the adapter will not say. Every field of it is a legal
        // request, which is the property that matters here: a zeroed struct is not.
        var floor = WebGpuLimits.Guaranteed;

        return new() {
            MaxTextureDimension1D = (uint)floor.MaxTextureDimension2D,
            MaxTextureDimension2D = (uint)floor.MaxTextureDimension2D,
            MaxTextureDimension3D = (uint)floor.MaxTextureDimension3D,
            MaxTextureArrayLayers = (uint)floor.MaxTextureArrayLayers,
            MaxBindGroups = (uint)floor.MaxBindGroups,
            MaxUniformBufferBindingSize = (ulong)floor.MaxUniformBufferBindingSize,
            MinUniformBufferOffsetAlignment = (uint)floor.MinUniformBufferOffsetAlignment,
            MinStorageBufferOffsetAlignment = (uint)floor.MinUniformBufferOffsetAlignment,
            MaxVertexBuffers = (uint)floor.MaxVertexBuffers,
            MaxBufferSize = (ulong)floor.MaxBufferSize,
            MaxVertexAttributes = (uint)floor.MaxVertexAttributes,
            MaxColorAttachments = (uint)floor.MaxColorAttachments,
            MaxDynamicUniformBuffersPerPipelineLayout = (uint)floor.MaxDynamicUniformBuffersPerPipelineLayout,
            MaxComputeWorkgroupSizeX = (uint)floor.MaxComputeWorkgroupSizeX,
            MaxComputeWorkgroupSizeY = (uint)floor.MaxComputeWorkgroupSizeY,
            MaxComputeWorkgroupSizeZ = (uint)floor.MaxComputeWorkgroupSizeZ
        };
    }

    /// <summary>The subset of them the RHI reports.</summary>
    static WebGpuLimits Describe(in Limits limits) => new() {
        MaxTextureDimension2D = (int)limits.MaxTextureDimension2D,
        MaxTextureDimension3D = (int)limits.MaxTextureDimension3D,
        MaxTextureArrayLayers = (int)limits.MaxTextureArrayLayers,
        MaxBindGroups = (int)limits.MaxBindGroups,
        MaxUniformBufferBindingSize = (long)limits.MaxUniformBufferBindingSize,
        MinUniformBufferOffsetAlignment = (int)limits.MinUniformBufferOffsetAlignment,
        MaxVertexBuffers = (int)limits.MaxVertexBuffers,
        MaxBufferSize = (long)limits.MaxBufferSize,
        MaxVertexAttributes = (int)limits.MaxVertexAttributes,
        MaxColorAttachments = (int)limits.MaxColorAttachments,
        MaxDynamicUniformBuffersPerPipelineLayout = (int)limits.MaxDynamicUniformBuffersPerPipelineLayout,
        MaxComputeWorkgroupSizeX = (int)limits.MaxComputeWorkgroupSizeX,
        MaxComputeWorkgroupSizeY = (int)limits.MaxComputeWorkgroupSizeY,
        MaxComputeWorkgroupSizeZ = (int)limits.MaxComputeWorkgroupSizeZ
    };

    static HashSet<WgpuFeatureName> ReadFeatures(Silk.NET.WebGPU.WebGPU api, Adapter* adapter) {
        var count = (int)api.AdapterEnumerateFeatures(adapter, null);

        if (count <= 0) {
            return [];
        }

        var names = stackalloc FeatureName[count];
        api.AdapterEnumerateFeatures(adapter, names);

        var found = new HashSet<WgpuFeatureName>(count);

        for (var index = 0; index < count; index++) {
            found.Add((WgpuFeatureName)names[index]);
        }

        return found;
    }

    static WebGpuAdapterInfo ReadInfo(Silk.NET.WebGPU.WebGPU api, Adapter* adapter) {
        AdapterProperties properties = default;
        api.AdapterGetProperties(adapter, &properties);

        var name = SilkMarshal.PtrToString((nint)properties.Name, NativeStringEncoding.UTF8);
        var driver = SilkMarshal.PtrToString((nint)properties.DriverDescription, NativeStringEncoding.UTF8);

        return new(
            string.IsNullOrEmpty(name) ? "WebGPU adapter" : name,
            (WgpuAdapterType)properties.AdapterType,
            string.IsNullOrEmpty(driver) ? properties.BackendType.ToString() : driver
        );
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnAdapter(RequestAdapterStatus status, Adapter* adapter, byte* message, void* userdata) {
        var result = (AdapterRequest*)userdata;
        result->Status = status;
        result->Adapter = adapter;
        result->Completed = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDevice(RequestDeviceStatus status, Device* device, byte* message, void* userdata) {
        var result = (DeviceRequest*)userdata;
        result->Status = status;
        result->Device = device;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnMapped(BufferMapAsyncStatus status, void* userdata) =>
        Unsafe.Write(userdata, status);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnWorkDone(QueueWorkDoneStatus status, void* userdata) =>
        Unsafe.Write(userdata, status);

    struct AdapterRequest {
        public RequestAdapterStatus Status;

        public Adapter* Adapter;

        public bool Completed;
    }

    struct DeviceRequest {
        public RequestDeviceStatus Status;

        public Device* Device;
    }
}
