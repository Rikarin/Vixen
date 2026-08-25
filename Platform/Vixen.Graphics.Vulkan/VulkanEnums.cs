// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;
using VkFrontFace = Silk.NET.Vulkan.FrontFace;
using VkPrimitiveTopology = Silk.NET.Vulkan.PrimitiveTopology;

namespace Vixen.Graphics.Vulkan;

/// <summary>The rest of the RHI's vocabulary in Vulkan's terms.</summary>
/// <remarks>
///     Pure, and tested exhaustively over every enum member, for the reason
///     <see cref="VulkanFormats" /> gives: a wrong mapping here does not throw. A blend factor that
///     maps to the wrong constant renders <em>something</em>, and finding out which of thirteen
///     factors is wrong from a screenshot is an afternoon.
/// </remarks>
static class VulkanEnums {
    public static VkPrimitiveTopology ToVulkan(PrimitiveTopology topology) => topology switch {
        PrimitiveTopology.PointList => VkPrimitiveTopology.PointList,
        PrimitiveTopology.LineList => VkPrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => VkPrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleList => VkPrimitiveTopology.TriangleList,
        PrimitiveTopology.TriangleStrip => VkPrimitiveTopology.TriangleStrip,
        _ => VkPrimitiveTopology.TriangleList
    };

    public static IndexType ToVulkan(IndexFormat format) =>
        format == IndexFormat.UInt32 ? IndexType.Uint32 : IndexType.Uint16;

    public static CullModeFlags ToVulkan(CullMode mode) => mode switch {
        CullMode.Front => CullModeFlags.FrontBit,
        CullMode.Back => CullModeFlags.BackBit,
        _ => CullModeFlags.None
    };

    /// <summary>The engine's winding, mapped straight through — and that is not an oversight.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The identity, and it has to be, because two mirrors cancel.</b>
    ///         <see cref="FrontFace" /> names a winding in the engine's clip space, where +Y is up.
    ///         Vulkan decides facing from the signed area in <i>framebuffer</i> coordinates, and two
    ///         mirrors lie between the two spaces: Vulkan's clip-to-framebuffer convention puts +Y
    ///         down (one), and <c>VulkanCommandList.SetViewport</c> submits a negative height to put
    ///         it back up (two). A triangle wound counter-clockwise in the engine's clip space
    ///         therefore arrives counter-clockwise in the coordinates facing is decided in.
    ///     </para>
    ///     <para>
    ///         This mapping has been inverted once, by an argument that counted the second mirror
    ///         and forgot the first — "the negative height flips the picture, so it must flip the
    ///         winding". It flips the winding <i>relative to Vulkan's own +Y-down convention</i>,
    ///         which the engine's meshes were never wound in. The cost was every closed mesh in the
    ///         repo classified inside-out: <c>CullMode.Back</c> culled the surface and golden
    ///         fixtures drew nothing, with nothing invalid for a validation layer to report. The
    ///         <c>cull-back</c> and <c>cull-front</c> golden references are the empirical record —
    ///         they pass under the identity and only under it.
    ///     </para>
    /// </remarks>
    public static VkFrontFace ToVulkan(FrontFace face) =>
        face == FrontFace.Clockwise ? VkFrontFace.Clockwise : VkFrontFace.CounterClockwise;

    public static PolygonMode ToVulkan(FillMode mode) =>
        mode == FillMode.Wireframe ? PolygonMode.Line : PolygonMode.Fill;

    public static CompareOp ToVulkan(CompareFunction function) => function switch {
        CompareFunction.Never => CompareOp.Never,
        CompareFunction.Less => CompareOp.Less,
        CompareFunction.Equal => CompareOp.Equal,
        CompareFunction.LessEqual => CompareOp.LessOrEqual,
        CompareFunction.Greater => CompareOp.Greater,
        CompareFunction.NotEqual => CompareOp.NotEqual,
        CompareFunction.GreaterEqual => CompareOp.GreaterOrEqual,
        CompareFunction.Always => CompareOp.Always,
        _ => CompareOp.Always
    };

    public static StencilOp ToVulkan(StencilOperation operation) => operation switch {
        StencilOperation.Keep => StencilOp.Keep,
        StencilOperation.Zero => StencilOp.Zero,
        StencilOperation.Replace => StencilOp.Replace,
        StencilOperation.IncrementClamp => StencilOp.IncrementAndClamp,
        StencilOperation.DecrementClamp => StencilOp.DecrementAndClamp,
        StencilOperation.Invert => StencilOp.Invert,
        StencilOperation.IncrementWrap => StencilOp.IncrementAndWrap,
        StencilOperation.DecrementWrap => StencilOp.DecrementAndWrap,
        _ => StencilOp.Keep
    };

