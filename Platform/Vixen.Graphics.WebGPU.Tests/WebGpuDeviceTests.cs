// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>The device, against an implementation that only writes down what it was asked.</summary>
/// <remarks>
///     Everything asserted here is code the browser surface runs unchanged, which is the point of
///     <see cref="IWebGpuBinding" /> and the reason these tests are worth more than their line count:
///     they are the only coverage the web path will ever get on a CI machine.
/// </remarks>
public class WebGpuDeviceTests {
    static WebGpuDevice Device(FakeWebGpuBinding binding, int framesInFlight = 2) =>
        new(binding, new WebGpuDeviceOptions { FramesInFlight = framesInFlight });

    [Fact]
    public void ADeviceReportsWhatTheBindingSaid() {
        var binding = new FakeWebGpuBinding(WebGpuLimits.Guaranteed with { MaxTextureDimension2D = 16384 });
        using var device = Device(binding);

        Assert.Equal(16384, device.Features.MaxTextureSize);
        Assert.Equal(AdapterKind.Discrete, device.Adapter.Kind);
        Assert.Equal("Fake", device.Adapter.Name);
        Assert.Equal(0ul, device.Adapter.DeviceMemory);
    }

    /// <summary>WebGPU has one queue, and all three of the RHI's submitters are it.</summary>
    [Fact]
    public void EveryQueueIsTheSameQueue() {
        using var device = Device(new());

        Assert.Same(device.GraphicsQueue, device.ComputeQueue);
        Assert.Same(device.GraphicsQueue, device.TransferQueue);
        Assert.False(device.Features.HasAsyncCompute);
    }

    [Fact]
    public void CreatingABufferAsksForOne() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        device.CreateBuffer(new(256, BufferUsage.Vertex | BufferUsage.CopyDestination, Name: "Mesh"));

