// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Collections;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics.WebGPU;

public sealed partial class WebGpuDevice {
    /// <inheritdoc />
    public BufferHandle CreateBuffer(in BufferDescription description) {
        description.Validate();

        if (description.Size > binding.Limits.MaxBufferSize) {
            throw new ArgumentException(
                $"Buffer '{description.Name}' asked for {description.Size} bytes, more than this device's "
                + $"{binding.Limits.MaxBufferSize}-byte limit."
            );
        }

        var handle = binding.CreateBuffer(WebGpuConversions.ToWebGpu(description));

        lock (gate) {
            return new(buffers.Add(new WebGpuBuffer(handle, description)));
        }
    }

    /// <inheritdoc />
    public TextureHandle CreateTexture(in TextureDescription description) {
        description.Validate();

        if (description.Width > Features.MaxTextureSize || description.Height > Features.MaxTextureSize) {
            throw new ArgumentException(
                $"Texture '{description.Name}' is {description.Width}×{description.Height}, larger than "
                + $"this device's {Features.MaxTextureSize} limit."
            );
        }

        if (!Features.SupportsSampleCount(description.SampleCount)) {
            throw new ArgumentException(
                $"Texture '{description.Name}' asked for {description.SampleCount} samples. WebGPU fixes "
                + "the set at one and four — there is nothing to query and nothing to enable."
            );
        }

        var handle = binding.CreateTexture(WebGpuConversions.ToWebGpu(description));

        lock (gate) {
            return new(textures.Add(new WebGpuTexture(handle, description, true)));
        }
    }

    /// <inheritdoc />
    public TextureViewHandle CreateTextureView(
        TextureHandle texture,
        PixelFormat format = PixelFormat.Undefined,
        int baseMipLevel = 0,
        int mipLevelCount = 0,
        int baseArrayLayer = 0,
        int arrayLayerCount = 0
    ) {
        WebGpuTexture target;

        lock (gate) {
            if (!textures.TryGet(texture.Value, out var found)) {
                throw new ArgumentException("The texture does not exist, or has been destroyed.", nameof(texture));
            }

            target = (WebGpuTexture)found;
        }

        var description = target.Description;
        var levels = description.EffectiveMipLevels;

        if (baseMipLevel < 0 || baseMipLevel >= levels) {
            throw new ArgumentOutOfRangeException(
                nameof(baseMipLevel),
                $"Texture '{description.Name}' has {levels} mip levels."
            );
        }

        if (baseArrayLayer < 0 || baseArrayLayer >= description.ArrayLayers) {
            throw new ArgumentOutOfRangeException(
                nameof(baseArrayLayer),
                $"Texture '{description.Name}' has {description.ArrayLayers} array layers."
            );
        }

        var viewFormat = format == PixelFormat.Undefined ? description.Format : format;
        var layers = arrayLayerCount > 0 ? arrayLayerCount : description.ArrayLayers - baseArrayLayer;

        var handle = binding.CreateTextureView(
            target.Handle,
            new(
                viewFormat.Require(description.Name),
                WebGpuConversions.ToViewDimension(description.Dimension, layers),
                baseMipLevel,
                mipLevelCount > 0 ? mipLevelCount : levels - baseMipLevel,
                baseArrayLayer,
                layers,
                viewFormat.SampledAspect(),
                description.Name
            )
        );

        lock (gate) {
            return new(views.Add(new WebGpuTextureView(handle, texture, viewFormat, true)));
        }
    }