    public static Silk.NET.Vulkan.BlendFactor ToVulkan(BlendFactor factor) => factor switch {
        BlendFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendFactor.SourceColour => Silk.NET.Vulkan.BlendFactor.SrcColor,
        BlendFactor.OneMinusSourceColour => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        BlendFactor.SourceAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendFactor.OneMinusSourceAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        BlendFactor.DestinationColour => Silk.NET.Vulkan.BlendFactor.DstColor,
        BlendFactor.OneMinusDestinationColour => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        BlendFactor.DestinationAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        BlendFactor.OneMinusDestinationAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha,
        BlendFactor.Constant => Silk.NET.Vulkan.BlendFactor.ConstantColor,
        BlendFactor.OneMinusConstant => Silk.NET.Vulkan.BlendFactor.OneMinusConstantColor,
        BlendFactor.SourceAlphaSaturated => Silk.NET.Vulkan.BlendFactor.SrcAlphaSaturate,
        _ => Silk.NET.Vulkan.BlendFactor.One
    };

    public static BlendOp ToVulkan(BlendOperation operation) => operation switch {
        BlendOperation.Add => BlendOp.Add,
        BlendOperation.Subtract => BlendOp.Subtract,
        BlendOperation.ReverseSubtract => BlendOp.ReverseSubtract,
        BlendOperation.Min => BlendOp.Min,
        BlendOperation.Max => BlendOp.Max,
        _ => BlendOp.Add
    };

    public static ColorComponentFlags ToVulkan(ColourWriteMask mask) {
        var flags = ColorComponentFlags.None;

        if ((mask & ColourWriteMask.Red) != 0) {
            flags |= ColorComponentFlags.RBit;
        }

        if ((mask & ColourWriteMask.Green) != 0) {
            flags |= ColorComponentFlags.GBit;
        }

        if ((mask & ColourWriteMask.Blue) != 0) {
            flags |= ColorComponentFlags.BBit;
        }

        if ((mask & ColourWriteMask.Alpha) != 0) {
            flags |= ColorComponentFlags.ABit;
        }

        return flags;
    }

    public static Filter ToVulkan(FilterMode mode) =>
        mode == FilterMode.Nearest ? Filter.Nearest : Filter.Linear;

    public static SamplerMipmapMode ToMipmapMode(FilterMode mode) =>
        mode == FilterMode.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear;

    public static SamplerAddressMode ToVulkan(AddressMode mode) => mode switch {
        AddressMode.Repeat => SamplerAddressMode.Repeat,
        AddressMode.MirrorRepeat => SamplerAddressMode.MirroredRepeat,
        AddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        AddressMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        _ => SamplerAddressMode.Repeat
    };

    public static BorderColor ToVulkan(BorderColour colour) => colour switch {
        BorderColour.TransparentBlack => BorderColor.FloatTransparentBlack,
        BorderColour.OpaqueBlack => BorderColor.FloatOpaqueBlack,
        BorderColour.OpaqueWhite => BorderColor.FloatOpaqueWhite,
        _ => BorderColor.FloatTransparentBlack
    };

    public static VertexInputRate ToVulkan(VertexStepMode mode) =>
        mode == VertexStepMode.Instance ? VertexInputRate.Instance : VertexInputRate.Vertex;

    public static AttachmentLoadOp ToVulkan(LoadAction action) => action switch {
        LoadAction.Load => AttachmentLoadOp.Load,
        LoadAction.Clear => AttachmentLoadOp.Clear,
        _ => AttachmentLoadOp.DontCare
    };

    /// <summary>The store op for an attachment action.</summary>
    /// <param name="action">What to do at the end of the pass.</param>
    /// <remarks>
    ///     <see cref="StoreAction.Resolve" /> stores as well: the resolve target is a separate
    ///     attachment, and mapping it to <c>DontCare</c> would discard the multisampled image the
    ///     resolve reads from.
    /// </remarks>
    public static AttachmentStoreOp ToVulkan(StoreAction action) =>
        action == StoreAction.DontCare ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store;

