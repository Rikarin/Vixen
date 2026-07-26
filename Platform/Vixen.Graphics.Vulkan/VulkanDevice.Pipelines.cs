// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;
using VkPushConstantRange = Silk.NET.Vulkan.PushConstantRange;

namespace Vixen.Graphics.Vulkan;

public sealed unsafe partial class VulkanDevice {
    /// <summary>How many sets one descriptor pool holds before another is made.</summary>
    const uint SetsPerPool = 256;

    /// <inheritdoc />
    public DescriptorSetLayoutHandle CreateDescriptorSetLayout(in DescriptorSetLayoutDescription description) {
        var bindings = description.Bindings ?? [];
        var entries = stackalloc DescriptorSetLayoutBinding[Math.Max(1, bindings.Length)];

        for (var index = 0; index < bindings.Length; index++) {
            var binding = bindings[index];

            if (binding.Count == 0 && !Features.HasBindless) {
                throw new ArgumentException(
                    $"Binding {binding.Binding} of '{description.Name}' is unbounded, which needs "
                    + "descriptor indexing. This device does not have it, so the non-bindless path is "
                    + "the one to take here."
                );
            }

            entries[index] = new() {
                Binding = binding.Binding,
                DescriptorType = VulkanEnums.ToVulkan(binding.Kind),
                DescriptorCount = (uint)Math.Max(1, binding.Count),
                StageFlags = VulkanEnums.ToVulkan(binding.Stages)
            };
        }

        var create = new DescriptorSetLayoutCreateInfo {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)bindings.Length,
            PBindings = bindings.Length > 0 ? entries : null
        };

        DescriptorSetLayout handle;
        Check(Api.CreateDescriptorSetLayout(device, &create, null, &handle), "vkCreateDescriptorSetLayout");
        Name(ObjectType.DescriptorSetLayout, handle.Handle, description.Name);

