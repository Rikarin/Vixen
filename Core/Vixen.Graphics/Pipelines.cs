// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics;

/// <summary>Which of the conventional descriptor sets a binding belongs to.</summary>
/// <remarks>
///     <para>
///         The set index is not a free choice. Four sets, ordered by how often they change, so a
///         frame rebinds as little as possible: set 0 once, set 1 per view, set 2 per material, set 3
///         per draw. Vulkan and D3D12 both reward this and both punish the alternative, because
///         changing set <c>n</c> invalidates every set above it.
///     </para>
///     <para>
///         Raven marks a binding <c>[PerFrame]</c>, <c>[PerView]</c>, <c>[PerMaterial]</c> or
///         <c>[PerDraw]</c> and the index follows — one <c>BindingPlan</c> shared by both of its
///         backends and its reflection, so the set the RHI builds a layout from is the one the
///         module was decorated with ([07 § C](07-raven-shader-pipeline.md)). An unmarked binding is
///         per-material.
///     </para>
///     <para>
///         <strong>And a fifth, which is not part of that ordering.</strong>
///         <see cref="Bindless" /> is the one set nothing in a frame writes: a
///         <see cref="BindlessTable" /> owns it, updates individual descriptors in place, and hands
///         out indices that shaders subscript it with. It is last rather than first — where its
///         change frequency would put it — because the four below it are a convention every existing
///         shader is compiled against, and renumbering them to gain one slot would be renumbering
///         every set in the engine to move the one that never changes.
///     </para>
/// </remarks>
public enum DescriptorSetSlot : byte {
    /// <summary>Camera, time, the lighting environment. Bound once a frame.</summary>
    PerFrame = 0,

    /// <summary>Shadow matrices and view-dependent buffers. Bound once a view.</summary>
    PerView = 1,

    /// <summary>Textures and material constants. The default for an unmarked binding.</summary>
    PerMaterial = 2,

    /// <summary>Transforms and instance data, usually through a dynamic offset.</summary>
    PerDraw = 3,

    /// <summary>The unbounded descriptor array a <see cref="BindlessTable" /> owns.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Its own set because it cannot share one.</strong> The other four are written
    ///         per frame from a <c>DescriptorAllocator</c>, which is content-addressed: a set whose
    ///         write list differs by one byte is a different set. A table's descriptors are written
    ///         once each, incrementally, and there may be a million of them — so a table living in a
    ///         set that is reallocated whenever a uniform block moves would have to be written out
    ///         again every time it moved, which is the entire cost the table exists to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ A fifth bound set is not free. Vulkan guarantees only four, so
    ///         <see cref="GraphicsDeviceFeatures.HasBindless" /> requires
    ///         <see cref="GraphicsDeviceFeatures.MaxDescriptorSets" /> of at least five — which every
    ///         device with descriptor indexing has met so far, and which is checked rather than
    ///         assumed.
    ///     </para>
    /// </remarks>
    Bindless = 4
}

/// <summary>What kind of resource a descriptor binds.</summary>
public enum DescriptorKind : byte {
    /// <summary>A uniform (constant) buffer.</summary>
    UniformBuffer = 0,

    /// <summary>A uniform buffer bound with a per-draw offset.</summary>
    /// <remarks>
    ///     How per-draw transforms are bound without a descriptor set per object: one buffer, one
    ///     descriptor, and an offset supplied at bind time. The alternative allocates a set per draw
    ///     and is the single most common reason a Vulkan renderer is slower than its D3D11
    ///     predecessor.
    /// </remarks>
    DynamicUniformBuffer = 1,

    /// <summary>A read-write structured buffer.</summary>
    StorageBuffer = 2,

    /// <summary>A structured buffer bound with a per-draw offset.</summary>
    DynamicStorageBuffer = 3,

    /// <summary>A texture read through a sampler declared separately.</summary>
    SampledTexture = 4,

    /// <summary>A texture read and written without a sampler.</summary>
    StorageTexture = 5,

    /// <summary>A sampler on its own.</summary>
    Sampler = 6
}

