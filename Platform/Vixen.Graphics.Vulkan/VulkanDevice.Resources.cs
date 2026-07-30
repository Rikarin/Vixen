// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Vixen.Graphics.Vulkan;

public sealed unsafe partial class VulkanDevice {
    /// <inheritdoc />
    public BufferHandle CreateBuffer(in BufferDescription description) {
        description.Validate();

        var usage = VulkanEnums.ToVulkan(description.Usage);

        // A device-local buffer nobody can copy into is a buffer nobody can fill. Adding the bit is
        // free — Vulkan charges nothing for an unused usage flag — and its absence is a failure at
        // upload time, a long way from the description that caused it.
        if (description.Access == MemoryAccess.DeviceLocal) {
            usage |= BufferUsageFlags.TransferDstBit;
        }

        var create = new BufferCreateInfo {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)description.Size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        VkBuffer handle;
        Check(Api.CreateBuffer(device, &create, null, &handle), "vkCreateBuffer");

        MemoryRequirements requirements;
        Api.GetBufferMemoryRequirements(device, handle, &requirements);

        VulkanAllocation memory;

        try {
            memory = allocator.Allocate(requirements, description.Access, true);
        } catch {
            Api.DestroyBuffer(device, handle, null);
            throw;
        }

        Check(
            Api.BindBufferMemory(device, handle, memory.Memory, (ulong)memory.Offset),
            "vkBindBufferMemory"
        );

        Name(ObjectType.Buffer, handle.Handle, description.Name);

        lock (gate) {
            return new(buffers.Add(new VulkanBuffer {
                Handle = handle,
                Memory = memory,
                Description = description
            }));
        }
    }