        lock (gate) {
            return new(setLayouts.Add(new VulkanDescriptorSetLayout {
                Handle = handle,
                Bindings = [.. bindings]
            }));
        }
    }

    /// <inheritdoc />
    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDescription description) {
        var sets = description.Sets ?? [];
        var pushConstants = description.PushConstants ?? [];
        var handles = stackalloc DescriptorSetLayout[Math.Max(1, sets.Length)];

        for (var index = 0; index < sets.Length; index++) {
            handles[index] = ResolveLayout(sets[index]).Handle;
        }

        var ranges = stackalloc VkPushConstantRange[Math.Max(1, pushConstants.Length)];

        for (var index = 0; index < pushConstants.Length; index++) {
            var range = pushConstants[index];

            if (range.Offset + range.Size > Features.MaxPushConstantSize) {
                throw new ArgumentException(
                    $"Push-constant range {range.Offset}..{range.Offset + range.Size} of "
                    + $"'{description.Name}' exceeds this device's {Features.MaxPushConstantSize}-byte block."
                );
            }

            ranges[index] = new() {
                StageFlags = VulkanEnums.ToVulkan(range.Stages),
                Offset = (uint)range.Offset,
                Size = (uint)range.Size
            };
        }

        var create = new PipelineLayoutCreateInfo {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = (uint)sets.Length,
            PSetLayouts = sets.Length > 0 ? handles : null,
            PushConstantRangeCount = (uint)pushConstants.Length,
            PPushConstantRanges = pushConstants.Length > 0 ? ranges : null
        };

        PipelineLayout handle;
        Check(Api.CreatePipelineLayout(device, &create, null, &handle), "vkCreatePipelineLayout");
        Name(ObjectType.PipelineLayout, handle.Handle, description.Name);

        lock (gate) {
            return new(pipelineLayouts.Add(new VulkanPipelineLayout {
                Handle = handle,
                PushConstants = [.. pushConstants]
            }));
        }
    }

    /// <inheritdoc />
    public DescriptorSetHandle CreateDescriptorSet(DescriptorSetLayoutHandle layout, string name = "") {
        var resolved = ResolveLayout(layout);

        lock (gate) {
            var pool = CurrentDescriptorPool();
            var handle = resolved.Handle;

            var allocate = new DescriptorSetAllocateInfo {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &handle
            };

            DescriptorSet set;
            var result = Api.AllocateDescriptorSets(device, &allocate, &set);

            // Out of pool memory is not an error, it is the pool being full — which is expected and is
            // why pools are a list rather than one object. Any other result is a real failure.
            if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool) {
                pool = CreateDescriptorPool();
                descriptorPools.Add(pool);
                allocate.DescriptorPool = pool;
                result = Api.AllocateDescriptorSets(device, &allocate, &set);
            }

            Check(result, "vkAllocateDescriptorSets");
            Name(ObjectType.DescriptorSet, set.Handle, name);

            return new(descriptorSets.Add(new VulkanDescriptorSet {
                Handle = set,
                Pool = pool,
                Layout = resolved
            }));
        }
    }

    /// <inheritdoc />
    public void UpdateDescriptorSet(DescriptorSetHandle descriptors, ReadOnlySpan<DescriptorWrite> writes) {
        if (writes.IsEmpty) {
            return;
        }

        var set = Resolve(descriptors);
        var entries = stackalloc WriteDescriptorSet[writes.Length];
        var bufferInfos = stackalloc DescriptorBufferInfo[writes.Length];
        var imageInfos = stackalloc DescriptorImageInfo[writes.Length];

        for (var index = 0; index < writes.Length; index++) {
            var write = writes[index];
            var declared = Declared(set.Layout, write.Binding);

            if (declared.Kind != write.Kind) {
                throw new ArgumentException(
                    $"Binding {write.Binding} was declared as {declared.Kind} and is being written as "
                    + $"{write.Kind}. Vulkan does not check this and the shader reads whichever it was "
                    + "compiled for, so the result would be silently wrong."
                );
            }

            entries[index] = new() {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set.Handle,
                DstBinding = write.Binding,
                DstArrayElement = (uint)write.ArrayIndex,
                DescriptorCount = 1,
                DescriptorType = VulkanEnums.ToVulkan(write.Kind)
            };

            switch (write.Kind) {
                case DescriptorKind.UniformBuffer:
                case DescriptorKind.DynamicUniformBuffer:
                case DescriptorKind.StorageBuffer:
                case DescriptorKind.DynamicStorageBuffer: {
                    var buffer = Resolve(write.Buffer);

                    bufferInfos[index] = new() {
                        Buffer = buffer.Handle,
                        Offset = (ulong)write.Offset,
                        Range = write.Size > 0 ? (ulong)write.Size : Vk.WholeSize
                    };

                    entries[index].PBufferInfo = &bufferInfos[index];
                    break;
                }

                case DescriptorKind.SampledTexture:
                case DescriptorKind.StorageTexture: {
                    var view = Resolve(write.TextureView);

                    imageInfos[index] = new() {
                        ImageView = view.Handle,
                        ImageLayout = write.Kind == DescriptorKind.StorageTexture
                            ? ImageLayout.General
                            : ImageLayout.ShaderReadOnlyOptimal
                    };

                    entries[index].PImageInfo = &imageInfos[index];
                    break;
                }

                case DescriptorKind.Sampler: {
                    imageInfos[index] = new() { Sampler = ResolveSampler(write.Sampler).Handle };
                    entries[index].PImageInfo = &imageInfos[index];
                    break;
                }

                default:
                    throw new ArgumentException($"Descriptor kind {write.Kind} is not one the RHI names.");
            }
        }

        Api.UpdateDescriptorSets(device, (uint)writes.Length, entries, 0, null);
    }

    /// <inheritdoc />
    public PipelineHandle CreateGraphicsPipeline(in GraphicsPipelineDescription description) {
        var layout = ResolvePipelineLayout(description.Layout);
        var vertex = ResolveShader(description.Vertex);
        var fragment = description.Fragment.IsValid ? ResolveShader(description.Fragment) : null;
        var colourTargets = description.ColourTargets ?? [];
        var vertexBuffers = description.VertexBuffers ?? [];

        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        var stageCount = 0u;

        stages[stageCount++] = new() {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertex.Handle,
            PName = entryPoint
        };

        if (fragment is not null) {
            stages[stageCount++] = new() {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment.Handle,
                PName = entryPoint
            };
        }

        var attributeCount = 0;

        foreach (var buffer in vertexBuffers) {
            attributeCount += buffer.Attributes?.Length ?? 0;
        }

        var bindings = stackalloc VertexInputBindingDescription[Math.Max(1, vertexBuffers.Length)];
        var attributes = stackalloc VertexInputAttributeDescription[Math.Max(1, attributeCount)];
        var attribute = 0;

        for (var index = 0; index < vertexBuffers.Length; index++) {
            var buffer = vertexBuffers[index];

            bindings[index] = new() {
                Binding = (uint)index,
                Stride = (uint)buffer.Stride,
                InputRate = VulkanEnums.ToVulkan(buffer.StepMode)
            };

            foreach (var element in buffer.Attributes ?? []) {
                attributes[attribute++] = new() {
                    Location = element.Location,
                    Binding = (uint)index,
                    Format = VulkanEnums.ToVulkan(element.Format),
                    Offset = (uint)element.Offset
                };
            }
        }

        var vertexInput = new PipelineVertexInputStateCreateInfo {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)vertexBuffers.Length,
            PVertexBindingDescriptions = vertexBuffers.Length > 0 ? bindings : null,
            VertexAttributeDescriptionCount = (uint)attributeCount,
            PVertexAttributeDescriptions = attributeCount > 0 ? attributes : null
        };

        var assembly = new PipelineInputAssemblyStateCreateInfo {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = VulkanEnums.ToVulkan(description.Topology)
        };

        // Viewport and scissor are dynamic, always. A pipeline that baked them would need recompiling
        // whenever the window resized, which is the single most common cause of a hitch on resize.
        var viewport = new PipelineViewportStateCreateInfo {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = Features.HasWireframe
                ? VulkanEnums.ToVulkan(description.Rasterizer.Fill)
                : PolygonMode.Fill,
            CullMode = VulkanEnums.ToVulkan(description.Rasterizer.Cull),
            FrontFace = VulkanEnums.ToVulkan(description.Rasterizer.FrontFace),
            DepthClampEnable = description.Rasterizer.DepthClamp && Features.HasDepthClamp,
            DepthBiasEnable = description.Rasterizer.DepthBias != 0f
                || description.Rasterizer.DepthBiasSlope != 0f,
            DepthBiasConstantFactor = description.Rasterizer.DepthBias,
            DepthBiasSlopeFactor = description.Rasterizer.DepthBiasSlope,
            LineWidth = 1f
        };

        var multisample = new PipelineMultisampleStateCreateInfo {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = VulkanFormats.ToSampleCount(description.SampleCount)
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = description.DepthStencil.DepthTest,
            DepthWriteEnable = description.DepthStencil.DepthWrite,
            DepthCompareOp = VulkanEnums.ToVulkan(description.DepthStencil.DepthCompare),
            StencilTestEnable = description.DepthStencil.StencilTest,
            Front = ToStencil(description.DepthStencil.Front, description.DepthStencil),
            Back = ToStencil(description.DepthStencil.Back, description.DepthStencil),
            MaxDepthBounds = 1f
        };

        var blends = stackalloc PipelineColorBlendAttachmentState[Math.Max(1, colourTargets.Length)];
        var formats = stackalloc VkFormat[Math.Max(1, colourTargets.Length)];

        for (var index = 0; index < colourTargets.Length; index++) {
            var target = colourTargets[index];
            formats[index] = VulkanFormats.ToVulkan(target.Format);

            // EffectiveBlend, not Blend: an omitted blend state is all-zeros, and an all-zero write
            // mask writes nothing at all. See ColourTargetState.EffectiveBlend for why that has to be
            // resolved in the RHI rather than here.
            var state = target.EffectiveBlend;

            blends[index] = new() {
                BlendEnable = state.Enabled,
                SrcColorBlendFactor = VulkanEnums.ToVulkan(state.SourceColour),
                DstColorBlendFactor = VulkanEnums.ToVulkan(state.DestinationColour),
                ColorBlendOp = VulkanEnums.ToVulkan(state.ColourOperation),
                SrcAlphaBlendFactor = VulkanEnums.ToVulkan(state.SourceAlpha),
                DstAlphaBlendFactor = VulkanEnums.ToVulkan(state.DestinationAlpha),
                AlphaBlendOp = VulkanEnums.ToVulkan(state.AlphaOperation),
                ColorWriteMask = VulkanEnums.ToVulkan(state.WriteMask)
            };
        }

        var blend = new PipelineColorBlendStateCreateInfo {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = (uint)colourTargets.Length,
            PAttachments = colourTargets.Length > 0 ? blends : null
        };

        var dynamicStates = stackalloc DynamicState[4];
        dynamicStates[0] = DynamicState.Viewport;
        dynamicStates[1] = DynamicState.Scissor;
        dynamicStates[2] = DynamicState.BlendConstants;
        dynamicStates[3] = DynamicState.StencilReference;

        var dynamic = new PipelineDynamicStateCreateInfo {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 4,
            PDynamicStates = dynamicStates
        };

        var depthFormat = VulkanFormats.ToVulkan(description.DepthFormat);

        var rendering = new PipelineRenderingCreateInfoKHR {
            SType = StructureType.PipelineRenderingCreateInfoKhr,
            ColorAttachmentCount = (uint)colourTargets.Length,
            PColorAttachmentFormats = colourTargets.Length > 0 ? formats : null,
            DepthAttachmentFormat = depthFormat,
            StencilAttachmentFormat = description.DepthFormat.HasStencil() ? depthFormat : VkFormat.Undefined
        };

        // The pipeline has to agree with the pass it will run in, and which of the two forms that
        // takes depends on the device: dynamic rendering names the formats here, the fallback names a
        // compatible VkRenderPass. Same description either way — this is where the plan's
        // "configuration switch, not two code paths" is actually cashed in.
        var renderPass = UsesDynamicRendering
            ? default
            : renderPasses.Get(PipelineCompatibilityKey(description));

        try {
            var create = new GraphicsPipelineCreateInfo {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout.Handle,
                RenderPass = renderPass,
                Subpass = 0,
                PNext = UsesDynamicRendering ? &rendering : null
            };

            Pipeline handle;


            Check(
                Api.CreateGraphicsPipelines(device, default, 1, &create, null, &handle),
                "vkCreateGraphicsPipelines"
            );

            Name(ObjectType.Pipeline, handle.Handle, description.Name);

            lock (gate) {
                return new(pipelines.Add(new VulkanPipeline {
                    Handle = handle,
                    Layout = layout.Handle,
                    BindPoint = PipelineBindPoint.Graphics,
                    PushConstants = layout.PushConstants
                }));
            }
        } finally {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    /// <inheritdoc />
    public PipelineHandle CreateComputePipeline(in ComputePipelineDescription description) {
        var layout = ResolvePipelineLayout(description.Layout);
        var shader = ResolveShader(description.Compute);
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        try {
            var create = new ComputePipelineCreateInfo {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new() {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shader.Handle,
                    PName = entryPoint
                },
                Layout = layout.Handle
            };

            Pipeline handle;

            Check(
                Api.CreateComputePipelines(device, default, 1, &create, null, &handle),
                "vkCreateComputePipelines"
            );

            Name(ObjectType.Pipeline, handle.Handle, description.Name);

            lock (gate) {
                return new(pipelines.Add(new VulkanPipeline {
                    Handle = handle,
                    Layout = layout.Handle,
                    BindPoint = PipelineBindPoint.Compute,
                    PushConstants = layout.PushConstants
                }));
            }
        } finally {
            SilkMarshal.Free((nint)entryPoint);
        }
    }


    /// <inheritdoc />
    public ISwapChain CreateSwapChain(in SwapChainDescription description) {
        ThrowIfDisposed();
        return new VulkanSwapChain(this, description);
    }

    /// <inheritdoc />
    public void Destroy(PipelineHandle handle) {
        if (Take(pipelines, handle.Value) is not VulkanPipeline pipeline) {
            return;
        }

        Retire(() => Api.DestroyPipeline(device, pipeline.Handle, null));
    }

    /// <inheritdoc />
    public void Destroy(PipelineLayoutHandle handle) {
        if (Take(pipelineLayouts, handle.Value) is not VulkanPipelineLayout layout) {
            return;
        }

        Retire(() => Api.DestroyPipelineLayout(device, layout.Handle, null));
    }

    /// <inheritdoc />
    public void Destroy(DescriptorSetLayoutHandle handle) {
        if (Take(setLayouts, handle.Value) is not VulkanDescriptorSetLayout layout) {
            return;
        }

        Retire(() => Api.DestroyDescriptorSetLayout(device, layout.Handle, null));
    }

    /// <inheritdoc />
    public void Destroy(DescriptorSetHandle handle) {
        // Deliberately not freed individually. The pools are created without
        // FreeDescriptorSetBit, which lets the driver bump-allocate within them; freeing a set would
        // need the flag and would cost that on every allocation to reclaim memory that is reclaimed
        // wholesale when the device goes away. Sets are cheap and long-lived by design.
        Take(descriptorSets, handle.Value);
    }

    static StencilOpState ToStencil(in StencilFaceState face, in DepthStencilState state) => new() {
        CompareOp = VulkanEnums.ToVulkan(face.Compare),
        FailOp = VulkanEnums.ToVulkan(face.Fail),
        DepthFailOp = VulkanEnums.ToVulkan(face.DepthFail),
        PassOp = VulkanEnums.ToVulkan(face.Pass),
        CompareMask = state.StencilReadMask,
        WriteMask = state.StencilWriteMask
    };

    static RenderPassKey PipelineCompatibilityKey(in GraphicsPipelineDescription description) {
        var colour = new AttachmentKey[description.ColourTargets?.Length ?? 0];
        var samples = VulkanFormats.ToSampleCount(description.SampleCount);

        for (var index = 0; index < colour.Length; index++) {
            colour[index] = new(
                VulkanFormats.ToVulkan(description.ColourTargets![index].Format),
                samples,

                // Compatibility ignores load and store ops, so any consistent choice works — and a
                // consistent one means the pipeline's pass and the frame's pass hit the same cache
                // entry rather than creating two objects that differ in nothing that matters.
                AttachmentLoadOp.DontCare,
                AttachmentStoreOp.Store
            );
        }

        AttachmentKey? depth = description.DepthFormat == PixelFormat.Undefined
            ? null
            : new(
                VulkanFormats.ToVulkan(description.DepthFormat),
                samples,
                AttachmentLoadOp.DontCare,
                AttachmentStoreOp.Store
            );

        return new(colour, depth);
    }

    static DescriptorBinding Declared(VulkanDescriptorSetLayout layout, uint binding) {
        foreach (var declared in layout.Bindings) {
            if (declared.Binding == binding) {
                return declared;
            }
        }

        throw new ArgumentException(
            $"Binding {binding} is not declared by this descriptor-set layout, so writing it would do "
            + "nothing the shader could read."
        );
    }

    DescriptorPool CurrentDescriptorPool() {
        if (descriptorPools.Count == 0) {
            descriptorPools.Add(CreateDescriptorPool());
        }

        return descriptorPools[^1];
    }

    DescriptorPool CreateDescriptorPool() {
        var sizes = stackalloc DescriptorPoolSize[7];
        sizes[0] = new() { Type = DescriptorType.UniformBuffer, DescriptorCount = SetsPerPool * 4 };
        sizes[1] = new() { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = SetsPerPool * 2 };
        sizes[2] = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = SetsPerPool * 4 };
        sizes[3] = new() { Type = DescriptorType.StorageBufferDynamic, DescriptorCount = SetsPerPool };
        sizes[4] = new() { Type = DescriptorType.SampledImage, DescriptorCount = SetsPerPool * 8 };
        sizes[5] = new() { Type = DescriptorType.StorageImage, DescriptorCount = SetsPerPool * 2 };
        sizes[6] = new() { Type = DescriptorType.Sampler, DescriptorCount = SetsPerPool * 2 };

        var create = new DescriptorPoolCreateInfo {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SetsPerPool,
            PoolSizeCount = 7,
            PPoolSizes = sizes
        };

        DescriptorPool pool;
        Check(Api.CreateDescriptorPool(device, &create, null, &pool), "vkCreateDescriptorPool");
        return pool;
    }

    VulkanDescriptorSetLayout ResolveLayout(DescriptorSetLayoutHandle handle) {
        lock (gate) {
            if (setLayouts.TryGet(handle.Value, out var layout) && layout is VulkanDescriptorSetLayout found) {
                return found;
            }
        }

        throw new ArgumentException("A descriptor-set-layout handle referred to nothing.");
    }

    VulkanPipelineLayout ResolvePipelineLayout(PipelineLayoutHandle handle) {
        lock (gate) {
            if (pipelineLayouts.TryGet(handle.Value, out var layout) && layout is VulkanPipelineLayout found) {
                return found;
            }
        }

        throw new ArgumentException("A pipeline-layout handle referred to nothing.");
    }

    VulkanShader ResolveShader(ShaderHandle handle) {
        lock (gate) {
            if (shaders.TryGet(handle.Value, out var shader) && shader is VulkanShader found) {
                return found;
            }
        }

        throw new ArgumentException("A shader handle referred to nothing.");
    }

    VulkanSampler ResolveSampler(SamplerHandle handle) {
        lock (gate) {
            if (samplers.TryGet(handle.Value, out var sampler) && sampler is VulkanSampler found) {
                return found;
            }
        }

        throw new ArgumentException("A sampler handle referred to nothing.");
    }
}