/// <summary>What a sampled binding's texels are, and — for a sampler — what it does with them.</summary>
/// <remarks>
///     <para>
///         The half of a descriptor layout that Vulkan and GL infer and WebGPU insists on being told.
///         A Vulkan set layout says "sampled image" and the shader's own type says the rest; a WebGPU
///         bind group layout has to declare <em>filterable float, unfilterable float, depth, sint or
///         uint</em> up front, and a texture whose format disagrees is refused at bind time. So the
///         RHI carries it, the web backend uses it, and the desktop ones read past it.
///     </para>
///     <para>
///         On a <see cref="DescriptorKind.Sampler" /> binding it means the texture that sampler is
///         paired with, which is the same decision stated from the other end:
///         <see cref="Float" /> is an ordinary filtering sampler, <see cref="Depth" /> is a
///         shadow-comparison one, and the rest do not filter, because an integer or unfilterable-float
///         texture may not be read through a sampler that does. One field rather than two, because
///         those are not two independent choices — a comparison sampler that reads a colour texture is
///         not a thing any backend will build.
///     </para>
/// </remarks>
public enum DescriptorSampleType : byte {
    /// <summary>Floats a sampler may filter — an ordinary colour texture, and the default.</summary>
    Float = 0,

    /// <summary>
    ///     Floats a sampler may not filter, which is what a 32-bit float texture is unless the device
    ///     says otherwise.
    /// </summary>
    UnfilterableFloat = 1,

    /// <summary>Depth. On a sampler binding, a shadow-comparison sampler.</summary>
    Depth = 2,

    /// <summary>Signed integers.</summary>
    SInt = 3,

    /// <summary>Unsigned integers.</summary>
    UInt = 4
}

/// <summary>One binding in a descriptor set.</summary>
/// <param name="Binding">Its index within the set.</param>
/// <param name="Kind">What it binds.</param>
/// <param name="Stages">Which shader stages see it.</param>
/// <param name="Count">
///     How many descriptors, for an array binding. <c>0</c> means the length is not in the shader —
///     which is <em>two</em> different things depending on the kind, and
///     <see cref="DescriptorBindingExtensions.IsUnbounded" /> is where they are told apart.
/// </param>
/// <param name="SampleType">
///     What a <see cref="DescriptorKind.SampledTexture" /> holds, or what a
///     <see cref="DescriptorKind.Sampler" /> does — see <see cref="DescriptorSampleType" />. Ignored
///     for anything else, and required to be left alone there rather than set to something a backend
///     would have to invent a meaning for.
/// </param>
public readonly record struct DescriptorBinding(
    uint Binding,
    DescriptorKind Kind,
    ShaderStage Stages,
    int Count = 1,
    DescriptorSampleType SampleType = DescriptorSampleType.Float
) {
    /// <summary>Whether this declares the shadow-comparison sampler a shadow map is read through.</summary>
    public bool IsComparisonSampler => Kind == DescriptorKind.Sampler && SampleType == DescriptorSampleType.Depth;

    /// <summary>Whether a sampler bound here is allowed to filter.</summary>
    /// <remarks>
    ///     False for depth, integers and unfilterable floats — the three cases WebGPU calls a
    ///     non-filtering or comparison sampler binding, and the one place a filtering sampler bound by
    ///     habit is a validation failure rather than a slightly wrong picture.
    /// </remarks>
    public bool Filters => SampleType == DescriptorSampleType.Float;
}

/// <summary>The one question about a binding that cannot be asked as <c>Count == 0</c>.</summary>
public static class DescriptorBindingExtensions {
    /// <summary>Whether this binding is the unbounded array a <see cref="BindlessTable" /> is made of.</summary>
    /// <param name="binding">The binding.</param>
    /// <remarks>
    ///     <para>
    ///         <strong>Zero means two things, and which one depends on the kind.</strong> On a buffer
    ///         it means the block ends in a runtime-sized array — <c>buffer objects { Object items[]; }</c>
    ///         — which is <em>one</em> descriptor whose length the host decides when it binds a range.
    ///         Raven's reflection says exactly that, and every storage buffer in the shader library
    ///         arrives here as a zero. On a texture or a sampler there is no such thing as a
    ///         runtime-sized resource, so zero can only mean the other reading: an unbounded
    ///         descriptor array, which needs <see cref="GraphicsDeviceFeatures.HasBindless" /> and is
    ///         sized by <see cref="GraphicsDeviceFeatures.MaxBindlessDescriptors" />.
    ///     </para>
    ///     <para>
    ///         A method rather than the comparison written at each use site, because the two readings
    ///         are one <c>Math.Max(1, Count)</c> apart and the failure is not symmetric. Treating a
    ///         storage buffer's zero as unbounded puts an update-after-bind flag on a binding whose
    ///         feature nobody enabled, and the validation layers refuse the layout — which is how this
    ///         was found, on the culling shaders, on the first run against a real driver.
    ///     </para>
    /// </remarks>
    public static bool IsUnbounded(this DescriptorBinding binding) =>
        binding.Count == 0
        && binding.Kind is DescriptorKind.SampledTexture or DescriptorKind.StorageTexture or DescriptorKind.Sampler;
}