    /// <inheritdoc />
    public TextureHandle CreateTexture(in TextureDescription description) {
        description.Validate();
        var format = VulkanFormats.ToVulkan(description.Format);

        if (format == Silk.NET.Vulkan.Format.Undefined) {
            throw new ArgumentException(
                $"Texture '{description.Name}' asked for {description.Format}, which Vulkan does not have."
            );
        }

        var create = new ImageCreateInfo {
            SType = StructureType.ImageCreateInfo,
            ImageType = VulkanEnums.ToImageType(description.Dimension),
            Format = format,
            Extent = new((uint)description.Width, (uint)description.Height, (uint)description.Depth),
            MipLevels = (uint)description.EffectiveMipLevels,
            ArrayLayers = (uint)description.ArrayLayers,
            Samples = VulkanFormats.ToSampleCount(description.SampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = VulkanEnums.ToVulkan(description.Usage),
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,

            // Without this a cube view of the image is invalid, and the failure is at view creation
            // rather than here — which reads as a bug in the view.
            Flags = description.Dimension == TextureDimension.TextureCube
                ? ImageCreateFlags.CreateCubeCompatibleBit
                : ImageCreateFlags.None
        };

        Image handle;
        Check(Api.CreateImage(device, &create, null, &handle), "vkCreateImage");

        MemoryRequirements requirements;
        Api.GetImageMemoryRequirements(device, handle, &requirements);

        VulkanAllocation memory;

        try {
            // Images are always device-local: an optimally-tiled image in host memory is legal on
            // almost no driver, and the RHI's host-visible access is for buffers.
            memory = allocator.Allocate(requirements, MemoryAccess.DeviceLocal, false);
        } catch {
            Api.DestroyImage(device, handle, null);
            throw;
        }

        Check(Api.BindImageMemory(device, handle, memory.Memory, (ulong)memory.Offset), "vkBindImageMemory");
        Name(ObjectType.Image, handle.Handle, description.Name);

        lock (gate) {
            return new(textures.Add(new VulkanTexture {
                Handle = handle,
                Memory = memory,
                Description = description,
                Owned = true
            }));
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
        var owner = Resolve(texture);
        var description = owner.Description;
        var viewFormat = format == PixelFormat.Undefined ? description.Format : format;
        var levels = mipLevelCount > 0 ? mipLevelCount : description.EffectiveMipLevels - baseMipLevel;
        var layers = arrayLayerCount > 0 ? arrayLayerCount : description.ArrayLayers - baseArrayLayer;

        if (levels <= 0 || layers <= 0) {
            throw new ArgumentException(
                $"A view of '{description.Name}' starting at mip {baseMipLevel} layer {baseArrayLayer} "
                + $"covers {levels} level(s) and {layers} layer(s), which is nothing."
            );
        }

        var aspect = VulkanFormats.AspectOf(viewFormat);

        var create = new ImageViewCreateInfo {
            SType = StructureType.ImageViewCreateInfo,
            Image = owner.Handle,
            ViewType = VulkanEnums.ToViewType(description.Dimension, layers),
            Format = VulkanFormats.ToVulkan(viewFormat),
            SubresourceRange = new() {
                AspectMask = aspect,
                BaseMipLevel = (uint)baseMipLevel,
                LevelCount = (uint)levels,
                BaseArrayLayer = (uint)baseArrayLayer,
                LayerCount = (uint)layers
            }
        };

        ImageView handle;
        Check(Api.CreateImageView(device, &create, null, &handle), "vkCreateImageView");
        Name(ObjectType.ImageView, handle.Handle, $"{description.Name} view");

        lock (gate) {
            return new(views.Add(new VulkanTextureView {
                Handle = handle,
                Texture = texture,
                Format = viewFormat,
                Aspect = aspect,
                BaseMipLevel = baseMipLevel,
                MipLevelCount = levels,
                BaseArrayLayer = baseArrayLayer,
                ArrayLayerCount = layers
            }));
        }
    }

    /// <inheritdoc />
    public SamplerHandle CreateSampler(in SamplerDescription description) {
        var anisotropy = Math.Clamp(description.Anisotropy, 1f, Math.Max(1f, Features.MaxAnisotropy));

        var create = new SamplerCreateInfo {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = VulkanEnums.ToVulkan(description.MinFilter),
            MagFilter = VulkanEnums.ToVulkan(description.MagFilter),
            MipmapMode = VulkanEnums.ToMipmapMode(description.MipFilter),
            AddressModeU = VulkanEnums.ToVulkan(description.AddressU),
            AddressModeV = VulkanEnums.ToVulkan(description.AddressV),
            AddressModeW = VulkanEnums.ToVulkan(description.AddressW),
            AnisotropyEnable = anisotropy > 1f && Features.HasAnisotropicFiltering,
            MaxAnisotropy = anisotropy,
            CompareEnable = description.Compare is not null,
            CompareOp = VulkanEnums.ToVulkan(description.Compare ?? CompareFunction.Always),
            BorderColor = VulkanEnums.ToVulkan(description.Border),
            MinLod = description.MinLod,
            MaxLod = description.MaxLod,
            MipLodBias = description.LodBias
        };

        Sampler handle;
        Check(Api.CreateSampler(device, &create, null, &handle), "vkCreateSampler");
        Name(ObjectType.Sampler, handle.Handle, description.Name);

        lock (gate) {
            return new(samplers.Add(new VulkanSampler {
                Handle = handle,
                Compares = description.Compare is not null,
                Name = description.Name
            }));
        }
    }

    /// <inheritdoc />
    public ShaderHandle CreateShader(ShaderStage stage, ReadOnlySpan<byte> bytecode, string name = "") {
        if (bytecode.Length == 0 || bytecode.Length % 4 != 0) {
            throw new ArgumentException(
                $"SPIR-V for '{name}' is {bytecode.Length} bytes, which is not a whole number of words. "
                + "The RHI never parses shader source, so this is a truncated or non-SPIR-V module."
            );
        }

        fixed (byte* code = bytecode) {
            var create = new ShaderModuleCreateInfo {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)bytecode.Length,
                PCode = (uint*)code
            };

            ShaderModule handle;
            Check(Api.CreateShaderModule(device, &create, null, &handle), "vkCreateShaderModule");
            Name(ObjectType.ShaderModule, handle.Handle, name);

            lock (gate) {
                return new(shaders.Add(new VulkanShader { Handle = handle, Stage = stage }));
            }
        }
    }

    /// <inheritdoc />
    public void Write(BufferHandle buffer, long offset, ReadOnlySpan<byte> data) {
        var target = Resolve(buffer);

        if (target.Description.Access == MemoryAccess.DeviceLocal) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is device-local, so the CPU cannot write to it. "
                + "Write to a HostUpload buffer and copy, which is what a staging buffer is."
            );
        }

