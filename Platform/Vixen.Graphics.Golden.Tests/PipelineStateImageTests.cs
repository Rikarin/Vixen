// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     One fixture per piece of pipeline state, each about a bit that a command-stream assertion
///     cannot see.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why there are so many of these and why they are so small.</b> Every state bit here is
///         one a backend can silently ignore. Recording <c>BindPipeline</c> proves the call was made;
///         it proves nothing about whether the driver was told to cull the right face, blend with the
///         right factor, or compare depth the right way round. <c>docs/plan/05</c> § Cross-backend
///         equivalence names this exact class of bug — "a backend silently ignores a state bit" — and
///         a picture is the only thing that catches it.
///     </para>
///     <para>
///         Each is about <em>one</em> bit, and each is arranged so that getting that bit wrong
///         changes the picture rather than shading it differently. A fixture whose sabotage produces
///         a two-level colour shift is a fixture that will be within tolerance on the next driver.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class PipelineStateImageTests {
    static readonly ushort[] QuadIndices = [0, 1, 2, 2, 1, 3];

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
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

    // ── Rasterisation ───────────────────────────────────────────────────────────────────────

    /// <summary>Culling back faces keeps the counter-clockwise triangle and drops the other.</summary>
    /// <remarks>
    ///     <para>
    ///         The pair of this and <see cref="CullFront" /> is what pins the winding convention
    ///         against the viewport's Y flip, and neither is load-bearing alone: a backend that
    ///         inverted <em>both</em> the winding and the cull face would pass either one on its own
    ///         and fail the pair.
    ///     </para>
    ///     <para>
    ///         Two triangles side by side, wound opposite ways. Whichever survives says which winding
    ///         the rasteriser called front — and the OpenGL backend has to invert this deliberately,
    ///         because the Y flip that makes its clip space Vulkan's also reverses winding.
    ///     </para>
    /// </remarks>
    [Fact]
    public void CullBack() => Culling("cull-back", CullMode.Back);

    /// <summary>Culling front faces keeps the clockwise one instead.</summary>
    [Fact]
    public void CullFront() => Culling("cull-front", CullMode.Front);

    /// <summary>A triangle strip is four vertices and two triangles, not four separate ones.</summary>
    /// <remarks>
    ///     A backend that mapped every topology to <c>TriangleList</c> draws one triangle out of the
    ///     four vertices and drops the rest, which looks like a mesh with a missing face.
    /// </remarks>
    [Fact]
    public void TriangleStrip() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("strip");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout,
            topology: PrimitiveTopology.TriangleStrip
        );

        // Strip order: bottom-left, bottom-right, top-left, top-right. As a list this would be one
        // triangle and a stray.
        var vertices = owned.Buffer<byte>(
            Packed([
                (-0.8f, -0.8f, 0xFF2010FF),
                (0.8f, -0.8f, 0xFF10FF20),
                (-0.8f, 0.8f, 0xFFFF2010),
                (0.8f, 0.8f, 0xFF20FFFF)
            ]),
            BufferUsage.Vertex
        );

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, vertices);
            list.Draw(4);
        });

        GoldenImage.Verify("topology-strip", owned.Render(colour), Tolerance.Interpolated);
    }

    /// <summary>A line strip is lines, and one pixel wide.</summary>
    [Fact]
    public void LineStrip() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("lines");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout,
            topology: PrimitiveTopology.LineStrip
        );

        var vertices = owned.Buffer<byte>(
            Packed([
                (-0.8f, -0.6f, 0xFF40FFFF),
                (-0.2f, 0.7f, 0xFF40FFFF),
                (0.3f, -0.7f, 0xFF40FFFF),
                (0.8f, 0.5f, 0xFF40FFFF)
            ]),
            BufferUsage.Vertex
        );

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, vertices);
            list.Draw(4);
        });

        // Line rasterisation is the one place Vulkan's rules leave a driver room, so the tolerance
        // allows a small number of pixels to land on the other side of a diagonal.
        GoldenImage.Verify("topology-linestrip", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>A per-instance vertex buffer advances once per instance, not once per vertex.</summary>
    /// <remarks>
    ///     Four instances of one quad, each moved and tinted by the second buffer. Declared per-vertex
    ///     instead, the attribute advances four times inside the first instance and runs off the end —
    ///     which on most drivers draws one quad in four colours and looks like an instance count that
    ///     was ignored.
    /// </remarks>
    [Fact]
    public void Instancing() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("instances");

        var pipeline = owned.Pipeline(
            owned.Shader("instanced.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            [
                new(sizeof(float) * 2, [new(0, VertexFormat.Float32X2, 0)]),
                new(
                    sizeof(float) * 6,
                    [new(1, VertexFormat.Float32X2, 0), new(2, VertexFormat.Float32X4, sizeof(float) * 2)],
                    VertexStepMode.Instance
                )
            ]
        );

        var quad = owned.Buffer<float>(
            [-0.35f, -0.35f, 0.35f, -0.35f, -0.35f, 0.35f, 0.35f, 0.35f],
            BufferUsage.Vertex
        );

        var instances = owned.Buffer<float>(
            [
                -0.45f, -0.45f, 1f, 0.25f, 0.2f, 1f,
                0.45f, -0.45f, 0.2f, 1f, 0.3f, 1f,
                -0.45f, 0.45f, 0.25f, 0.4f, 1f, 1f,
                0.45f, 0.45f, 1f, 0.9f, 0.2f, 1f
            ],
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, quad);
            list.BindVertexBuffer(1, instances);
            list.BindIndexBuffer(indices, IndexFormat.UInt16);
            list.DrawIndexed(QuadIndices.Length, 4);
        });

        GoldenImage.Verify("instancing", owned.Render(colour), Tolerance.Flat);
    }

    /// <summary>A <c>UNorm8X4</c> attribute is normalised on the way into the shader.</summary>
    /// <remarks>
    ///     The one vertex format whose mistake is invisible in every other kind of test: read as
    ///     <c>UInt8X4</c> the bytes arrive as 0–255 instead of 0–1, every channel saturates, and the
    ///     picture is simply white. Nothing errors and the geometry is identical.
    /// </remarks>
    [Fact]
    public void PackedVertexColour() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("packed");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout
        );

        // Deliberately mid-range values: 0x40 is 0.25 normalised and 64 unnormalised, and only one of
        // those is distinguishable from 0xFF once it has been clamped.
        var vertices = owned.Buffer<byte>(
            Packed([
                (-0.8f, -0.8f, 0xFF804020),
                (0.8f, -0.8f, 0xFF204080),
                (-0.8f, 0.8f, 0xFF208040),
                (0.8f, 0.8f, 0xFF404040)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, vertices);
            list.BindIndexBuffer(indices, IndexFormat.UInt16);
            list.DrawIndexed(QuadIndices.Length);
        });

        GoldenImage.Verify("vertex-unorm8", owned.Render(colour), Tolerance.Interpolated);
    }

    /// <summary>Thirty-two-bit indices, with a first index and a vertex offset.</summary>
    /// <remarks>
    ///     GL takes a byte offset where the RHI takes an index, so the multiplication by the index
    ///     width happens in the backend. Getting it wrong with 16-bit indices draws the right
    ///     <em>count</em> of the wrong triangles — a picture, not an error — which is why this fixture
    ///     uses both a non-zero first index and a non-zero vertex offset.
    /// </remarks>
    [Fact]
    public void IndexOffsets() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("indices");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout
        );

        // Eight vertices: a decoy quad the fixture must not draw, then the real one.
        var vertices = owned.Buffer<byte>(
            Packed([
                (-0.9f, -0.9f, 0xFF0000FF),
                (-0.5f, -0.9f, 0xFF0000FF),
                (-0.9f, -0.5f, 0xFF0000FF),
                (-0.5f, -0.5f, 0xFF0000FF),
                (-0.7f, -0.7f, 0xFF30E060),
                (0.7f, -0.7f, 0xFF30E060),
                (-0.7f, 0.7f, 0xFF30E060),
                (0.7f, 0.7f, 0xFF30E060)
            ]),
            BufferUsage.Vertex
        );

        // Six decoy indices, then the six that matter. A first index of 6 skips the decoys and a
        // vertex offset of 4 selects the second quad's vertices.
        var indices = owned.Buffer<uint>(
            [0, 1, 2, 2, 1, 3, 0, 1, 2, 2, 1, 3],
            BufferUsage.Index
        );

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, vertices);
            list.BindIndexBuffer(indices, IndexFormat.UInt32);
            list.DrawIndexed(6, 1, 6, 4);
        });

        GoldenImage.Verify("index-offsets", owned.Render(colour), Tolerance.Flat);
    }

    // ── Depth and stencil ───────────────────────────────────────────────────────────────────

    /// <summary>A depth test that only passes on equality, after a prepass wrote the depth.</summary>
    /// <remarks>
    ///     <c>Equal</c> is the comparison a depth prepass leaves behind, and it is unforgiving: a
    ///     backend that rounded depth differently between the two passes, or that wrote depth in the
    ///     second when it should not, produces z-fighting rather than a clean fill.
    /// </remarks>
    [Fact]
    public void DepthEqual() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("equal");
        var depth = owned.DepthTarget("equal depth");
        var vertex = owned.Shader("depth.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);

        var prepass = owned.Pipeline(vertex, fragment, BlendState.Opaque, DepthStencilState.Default, DepthLayout);

        var equal = owned.Pipeline(
            vertex,
            fragment,
            BlendState.Opaque,
            DepthStencilState.Default with { DepthWrite = false, DepthCompare = CompareFunction.Equal },
            DepthLayout
        );

        var near = owned.Buffer<float>(Quad(-0.8f, 0.2f, 0.7f, 0.1f, 0.1f, 0.15f), BufferUsage.Vertex);
        var far = owned.Buffer<float>(Quad(-0.2f, 0.8f, 0.3f, 0.15f, 0.1f, 0.1f), BufferUsage.Vertex);
        var tint = owned.Buffer<float>(Quad(-0.8f, 0.8f, 0.7f, 0.2f, 0.9f, 0.4f), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("equal", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.DepthAttachment(depth, LoadAction.Clear);
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindIndexBuffer(indices, IndexFormat.UInt16);

                list.BindPipeline(prepass);
                list.BindVertexBuffer(0, near);
                list.DrawIndexed(QuadIndices.Length);
                list.BindVertexBuffer(0, far);
                list.DrawIndexed(QuadIndices.Length);

                // At z = 0.7 this equals the near quad's depth and nothing else's, so only the left
                // two-thirds is tinted — and only where the far quad did not overwrite the depth.
                list.BindPipeline(equal);
                list.BindVertexBuffer(0, tint);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("depth-equal", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>A pass that tests depth without writing it lets a later, further draw through.</summary>
    /// <remarks>
    ///     What a forward transparency pass does. A backend that left the depth mask on hides the
    ///     second quad entirely, which is the single most common transparency bug and looks like a
    ///     sorting problem.
    /// </remarks>
    [Fact]
    public void DepthWriteOff() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("no write");
        var depth = owned.DepthTarget("no write depth");
        var vertex = owned.Shader("depth.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);

        var testOnly = owned.Pipeline(
            vertex,
            fragment,
            BlendState.Opaque,
            DepthStencilState.TestOnly,
            DepthLayout
        );

        var first = owned.Buffer<float>(Quad(-0.8f, 0.2f, 0.6f, 0.9f, 0.4f, 0.15f), BufferUsage.Vertex);
        var second = owned.Buffer<float>(Quad(-0.2f, 0.8f, 0.4f, 0.2f, 0.5f, 1f), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("no write", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.DepthAttachment(depth, LoadAction.Clear);
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(testOnly);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);

                // Neither writes depth, so the second wins everywhere it overlaps even though it is
                // further away. With writes on, it would lose.
                list.BindVertexBuffer(0, first);
                list.DrawIndexed(QuadIndices.Length);
                list.BindVertexBuffer(0, second);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("depth-write-off", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>A depth bias separates two coplanar quads.</summary>
    /// <remarks>
    ///     <para>
    ///         Two quads at exactly the same depth, under the engine's <c>Greater</c> test: without a
    ///         bias the second loses everywhere and the picture is the first quad's colour. The bias
    ///         is what makes it win, and it is what a decal or a shadow map's constant offset depends
    ///         on.
    ///     </para>
    ///     <para>
    ///         ⚠ The magnitude looks absurd and is not. <c>RasterizerState.DepthBias</c> documents
    ///         itself as "in depth units", and every API multiplies it by <em>r</em>, the smallest
    ///         resolvable difference in the depth format — which for a 32-bit float buffer around
    ///         <c>z = 0.5</c> is about <c>6 × 10⁻⁸</c>. A bias of <c>0.002</c> is therefore about
    ///         <c>10⁻¹⁰</c> of depth and changes nothing at all, which is what the first version of
    ///         this fixture asserted: a picture identical to no bias, passing forever.
    ///     </para>
    /// </remarks>
    [Fact]
    public void DepthBias() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("bias");
        var depth = owned.DepthTarget("bias depth");
        var vertex = owned.Shader("depth.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);

        var flat = owned.Pipeline(vertex, fragment, BlendState.Opaque, DepthStencilState.Default, DepthLayout);

        var biased = owned.Pipeline(
            vertex,
            fragment,
            BlendState.Opaque,
            DepthStencilState.Default,
            DepthLayout,
            rasterizer: RasterizerState.TwoSided with { DepthBias = 1024f }
        );

        var under = owned.Buffer<float>(Quad(-0.8f, 0.8f, 0.5f, 0.15f, 0.2f, 0.4f), BufferUsage.Vertex);
        var over = owned.Buffer<float>(Quad(-0.4f, 0.4f, 0.5f, 1f, 0.7f, 0.2f), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("bias", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.DepthAttachment(depth, LoadAction.Clear);
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindIndexBuffer(indices, IndexFormat.UInt16);

                list.BindPipeline(flat);
                list.BindVertexBuffer(0, under);
                list.DrawIndexed(QuadIndices.Length);

                list.BindPipeline(biased);
                list.BindVertexBuffer(0, over);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("depth-bias", owned.Render(colour), Tolerance.Edges);
    }

    /// <summary>A stencil write followed by a stencil test that only passes where it wrote.</summary>
    /// <remarks>
    ///     Two draws and a mask. The first writes the reference value into a narrow band with the
    ///     colour mask closed; the second passes only where the stencil equals it. A backend that
    ///     ignored the write mask, the reference, or the comparison paints the whole target instead of
    ///     the band — which is unmistakable, and is why this is worth a fixture at all.
    /// </remarks>
    [Fact]
    public void StencilMask() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("stencil");
        var depth = owned.DepthStencilTarget("stencil buffer");
        var vertex = owned.Shader("packed.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);

        var write = owned.Pipeline(
            vertex,
            fragment,
            new BlendState(WriteMask: ColourWriteMask.None),
            new DepthStencilState(
                false,
                false,
                CompareFunction.Always,
                true,
                new(CompareFunction.Always, Pass: StencilOperation.Replace),
                new(CompareFunction.Always, Pass: StencilOperation.Replace)
            ),
            PackedLayout,
            depthFormat: PixelFormat.Depth32FloatStencil8
        );

        var test = owned.Pipeline(
            vertex,
            fragment,
            BlendState.Opaque,
            new DepthStencilState(
                false,
                false,
                CompareFunction.Always,
                true,
                new(CompareFunction.Equal),
                new(CompareFunction.Equal),
                StencilWriteMask: 0
            ),
            PackedLayout,
            depthFormat: PixelFormat.Depth32FloatStencil8
        );

        var band = owned.Buffer<byte>(
            Packed([
                (-0.3f, -0.9f, 0xFFFFFFFF),
                (0.3f, -0.9f, 0xFFFFFFFF),
                (-0.3f, 0.9f, 0xFFFFFFFF),
                (0.3f, 0.9f, 0xFFFFFFFF)
            ]),
            BufferUsage.Vertex
        );

        var everything = owned.Buffer<byte>(
            Packed([
                (-1f, -1f, 0xFF20D0F0),
                (1f, -1f, 0xFF20D0F0),
                (-1f, 1f, 0xFF20D0F0),
                (1f, 1f, 0xFF20D0F0)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("stencil", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.02f, 0.08f, 1f));
            pass.DepthAttachment(depth, LoadAction.Clear);
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.SetStencilReference(1);

                list.BindPipeline(write);
                list.BindVertexBuffer(0, band);
                list.DrawIndexed(QuadIndices.Length);

                list.BindPipeline(test);
                list.BindVertexBuffer(0, everything);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("stencil-mask", owned.Render(colour), Tolerance.Flat);
    }

    // ── Blending and write masks ────────────────────────────────────────────────────────────

    /// <summary>Additive blending, which brightens rather than replacing.</summary>
    [Fact]
    public void Additive() => Blending("blend-additive", BlendState.Additive, null);

    /// <summary>Premultiplied alpha, which is what the compositor and the UI use.</summary>
    /// <remarks>
    ///     Distinguishable from straight alpha only where the source colour and its alpha disagree,
    ///     which is why the overlay's colour here is far from its alpha rather than near it.
    /// </remarks>
    [Fact]
    public void Premultiplied() => Blending("blend-premultiplied", BlendState.PremultipliedAlpha, null);

    /// <summary>A <c>Max</c> blend, which is neither a sum nor a replacement.</summary>
    /// <remarks>
    ///     The blend operation is a separate mapping from the blend factors and is the one a backend
    ///     is most likely to leave at <c>Add</c> — an operation nobody sets explicitly until something
    ///     needs it. Under <c>Max</c> the result is per-channel, so the overlap here takes red from one
    ///     quad and blue from the other, which no additive or alpha blend produces.
    /// </remarks>
    [Fact]
    public void MaxBlend() => Blending(
        "blend-max",
        new BlendState(
            true,
            BlendFactor.One,
            BlendFactor.One,
            BlendOperation.Max,
            BlendFactor.One,
            BlendFactor.One,
            BlendOperation.Max
        ),
        null
    );

    /// <summary>A blend against the constant colour the command list set.</summary>
    /// <remarks>
    ///     <c>SetBlendConstant</c> is dynamic state, set on the list rather than baked into the
    ///     pipeline, and it is the only blend factor a backend can get right in the pipeline and wrong
    ///     at draw time. Left at its default of zero, the overlay disappears entirely.
    /// </remarks>
    [Fact]
    public void BlendConstant() => Blending(
        "blend-constant",
        new BlendState(
            true,
            BlendFactor.Constant,
            BlendFactor.OneMinusConstant,
            BlendOperation.Add,
            BlendFactor.One,
            BlendFactor.Zero
        ),
        new Color4(0.75f, 0.25f, 0.5f, 1f)
    );

    /// <summary>A colour write mask that lets only two channels through.</summary>
    /// <remarks>
    ///     The failure this guards is the one that cost an afternoon and is recorded in
    ///     <c>docs/plan/05</c>: <c>BlendState.Opaque</c> was silently zero-initialised to a write mask
    ///     of <c>None</c>, and every draw in the engine produced an untouched attachment with no error
    ///     from the API, the layers or the driver. A mask that is <em>partly</em> open is the version
    ///     of that a picture can tell apart from both extremes.
    /// </remarks>
    [Fact]
    public void WriteMask() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("mask");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            new BlendState(WriteMask: ColourWriteMask.Red | ColourWriteMask.Alpha),
            DepthStencilState.Disabled,
            PackedLayout
        );

        var quad = owned.Buffer<byte>(
            Packed([
                (-0.7f, -0.7f, 0xFFFFFFFF),
                (0.7f, -0.7f, 0xFFFFFFFF),
                (-0.7f, 0.7f, 0xFFFFFFFF),
                (0.7f, 0.7f, 0xFFFFFFFF)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("mask", pass => {
            // Cleared to a colour with a green and blue the draw must not disturb, so the result is
            // the clear's green and blue with the draw's red.
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.1f, 0.45f, 0.7f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindVertexBuffer(0, quad);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("write-mask", owned.Render(colour), Tolerance.Flat);
    }

    // ── Viewport, scissor and targets ───────────────────────────────────────────────────────

    /// <summary>A scissor rectangle clips a full-target draw to a corner.</summary>
    /// <remarks>
    ///     And it is a corner deliberately, not a centred box: the RHI's rectangle is top-left-origin
    ///     following Vulkan, GL's is bottom-left, and a backend that forgot the conversion produces a
    ///     rectangle in the <em>wrong</em> corner — which a symmetric fixture cannot see.
    /// </remarks>
    [Fact]
    public void Scissor() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("scissor");
        var pipeline = FullQuad(owned, out var quad, out var indices);

        owned.Graph.AddPass("scissor", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.08f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.SetScissor(new(8, 8, 48, 32));
                list.BindVertexBuffer(0, quad);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("scissor", owned.Render(colour), Tolerance.Flat);
    }

    /// <summary>A viewport confines and squashes a full-target draw.</summary>
    /// <remarks>
    ///     Off-centre and non-square for the same reason the scissor is: a viewport in the wrong half
    ///     is the failure, and a centred one would look identical either way up.
    /// </remarks>
    [Fact]
    public void Viewport() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("viewport");
        var pipeline = FullQuad(owned, out var quad, out var indices);

        owned.Graph.AddPass("viewport", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.08f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.SetViewport(new(16, 8, 96, 48));
                list.BindVertexBuffer(0, quad);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("viewport", owned.Render(colour), Tolerance.Flat);
    }

    /// <summary>Two colour attachments, and the fixture reads the second one back.</summary>
    /// <remarks>
    ///     The second deliberately. A backend that named only the first draw buffer — which is GL's
    ///     default for a user framebuffer — writes one attachment and discards the other with no error
    ///     from anything, and a fixture that read the first back would pass.
    /// </remarks>
    [Fact]
    public void SecondColourTarget() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var first = owned.Graph.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Fixture.Side,
            Fixture.Side,
            TextureUsage.ColourTarget,
            Name: "first"
        ));

        var second = owned.ColourTarget("second");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("dual.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout,
            targets: [
                new(PixelFormat.Rgba8UNorm, BlendState.Opaque),
                new(PixelFormat.Rgba8UNorm, BlendState.Opaque)
            ]
        );

        var quad = owned.Buffer<byte>(
            Packed([
                (-0.7f, -0.7f, 0xFF3060C0),
                (0.7f, -0.7f, 0xFF3060C0),
                (-0.7f, 0.7f, 0xFF3060C0),
                (0.7f, 0.7f, 0xFF3060C0)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("dual", pass => {
            pass.ColourAttachment(first, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.ColourAttachment(second, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindVertexBuffer(0, quad);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("second-target", owned.Render(second), Tolerance.Flat);
    }

    /// <summary>A second pass that loads rather than clears keeps what the first drew.</summary>
    /// <remarks>
    ///     Which is the store action of the first pass and the load action of the second, both at
    ///     once. A graph that derived <c>DontCare</c> for the first pass's store discards it — and
    ///     that is not hypothetical: every fixture in this suite rendered a discarded target before
    ///     importing was understood, and each produced a uniform block of undefined memory.
    /// </remarks>
    [Fact]
    public void LoadPreservesTheEarlierPass() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("load");
        var vertex = owned.Shader("packed.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);
        var pipeline = owned.Pipeline(vertex, fragment, BlendState.Opaque, DepthStencilState.Disabled, PackedLayout);

        var left = owned.Buffer<byte>(
            Packed([
                (-0.9f, -0.6f, 0xFFE05020),
                (-0.1f, -0.6f, 0xFFE05020),
                (-0.9f, 0.6f, 0xFFE05020),
                (-0.1f, 0.6f, 0xFFE05020)
            ]),
            BufferUsage.Vertex
        );

        var right = owned.Buffer<byte>(
            Packed([
                (0.1f, -0.6f, 0xFF2080E0),
                (0.9f, -0.6f, 0xFF2080E0),
                (0.1f, 0.6f, 0xFF2080E0),
                (0.9f, 0.6f, 0xFF2080E0)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("first", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.06f, 0.06f, 0.1f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindVertexBuffer(0, left);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        owned.Graph.AddPass("second", pass => {
            pass.ColourAttachment(colour, LoadAction.Load);
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindVertexBuffer(0, right);
                list.BindIndexBuffer(indices, IndexFormat.UInt16);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify("load-preserves", owned.Render(colour), Tolerance.Flat);
    }

    // ── Samplers ────────────────────────────────────────────────────────────────────────────

    /// <summary>A 4×4 texture magnified thirty-two times with nearest filtering.</summary>
    /// <remarks>
    ///     Nearest and linear at this magnification are unmistakably different pictures — sixteen flat
    ///     squares against a smooth field — which is what makes the pair worth having. A backend that
    ///     folded the RHI's three filter choices into GL's combined minification enumerant wrongly
    ///     produces one where the other was asked for.
    /// </remarks>
    [Fact]
    public void SamplerNearest() => Sampling("sampler-nearest", SamplerDescription.PointClamp, 1f);

    /// <summary>The same texture with linear filtering.</summary>
    [Fact]
    public void SamplerLinear() => Sampling("sampler-linear", SamplerDescription.LinearClamp, 1f);

    /// <summary>Sampled past <c>[0, 1]</c> with the address mode repeating.</summary>
    /// <remarks>
    ///     At a UV scale of two, <c>Repeat</c> tiles the source four times and <c>ClampToEdge</c>
    ///     stretches its edge texels across three quarters of the target. Nothing else about the two
    ///     fixtures differs.
    /// </remarks>
    [Fact]
    public void SamplerRepeat() => Sampling("sampler-repeat", SamplerDescription.PointClamp with {
        AddressU = AddressMode.Repeat,
        AddressV = AddressMode.Repeat
    }, 2f);

    /// <summary>And with it clamping to the edge instead.</summary>
    [Fact]
    public void SamplerClamp() => Sampling("sampler-clamp", SamplerDescription.PointClamp, 2f);

    // ── Copies ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A region of a buffer copied into a region of a texture, then sampled.</summary>
    /// <remarks>
    ///     <para>
    ///         The offsets are all non-zero, deliberately. A copy with a zero source offset and a zero
    ///         destination origin is the one case that works whatever the arithmetic does, and it is
    ///         the case every hand-written test uses.
    ///     </para>
    ///     <para>
    ///         It also pins the row order at the RHI's copy boundary: the source rows are laid out top
    ///         first, and a backend that stores textures the other way up — which OpenGL does — has to
    ///         flip them on the way in for this picture to come out the same as Vulkan's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void CopyRegion() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("copy");

        // An 8×8 texture filled once with a flat colour, of which a 4×4 region in the lower right is
        // then overwritten from the middle of a larger staging buffer.
        //
        // ⚠ The first fill is not decoration. The region copy reaches a quarter of the texture and a
        // texture whose other three quarters were never written holds undefined memory — which the
        // shader samples and the reference records, so the fixture would be asserting whatever the
        // driver's allocator happened to leave there. The first version of this did exactly that and
        // produced a white background on one machine.
        var texels = new byte[8 * 8 * 4];

        for (var index = 0; index < 8 * 8; index++) {
            texels[index * 4] = 20;
            texels[(index * 4) + 1] = 30;
            texels[(index * 4) + 2] = 60;
            texels[(index * 4) + 3] = 255;
        }

        var source = new byte[(4 * 4 * 4) + 64];

        for (var row = 0; row < 4; row++) {
            for (var column = 0; column < 4; column++) {
                var offset = 64 + (((row * 4) + column) * 4);
                source[offset] = (byte)(60 + (row * 60));
                source[offset + 1] = (byte)(40 + (column * 60));
                source[offset + 2] = 200;
                source[offset + 3] = 255;
            }
        }

        var (texture, view, background) = owned.Sampled("region", 8, texels);

        var staging = owned.Buffer<byte>(source, BufferUsage.CopySource);
        var sampler = owned.Sampler(SamplerDescription.PointClamp);

        var empty = owned.SetLayout(new(DescriptorSetSlot.PerFrame, [], "empty"));

        var sampled = owned.SetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "sampled"
        ));

        var set = owned.DescriptorSet(sampled, "sampled");
        owned.Bind(set, DescriptorWrite.Texture(0, view), DescriptorWrite.SamplerAt(1, sampler));

        var pipeline = owned.Pipeline(
            owned.Shader("fullscreen.vert.spv", ShaderStage.Vertex),
            owned.Shader("sample.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            sets: [empty, empty, sampled]
        );

        owned.Graph.AddPass("copy", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.1f, 0.05f, 0.05f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
                list.Draw(3);
            });
        });

        GoldenImage.Verify(
            "copy-region",
            owned.Render(colour, before => {
                Transition(before, texture, ResourceState.Undefined, ResourceState.CopyDestination);
                before.CopyBufferToTexture(background, 0, new(texture), new(8, 8, 1));

                before.CopyBufferToTexture(
                    staging,
                    64,
                    new(texture, Origin: new(4, 4, 0)),
                    new(4, 4, 1)
                );

                Transition(before, texture, ResourceState.CopyDestination, ResourceState.ShaderRead);
            }),
            Tolerance.Flat
        );
    }

    /// <summary>One texture copied into another and sampled from there.</summary>
    /// <remarks>
    ///     A texture-to-texture copy is the transfer with no shader anywhere in it, so a mistake in
    ///     its region arithmetic cannot be blamed on anything else.
    /// </remarks>
    [Fact]
    public void CopyBetweenTextures() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("copied");

        var texels = new byte[4 * 4 * 4];

        for (var row = 0; row < 4; row++) {
            for (var column = 0; column < 4; column++) {
                var offset = ((row * 4) + column) * 4;
                texels[offset] = (byte)(row * 80);
                texels[offset + 1] = (byte)(column * 80);
                texels[offset + 2] = 160;
                texels[offset + 3] = 255;
            }
        }

        var device = owned.Device;

        var origin = owned.Owned(
            "origin",
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            4,
            4
        );

        var staging = owned.Buffer<byte>(texels, BufferUsage.CopySource);
        var (destination, view, _) = owned.Sampled("destination", 4, new byte[4 * 4 * 4]);
        var sampler = owned.Sampler(SamplerDescription.PointClamp);

        var empty = owned.SetLayout(new(DescriptorSetSlot.PerFrame, [], "empty"));

        var sampled = owned.SetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "sampled"
        ));

        var set = owned.DescriptorSet(sampled, "sampled");
        owned.Bind(set, DescriptorWrite.Texture(0, view), DescriptorWrite.SamplerAt(1, sampler));

        var pipeline = owned.Pipeline(
            owned.Shader("fullscreen.vert.spv", ShaderStage.Vertex),
            owned.Shader("sample.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            sets: [empty, empty, sampled]
        );

        owned.Graph.AddPass("copied", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
                list.Draw(3);
            });
        });

        GoldenImage.Verify(
            "copy-texture",
            owned.Render(colour, before => {
                Transition(before, origin.Texture, ResourceState.Undefined, ResourceState.CopyDestination);
                before.CopyBufferToTexture(staging, 0, new(origin.Texture), new(4, 4, 1));
                Transition(before, origin.Texture, ResourceState.CopyDestination, ResourceState.CopySource);
                Transition(before, destination, ResourceState.Undefined, ResourceState.CopyDestination);
                before.CopyTexture(new(origin.Texture), new(destination), new(4, 4, 1));
                Transition(before, destination, ResourceState.CopyDestination, ResourceState.ShaderRead);
            }),
            Tolerance.Flat
        );
    }

    // ── Shared shapes ───────────────────────────────────────────────────────────────────────

    /// <summary>The layout <c>packed.vert</c> declares: a <c>vec2</c> and four bytes.</summary>
    static VertexBufferLayout[] PackedLayout => [
        new(12, [new(0, VertexFormat.Float32X2, 0), new(1, VertexFormat.UNorm8X4, 8)])
    ];

    /// <summary>The layout <c>depth.vert</c> declares.</summary>
    static VertexBufferLayout[] DepthLayout => [
        new(
            sizeof(float) * 7,
            [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 3)]
        )
    ];

    static void Culling(string name, CullMode mode) {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget(name);

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout,
            rasterizer: new RasterizerState(mode)
        );

        // Left triangle counter-clockwise, right triangle clockwise. Under the engine's convention
        // the left one is front-facing.
        var vertices = owned.Buffer<byte>(
            Packed([
                (-0.9f, -0.7f, 0xFF20E040),
                (-0.1f, -0.7f, 0xFF20E040),
                (-0.5f, 0.7f, 0xFF20E040),
                (0.1f, -0.7f, 0xFF4020E0),
                (0.5f, 0.7f, 0xFF4020E0),
                (0.9f, -0.7f, 0xFF4020E0)
            ]),
            BufferUsage.Vertex
        );

        Draw(owned, colour, pipeline, list => {
            list.BindVertexBuffer(0, vertices);
            list.Draw(6);
        });

        GoldenImage.Verify(name, owned.Render(colour), Tolerance.Edges);
    }

    static void Blending(string name, BlendState blend, Color4? constant) {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget(name);
        var vertex = owned.Shader("packed.vert.spv", ShaderStage.Vertex);
        var fragment = owned.Shader("mesh.frag.spv", ShaderStage.Fragment);

        var opaque = owned.Pipeline(vertex, fragment, BlendState.Opaque, DepthStencilState.Disabled, PackedLayout);
        var blended = owned.Pipeline(vertex, fragment, blend, DepthStencilState.Disabled, PackedLayout);

        var under = owned.Buffer<byte>(
            Packed([
                (-0.9f, -0.85f, 0xFF9A3010),
                (0.3f, -0.85f, 0xFF9A3010),
                (-0.9f, 0.85f, 0xFF9A3010),
                (0.3f, 0.85f, 0xFF9A3010)
            ]),
            BufferUsage.Vertex
        );

        // Alpha 0x60 with a colour nowhere near it, so straight and premultiplied alpha differ and
        // an additive blend differs from both.
        var over = owned.Buffer<byte>(
            Packed([
                (-0.3f, -0.85f, 0x6018C0F0),
                (0.9f, -0.85f, 0x6018C0F0),
                (-0.3f, 0.85f, 0x6018C0F0),
                (0.9f, 0.85f, 0x6018C0F0)
            ]),
            BufferUsage.Vertex
        );

        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.02f, 0.02f, 0.02f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindIndexBuffer(indices, IndexFormat.UInt16);

                list.BindPipeline(opaque);
                list.BindVertexBuffer(0, under);
                list.DrawIndexed(QuadIndices.Length);

                list.BindPipeline(blended);

                if (constant is { } value) {
                    list.SetBlendConstant(value);
                }

                list.BindVertexBuffer(0, over);
                list.DrawIndexed(QuadIndices.Length);
            });
        });

        GoldenImage.Verify(name, owned.Render(colour), Tolerance.Edges);
    }

    static void Sampling(string name, SamplerDescription description, float scale) {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget(name);

        // A 4×4 checker with two distinguishable colours per row, so a flip, a transpose or a wrong
        // filter all change the picture in a different way.
        var texels = new byte[4 * 4 * 4];

        for (var row = 0; row < 4; row++) {
            for (var column = 0; column < 4; column++) {
                var offset = ((row * 4) + column) * 4;
                var light = (row + column) % 2 == 0;
                texels[offset] = (byte)(light ? 240 : 30);
                texels[offset + 1] = (byte)(row * 70);
                texels[offset + 2] = (byte)(column * 70);
                texels[offset + 3] = 255;
            }
        }

        var (texture, view, staging) = owned.Sampled("checker", 4, texels);
        var sampler = owned.Sampler(description);
        var empty = owned.SetLayout(new(DescriptorSetSlot.PerFrame, [], "empty"));

        var sampled = owned.SetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "sampled"
        ));

        var set = owned.DescriptorSet(sampled, "sampled");
        owned.Bind(set, DescriptorWrite.Texture(0, view), DescriptorWrite.SamplerAt(1, sampler));

        var pipeline = owned.Pipeline(
            owned.Shader("tiled.vert.spv", ShaderStage.Vertex),
            owned.Shader("sample.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            pushConstantBytes: sizeof(float) * 2,
            sets: [empty, empty, sampled]
        );

        var uv = new[] { scale, scale };

        owned.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                var list = context.CommandList;
                list.BindPipeline(pipeline);
                list.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(uv.AsSpan()));
                list.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
                list.Draw(3);
            });
        });

        GoldenImage.Verify(
            name,
            owned.Render(colour, before => {
                Transition(before, texture, ResourceState.Undefined, ResourceState.CopyDestination);
                before.CopyBufferToTexture(staging, 0, new(texture), new(4, 4, 1));
                Transition(before, texture, ResourceState.CopyDestination, ResourceState.ShaderRead);
            }),
            Tolerance.Interpolated
        );
    }

    /// <summary>A pipeline drawing one quad over the whole target, for the clipping fixtures.</summary>
    static PipelineHandle FullQuad(Fixture owned, out BufferHandle quad, out BufferHandle indices) {
        quad = owned.Buffer<byte>(
            Packed([
                (-1f, -1f, 0xFFE0A020),
                (1f, -1f, 0xFFE0A020),
                (-1f, 1f, 0xFFE0A020),
                (1f, 1f, 0xFFE0A020)
            ]),
            BufferUsage.Vertex
        );

        indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        return owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            PackedLayout
        );
    }

    static void Draw(Fixture owned, GraphTexture colour, PipelineHandle pipeline, Action<ICommandList> body) =>
        owned.Graph.AddPass("draw", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.03f, 0.03f, 0.05f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(pipeline);
                body(context.CommandList);
            });
        });

    /// <summary>Moves a texture the graph does not know about between states.</summary>
    static void Transition(ICommandList list, TextureHandle texture, ResourceState before, ResourceState after) {
        Span<TextureBarrier> barriers = [new(texture, before, after)];
        list.Barrier(new([], barriers));
    }

    /// <summary>Four vertices spanning a horizontal range, at one depth, in one colour.</summary>
    static float[] Quad(float left, float right, float z, float r, float g, float b, float a = 1f) => [
        left, -0.85f, z, r, g, b, a,
        right, -0.85f, z, r, g, b, a,
        left, 0.85f, z, r, g, b, a,
        right, 0.85f, z, r, g, b, a
    ];

    /// <summary>Vertices for <c>packed.vert</c>: two floats and a packed ABGR colour.</summary>
    /// <remarks>
    ///     ABGR rather than ARGB, because the bytes are read as R, G, B, A in memory order and a
    ///     literal is written most-significant first. Naming it here rather than at each call site,
    ///     since getting it backwards is a channel swap that looks deliberate.
    /// </remarks>
    static byte[] Packed(ReadOnlySpan<(float X, float Y, uint Colour)> vertices) {
        var bytes = new byte[vertices.Length * 12];

        for (var index = 0; index < vertices.Length; index++) {
            var (x, y, colour) = vertices[index];
            var offset = index * 12;
            BitConverter.TryWriteBytes(bytes.AsSpan(offset), x);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4), y);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8), colour);
        }

        return bytes;
    }
}