/// <summary>The shape of one descriptor set.</summary>
/// <param name="Slot">Which of the four conventional sets this is.</param>
/// <param name="Bindings">What it contains.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
/// <param name="BindlessCapacity">
///     How many descriptors an unbounded binding in this set holds, or <c>0</c> for the device's
///     <see cref="GraphicsDeviceFeatures.MaxBindlessDescriptors" />.
/// </param>
public readonly record struct DescriptorSetLayoutDescription(
    DescriptorSetSlot Slot,
    DescriptorBinding[] Bindings,
    string Name = "",
    int BindlessCapacity = 0
) {
    /// <summary>How long an unbounded binding in this set actually is, on a given device.</summary>
    /// <param name="features">What the device reported.</param>
    /// <remarks>
    ///     <para>
    ///         "Unbounded" is the shader's word. The shader declares a runtime array and never asks
    ///         how long it is; the layout has to state a number, and this is where that number comes
    ///         from — the host's, or the device's ceiling when the host had no opinion.
    ///     </para>
    ///     <para>
    ///         Worth stating rather than always taking the maximum, because the maximum is not free:
    ///         a descriptor pool is sized from the layout, and a desktop driver's ceiling is a number
    ///         with six digits in it. A host that knows it will hold a thousand textures should not
    ///         reserve a million descriptors to hold them, and a host that does not know should not
    ///         have to guess.
    ///     </para>
    /// </remarks>
    public int CapacityFor(in GraphicsDeviceFeatures features) =>
        BindlessCapacity > 0 ? BindlessCapacity : features.MaxBindlessDescriptors;

    /// <summary>Checks the layout is one a backend could build.</summary>
    /// <exception cref="ArgumentException">It is not.</exception>
    public void Validate() {
        ArgumentNullException.ThrowIfNull(Bindings);

        if (BindlessCapacity < 0) {
            throw new ArgumentException($"Descriptor set '{Name}' asked for a negative bindless capacity.");
        }

        var seen = new HashSet<uint>();

        foreach (var binding in Bindings) {
            if (!seen.Add(binding.Binding)) {
                throw new ArgumentException(
                    $"Descriptor set '{Name}' binds slot {binding.Binding} twice."
                );
            }

            if (binding.Stages == ShaderStage.None) {
                throw new ArgumentException(
                    $"Binding {binding.Binding} in '{Name}' is visible to no stage, so nothing could read it."
                );
            }

            if (binding.Count < 0) {
                throw new ArgumentException(
                    $"Binding {binding.Binding} in '{Name}' has a negative count."
                );
            }

            // A sample type on a buffer or a storage image is not a harmless extra. A storage image
            // declares a *format*, which is a different thing said in a different place, and a caller
            // who wrote DescriptorSampleType.Depth on one meant something no backend can build — so
            // saying so here is better than WebGPU ignoring it and the author believing it took.
            if (binding.SampleType != DescriptorSampleType.Float
                && binding.Kind is not (DescriptorKind.SampledTexture or DescriptorKind.Sampler)) {
                throw new ArgumentException(
                    $"Binding {binding.Binding} in '{Name}' is a {binding.Kind} with a "
                    + $"{binding.SampleType} sample type. Only a sampled texture and a sampler have one."
                );
            }
        }
    }
}

/// <summary>A range of push constants.</summary>
/// <param name="Stages">Which stages see it.</param>
/// <param name="Offset">Its offset within the block, in bytes.</param>
/// <param name="Size">Its size in bytes.</param>
public readonly record struct PushConstantRange(ShaderStage Stages, int Offset, int Size);

/// <summary>The descriptor sets and push constants a pipeline expects.</summary>
/// <param name="Sets">The set layouts, in slot order.</param>
/// <param name="PushConstants">The push-constant ranges.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct PipelineLayoutDescription(
    DescriptorSetLayoutHandle[] Sets,
    PushConstantRange[]? PushConstants = null,
    string Name = ""
);

/// <summary>One vertex element — an attribute, named to stay clear of System.Attribute.</summary>
/// <param name="Location">Its shader location.</param>
/// <param name="Format">Its type.</param>
/// <param name="Offset">Its offset within the vertex, in bytes.</param>
public readonly record struct VertexElement(uint Location, VertexFormat Format, int Offset);

