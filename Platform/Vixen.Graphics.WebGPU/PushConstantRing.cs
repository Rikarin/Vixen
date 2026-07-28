// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>Push constants, on an API that has none.</summary>
/// <remarks>
///     <para>
///         WebGPU has no push constants and no plan for them: the specification's answer is a
///         uniform buffer. So the backend keeps one, and every <c>PushConstants</c> takes a fresh
///         aligned slice of it, writes the whole block there, and binds it with a dynamic offset.
///         That is what a draw's constants have to be — a range no other draw is also writing — and
///         it is why a ring rather than a single buffer: overwriting one buffer per draw would have
///         every draw in the frame read the last draw's values.
///     </para>
///     <para>
///         <b>Where the block is bound is the part a shader has to agree with.</b> It is bind group
///         <c>PipelineLayoutDescription.Sets.Length</c> — immediately after the caller's own sets —
///         binding 0. That is where SPIRV-Cross puts a Vulkan push-constant block when it emits WGSL,
///         so a module that came through Raven's cross-compilation ([07](../../docs/plan/07-raven-shader-pipeline.md))
///         already declares it there. A pipeline layout that uses every bind group the device has and
///         <em>also</em> wants push constants is refused at creation, with that arithmetic in the
///         message, because there is nowhere left to put it.
///     </para>
///     <para>
///         Created lazily, on the first pipeline layout that declares push constants. A renderer that
///         does not use them pays nothing, which on the web is a quarter of a megabyte it does not
///         download the need for.
///     </para>
/// </remarks>
sealed class PushConstantRing {
    readonly IWebGpuBinding binding;
    readonly int slotSize;
    readonly int slotsPerFrame;
    readonly int framesInFlight;

    int cursor;
    int frameBase;

    internal PushConstantRing(IWebGpuBinding binding, int slotsPerFrame, int framesInFlight) {
        ArgumentNullException.ThrowIfNull(binding);

        this.binding = binding;
        this.slotsPerFrame = Math.Max(1, slotsPerFrame);
        this.framesInFlight = Math.Max(1, framesInFlight);

        // A dynamic offset has to satisfy minUniformBufferOffsetAlignment, which is 256 on every
        // implementation that has ever reported it. So a 128-byte block occupies a 256-byte slot;
        // packing them tighter is not an option the API leaves open.
        var alignment = Math.Max(1, binding.Limits.MinUniformBufferOffsetAlignment);
        slotSize = (WebGpuCapabilities.PushConstantSize + alignment - 1) / alignment * alignment;

        Buffer = binding.CreateBuffer(
            new(
                (long)slotSize * this.slotsPerFrame * this.framesInFlight,
                WgpuBufferUsage.Uniform | WgpuBufferUsage.CopyDst,
                "PushConstants"
            )
        );

        Layout = binding.CreateBindGroupLayout(
            new(
                [
                    new(
                        0,
                        WgpuShaderStage.Vertex | WgpuShaderStage.Fragment | WgpuShaderStage.Compute,
                        WgpuBufferBindingType.Uniform,
                        HasDynamicOffset: true
                    )
                ],
                "PushConstants"
            )
        );

        BindGroup = binding.CreateBindGroup(
            new(
                Layout,
                [new(0, Buffer, 0, WebGpuCapabilities.PushConstantSize)],
                "PushConstants"
            )
        );
    }

    /// <summary>The bind group layout every pipeline layout with push constants ends with.</summary>
    internal WebGpuObject Layout { get; }

    /// <summary>The bind group a push-constant write is bound through.</summary>
    internal WebGpuObject BindGroup { get; }

    /// <summary>The buffer behind it.</summary>
    internal WebGpuObject Buffer { get; }

    /// <summary>Points the ring at a frame's slice and empties it.</summary>
    /// <param name="frameSlot">Which of the frames in flight is being recorded.</param>
    /// <remarks>
    ///     Called from <c>BeginFrame</c>, which is after the frame that last used this slice has
    ///     retired — so the slots being handed out again are ones no submitted work still reads.
    /// </remarks>
    internal void BeginFrame(int frameSlot) {
        frameBase = frameSlot % framesInFlight * slotsPerFrame * slotSize;
        cursor = frameBase;
    }

    /// <summary>Takes a slot, writes a block into it and returns the offset to bind with.</summary>
    /// <param name="block">The whole push-constant block.</param>
    /// <exception cref="InvalidOperationException">The frame's slice is full.</exception>
    internal uint Allocate(ReadOnlySpan<byte> block) {
        var end = frameBase + slotsPerFrame * slotSize;

        if (cursor + slotSize > end) {
            throw new InvalidOperationException(
                $"More than {slotsPerFrame} push-constant writes in one frame. WebGPU has no push "
                + "constants, so each one takes a slot of an aligned ring buffer; the ring is sized at "
                + "device creation because growing it mid-frame means allocating while the GPU is busy. "
                + "Raise WebGpuDeviceOptions.PushConstantSlotsPerFrame."
            );
        }

        var offset = cursor;
        cursor += slotSize;
        binding.WriteBuffer(Buffer, offset, block);

        return (uint)offset;
    }

    /// <summary>Releases everything it owns.</summary>
    internal void Dispose() {
        binding.Release(WebGpuObjectKind.BindGroup, BindGroup);
        binding.Release(WebGpuObjectKind.BindGroupLayout, Layout);
        binding.Release(WebGpuObjectKind.Buffer, Buffer);
    }
}