    /// <inheritdoc />
    public SamplerHandle CreateSampler(in SamplerDescription description) {
        var handle = binding.CreateSampler(
            WebGpuConversions.ToWebGpu(description, Features.HasAnisotropicFiltering)
        );

        lock (gate) {
            return new(samplers.Add(new WebGpuSampler(handle, description)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         The bytecode is WGSL source or SPIR-V, and which one is decided by looking: a SPIR-V
    ///         module starts with the magic number <c>0x07230203</c> and nothing else does. That is
    ///         how <c>docs/plan/05</c>'s <c>ShaderFormat</c> would be carried if the RHI's
    ///         <see cref="IGraphicsDevice.CreateShader" /> took one, and sniffing four bytes is
    ///         better than guessing.
    ///     </para>
    ///     <para>
    ///         <b>A browser accepts only WGSL.</b> Dawn and wgpu-native accept both, so a SPIR-V
    ///         module compiles on the native surface and is refused on the browser one — by the
    ///         browser surface, with a message that says so, rather than by a browser console
    ///         message nobody is reading.
    ///     </para>
    /// </remarks>
    public ShaderHandle CreateShader(ShaderStage stage, ReadOnlySpan<byte> bytecode, string name = "") {
        if (bytecode.IsEmpty) {
            throw new ArgumentException($"Shader '{name}' has no bytecode.", nameof(bytecode));
        }

        var source = LooksLikeSpirV(bytecode) ? WgpuShaderSource.SpirV : WgpuShaderSource.Wgsl;
        var handle = binding.CreateShaderModule(new(source, bytecode.ToArray(), name));

        lock (gate) {
            return new(shaders.Add(new WebGpuShader(handle, stage, name)));
        }
    }

    /// <inheritdoc />
    public DescriptorSetLayoutHandle CreateDescriptorSetLayout(in DescriptorSetLayoutDescription description) {
        description.Validate();

        var entries = new WgpuBindGroupLayoutEntry[description.Bindings.Length];

        for (var index = 0; index < entries.Length; index++) {
            var source = description.Bindings[index];

            if (source.Count != 1) {
                throw new NotSupportedException(
                    $"Binding {source.Binding} in '{description.Name}' asks for {source.Count} elements. "
                    + "WebGPU has no binding arrays and no unbounded ones — which is what "
                    + "Features.HasBindless reports — so a texture array has to be a 2D array texture or "
                    + "a run of separate bindings."
                );
            }

            entries[index] = WebGpuConversions.ToWebGpu(source);
        }

        var handle = binding.CreateBindGroupLayout(new(entries, description.Name));

        lock (gate) {
            return new(setLayouts.Add(new WebGpuDescriptorSetLayout(handle, description)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Where the emulated push-constant block goes is decided here, and only here: it becomes the
    ///     bind group after the caller's own, so a layout with four sets on a device that allows four
    ///     bind groups has nowhere to put it. See <see cref="PushConstantRing" /> for why that is the
    ///     index and what a shader has to agree with.
    /// </remarks>
    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDescription description) {
        var sets = description.Sets ?? [];
        var wantsPushConstants = description.PushConstants is { Length: > 0 };
        var groups = new WebGpuObject[sets.Length + (wantsPushConstants ? 1 : 0)];

        lock (gate) {
            for (var index = 0; index < sets.Length; index++) {
                if (!setLayouts.TryGet(sets[index].Value, out var found)) {
                    throw new ArgumentException(
                        $"Set layout {index} of '{description.Name}' does not exist, or has been destroyed.",
                        nameof(description)
                    );
                }

                groups[index] = ((WebGpuDescriptorSetLayout)found).Handle;
            }
        }

        var pushGroup = -1;

        if (wantsPushConstants) {
            var declared = 0;

            foreach (var range in description.PushConstants!) {
                declared = Math.Max(declared, range.Offset + range.Size);
            }

            if (declared > WebGpuCapabilities.PushConstantSize) {
                throw new NotSupportedException(
                    $"Pipeline layout '{description.Name}' declares {declared} bytes of push constants. "
                    + $"WebGPU has none at all; the backend emulates {WebGpuCapabilities.PushConstantSize} "
                    + "bytes through a uniform buffer, which is what Features.MaxPushConstantSize reports."
                );
            }

            if (sets.Length >= binding.Limits.MaxBindGroups) {
                throw new NotSupportedException(
                    $"Pipeline layout '{description.Name}' uses all {sets.Length} of this device's bind "
                    + "groups and also declares push constants. WebGPU has no push constants, so they are "
                    + "emulated as one more bind group — and there is no group left. Fold the per-draw "
                    + "constants into the per-draw set, through a dynamic uniform offset."
                );
            }

            pushGroup = sets.Length;

            // Under the gate: the ring is created once, lazily, and the first pipeline layout to
            // declare push constants is as likely to arrive from a loading thread as from the main
            // one. Replay creates it under the same lock.
            lock (gate) {
                groups[pushGroup] = EnsurePushConstantRing().Layout;
            }
        }

        var handle = binding.CreatePipelineLayout(new(groups, description.Name));

        lock (gate) {
            return new(
                pipelineLayouts.Add(new WebGpuPipelineLayout(handle, pushGroup, sets.Length, description.Name))
            );
        }
    }

    /// <inheritdoc />
    public DescriptorSetHandle CreateDescriptorSet(DescriptorSetLayoutHandle layout, string name = "") {
        lock (gate) {
            if (!setLayouts.TryGet(layout.Value, out var found)) {
                throw new ArgumentException("The layout does not exist, or has been destroyed.", nameof(layout));
            }

            return new(
                descriptorSets.Add(new WebGpuDescriptorSet(layout, (WebGpuDescriptorSetLayout)found, name))
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>A WebGPU bind group is immutable and the RHI's descriptor set is not.</b> So this
    ///         records what was written, and rebuilds the bind group once every binding in the layout
    ///         has a resource — retiring the previous one, which is safe because retirement is
    ///         deferred and because WebGPU keeps a released object alive while work still names it.
    ///     </para>
    ///     <para>
    ///         Rebuilding rather than patching costs one object per update, and an update per frame
    ///         per material would be a poor way to use this API on any backend. The way to bind
    ///         per-draw data is a dynamic offset into one buffer, which is what
    ///         <see cref="DescriptorKind.DynamicUniformBuffer" /> exists for and what
    ///         <see cref="DescriptorAllocator" /> is shaped around.
    ///     </para>
    /// </remarks>
    public void UpdateDescriptorSet(DescriptorSetHandle descriptors, ReadOnlySpan<DescriptorWrite> writes) {
        WebGpuDescriptorSet set;

        lock (gate) {
            if (!descriptorSets.TryGet(descriptors.Value, out var found)) {
                throw new ArgumentException("The set does not exist, or has been destroyed.", nameof(descriptors));
            }

            set = (WebGpuDescriptorSet)found;
        }

        var bindings = set.ResolvedLayout.Bindings;

        foreach (var write in writes) {
            var slot = IndexOf(bindings, write.Binding);

            if (slot < 0) {
                throw new ArgumentException(
                    $"Descriptor set '{set.Name}' has no binding {write.Binding}. Its layout declares "
                    + $"{string.Join(", ", bindings.Select(item => item.Binding))}.",
                    nameof(writes)
                );
            }

            if (write.ArrayIndex != 0) {
                throw new NotSupportedException(
                    $"Binding {write.Binding} of '{set.Name}' was written at array index {write.ArrayIndex}. "
                    + "WebGPU has no binding arrays."
                );
            }

            set.Entries[slot] = Entry(set, bindings[slot], write);
            set.Filled[slot] = true;
        }

        if (!set.IsComplete) {
            return;
        }

        var handle = binding.CreateBindGroup(
            new(set.ResolvedLayout.Handle, [.. set.Entries], set.Name)
        );

        var previous = set.Handle;
        set.Handle = handle;

        if (previous.IsValid) {
            lock (gate) {
                Retire(() => binding.Release(WebGpuObjectKind.BindGroup, previous));
            }
        }
    }

    /// <inheritdoc />
    public void Write(BufferHandle buffer, long offset, ReadOnlySpan<byte> data) {
        var target = ResolveBuffer(buffer, "a host write");
        var description = target.Description;

        if (description.Access == MemoryAccess.DeviceLocal) {
            throw new InvalidOperationException(
                $"Buffer '{description.Name}' is device-local and cannot be written by the host. Stage it "
                + "through an upload buffer and copy."
            );
        }

        if (offset < 0 || offset + data.Length > description.Size) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Writing {data.Length} bytes at {offset} runs off the end of '{description.Name}', which "
                + $"is {description.Size} bytes."
            );
        }

        if (data.IsEmpty) {
            return;
        }

        // queue.writeBuffer wants a multiple of four, and so does its offset. The engine's callers
        // do not, so a tail that is not a multiple of four is padded here — reading past the data
        // the caller gave us would be wrong, so the pad is zeroed rather than left as whatever
        // followed it in their span.
        if ((data.Length & 3) == 0 && (offset & 3) == 0) {
            binding.WriteBuffer(target.Handle, offset, data);
            return;
        }

        WriteUnaligned(target, offset, data);
    }

    /// <inheritdoc />
    public void Read(BufferHandle buffer, long offset, Span<byte> destination) {
        var target = ResolveBuffer(buffer, "a host read");

        if (target.Description.Access != MemoryAccess.HostReadback) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is not a readback buffer."
            );
        }

        if (offset < 0 || offset + destination.Length > target.Description.Size) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Reading {destination.Length} bytes at {offset} runs off the end of "
                + $"'{target.Description.Name}'."
            );
        }

        if (!binding.ReadBuffer(target.Handle, offset, destination)) {
            throw new NotSupportedException(
                $"'{target.Description.Name}' could not be read back. WebGPU's buffer map is asynchronous "
                + "everywhere, and in a browser there is no thread that may block until it completes — so "
                + "readback works on the native surface and not in a tab. A frame that needs a value back "
                + "on the web has to ask for it a frame early and pick it up later."
            );
        }
    }

    /// <inheritdoc />
    public void Destroy(BufferHandle handle) => Destroy(buffers, handle.Value, static (device, item) => {
        device.binding.Release(WebGpuObjectKind.Buffer, ((WebGpuBuffer)item).Handle);
    });

    /// <inheritdoc />
    public void Destroy(TextureHandle handle) => Destroy(textures, handle.Value, static (device, item) => {
        var texture = (WebGpuTexture)item;

        if (texture.Owned) {
            device.binding.Release(WebGpuObjectKind.Texture, texture.Handle);
        }
    });

    /// <inheritdoc />
    public void Destroy(TextureViewHandle handle) => Destroy(views, handle.Value, static (device, item) => {
        var view = (WebGpuTextureView)item;

        if (view.Owned) {
            device.binding.Release(WebGpuObjectKind.TextureView, view.Handle);
        }
    });

    /// <inheritdoc />
    public void Destroy(SamplerHandle handle) => Destroy(samplers, handle.Value, static (device, item) => {
        device.binding.Release(WebGpuObjectKind.Sampler, ((WebGpuSampler)item).Handle);
    });

    /// <inheritdoc />
    public void Destroy(ShaderHandle handle) => Destroy(shaders, handle.Value, static (device, item) => {
        device.binding.Release(WebGpuObjectKind.ShaderModule, ((WebGpuShader)item).Handle);
    });

    /// <inheritdoc />
    public void Destroy(PipelineHandle handle) => Destroy(pipelines, handle.Value, static (device, item) => {
        var pipeline = (WebGpuPipeline)item;

        device.binding.Release(
            pipeline.IsCompute ? WebGpuObjectKind.ComputePipeline : WebGpuObjectKind.RenderPipeline,
            pipeline.Handle
        );
    });

    /// <inheritdoc />
    public void Destroy(PipelineLayoutHandle handle) =>
        Destroy(pipelineLayouts, handle.Value, static (device, item) => {
            device.binding.Release(WebGpuObjectKind.PipelineLayout, ((WebGpuPipelineLayout)item).Handle);
        });

    /// <inheritdoc />
    public void Destroy(DescriptorSetLayoutHandle handle) =>
        Destroy(setLayouts, handle.Value, static (device, item) => {
            device.binding.Release(WebGpuObjectKind.BindGroupLayout, ((WebGpuDescriptorSetLayout)item).Handle);
        });

    /// <inheritdoc />
    public void Destroy(DescriptorSetHandle handle) =>
        Destroy(descriptorSets, handle.Value, static (device, item) => {
            var set = (WebGpuDescriptorSet)item;

            if (set.Handle.IsValid) {
                device.binding.Release(WebGpuObjectKind.BindGroup, set.Handle);
            }
        });

    // ── What the command list resolves through ──────────────────────────────────────────────

    internal WebGpuBuffer ResolveBuffer(BufferHandle handle, string what) {
        lock (gate) {
            if (!buffers.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    $"The buffer bound as {what} does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuBuffer)found;
        }
    }

    internal WebGpuTexture ResolveTexture(TextureHandle handle, string what) {
        lock (gate) {
            if (!textures.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    $"The texture used as {what} does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuTexture)found;
        }
    }

    internal WebGpuObject ResolveView(TextureViewHandle handle, string what) {
        lock (gate) {
            if (!views.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    $"The view used as {what} does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return ((WebGpuTextureView)found).Handle;
        }
    }

    internal WebGpuPipeline ResolvePipeline(PipelineHandle handle) {
        lock (gate) {
            if (!pipelines.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    "The pipeline does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuPipeline)found;
        }
    }

    internal WebGpuDescriptorSet ResolveDescriptorSet(DescriptorSetHandle handle) {
        lock (gate) {
            if (!descriptorSets.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    "The descriptor set does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuDescriptorSet)found;
        }
    }

    /// <summary>Wraps a texture the surface owns, so the swapchain can hand one out.</summary>
    /// <param name="texture">The WebGPU texture.</param>
    /// <param name="format">Its format.</param>
    /// <param name="size">Its size in pixels.</param>
    /// <returns>A handle pair the caller destroys when the image is retired.</returns>
    internal (TextureHandle Texture, TextureViewHandle View) AdoptSurfaceTexture(
        WebGpuObject texture,
        PixelFormat format,
        Int2 size
    ) {
        var description = new TextureDescription(
            format,
            Math.Max(1, size.X),
            Math.Max(1, size.Y),
            TextureUsage.ColourTarget,
            Name: "SwapChain"
        );

        var view = binding.CreateTextureView(
            texture,
            new(
                format.Require("the swapchain"),
                WgpuTextureViewDimension.Dimension2D,
                0,
                1,
                0,
                1,
                WgpuTextureAspect.All,
                "SwapChain"
            )
        );

        lock (gate) {
            // The texture is not owned — the surface will invalidate it at the next present, and
            // releasing it ourselves is a double free. The view is ours, and is released with it.
            var textureHandle = new TextureHandle(textures.Add(new WebGpuTexture(texture, description, false)));
            var viewHandle = new TextureViewHandle(
                views.Add(new WebGpuTextureView(view, textureHandle, format, true))
            );

            return (textureHandle, viewHandle);
        }
    }

    void WriteUnaligned(WebGpuBuffer target, long offset, ReadOnlySpan<byte> data) {
        var start = offset & ~3L;
        var end = (offset + data.Length + 3) & ~3L;
        var length = (int)(end - start);
        Span<byte> scratch = length <= 512 ? stackalloc byte[length] : new byte[length];

        scratch.Clear();
        data.CopyTo(scratch[(int)(offset - start)..]);
        binding.WriteBuffer(target.Handle, start, scratch);
    }

    WgpuBindGroupEntry Entry(WebGpuDescriptorSet set, in DescriptorBinding declared, in DescriptorWrite write) {
        switch (write.Kind) {
            case DescriptorKind.UniformBuffer:
            case DescriptorKind.DynamicUniformBuffer:
            case DescriptorKind.StorageBuffer:
            case DescriptorKind.DynamicStorageBuffer: {
                var buffer = ResolveBuffer(write.Buffer, $"binding {write.Binding} of '{set.Name}'");
                var size = write.Size > 0 ? write.Size : buffer.AllocatedSize - write.Offset;

                return new(write.Binding, buffer.Handle, write.Offset, size);
            }

            case DescriptorKind.SampledTexture:
            case DescriptorKind.StorageTexture: {
                WebGpuTextureView view;

                lock (gate) {
                    if (!views.TryGet(write.TextureView.Value, out var found)) {
                        throw new ArgumentException(
                            $"The view written to binding {write.Binding} of '{set.Name}' does not exist, or "
                            + "has been destroyed.",
                            nameof(write)
                        );
                    }

                    view = (WebGpuTextureView)found;
                }

                // The layout declared a sample type before there was a texture to ask, and WebGPU
                // holds it to that: a depth view bound through a binding that says "filterable float"
                // is a validation failure a browser reports in its own words, a frame later, with no
                // mention of which binding. Said here instead, and in terms of the declaration the
                // caller can change.
                if (declared.Kind == DescriptorKind.SampledTexture && !declared.SampleType.Accepts(view.Format)) {
                    throw new ArgumentException(
                        $"A {view.Format} view was bound to binding {write.Binding} of '{set.Name}', which the "
                        + $"layout declares as {declared.SampleType}. WebGPU reads a sampled texture as its "
                        + "bind group layout says, not as its format says, so the two have to agree — declare "
                        + $"the binding {view.Format.SampleTypeOf()} (DescriptorBinding.SampleType) or bind a "
                        + "view of a matching format."
                    );
                }

                return new(write.Binding, TextureView: view.Handle);
            }

            // Unreachable in principle — the bind group layout conversion refuses a layout that
            // declares one — but a write that somehow got here would otherwise be read as a
            // sampler and fail in the browser's words, a frame later.
            case DescriptorKind.AccelerationStructure:
                throw new NotSupportedException(
                    $"An acceleration structure was written to binding {write.Binding} of "
                    + $"'{set.Name}', and ray tracing is not in the WebGPU specification. Ask "
                    + "Features.HasRayTracing and take the distance-field tracer."
                );

            default: {
                WebGpuSampler sampler;

                lock (gate) {
                    if (!samplers.TryGet(write.Sampler.Value, out var found)) {
                        throw new ArgumentException(
                            $"The sampler written to binding {write.Binding} of '{set.Name}' does not exist, "
                            + "or has been destroyed.",
                            nameof(write)
                        );
                    }

                    sampler = (WebGpuSampler)found;
                }

                if (declared.Kind == DescriptorKind.Sampler && sampler.Compares != declared.IsComparisonSampler) {
                    throw new ArgumentException(
                        $"Sampler '{sampler.Name}' {(sampler.Compares ? "compares" : "does not compare")} and "
                        + $"binding {write.Binding} of '{set.Name}' declares a "
                        + $"{(declared.IsComparisonSampler ? "comparison" : "non-comparison")} sampler. A WebGPU "
                        + "bind group layout says which it is, and a shadow map wants "
                        + "DescriptorSampleType.Depth on both the texture binding and this one."
                    );
                }

                // A non-filtering binding is the other half of an integer or unfilterable-float
                // texture: WebGPU refuses a sampler that filters there, and the habit that produces
                // one is SamplerDescription's default being trilinear. A comparison sampler is past
                // this already — the check above pairs it with the only declaration it fits, and
                // filtering one is how PCF is spelled.
                if (declared.Kind == DescriptorKind.Sampler && !declared.Filters && !declared.IsComparisonSampler
                    && sampler.Filters) {
                    throw new ArgumentException(
                        $"Filtering sampler '{sampler.Name}' was bound to binding {write.Binding} of "
                        + $"'{set.Name}', which declares {declared.SampleType} and so may not filter. Bind "
                        + "SamplerDescription.PointClamp, or one of its own with nearest filtering and no "
                        + "anisotropy."
                    );
                }

                return new(write.Binding, Sampler: sampler.Handle);
            }
        }
    }

    void Destroy<T>(HandlePool<T> pool, Handle<T> handle, Action<WebGpuDevice, T> release) where T : class {
        lock (gate) {
            if (!pool.TryGet(handle, out var item)) {
                return;
            }

            pool.Remove(handle);
            Retire(() => release(this, item));
        }
    }

    static int IndexOf(DescriptorBinding[] bindings, uint binding) {
        for (var index = 0; index < bindings.Length; index++) {
            if (bindings[index].Binding == binding) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Whether a module is SPIR-V rather than WGSL source.</summary>
    /// <remarks>
    ///     The magic number, in either byte order. Four bytes rather than a flag because
    ///     <see cref="IGraphicsDevice.CreateShader" /> takes bytecode and a stage and nothing else,
    ///     and "the RHI never parses shader source" — reading the header a format defines for exactly
    ///     this purpose is not parsing it.
    /// </remarks>
    static bool LooksLikeSpirV(ReadOnlySpan<byte> bytecode) =>
        bytecode.Length >= 4
        && ((bytecode[0] == 0x03 && bytecode[1] == 0x02 && bytecode[2] == 0x23 && bytecode[3] == 0x07)
            || (bytecode[0] == 0x07 && bytecode[1] == 0x23 && bytecode[2] == 0x02 && bytecode[3] == 0x03));
}