/// <summary>One vertex buffer's layout.</summary>
/// <param name="Stride">Bytes between consecutive elements.</param>
/// <param name="Attributes">What the element contains.</param>
/// <param name="StepMode">Whether the element advances per vertex or per instance.</param>
public readonly record struct VertexBufferLayout(
    int Stride,
    VertexElement[] Attributes,
    VertexStepMode StepMode = VertexStepMode.Vertex
);

/// <summary>How triangles are turned into fragments.</summary>
/// <param name="Cull">Which faces to discard.</param>
/// <param name="FrontFace">Which winding is front.</param>
/// <param name="Fill">Filled or wireframe.</param>
/// <param name="DepthClamp">
///     Whether to clamp depth rather than clip it — what a shadow pass wants, so a caster in front
///     of the near plane still casts. Needs
///     <see cref="GraphicsDeviceFeatures.HasDepthClamp" />.
/// </param>
/// <param name="DepthBias">A constant added to depth, in depth units.</param>
/// <param name="DepthBiasSlope">A factor on the polygon's depth slope.</param>
public readonly record struct RasterizerState(
    CullMode Cull = CullMode.Back,
    FrontFace FrontFace = FrontFace.CounterClockwise,
    FillMode Fill = FillMode.Solid,
    bool DepthClamp = false,
    float DepthBias = 0f,
    float DepthBiasSlope = 0f
) {
    /// <summary>Cull back faces, no bias. What almost every draw wants.</summary>
    // `new()` rather than `new(...)` would be wrong here, and silently so. On a record struct whose
    // primary constructor parameters are all optional, `new()` binds the *implicit parameterless
    // struct constructor* — which zero-initialises and never runs the primary constructor at all — so
    // every documented default below would come out as its enum's zero value. That made
    // `BlendState.Opaque` mean "write no colour channels", and a backend built on it drew nothing at
    // all, with no error from anywhere. Passing one argument forces the primary constructor to bind.
    public static RasterizerState Default => new(CullMode.Back);

    /// <summary>Cull nothing — a two-sided material, or a full-screen pass.</summary>
    public static RasterizerState TwoSided => new(CullMode.None);
}

/// <summary>What the stencil test does on one side.</summary>
/// <param name="Compare">The comparison.</param>
/// <param name="Fail">What to do when the stencil test fails.</param>
/// <param name="DepthFail">What to do when stencil passes and depth fails.</param>
/// <param name="Pass">What to do when both pass.</param>
public readonly record struct StencilFaceState(
    CompareFunction Compare = CompareFunction.Always,
    StencilOperation Fail = StencilOperation.Keep,
    StencilOperation DepthFail = StencilOperation.Keep,
    StencilOperation Pass = StencilOperation.Keep
);

/// <summary>The depth and stencil tests.</summary>
/// <param name="DepthTest">Whether depth is tested.</param>
/// <param name="DepthWrite">Whether depth is written.</param>
/// <param name="DepthCompare">
///     The comparison. <see cref="CompareFunction.Greater" /> under the engine's reversed depth,
///     which is what <see cref="Default" /> uses.
/// </param>
/// <param name="StencilTest">Whether stencil is tested.</param>
/// <param name="Front">The stencil test for front faces.</param>
/// <param name="Back">The stencil test for back faces.</param>
/// <param name="StencilReadMask">Which stencil bits the comparison sees.</param>
/// <param name="StencilWriteMask">Which stencil bits may be written.</param>
public readonly record struct DepthStencilState(
    bool DepthTest = true,
    bool DepthWrite = true,
    CompareFunction DepthCompare = CompareFunction.Greater,
    bool StencilTest = false,
    StencilFaceState Front = default,
    StencilFaceState Back = default,
    byte StencilReadMask = 0xFF,
    byte StencilWriteMask = 0xFF
) {
    /// <summary>Test and write depth with the engine's reversed comparison.</summary>
    public static DepthStencilState Default => new(DepthTest: true);

    /// <summary>Test depth but do not write it — what a forward transparency pass wants.</summary>
    public static DepthStencilState TestOnly => new(DepthWrite: false);

    /// <summary>No depth at all — a full-screen pass, or UI.</summary>
    public static DepthStencilState Disabled => new(false, false, CompareFunction.Always);
}