        var call = Assert.Single(binding.OfName("CreateBuffer"));
        Assert.Equal("Mesh", call.Text);
        Assert.Equal(256, call.Values[0]);
        Assert.Equal((long)(WgpuBufferUsage.Vertex | WgpuBufferUsage.CopyDst), call.Values[1]);
    }

    [Fact]
    public void ATextureLargerThanTheDeviceAllowsIsRefused() {
        using var device = Device(new());

        var thrown = Assert.Throws<ArgumentException>(
            () => device.CreateTexture(new(PixelFormat.Rgba8UNorm, 32768, 32768, TextureUsage.Sampled, Name: "Huge"))
        );

        Assert.Contains("Huge", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>WebGPU fixes the sample counts at one and four; there is nothing to query.</summary>
    [Fact]
    public void EightSamplesIsRefused() {
        using var device = Device(new());

        Assert.Throws<ArgumentException>(
            () => device.CreateTexture(
                new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget, SampleCount: 8, Name: "Msaa")
            )
        );

        device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget, SampleCount: 4, Name: "Msaa")
        );
    }

    /// <summary>WebGPU has no binding arrays, so an array binding is refused rather than truncated.</summary>
    [Fact]
    public void ABindingArrayIsRefused() {
        using var device = Device(new());

        var thrown = Assert.Throws<NotSupportedException>(
            () => device.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerMaterial,
                    [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 16)],
                    "Textures"
                )
            )
        );

        Assert.Contains("HasBindless", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A SPIR-V module is told apart from WGSL by its magic number, because
    ///     <c>CreateShader</c> takes bytecode and a stage and nothing else.
    /// </summary>
    [Fact]
    public void SpirVIsToldApartFromWgslByItsHeader() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        device.CreateShader(ShaderStage.Vertex, [0x03, 0x02, 0x23, 0x07, 0, 0, 0, 0], "Spirv");
        Assert.Equal(WgpuShaderSource.SpirV, binding.LastShaderSource);

        device.CreateShader(ShaderStage.Vertex, "@vertex fn main() {}"u8, "Wgsl");
        Assert.Equal(WgpuShaderSource.Wgsl, binding.LastShaderSource);
    }

    // ── Push constants ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Push constants become the bind group after the caller's own, and the layout says so.
    /// </summary>
    [Fact]
    public void PushConstantsBecomeOneMoreBindGroup() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var set = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "Frame")
        );

        device.CreatePipelineLayout(new([set], [new(ShaderStage.Vertex, 0, 64)], "Layout"));

        var call = Assert.Single(binding.OfName("CreatePipelineLayout"));

        // One set of the caller's, plus the emulated push-constant group.
        Assert.Equal(2, call.Values[0]);
    }

    /// <summary>
    ///     A layout that uses every bind group the device has and also wants push constants is
    ///     refused, because WebGPU has nowhere left to put them.
    /// </summary>
    [Fact]
    public void PushConstantsWithNoBindGroupLeftAreRefused() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layouts = new DescriptorSetLayoutHandle[4];

        for (var index = 0; index < layouts.Length; index++) {
            layouts[index] = device.CreateDescriptorSetLayout(
                new((DescriptorSetSlot)index, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], $"Set{index}")
            );
        }

        var thrown = Assert.Throws<NotSupportedException>(
            () => device.CreatePipelineLayout(new(layouts, [new(ShaderStage.Vertex, 0, 64)], "Full"))
        );

        Assert.Contains("dynamic uniform offset", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanTheEmulatedBlockIsRefused() {
        using var device = Device(new());

        Assert.Throws<NotSupportedException>(
            () => device.CreatePipelineLayout(new([], [new(ShaderStage.Vertex, 0, 512)], "Big"))
        );
    }

    // ── Descriptor sets ─────────────────────────────────────────────────────────────────────

    /// <summary>A bind group is built whole or not at all, so an incomplete set has none.</summary>
    [Fact]
    public void ABindGroupAppearsOnlyWhenEveryBindingIsFilled() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "Material"
            )
        );

        var set = device.CreateDescriptorSet(layout, "Material");
        var buffer = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        var sampler = device.CreateSampler(SamplerDescription.LinearRepeat);

        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, buffer)]);
        Assert.Empty(binding.OfName("CreateBindGroup"));

        device.UpdateDescriptorSet(set, [DescriptorWrite.SamplerAt(1, sampler)]);
        Assert.Single(binding.OfName("CreateBindGroup"));
    }

    /// <summary>
    ///     WebGPU bind groups are immutable, so an update builds a new one and retires the old.
    /// </summary>
    [Fact]
    public void UpdatingARealisedSetRebuildsIt() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerDraw, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "Draw")
        );

        var set = device.CreateDescriptorSet(layout, "Draw");
        var first = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "A"));
        var second = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "B"));

        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, first)]);
        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, second)]);

        Assert.Equal(2, binding.OfName("CreateBindGroup").Count);
    }

    /// <summary>
    ///     A shadow map: a depth texture and a comparison sampler, declared as such, which is the
    ///     whole of what WebGPU asks for and what the RHI could not say until it carried a sample
    ///     type.
    /// </summary>
    [Fact]
    public void ADepthTextureAndAComparisonSamplerBindWhenDeclared() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [
                    new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth),
                    new(1, DescriptorKind.Sampler, ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth)
                ],
                "Shadow"
            )
        );

        var set = device.CreateDescriptorSet(layout, "Shadow");
        var view = device.CreateTextureView(ShadowMap(device));
        var sampler = device.CreateSampler(SamplerDescription.Shadow);

        device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, view), DescriptorWrite.SamplerAt(1, sampler)]);

        Assert.Single(binding.OfName("CreateBindGroup"));
    }

    /// <summary>
    ///     And the same map through a layout that never said so: the refusal names the declaration to
    ///     change, rather than leaving it to a browser console a frame later.
    /// </summary>
    [Fact]
    public void ADepthViewInAFloatBindingSaysWhichDeclarationIsWrong() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerView, [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)], "Shadow")
        );

        var set = device.CreateDescriptorSet(layout, "Shadow");
        var view = device.CreateTextureView(ShadowMap(device));

        var thrown = Assert.Throws<ArgumentException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, view)])
        );

        Assert.Contains("SampleType", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Depth", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A comparison sampler in an ordinary sampler binding is the other half of the same mismatch.</summary>
    [Fact]
    public void AComparisonSamplerInAFilteringBindingIsRefused() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerView, [new(0, DescriptorKind.Sampler, ShaderStage.Fragment)], "Shadow")
        );

        var set = device.CreateDescriptorSet(layout, "Shadow");
        var sampler = device.CreateSampler(SamplerDescription.Shadow);

        var thrown = Assert.Throws<ArgumentException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.SamplerAt(0, sampler)])
        );

        Assert.Contains("compares", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A binding that may not filter is given the sampler everything defaults to, which WebGPU
    ///     refuses — so this does, by name.
    /// </summary>
    [Fact]
    public void AFilteringSamplerInANonFilteringBindingIsRefused() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerView,
                [new(0, DescriptorKind.Sampler, ShaderStage.Fragment, SampleType: DescriptorSampleType.UInt)],
                "Ids"
            )
        );

        var set = device.CreateDescriptorSet(layout, "Ids");
        var sampler = device.CreateSampler(SamplerDescription.LinearClamp);

        var thrown = Assert.Throws<ArgumentException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.SamplerAt(0, sampler)])
        );

        Assert.Contains("may not filter", thrown.Message, StringComparison.Ordinal);
    }

    static TextureHandle ShadowMap(WebGpuDevice device) =>
        device.CreateTexture(
            new(PixelFormat.Depth32Float, 512, 512, TextureUsage.DepthStencilTarget | TextureUsage.Sampled, Name: "Map")
        );

    // ── Lifetime ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A destroyed handle is invalid immediately and the object is released later — which is what
    ///     lets a renderer recreate a buffer mid-frame without waiting.
    /// </summary>
    [Fact]
    public void DestroyingIsDeferredByTheFramesInFlight() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding, 2);

        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));
        binding.Clear();

        device.Destroy(buffer);
        Assert.Empty(binding.OfName("Release"));

        // Two whole frames, because that is what FramesInFlight means: beginning frame N is what
        // says frame N - 2 has finished, and until frame 2 begins there is no such frame.
        device.BeginFrame();
        device.EndFrame();
        Assert.Empty(binding.OfName("Release"));

        device.BeginFrame();
        device.EndFrame();
        Assert.Empty(binding.OfName("Release"));

        device.BeginFrame();
        Assert.Single(binding.OfName("Release"));
    }

    [Fact]
    public void ADestroyedHandleIsInvalidStraightAway() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, MemoryAccess.HostUpload, "Mesh"));
        device.Destroy(buffer);

        Assert.Throws<ArgumentException>(() => device.Write(buffer, 0, [1, 2, 3, 4]));
    }

    [Fact]
    public void EverythingCreatedIsReleasedWhenTheDeviceIs() {
        var binding = new FakeWebGpuBinding();
        var device = Device(binding);

        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));
        var texture = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "Albedo"));
        device.CreateTextureView(texture);
        device.CreateSampler(SamplerDescription.LinearClamp);

        Assert.Equal(4, device.LiveResourceCount);
        device.Dispose();

        Assert.Equal(0, binding.LiveObjects);
        _ = buffer;
    }

    // ── Host access ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeviceLocalBufferCannotBeWrittenByTheHost() {
        using var device = Device(new());
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));

        Assert.Throws<InvalidOperationException>(() => device.Write(buffer, 0, [1, 2, 3, 4]));
    }

    [Fact]
    public void AWritePastTheEndIsRefused() {
        using var device = Device(new());
        var buffer = device.CreateBuffer(new(8, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));

        Assert.Throws<ArgumentOutOfRangeException>(() => device.Write(buffer, 6, [1, 2, 3, 4]));
    }

    /// <summary>
    ///     queue.writeBuffer wants a multiple of four at a multiple of four, and callers do not know
    ///     that — so an unaligned write is widened and the padding is zeroed rather than left as
    ///     whatever followed the caller's data.
    /// </summary>
    [Fact]
    public void AnUnalignedWriteIsWidenedAndZeroPadded() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var buffer = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        device.Write(buffer, 5, [0xAA, 0xBB, 0xCC]);

        // Widened to the enclosing four-byte window — offset 4, four bytes — and the byte before
        // the caller's data is zeroed rather than left as whatever was there.
        var call = Assert.Single(binding.OfName("WriteBuffer"));
        Assert.Equal(4, call.Values[1]);
        Assert.Equal(4, call.Values[2]);
        Assert.Equal<byte[]>([0, 0xAA, 0xBB, 0xCC], binding.LastWrite);
    }

    [Fact]
    public void AnAlignedWriteIsPassedStraightThrough() {
        var binding = new FakeWebGpuBinding();
        using var device = Device(binding);

        var buffer = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        device.Write(buffer, 8, [1, 2, 3, 4]);

        var call = Assert.Single(binding.OfName("WriteBuffer"));
        Assert.Equal(8, call.Values[1]);
        Assert.Equal(4, call.Values[2]);
    }

    /// <summary>
    ///     A browser cannot block on a map, and the message says what to do instead rather than
    ///     returning zeroes that look like data.
    /// </summary>
    [Fact]
    public void AReadbackThatCannotCompleteSaysSo() {
        var binding = new FakeWebGpuBinding { CanWait = false };
        using var device = Device(binding);

        var buffer = device.CreateBuffer(
            new(64, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Readback")
        );

        var thrown = Assert.Throws<NotSupportedException>(() => device.Read(buffer, 0, new byte[4]));
        Assert.Contains("a frame early", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingANonReadbackBufferIsRefused() {
        using var device = Device(new());
        var buffer = device.CreateBuffer(new(64, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));

        Assert.Throws<InvalidOperationException>(() => device.Read(buffer, 0, new byte[4]));
    }

    // ── Swapchain ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASwapChainNeedsASurface() {
        using var device = Device(new(hasSurface: false));

        Assert.Throws<InvalidOperationException>(
            () => device.CreateSwapChain(new(SurfaceHandle.None, new Int2(800, 600)))
        );
    }

    [Fact]
    public void ASwapChainConfiguresItsSurface() {
        var binding = new FakeWebGpuBinding(hasSurface: true);
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(1280, 720)));

        var call = Assert.Single(binding.OfName("ConfigureSurface"));
        Assert.Equal(1280, call.Values[2]);
        Assert.Equal(720, call.Values[3]);
        Assert.Equal((long)WgpuCompositeAlphaMode.Opaque, call.Values[5]);
        Assert.Equal(new Int2(1280, 720), swapChain.Size);
    }

    /// <summary>
    ///     A surface texture belongs to the surface, so each acquire wraps a fresh pair of handles
    ///     and the present that follows retires them.
    /// </summary>
    [Fact]
    public void AcquiringAndPresentingLeavesNoHandlesBehind() {
        var binding = new FakeWebGpuBinding(hasSurface: true);
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(64, 64)));

        var before = device.LiveResourceCount;

        for (var frame = 0; frame < 4; frame++) {
            Assert.Equal(SwapChainStatus.Ready, swapChain.AcquireNextImage(out var view));
            Assert.True(view.IsValid);
            Assert.True(swapChain.CurrentTexture.IsValid);
            Assert.Equal(SwapChainStatus.Ready, swapChain.Present());
        }

        Assert.Equal(before, device.LiveResourceCount);
        Assert.Equal(4, binding.OfName("PresentSurface").Count);
    }

    [Fact]
    public void AnOutOfDateSurfaceIsReportedAndNotThrown() {
        var binding = new FakeWebGpuBinding(hasSurface: true) { NextSurfaceStatus = WgpuSurfaceStatus.Outdated };
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(64, 64)));

        Assert.Equal(SwapChainStatus.OutOfDate, swapChain.AcquireNextImage(out var view));
        Assert.False(view.IsValid);
    }

    [Fact]
    public void PresentingWithoutAcquiringIsRefused() {
        var binding = new FakeWebGpuBinding(hasSurface: true);
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(64, 64)));

        Assert.Throws<InvalidOperationException>(() => swapChain.Present());
    }

    [Fact]
    public void ResizingReconfiguresTheSurface() {
        var binding = new FakeWebGpuBinding(hasSurface: true);
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(64, 64)));

        swapChain.Resize(new(320, 240));

        Assert.Equal(2, binding.OfName("ConfigureSurface").Count);
        Assert.Equal(new Int2(320, 240), swapChain.Size);
    }

    /// <summary>
    ///     What was asked for cannot always be had, so the swapchain reads the surface's preference
    ///     back — which is what ISwapChain.Format documents.
    /// </summary>
    [Fact]
    public void AFormatWebGpuLacksFallsBackToTheSurfacePreference() {
        var binding = new FakeWebGpuBinding(hasSurface: true);
        using var device = Device(binding);
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new Int2(64, 64), PixelFormat.Rgba16UNorm));

        Assert.Equal(PixelFormat.Bgra8UNorm, swapChain.Format);
    }
}
