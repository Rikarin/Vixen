// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Vixen.Graphics.Vulkan;

/// <summary>A buffer and the memory behind it.</summary>
sealed class VulkanBuffer : GpuBuffer {
    public required VkBuffer Handle { get; init; }

    public required VulkanAllocation Memory { get; init; }

    public required BufferDescription Description { get; init; }
}

/// <summary>An image and the memory behind it.</summary>
/// <remarks>
///     <see cref="Owned" /> is false for a swapchain image. Those are created and destroyed by the
///     presentation engine, and destroying one is both a validation error and, on some drivers, a
///     crash inside the driver rather than in us — so the flag is worth carrying rather than
///     inferring from whether the allocation is empty.
/// </remarks>
sealed class VulkanTexture : GpuTexture {
    public required Image Handle { get; init; }

    public required VulkanAllocation Memory { get; init; }

    public required TextureDescription Description { get; init; }

    public required bool Owned { get; init; }
}

/// <summary>A view of part of an image.</summary>
sealed class VulkanTextureView : GpuTextureView {
    public required ImageView Handle { get; init; }

    public required TextureHandle Texture { get; init; }

    public required PixelFormat Format { get; init; }

    public required ImageAspectFlags Aspect { get; init; }

    public required int BaseMipLevel { get; init; }

    public required int MipLevelCount { get; init; }

    public required int BaseArrayLayer { get; init; }

    public required int ArrayLayerCount { get; init; }
}

/// <summary>A sampler.</summary>
sealed class VulkanSampler : GpuSampler {
    public required Sampler Handle { get; init; }

    /// <summary>Whether it compares rather than returning what it read.</summary>
    /// <remarks>
    ///     Vulkan needs no such thing at bind time — the sampler carries its own <c>compareEnable</c>
    ///     and the layout says nothing about it. It is kept so a layout that <em>does</em> declare
    ///     which it wants can be held to it here, on the backend a renderer is developed against,
    ///     rather than only by WebGPU, where the declaration is mandatory.
    /// </remarks>
    public required bool Compares { get; init; }

    /// <summary>A name for the diagnostic that reports a mismatch.</summary>
    public required string Name { get; init; }
}

/// <summary>A shader module and the stage it was compiled for.</summary>
sealed class VulkanShader : GpuShader {
    public required ShaderModule Handle { get; init; }

    public required ShaderStage Stage { get; init; }
}

/// <summary>A descriptor-set layout, and the bindings it was built from.</summary>
/// <remarks>
///     The bindings are kept because a descriptor <em>write</em> names a binding and a kind, and the
///     kind has to agree with what the layout declared. Checking it here turns a mismatch into a
///     readable exception rather than undefined behaviour that validation may or may not catch.
/// </remarks>
sealed class VulkanDescriptorSetLayout : GpuDescriptorSetLayout {
    public required DescriptorSetLayout Handle { get; init; }

    public required DescriptorBinding[] Bindings { get; init; }
}

/// <summary>A pipeline layout.</summary>
sealed class VulkanPipelineLayout : GpuPipelineLayout {
    public required PipelineLayout Handle { get; init; }

    public required PushConstantRange[] PushConstants { get; init; }
}

/// <summary>A descriptor set, and the pool it came from.</summary>
sealed class VulkanDescriptorSet : GpuDescriptorSet {
    public required DescriptorSet Handle { get; init; }

    public required DescriptorPool Pool { get; init; }

    public required VulkanDescriptorSetLayout Layout { get; init; }
}

/// <summary>A compiled pipeline.</summary>
sealed class VulkanPipeline : GpuPipeline {
    public required Pipeline Handle { get; init; }

    public required PipelineLayout Layout { get; init; }

    public required PipelineBindPoint BindPoint { get; init; }

    /// <summary>What the layout declared, so that a push-constant write can be checked.</summary>
    public required PushConstantRange[] PushConstants { get; init; }
}