/// <summary>How a fragment combines with what is already in the target.</summary>
/// <param name="Enabled">Whether to blend at all.</param>
/// <param name="SourceColour">The source colour factor.</param>
/// <param name="DestinationColour">The destination colour factor.</param>
/// <param name="ColourOperation">How the two combine.</param>
/// <param name="SourceAlpha">The source alpha factor.</param>
/// <param name="DestinationAlpha">The destination alpha factor.</param>
/// <param name="AlphaOperation">How the two alphas combine.</param>
/// <param name="WriteMask">Which channels may be written.</param>
public readonly record struct BlendState(
    bool Enabled = false,
    BlendFactor SourceColour = BlendFactor.One,
    BlendFactor DestinationColour = BlendFactor.Zero,
    BlendOperation ColourOperation = BlendOperation.Add,
    BlendFactor SourceAlpha = BlendFactor.One,
    BlendFactor DestinationAlpha = BlendFactor.Zero,
    BlendOperation AlphaOperation = BlendOperation.Add,
    ColourWriteMask WriteMask = ColourWriteMask.All
) {
    /// <summary>Overwrite. The default.</summary>
    public static BlendState Opaque => new(Enabled: false);

    /// <summary>Straight alpha blending.</summary>
    public static BlendState AlphaBlend => new(
        true,
        BlendFactor.SourceAlpha,
        BlendFactor.OneMinusSourceAlpha,
        BlendOperation.Add,
        BlendFactor.One,
        BlendFactor.OneMinusSourceAlpha
    );

    /// <summary>Premultiplied alpha blending.</summary>
    /// <remarks>
    ///     What the UI and the compositor use, and what any pipeline that filters or resamples
    ///     translucent images has to use: straight alpha produces dark halos wherever a transparent
    ///     texel's colour is interpolated with an opaque one.
    /// </remarks>
    public static BlendState PremultipliedAlpha => new(
        true,
        BlendFactor.One,
        BlendFactor.OneMinusSourceAlpha,
        BlendOperation.Add,
        BlendFactor.One,
        BlendFactor.OneMinusSourceAlpha
    );

    /// <summary>Additive — particles, glow.</summary>
    public static BlendState Additive => new(
        true,
        BlendFactor.SourceAlpha,
        BlendFactor.One,
        BlendOperation.Add,
        BlendFactor.One,
        BlendFactor.One
    );
}

/// <summary>One colour target a pipeline writes to.</summary>
/// <param name="Format">Its format. Must match the attachment the pipeline is used with.</param>
/// <param name="Blend">
///     How fragments combine with it. Omitting it means <see cref="BlendState.Opaque" /> — see
///     <see cref="EffectiveBlend" />, which is what a backend must read.
/// </param>
public readonly record struct ColourTargetState(PixelFormat Format, BlendState Blend = default) {
    /// <summary>The blend state to build the pipeline from.</summary>
    /// <remarks>
    ///     <para>
    ///         C# has no way to give a struct parameter a default other than all-zeros, and an
    ///         all-zero <see cref="BlendState" /> has <see cref="ColourWriteMask.None" /> — a target
    ///         that writes no channels at all. A caller who omitted the argument has never meant
    ///         that, and the failure is the worst kind: a pipeline that compiles, binds, draws, and
    ///         produces an untouched attachment, with no error from the API, the layers or the driver.
    ///     </para>
    ///     <para>
    ///         So a wholly default blend means opaque. Writing no channels deliberately stays
    ///         expressible — <c>new BlendState(WriteMask: ColourWriteMask.None)</c> differs from
    ///         <c>default</c> in its blend factors and is passed through unchanged.
    ///     </para>
    /// </remarks>
    public BlendState EffectiveBlend => Blend == default ? BlendState.Opaque : Blend;
}

