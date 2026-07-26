// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Pipelines, and a triangle drawn with one.
/// </summary>
/// <remarks>
///     The picture is the point. Every other test in this project can pass with a backend that
///     records the right calls and renders nothing — a wrong front face, a wrong viewport sign, a
///     colour target the pipeline does not agree with. Reading the pixels back is the only assertion
///     that cannot.
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanPipelineTests {
    const int Side = 64;
    const int Bytes = Side * Side * 4;

    static bool TryOpen(bool renderPassObjects, out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(
            new() { PreferRenderPassObjects = renderPassObjects },
            out device,
            out reason
        );

    [Fact]
    public void AShaderModuleIsCreated() {
        VulkanRequirement.Available(TryOpen(false, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var shader = owned.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "triangle vertex");
        Assert.True(shader.IsValid);
        owned.Destroy(shader);
    }

    /// <summary>
    ///     The RHI never parses shader source, so a module that is not a whole number of SPIR-V words
    ///     is either truncated or not SPIR-V — and saying so beats handing it to the driver.
    /// </summary>
    [Fact]
    public void MalformedBytecodeIsRefused() {
        VulkanRequirement.Available(TryOpen(false, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.Throws<ArgumentException>(
            () => owned.CreateShader(ShaderStage.Vertex, new byte[7], "truncated")
        );

        Assert.Throws<ArgumentException>(
            () => owned.CreateShader(ShaderStage.Vertex, [], "empty")
        );
    }

    [Fact]
    public void APushConstantRangeBeyondTheDeviceLimitIsRefused() {
        VulkanRequirement.Available(TryOpen(false, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var thrown = Assert.Throws<ArgumentException>(
            () => owned.CreatePipelineLayout(
                new([], [new(ShaderStage.Vertex, 0, owned.Features.MaxPushConstantSize + 4)], "too big")
            )
        );

        Assert.Contains("push-constant", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A triangle, rendered offscreen and read back, on both pass paths.
    /// </summary>
    /// <remarks>
    ///     The assertions are about geometry rather than exact colour: the centre is inside the
    ///     triangle and must not be the clear colour, and the corners are outside it and must be. That
    ///     is what catches a flipped viewport, an inverted front face, or a scissor that shrank the
    ///     render area — each of which draws something, and none of which a call-recording test can
    ///     see.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATriangleIsDrawnWhereItShouldBe(bool renderPassObjects) {
        VulkanRequirement.Available(TryOpen(renderPassObjects, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.SkipWhen(
            renderPassObjects && owned.UsesDynamicRendering,
            "the device refused to take the render-pass-object path"
        );

        VulkanDiagnostics.Reset();

        var vertex = owned.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "triangle vertex");
        var fragment = owned.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "triangle fragment");
        var layout = owned.CreatePipelineLayout(new([], null, "triangle layout"));

        var pipeline = owned.CreateGraphicsPipeline(new(
            vertex,
            fragment,
            layout,
            [new(PixelFormat.Rgba8UNorm)],

            // No vertex buffers: the shader builds the triangle from gl_VertexIndex, which keeps this
            // about the pipeline and the pass rather than about vertex-input translation.
            VertexBuffers: null,

            // Both faces, so that a front-face or winding mistake shows up as a wrong *picture* in the
            // dedicated winding test below rather than as an empty one here.
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "triangle"
        ));

        var target = owned.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "triangle target"
        ));

        var view = owned.CreateTextureView(target);

        var readback = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "triangle readback")
        );

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Graphics, "triangle")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0f, 0f, 0f, 1f))],
                name: "triangle pass"
            ));

            list.BindPipeline(pipeline);
            list.Draw(3);
            list.EndRenderPass();

            list.Barrier(new([], [new(target, ResourceState.ColourTarget, ResourceState.CopySource)]));
            list.CopyTextureToBuffer(new(target), new(Side, Side, 1), readback, 0);
            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var pixels = new byte[Bytes];
        owned.Read(readback, 0, pixels);

        Assert.True(Lit(pixels, Side / 2, Side / 2), "The centre of the target is not covered.");
        Assert.False(Lit(pixels, 1, 1), "The top-left corner is outside the triangle and was painted.");
        Assert.False(Lit(pixels, Side - 2, 1), "The top-right corner is outside the triangle.");

        // The clear colour reached the far edges, which a render area computed from the wrong
        // extent — the texture's base size rather than the view's — would not.
        Assert.False(Lit(pixels, Side - 2, Side - 2), "The bottom-right corner is outside the triangle.");

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "Drawing produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        owned.Destroy(readback);
        owned.Destroy(view);
        owned.Destroy(target);
        owned.Destroy(pipeline);
        owned.Destroy(layout);
        owned.Destroy(fragment);
        owned.Destroy(vertex);
    }

    /// <summary>
    ///     The same triangle with back-face culling, drawn once each way round.
    /// </summary>
    /// <remarks>
    ///     This is what actually pins the winding and viewport conventions together. The shader emits
    ///     its vertices in one order; with the engine's counter-clockwise front face and its +Y-up
    ///     viewport, exactly one of <c>Cull.Front</c> and <c>Cull.Back</c> may produce a picture. If
    ///     the viewport flip and the front-face convention disagreed, both would cull or neither
    ///     would — and every other test in this file would still pass.
    /// </remarks>
    [Fact]
    public void CullingRemovesExactlyOneWinding() {
        VulkanRequirement.Available(TryOpen(false, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var front = Draw(owned, CullMode.Front);
        var back = Draw(owned, CullMode.Back);

        Assert.True(
            front != back,
            "Culling front faces and culling back faces produced the same picture, so the front-face "
            + "convention and the viewport's Y flip are not agreeing with each other."
        );
    }

    static bool Draw(VulkanDevice device, CullMode cull) {
        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "fragment");
        var layout = device.CreatePipelineLayout(new([], null, "layout"));

        var pipeline = device.CreateGraphicsPipeline(new(
            vertex,
            fragment,
            layout,
            [new(PixelFormat.Rgba8UNorm)],
            Rasterizer: new(cull),
            DepthStencil: DepthStencilState.Disabled,
            Name: $"cull {cull}"
        ));

        var target = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "target"
        ));

        var view = device.CreateTextureView(target);

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback")
        );

        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, $"cull {cull}")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0f, 0f, 0f, 1f))],
                name: "pass"
            ));

            list.BindPipeline(pipeline);
            list.Draw(3);
            list.EndRenderPass();

            list.Barrier(new([], [new(target, ResourceState.ColourTarget, ResourceState.CopySource)]));
            list.CopyTextureToBuffer(new(target), new(Side, Side, 1), readback, 0);
            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[Bytes];
        device.Read(readback, 0, pixels);
        var drew = Lit(pixels, Side / 2, Side / 2);

        device.Destroy(readback);
        device.Destroy(view);
        device.Destroy(target);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(fragment);
        device.Destroy(vertex);

        return drew;
    }

    /// <summary>Whether a texel is anything other than the black it was cleared to.</summary>
    static bool Lit(byte[] pixels, int x, int y) {
        var offset = ((y * Side) + x) * 4;
        return pixels[offset] > 8 || pixels[offset + 1] > 8 || pixels[offset + 2] > 8;
    }
}