        CheckRange(target.Description, offset, data.Length, "written");

        if (!target.Memory.IsMapped) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' asked for host-visible memory and its block could "
                + "not be mapped, which usually means the device is out of address space."
            );
        }

        data.CopyTo(new Span<byte>((void*)(target.Memory.Mapped + (nint)offset), data.Length));
    }

    /// <inheritdoc />
    public void Read(BufferHandle buffer, long offset, Span<byte> destination) {
        var target = Resolve(buffer);

        if (target.Description.Access != MemoryAccess.HostReadback) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is {target.Description.Access}, not HostReadback. "
                + "Reading from upload memory is legal Vulkan and, on write-combined memory, roughly an "
                + "order of magnitude slower than reading from cached memory — so the RHI declines it."
            );
        }

        CheckRange(target.Description, offset, destination.Length, "read");

        if (!target.Memory.IsMapped) {
            throw new InvalidOperationException(
                $"Buffer '{target.Description.Name}' is not mapped, so it cannot be read."
            );
        }

        new ReadOnlySpan<byte>((void*)(target.Memory.Mapped + (nint)offset), destination.Length)
            .CopyTo(destination);
    }

    /// <inheritdoc />
    public void Destroy(BufferHandle handle) {
        if (Take(buffers, handle.Value) is not VulkanBuffer buffer) {
            return;
        }

        Retire(() => {
            Api.DestroyBuffer(device, buffer.Handle, null);
            allocator.Free(buffer.Memory);
        });
    }

    /// <inheritdoc />
    public void Destroy(TextureHandle handle) {
        if (Take(textures, handle.Value) is not VulkanTexture texture) {
            return;
        }

        if (!texture.Owned) {
            // A swapchain image. The presentation engine created it and will destroy it; destroying
            // it here is a validation error and, on some drivers, a crash inside the driver.
            return;
        }

        Retire(() => {
            Api.DestroyImage(device, texture.Handle, null);
            allocator.Free(texture.Memory);
        });
    }

    /// <inheritdoc />
    public void Destroy(TextureViewHandle handle) {
        if (Take(views, handle.Value) is not VulkanTextureView view) {
            return;
        }

        Retire(() => {
            renderPasses.Invalidate(view.Handle);
            Api.DestroyImageView(device, view.Handle, null);
        });
    }

    /// <inheritdoc />
    public void Destroy(SamplerHandle handle) {
        if (Take(samplers, handle.Value) is not VulkanSampler sampler) {
            return;
        }

        Retire(() => Api.DestroySampler(device, sampler.Handle, null));
    }

    /// <inheritdoc />
    public void Destroy(ShaderHandle handle) {
        if (Take(shaders, handle.Value) is not VulkanShader shader) {
            return;
        }

        Retire(() => Api.DestroyShaderModule(device, shader.Handle, null));
    }

    /// <inheritdoc />
    public QueryPoolHandle CreateQueryPool(in QueryPoolDescription description) {
        if (!Features.HasTimestampQueries) {
            throw new NotSupportedException(
                $"Query pool '{description.Name}' was asked for on a device whose graphics queue "
                + "reports no timestamp bits. Ask Features.HasTimestampQueries first."
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Count, nameof(description));

        var create = new QueryPoolCreateInfo {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)description.Count
        };

        QueryPool handle;
        Check(Api.CreateQueryPool(device, &create, null, &handle), "vkCreateQueryPool");
        Name(ObjectType.QueryPool, handle.Handle, description.Name);

        lock (gate) {
            return new(queryPools.Add(new VulkanQueryPool { Handle = handle, Count = description.Count }));
        }
    }

    /// <inheritdoc />
    public void Destroy(QueryPoolHandle handle) {
        if (Take(queryPools, handle.Value) is not VulkanQueryPool pool) {
            return;
        }

        Retire(() => Api.DestroyQueryPool(device, pool.Handle, null));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Without <c>WAIT_BIT</c>, and that is the contract rather than an optimisation.</b>
    ///     Asking Vulkan to wait would block the calling thread until the GPU had finished the
    ///     submission that wrote the range — once a frame, on the frame thread, for a panel nobody
    ///     may even have open. <c>NotReady</c> comes back as <see langword="false" /> and the caller
    ///     asks again next frame.
    /// </remarks>
    public bool TryResolveQueries(QueryPoolHandle pool, int first, Span<ulong> results) {
        ArgumentOutOfRangeException.ThrowIfNegative(first);

        if (results.IsEmpty) {
            return true;
        }

        VulkanQueryPool target;

        lock (gate) {
            if (!queryPools.TryGet(pool.Value, out var found) || found is not VulkanQueryPool resolved) {
                throw new ArgumentException(
                    "A query-pool handle referred to nothing. Either it was never created, or it was "
                    + "destroyed and the handle kept.",
                    nameof(pool)
                );
            }

            target = resolved;
        }

        if (first + results.Length > target.Count) {
            throw new ArgumentOutOfRangeException(
                nameof(first),
                $"Reading {results.Length} queries from {first} runs off the end of a pool holding "
                + $"{target.Count}. Vulkan's answer to that is undefined rather than an error."
            );
        }

        fixed (ulong* destination = results) {
            var status = Api.GetQueryPoolResults(
                device,
                target.Handle,
                (uint)first,
                (uint)results.Length,
                (nuint)(results.Length * sizeof(ulong)),
                destination,
                sizeof(ulong),
                QueryResultFlags.Result64Bit
            );

            return status switch {
                Result.Success => true,
                Result.NotReady => false,
                _ => throw new VulkanException($"vkGetQueryPoolResults failed with {status}.")
            };
        }
    }

    internal VulkanBuffer Resolve(BufferHandle handle) {
        lock (gate) {
            if (buffers.TryGet(handle.Value, out var buffer) && buffer is VulkanBuffer resolved) {
                return resolved;
            }
        }

        throw new ArgumentException(
            "A buffer handle referred to nothing. Either it was never created, or it was destroyed and "
            + "the handle kept — which the generation counter is there to catch."
        );
    }

    internal VulkanTexture Resolve(TextureHandle handle) {
        lock (gate) {
            if (textures.TryGet(handle.Value, out var texture) && texture is VulkanTexture resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("A texture handle referred to nothing.");
    }

    internal VulkanTextureView Resolve(TextureViewHandle handle) {
        lock (gate) {
            if (views.TryGet(handle.Value, out var view) && view is VulkanTextureView resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("A texture-view handle referred to nothing.");
    }

    internal VulkanPipeline Resolve(PipelineHandle handle) {
        lock (gate) {
            if (pipelines.TryGet(handle.Value, out var pipeline) && pipeline is VulkanPipeline resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("A pipeline handle referred to nothing.");
    }

    internal VulkanQueryPool Resolve(QueryPoolHandle handle) {
        lock (gate) {
            if (queryPools.TryGet(handle.Value, out var pool) && pool is VulkanQueryPool resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("A query-pool handle referred to nothing.");
    }

    internal VulkanDescriptorSet Resolve(DescriptorSetHandle handle) {
        lock (gate) {
            if (descriptorSets.TryGet(handle.Value, out var set) && set is VulkanDescriptorSet resolved) {
                return resolved;
            }
        }

        throw new ArgumentException("A descriptor-set handle referred to nothing.");
    }

    /// <summary>Attaches a name to a Vulkan object, when the layers are there to read it.</summary>
    /// <remarks>
    ///     Silently does nothing without the debug-utils extension, which is the right behaviour: a
    ///     release build has no layers and naming is pure overhead there, and a capture is exactly
    ///     where the names matter.
    /// </remarks>
    internal void Name(ObjectType type, ulong handle, string name) {
        if (debugUtils is null || string.IsNullOrEmpty(name)) {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(name);

        try {
            var info = new DebugUtilsObjectNameInfoEXT {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = text
            };

            debugUtils.SetDebugUtilsObjectName(device, &info);
        } finally {
            SilkMarshal.Free((nint)text);
        }
    }

    /// <summary>Registers a swapchain's image, which the device does not own.</summary>
    internal TextureHandle AdoptSwapChainImage(Image image, in TextureDescription description) {
        lock (gate) {
            return new(textures.Add(new VulkanTexture {
                Handle = image,
                Memory = default,
                Description = description,
                Owned = false
            }));
        }
    }

    static void CheckRange(in BufferDescription description, long offset, int length, string verb) {
        if (offset < 0 || length < 0 || offset + length > description.Size) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"{length} bytes at offset {offset} cannot be {verb} in '{description.Name}', which is "
                + $"{description.Size} bytes."
            );
        }
    }

    T? Take<T>(Vixen.Core.Collections.HandlePool<T> pool, Vixen.Core.Collections.Handle<T> handle)
        where T : class {
        lock (gate) {
            if (!pool.TryGet(handle, out var item)) {
                return null;
            }

            pool.Remove(handle);
            return item;
        }
    }

    void DestroyAll() {
        foreach (var (_, item) in queryPools) {
            if (item is VulkanQueryPool pool) {
                Api.DestroyQueryPool(device, pool.Handle, null);
            }
        }

        foreach (var (_, item) in pipelines) {
            if (item is VulkanPipeline pipeline) {
                Api.DestroyPipeline(device, pipeline.Handle, null);
            }
        }

        foreach (var (_, item) in pipelineLayouts) {
            if (item is VulkanPipelineLayout layout) {
                Api.DestroyPipelineLayout(device, layout.Handle, null);
            }
        }

        foreach (var (_, item) in setLayouts) {
            if (item is VulkanDescriptorSetLayout layout) {
                Api.DestroyDescriptorSetLayout(device, layout.Handle, null);
            }
        }

        foreach (var (_, item) in shaders) {
            if (item is VulkanShader shader) {
                Api.DestroyShaderModule(device, shader.Handle, null);
            }
        }

        foreach (var (_, item) in samplers) {
            if (item is VulkanSampler sampler) {
                Api.DestroySampler(device, sampler.Handle, null);
            }
        }

        foreach (var (_, item) in views) {
            if (item is VulkanTextureView view) {
                Api.DestroyImageView(device, view.Handle, null);
            }
        }

        foreach (var (_, item) in textures) {
            if (item is VulkanTexture { Owned: true } texture) {
                Api.DestroyImage(device, texture.Handle, null);
                allocator.Free(texture.Memory);
            }
        }

        foreach (var (_, item) in buffers) {
            if (item is VulkanBuffer buffer) {
                Api.DestroyBuffer(device, buffer.Handle, null);
                allocator.Free(buffer.Memory);
            }
        }

        queryPools.Clear();
        pipelines.Clear();
        pipelineLayouts.Clear();
        setLayouts.Clear();
        descriptorSets.Clear();
        shaders.Clear();
        samplers.Clear();
        views.Clear();
        textures.Clear();
        buffers.Clear();
    }
}
