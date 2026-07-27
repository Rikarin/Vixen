// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Rendering fixtures, compared against committed reference images.
/// </summary>
/// <remarks>
///     <para>
///         The level of testing every other kind is a proxy for. A command-stream assertion proves
///         the backend emitted the calls somebody intended; only a picture proves those calls draw
///         what they were meant to. Every fixture here is small, is about one thing, and would go on
///         passing its unit tests while producing the wrong image.
///     </para>
///     <para>
///         Everything renders through the render graph, so the barriers and store actions it derives
///         are under test as well — those are exactly the sort of thing whose mistakes stay invisible
///         until they are catastrophic.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide, and a fixture that failed because of another test's validation error would
///         be the least useful failure in the suite.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class GoldenImageTests {
    /// <summary>A quad on the left, in the layout the mesh shader declares.</summary>
    static readonly float[] LeftQuad = [
        -0.8f, -0.8f, 1f, 0.2f, 0.1f, 1f,
        -0.1f, -0.8f, 0.2f, 1f, 0.1f, 1f,
        -0.8f, 0.8f, 0.1f, 0.2f, 1f, 1f,
        -0.1f, 0.8f, 1f, 1f, 0.2f, 1f
    ];

    static readonly ushort[] QuadIndices = [0, 1, 2, 2, 1, 3];

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    /// <remarks>
    ///     The same rule as the Vulkan suite's, five lines rather than a project reference between two
    ///     test assemblies. On a machine with no driver these fixtures should stand aside; on the CI
    ///     leg that installed lavapipe, a golden-image suite that quietly skipped would be a green
    ///     build asserting nothing about any picture at all.
    /// </remarks>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>
    ///     A clear, which is the simplest thing that can be wrong.
    /// </summary>
    /// <remarks>
    ///     It catches a surprising amount: a clear colour written to the wrong channel order, a
    ///     render area computed from the wrong extent, a store action derived as <c>DontCare</c> when
    ///     something reads the target afterwards.
    /// </remarks>
    [Fact]
    public void Clear() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("clear");

        owned.Graph.AddPass("clear", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.25f, 0.5f, 0.75f, 1f));
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        GoldenImage.Verify("clear", owned.Render(colour), Tolerance.Flat);
    }

    /// <summary>
    ///     One triangle with interpolated vertex colours.
    /// </summary>
    /// <remarks>
    ///     Coverage and interpolation at once. A flipped viewport, an inverted front face or a
    ///     mis-set scissor all move it; a wrong interpolation qualifier changes its gradient without
    ///     moving anything.
    /// </remarks>
    [Fact]
    public void Triangle() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("triangle");

        var pipeline = owned.Pipeline(
            owned.Shader("triangle.vert.spv", ShaderStage.Vertex),
            owned.Shader("triangle.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled
        );

        owned.Graph.AddPass("triangle", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(pipeline);
                context.CommandList.Draw(3);
            });
        });

        GoldenImage.Verify("triangle", owned.Render(colour), Tolerance.Interpolated);
    }

    /// <summary>
    ///     An indexed quad from a vertex buffer, moved by a push constant.
    /// </summary>
    /// <remarks>
    ///     Vertex-input strides and offsets, index buffers, and push constants — all three visible as
    ///     position and colour rather than as a call that was recorded.
    /// </remarks>
    [Fact]
    public void IndexedQuad() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("indexed quad");
        var vertices = owned.Buffer<float>(LeftQuad, BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        var pipeline = owned.Pipeline(
            owned.Shader("mesh.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            [
                new(
                    sizeof(float) * 6,
                    [new(0, VertexFormat.Float32X2, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 2)]
                )
            ],
            sizeof(float) * 2
        );

        var offset = new[] { 0.45f, 0f };

        owned.Graph.AddPass("quad", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.08f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(pipeline);

                context.CommandList.PushConstants(
                    ShaderStage.Vertex,
                    0,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(offset.AsSpan())
                );

                context.CommandList.BindVertexBuffer(0, vertices);
                context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt16);
                context.CommandList.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("indexed-quad", owned.Render(colour), Tolerance.Interpolated);
    }

    /// <summary>
    ///     Two overlapping quads, depth-tested under the engine's reversed-Z convention.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The fixture that would catch the single most expensive mistake in the codebase. Under
    ///         reversed-Z, <c>0</c> is far and the comparison is <c>Greater</c> — and a backend that
    ///         cleared depth to 1 or compared with <c>Less</c> renders a scene that is either entirely
    ///         present or entirely absent, with no error anywhere.
    ///     </para>
    ///     <para>
    ///         The near quad is drawn <em>second</em> deliberately. Drawing it first would produce the
    ///         same picture whether or not the depth test worked at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ReversedDepth() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("depth colour");
        var depth = owned.DepthTarget("depth");

        // A vertex shader taking a vec3, so that depth is data rather than a constant. The mesh
        // shader the other fixtures use writes z = 0 for every vertex, which would make this fixture
        // pass identically whether the depth test worked or not.
        var pipeline = owned.Pipeline(
            owned.Shader("depth.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Default,
            [
                new(
                    sizeof(float) * 7,
                    [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 3)]
                )
            ]
        );

        // Far: z = 0.2, the whole left two-thirds, blue.
        var far = owned.Buffer<float>(Quad(-0.9f, 0.3f, 0.2f, 0.2f, 0.4f, 1f), BufferUsage.Vertex);

        // Near: z = 0.8, overlapping it, orange. Under reversed-Z the larger z is nearer, so this
        // one wins where they overlap — and if the convention were inverted, it would lose.
        var near = owned.Buffer<float>(Quad(-0.3f, 0.9f, 0.8f, 1f, 0.55f, 0.1f), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("depth", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));

            // Zero is far. Clearing to 1 here is the classic mistake and depth-tests the scene away.
            pass.DepthAttachment(depth, LoadAction.Clear);
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(pipeline);
                context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt16);

                context.CommandList.BindVertexBuffer(0, far);
                context.CommandList.DrawIndexed(QuadIndices.Length);

                context.CommandList.BindVertexBuffer(0, near);
                context.CommandList.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("reversed-depth", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>
    ///     A translucent quad over an opaque one.
    /// </summary>
    /// <remarks>
    ///     Blend factors are the mapping most likely to be subtly wrong and least likely to be caught:
    ///     swapping source and destination alpha still blends, still looks plausible, and is wrong
    ///     everywhere the two differ.
    /// </remarks>
    [Fact]
    public void AlphaBlend() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("blend");
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);
        var offset = new[] { 0f, 0f };

        var opaque = owned.Pipeline(
            owned.Shader("mesh.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            [
                new(
                    sizeof(float) * 6,
                    [new(0, VertexFormat.Float32X2, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 2)]
                )
            ],
            sizeof(float) * 2
        );

        var blended = owned.Pipeline(
            owned.Shader("mesh.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.AlphaBlend,
            DepthStencilState.Disabled,
            [
                new(
                    sizeof(float) * 6,
                    [new(0, VertexFormat.Float32X2, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 2)]
                )
            ],
            sizeof(float) * 2
        );

        var under = owned.Buffer<float>(FlatQuad(-0.9f, 0.3f, 0.1f, 0.6f, 1f), BufferUsage.Vertex);
        var over = owned.Buffer<float>(FlatQuad(-0.3f, 0.9f, 1f, 0.4f, 0.1f, 0.5f), BufferUsage.Vertex);

        owned.Graph.AddPass("blend", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt16);

                // Push constants belong to a bound pipeline's layout, so the bind comes first. The
                // RHI refuses the other order rather than letting Vulkan call it undefined.
                context.CommandList.BindPipeline(opaque);

                context.CommandList.PushConstants(
                    ShaderStage.Vertex,
                    0,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(offset.AsSpan())
                );

                context.CommandList.BindVertexBuffer(0, under);
                context.CommandList.DrawIndexed(QuadIndices.Length);

                context.CommandList.BindPipeline(blended);

                context.CommandList.PushConstants(
                    ShaderStage.Vertex,
                    0,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(offset.AsSpan())
                );

                context.CommandList.BindVertexBuffer(0, over);
                context.CommandList.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("alpha-blend", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>Four vertices spanning a horizontal range, at one depth, in one colour.</summary>
    /// <remarks>For the depth fixture's <c>vec3</c> layout: x, y, z, then RGBA.</remarks>
    static float[] Quad(float left, float right, float z, float r, float g, float b, float a = 1f) => [
        left, -0.85f, z, r, g, b, a,
        right, -0.85f, z, r, g, b, a,
        left, 0.85f, z, r, g, b, a,
        right, 0.85f, z, r, g, b, a
    ];

    /// <summary>Four vertices for the <c>vec2</c> mesh layout: x, y, then RGBA.</summary>
    static float[] FlatQuad(float left, float right, float r, float g, float b, float a = 1f) => [
        left, -0.85f, r, g, b, a,
        right, -0.85f, r, g, b, a,
        left, 0.85f, r, g, b, a,
        right, 0.85f, r, g, b, a
    ];
}