/// <summary>What to create a graphics pipeline from.</summary>
/// <remarks>
///     Built ahead of time and complete: shaders, vertex layout, raster, depth-stencil, blend and
///     the formats it is compatible with. That is Vulkan's and D3D12's model, and it is what makes
///     the pre-warm in <c>docs/plan/05</c> possible — a build step records every description the
///     content needs, and boot creates them before the first frame instead of hitching on first
///     encounter.
/// </remarks>
/// <param name="Vertex">The vertex shader.</param>
/// <param name="Fragment">The fragment shader, or <see cref="ShaderHandle.Null" /> for a depth-only
/// pass.</param>
/// <param name="Layout">The descriptor sets and push constants it expects.</param>
/// <param name="ColourTargets">The colour targets, in order.</param>
/// <param name="VertexBuffers">The vertex buffer layouts, in binding order.</param>
/// <param name="Topology">What the vertices mean.</param>
/// <param name="Rasterizer">How triangles become fragments.</param>
/// <param name="DepthStencil">The depth and stencil tests.</param>
/// <param name="DepthFormat">The depth attachment's format, or
/// <see cref="PixelFormat.Undefined" /> for none.</param>
/// <param name="SampleCount">How many samples the attachments have.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct GraphicsPipelineDescription(
    ShaderHandle Vertex,
    ShaderHandle Fragment,
    PipelineLayoutHandle Layout,
    ColourTargetState[] ColourTargets,
    VertexBufferLayout[]? VertexBuffers = null,
    PrimitiveTopology Topology = PrimitiveTopology.TriangleList,
    RasterizerState Rasterizer = default,
    DepthStencilState DepthStencil = default,
    PixelFormat DepthFormat = PixelFormat.Undefined,
    int SampleCount = 1,
    string Name = ""
) {
    /// <summary>Checks the description is one a backend could compile.</summary>
    /// <exception cref="ArgumentException">It is not, with a message naming what is wrong.</exception>
    public void Validate() {
        if (!Vertex.IsValid) {
            throw new ArgumentException($"Pipeline '{Name}' has no vertex shader.");
        }

        ArgumentNullException.ThrowIfNull(ColourTargets);

        if (ColourTargets.Length == 0 && DepthFormat == PixelFormat.Undefined) {
            throw new ArgumentException(
                $"Pipeline '{Name}' writes to nothing — no colour targets and no depth attachment."
            );
        }

        foreach (var target in ColourTargets) {
            if (target.Format == PixelFormat.Undefined) {
                throw new ArgumentException($"Pipeline '{Name}' has a colour target with no format.");
            }

            if (target.Format.IsDepthStencil()) {
                throw new ArgumentException(
                    $"Pipeline '{Name}' uses depth format {target.Format} as a colour target."
                );
            }
        }

        if (DepthFormat != PixelFormat.Undefined && !DepthFormat.IsDepthStencil()) {
            throw new ArgumentException(
                $"Pipeline '{Name}' uses colour format {DepthFormat} as its depth attachment."
            );
        }

        if (DepthStencil.DepthTest && DepthFormat == PixelFormat.Undefined) {
            throw new ArgumentException(
                $"Pipeline '{Name}' tests depth but declares no depth attachment, so the test has nothing "
                + "to read."
            );
        }

        if (SampleCount <= 0 || (SampleCount & (SampleCount - 1)) != 0) {
            throw new ArgumentException(
                $"Pipeline '{Name}' asked for {SampleCount} samples; a sample count must be a power of two."
            );
        }

        if (VertexBuffers is null) {
            return;
        }

        var locations = new HashSet<uint>();

        foreach (var buffer in VertexBuffers) {
            if (buffer.Stride <= 0) {
                throw new ArgumentException($"Pipeline '{Name}' has a vertex buffer with no stride.");
            }

            foreach (var attribute in buffer.Attributes ?? []) {
                if (!locations.Add(attribute.Location)) {
                    throw new ArgumentException(
                        $"Pipeline '{Name}' binds vertex location {attribute.Location} twice."
                    );
                }

                if (attribute.Offset < 0 || attribute.Offset >= buffer.Stride) {
                    throw new ArgumentException(
                        $"Pipeline '{Name}' has vertex location {attribute.Location} at offset "
                        + $"{attribute.Offset}, which is outside its {buffer.Stride}-byte stride."
                    );
                }
            }
        }
    }
}

/// <summary>What to create a compute pipeline from.</summary>
/// <param name="Compute">The compute shader.</param>
/// <param name="Layout">The descriptor sets and push constants it expects.</param>
/// <param name="Name">A name for the debugger and the validation layers.</param>
public readonly record struct ComputePipelineDescription(
    ShaderHandle Compute,
    PipelineLayoutHandle Layout,
    string Name = ""
) {
    /// <summary>Checks the description is one a backend could compile.</summary>
    /// <exception cref="ArgumentException">It is not.</exception>
    public void Validate() {
        if (!Compute.IsValid) {
            throw new ArgumentException($"Compute pipeline '{Name}' has no shader.");
        }
    }
}

/// <summary>A rectangle of the render target that fragments are kept in.</summary>
/// <param name="X">The left edge, in pixels.</param>
/// <param name="Y">The top edge, in pixels.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public readonly record struct ScissorRect(int X, int Y, int Width, int Height) {
    /// <summary>The whole of a target of a given size.</summary>
    /// <param name="size">The target's size in pixels.</param>
    public static ScissorRect Full(Int2 size) => new(0, 0, size.X, size.Y);
}