    /// <summary>The resolve mode for a multisampled depth attachment.</summary>
    /// <param name="mode">Which sample the resolve keeps.</param>
    /// <remarks>
    ///     ⚠ There is deliberately no <c>Average</c> arm. Vulkan forbids
    ///     <c>VK_RESOLVE_MODE_AVERAGE_BIT</c> for a depth-stencil attachment for the same reason the
    ///     engine does not offer it: the mean of several depths describes no surface. The mapping is
    ///     total, so a value outside the enum resolves as <see cref="DepthResolveMode.SampleZero" />
    ///     — the mode every implementation supports.
    /// </remarks>
    public static ResolveModeFlags ToVulkan(DepthResolveMode mode) => mode switch {
        DepthResolveMode.Min => ResolveModeFlags.MinBit,
        DepthResolveMode.Max => ResolveModeFlags.MaxBit,
        _ => ResolveModeFlags.SampleZeroBit
    };

    public static PresentModeKHR ToVulkan(PresentMode mode) => mode switch {
        PresentMode.Fifo => PresentModeKHR.FifoKhr,
        PresentMode.FifoRelaxed => PresentModeKHR.FifoRelaxedKhr,
        PresentMode.Mailbox => PresentModeKHR.MailboxKhr,
        PresentMode.Immediate => PresentModeKHR.ImmediateKhr,
        _ => PresentModeKHR.FifoKhr
    };

    public static PresentMode FromVulkan(PresentModeKHR mode) => mode switch {
        PresentModeKHR.FifoRelaxedKhr => PresentMode.FifoRelaxed,
        PresentModeKHR.MailboxKhr => PresentMode.Mailbox,
        PresentModeKHR.ImmediateKhr => PresentMode.Immediate,
        _ => PresentMode.Fifo
    };

    public static ImageType ToImageType(TextureDimension dimension) => dimension switch {
        TextureDimension.Texture1D => ImageType.Type1D,
        TextureDimension.Texture3D => ImageType.Type3D,
        _ => ImageType.Type2D
    };

    /// <summary>The view type for a texture's shape and layer count.</summary>
    /// <param name="dimension">Its shape.</param>
    /// <param name="layers">How many array layers the <em>view</em> covers.</param>
    /// <remarks>
    ///     Layer count decides array-ness, not the texture's declared dimension: a one-layer view of
    ///     an array texture is a plain 2D view, and a cube with twelve layers is a cube array. Getting
    ///     this wrong produces a view the shader reads as the wrong type, which validation catches and
    ///     a release build does not.
    /// </remarks>
    public static ImageViewType ToViewType(TextureDimension dimension, int layers) => dimension switch {
        TextureDimension.Texture1D => layers > 1 ? ImageViewType.Type1DArray : ImageViewType.Type1D,
        TextureDimension.Texture3D => ImageViewType.Type3D,
        TextureDimension.TextureCube => layers > 6 ? ImageViewType.TypeCubeArray : ImageViewType.TypeCube,
        _ => layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D
    };

    public static ShaderStageFlags ToVulkan(ShaderStage stages) {
        var flags = ShaderStageFlags.None;

        if ((stages & ShaderStage.Vertex) != 0) {
            flags |= ShaderStageFlags.VertexBit;
        }

        if ((stages & ShaderStage.Fragment) != 0) {
            flags |= ShaderStageFlags.FragmentBit;
        }

        if ((stages & ShaderStage.Compute) != 0) {
            flags |= ShaderStageFlags.ComputeBit;
        }

        if ((stages & ShaderStage.Geometry) != 0) {
            flags |= ShaderStageFlags.GeometryBit;
        }

        if ((stages & ShaderStage.TessellationControl) != 0) {
            flags |= ShaderStageFlags.TessellationControlBit;
        }

        if ((stages & ShaderStage.TessellationEvaluation) != 0) {
            flags |= ShaderStageFlags.TessellationEvaluationBit;
        }

        if ((stages & ShaderStage.Task) != 0) {
            flags |= ShaderStageFlags.TaskBitExt;
        }

        if ((stages & ShaderStage.Mesh) != 0) {
            flags |= ShaderStageFlags.MeshBitExt;
        }

        return flags;
    }

    public static DescriptorType ToVulkan(DescriptorKind kind) => kind switch {
        DescriptorKind.UniformBuffer => DescriptorType.UniformBuffer,
        DescriptorKind.DynamicUniformBuffer => DescriptorType.UniformBufferDynamic,
        DescriptorKind.StorageBuffer => DescriptorType.StorageBuffer,
        DescriptorKind.DynamicStorageBuffer => DescriptorType.StorageBufferDynamic,
        DescriptorKind.SampledTexture => DescriptorType.SampledImage,
        DescriptorKind.StorageTexture => DescriptorType.StorageImage,
        DescriptorKind.Sampler => DescriptorType.Sampler,

        // An explicit arm, not the fallback: the fallback maps to UniformBuffer, and a structure
        // written as a uniform buffer is a layout the driver accepts and a query that opens nothing.
        DescriptorKind.AccelerationStructure => DescriptorType.AccelerationStructureKhr,
        _ => DescriptorType.UniformBuffer
    };

