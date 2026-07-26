// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     An indexed draw from a vertex buffer, positioned by a push constant.
/// </summary>
/// <remarks>
///     Between them these cover the parts of the pipeline that the gl_VertexIndex triangle does not
///     reach at all: vertex-input bindings and attributes, index buffers, and push constants. Each is
///     asserted by <em>where the picture ends up</em>, because a vertex attribute pointed at the
///     wrong offset and a push constant written to the wrong stage both draw something.
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanDrawTests {
    const int Side = 64;
    const int Bytes = Side * Side * 4;

    /// <summary>A quad on the left half of clip space, in the layout the shader declares.</summary>
    static readonly float[] Quad = [
        -0.9f, -0.9f, 1f, 0f, 1f, 1f,
        -0.1f, -0.9f, 1f, 0f, 1f, 1f,
        -0.9f, 0.9f, 1f, 0f, 1f, 1f,
        -0.1f, 0.9f, 1f, 0f, 1f, 1f
    ];

    static readonly ushort[] Indices = [0, 1, 2, 2, 1, 3];

    /// <summary>
    ///     The quad is drawn where the vertex buffer put it, and a push constant moves it.
    /// </summary>
    /// <remarks>
    ///     Two draws, and the assertion is that they land in different halves. A push constant that
    ///     never reached the shader would draw the same picture twice — which a single draw checked
    ///     against "something was rendered" would call a pass.
    /// </remarks>
    [Fact]
    public void AnIndexedDrawLandsWhereItsPushConstantPutsIt() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;
        VulkanDiagnostics.Reset();

        var left = Render(owned, 0f);
        var right = Render(owned, 1f);

        Assert.True(Lit(left, Side / 4, Side / 2), "The quad is not on the left half with no offset.");
        Assert.False(Lit(left, Side * 3 / 4, Side / 2), "Something was drawn on the right half.");

        Assert.True(Lit(right, Side * 3 / 4, Side / 2), "A push constant of +1 did not move the quad right.");
        Assert.False(Lit(right, Side / 4, Side / 2), "The quad is still on the left after being offset.");

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "Drawing produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>
    ///     A colour attribute read from the wrong offset in the vertex still draws — in the wrong
    ///     colour. The quad's vertices are all opaque magenta, which no other part of the fixture
    ///     produces.
    /// </summary>
    [Fact]
    public void TheVertexColourAttributeArrivesIntact() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var pixels = Render(owned, 0f);
        var offset = (((Side / 2) * Side) + (Side / 4)) * 4;

        Assert.InRange(pixels[offset], 250, 255);
        Assert.InRange(pixels[offset + 1], 0, 4);
        Assert.InRange(pixels[offset + 2], 250, 255);
        Assert.InRange(pixels[offset + 3], 250, 255);
    }

    static byte[] Render(VulkanDevice device, float offsetX) {
        var vertexShader = device.CreateShader(ShaderStage.Vertex, TestShaders.MeshVertex, "mesh vertex");
        var fragmentShader = device.CreateShader(ShaderStage.Fragment, TestShaders.MeshFragment, "mesh fragment");

        var layout = device.CreatePipelineLayout(new(
            [],
            [new(ShaderStage.Vertex, 0, sizeof(float) * 2)],
            "mesh layout"
        ));

        var pipeline = device.CreateGraphicsPipeline(new(
            vertexShader,
            fragmentShader,
            layout,
            [new(PixelFormat.Rgba8UNorm)],
            [
                new(
                    sizeof(float) * 6,
                    [
                        new(0, VertexFormat.Float32X2, 0),
                        new(1, VertexFormat.Float32X4, sizeof(float) * 2)
                    ]
                )
            ],
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "mesh"
        ));

        var vertices = device.CreateBuffer(new(
            Quad.Length * sizeof(float),
            BufferUsage.Vertex,
            MemoryAccess.HostUpload,
            "quad vertices"
        ));

        var indices = device.CreateBuffer(new(
            Indices.Length * sizeof(ushort),
            BufferUsage.Index,
            MemoryAccess.HostUpload,
            "quad indices"
        ));

        device.Write(vertices, 0, MemoryMarshal.AsBytes(Quad.AsSpan()));
        device.Write(indices, 0, MemoryMarshal.AsBytes(Indices.AsSpan()));

        var target = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "mesh target"
        ));

        var view = device.CreateTextureView(target);

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "mesh readback")
        );

        var push = new float[] { offsetX, 0f };

        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Graphics, "mesh")) {
            list.Barrier(new(
                [
                    new(vertices, ResourceState.HostAccess, ResourceState.VertexInput),
                    new(indices, ResourceState.HostAccess, ResourceState.VertexInput)
                ],
                [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]
            ));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0f, 0f, 0f, 1f))],
                name: "mesh pass"
            ));

            list.BindPipeline(pipeline);
            list.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(push.AsSpan()));
            list.BindVertexBuffer(0, vertices);
            list.BindIndexBuffer(indices, IndexFormat.UInt16);
            list.DrawIndexed(Indices.Length);
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

        device.Destroy(readback);
        device.Destroy(view);
        device.Destroy(target);
        device.Destroy(indices);
        device.Destroy(vertices);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(fragmentShader);
        device.Destroy(vertexShader);

        return pixels;
    }

    static bool Lit(byte[] pixels, int x, int y) {
        var offset = ((y * Side) + x) * 4;
        return pixels[offset] > 8 || pixels[offset + 1] > 8 || pixels[offset + 2] > 8;
    }
}