    /// <summary>Which level of the hierarchy, in Vulkan's terms.</summary>
    public static AccelerationStructureTypeKHR ToVulkan(AccelerationStructureKind kind) =>
        kind == AccelerationStructureKind.TopLevel
            ? AccelerationStructureTypeKHR.TopLevelKhr
            : AccelerationStructureTypeKHR.BottomLevelKhr;

    /// <summary>Whether a descriptor kind is one whose offset is supplied at bind time.</summary>
    public static bool IsDynamic(DescriptorKind kind) =>
        kind is DescriptorKind.DynamicUniformBuffer or DescriptorKind.DynamicStorageBuffer;

    public static BufferUsageFlags ToVulkan(BufferUsage usage) {
        var flags = BufferUsageFlags.None;

        if ((usage & BufferUsage.Vertex) != 0) {
            flags |= BufferUsageFlags.VertexBufferBit;
        }

        if ((usage & BufferUsage.Index) != 0) {
            flags |= BufferUsageFlags.IndexBufferBit;
        }

        if ((usage & BufferUsage.Uniform) != 0) {
            flags |= BufferUsageFlags.UniformBufferBit;
        }

        if ((usage & BufferUsage.Storage) != 0) {
            flags |= BufferUsageFlags.StorageBufferBit;
        }

        if ((usage & BufferUsage.Indirect) != 0) {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        if ((usage & BufferUsage.CopySource) != 0) {
            flags |= BufferUsageFlags.TransferSrcBit;
        }

        if ((usage & BufferUsage.CopyDestination) != 0) {
            flags |= BufferUsageFlags.TransferDstBit;
        }

        if ((usage & BufferUsage.AccelerationStructureInput) != 0) {
            flags |= BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
        }

        if ((usage & BufferUsage.AccelerationStructureStorage) != 0) {
            flags |= BufferUsageFlags.AccelerationStructureStorageBitKhr;
        }

        if ((usage & BufferUsage.ShaderDeviceAddress) != 0) {
            flags |= BufferUsageFlags.ShaderDeviceAddressBit;
        }

        return flags;
    }

    public static ImageUsageFlags ToVulkan(TextureUsage usage) {
        var flags = ImageUsageFlags.None;

        if ((usage & TextureUsage.Sampled) != 0) {
            flags |= ImageUsageFlags.SampledBit;
        }

        if ((usage & TextureUsage.Storage) != 0) {
            flags |= ImageUsageFlags.StorageBit;
        }

        if ((usage & TextureUsage.ColourTarget) != 0) {
            flags |= ImageUsageFlags.ColorAttachmentBit;
        }

        if ((usage & TextureUsage.DepthStencilTarget) != 0) {
            flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if ((usage & TextureUsage.CopySource) != 0) {
            flags |= ImageUsageFlags.TransferSrcBit;
        }

        if ((usage & TextureUsage.CopyDestination) != 0) {
            flags |= ImageUsageFlags.TransferDstBit;
        }

        return flags;
    }

    public static VkFormat ToVulkan(VertexFormat format) => format switch {
        VertexFormat.Float32 => VkFormat.R32Sfloat,
        VertexFormat.Float32X2 => VkFormat.R32G32Sfloat,
        VertexFormat.Float32X3 => VkFormat.R32G32B32Sfloat,
        VertexFormat.Float32X4 => VkFormat.R32G32B32A32Sfloat,
        VertexFormat.Float16X2 => VkFormat.R16G16Sfloat,
        VertexFormat.Float16X4 => VkFormat.R16G16B16A16Sfloat,
        VertexFormat.UNorm8X4 => VkFormat.R8G8B8A8Unorm,
        VertexFormat.SNorm8X4 => VkFormat.R8G8B8A8SNorm,
        VertexFormat.UInt8X4 => VkFormat.R8G8B8A8Uint,
        VertexFormat.UInt32 => VkFormat.R32Uint,
        VertexFormat.UNorm16X2 => VkFormat.R16G16Unorm,
        VertexFormat.SNorm16X4 => VkFormat.R16G16B16A16SNorm,
        _ => VkFormat.Undefined
    };
}
