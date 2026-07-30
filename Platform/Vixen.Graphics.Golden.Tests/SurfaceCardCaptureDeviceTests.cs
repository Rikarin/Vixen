// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.SurfaceCache;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The rasterised card capture, held against the traced reference on one scene.</summary>
/// <remarks>
///     <para>
///         <c>TracedCardCapture</c> marches an analytic field and is the deterministic reference;
///         <c>SurfaceCardCapture</c> rasterises triangles of the same surface and reads back into
///         the same store. Captured with both, the same card must hold the same texels: the same
///         valid set (the half of the card the geometry covers, and not the half it does not), the
///         same albedo, normal and emissive to float precision, and the same depth to within the
///         march's own arrival threshold — the traced side stops a hair above the surface by
///         design, and that hair is the entire disagreement.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class SurfaceCardCaptureDeviceTests {
    /// <summary>Texel (x, y)'s centre projects to texel (x, y)'s NDC centre, for every frame.</summary>
    /// <remarks>The convention everything else leans on, pinned without a device: if this holds,
    ///     the rasteriser and <c>TryProject</c> address the same texels by construction.</remarks>
    [Fact]
    public void TheProjectionPutsATexelWhereTheCardSaysItIs() {
        foreach (var axis in new[] { 0, 1, 2, 3, 4, 5 }) {
            var card = new SurfaceCard(axis, new(1.5f, -2f, 0.5f), new(2f, 0.75f, 1.25f), new(8, 4));
            var projection = SurfaceCardCapture.Projection(card);

            for (var y = 0; y < card.Resolution.Y; y++) {
                for (var x = 0; x < card.Resolution.X; x++) {
                    var texel = new Int2(x, y);
                    var clip = Matrix4x4.TransformVector4(new(card.TexelOrigin(texel), 1f), projection);

                    Assert.Equal(1f, clip.W, 1e-6f);
                    Assert.Equal(((x + 0.5f) / card.Resolution.X * 2f) - 1f, clip.X, 1e-5f);
                    Assert.Equal(((y + 0.5f) / card.Resolution.Y * 2f) - 1f, clip.Y, 1e-5f);

                    // The near plane is device depth one — reversed, like everything the engine draws.
                    Assert.Equal(1f, clip.Z, 1e-5f);
                }
            }

            // And a point halfway into the box is halfway down the reversed range.
            var inside = card.Centre;
            var mid = Matrix4x4.TransformVector4(new(inside, 1f), projection);

            Assert.Equal(0.5f, mid.Z, 1e-5f);
        }
    }

    /// <summary>One scene, both captures, one store — the comparison the class exists for.</summary>
    [Fact]
    public void TheRasterisedCaptureAgreesWithTheTracedReference() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // A +Y card over a half-floor: geometry under x < 0, sky over x > 0, so validity itself is
        // a compared answer rather than a constant.
        var card = new SurfaceCard(2, new(0f, 0f, 0f), new(1f, 0.2f, 1f), new(8, 8));

        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(16, 16)));
        var traced = new SurfaceCacheStore(new SurfaceCacheAtlas(new(16, 16)));
        var rastered = store.AddCard(card);
        var reference = traced.AddCard(card);

        Assert.Equal(
            32,
            new TracedCardCapture(new HalfFloor(), new Paint()).Capture(traced, reference)
        );

        var pipeline = owned.Pipeline(
            owned.Shader("line.vert.spv", ShaderStage.Vertex),
            owned.Shader("line.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            new DepthStencilState { DepthTest = true, DepthWrite = true, DepthCompare = CompareFunction.Greater },
            [new VertexBufferLayout(Vertex.Stride, [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)])],
            pushConstantBytes: 64,
            rasterizer: RasterizerState.TwoSided,
            targets: [new ColourTargetState(PixelFormat.Rgba32Float, BlendState.Opaque)]
        );

        // The half-floor as two triangles, wider than the card in z so the edge under test is the
        // one at x = 0 and no other.
        var quads = new Dictionary<SurfaceCapturePlane, BufferHandle> {
            [SurfaceCapturePlane.Albedo] = owned.Buffer<Vertex>(Quad(new(0.5f, 0.25f, 0.125f)), BufferUsage.Vertex),
            [SurfaceCapturePlane.Normal] = owned.Buffer<Vertex>(Quad(new(0f, 1f, 0f)), BufferUsage.Vertex),
            [SurfaceCapturePlane.Emissive] = owned.Buffer<Vertex>(Quad(new(2f, 1f, 0.5f)), BufferUsage.Vertex)
        };

        using var capture = new SurfaceCardCapture(device) { MaxResolution = 16 };

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "card-capture")) {
            capture.Record(
                commands,
                card,
                (list, plane, viewProjection) => {
                    list.BindPipeline(pipeline);
                    list.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes([viewProjection]));
                    list.BindVertexBuffer(0, quads[plane]);
                    list.Draw(6, 1);
                }
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();
        AssertClean();

        Assert.True(capture.TryRead(store, rastered, out var captured));
        Assert.Equal(32, captured);

        for (var y = 0; y < card.Resolution.Y; y++) {
            for (var x = 0; x < card.Resolution.X; x++) {
                var texel = new Int2(x, y);

                // The valid sets must be the same texels: the covered half and only it.
                Assert.Equal(traced.IsValid(reference, texel), store.IsValid(rastered, texel));

                if (!store.IsValid(rastered, texel)) {
                    continue;
                }

                var march = traced.Surface(reference, texel);
                var raster = store.Surface(rastered, texel);

                Assert.Equal(march.Albedo.X, raster.Albedo.X, 1e-5f);
                Assert.Equal(march.Albedo.Y, raster.Albedo.Y, 1e-5f);
                Assert.Equal(march.Emissive.X, raster.Emissive.X, 1e-5f);
                Assert.Equal(march.Normal.Y, raster.Normal.Y, 1e-4f);

                // The march stops within its arrival threshold above the surface; the rasteriser
                // lands on it. That hair is the whole disagreement, and it is the traced side's.
                Assert.Equal(0.2f, raster.Depth, 1e-4f);
                Assert.Equal(march.Depth, raster.Depth, 0.015f);
            }
        }
    }

    /// <summary>The floor's covered half: y = 0 under x &lt; 0, wider than the card in z.</summary>
    static Vertex[] Quad(Vector3 colour) {
        Vector3 a = new(-1.5f, 0f, -1.5f);
        Vector3 b = new(0f, 0f, -1.5f);
        Vector3 c = new(-1.5f, 0f, 1.5f);
        Vector3 d = new(0f, 0f, 1.5f);

        return [
            new(a, colour), new(b, colour), new(c, colour),
            new(c, colour), new(b, colour), new(d, colour)
        ];
    }

    /// <summary>The same half-floor, analytically: a box whose top face is the quad.</summary>
    sealed class HalfFloor : IDistanceField {
        static readonly Vector3 Centre = new(-0.75f, -0.5f, 0f);
        static readonly Vector3 Half = new(0.75f, 0.5f, 1.5f);

        public float Sample(Vector3 position) {
            var q = Vector3.Abs(position - Centre) - Half;
            var outside = Vector3.Max(q, Vector3.Zero).Length();
            var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);

            return outside + inside;
        }

        public Vector3 SampleGradient(Vector3 position) {
            const float Step = 0.001f;

            return Vector3.Normalize(
                new(
                    Sample(position + new Vector3(Step, 0f, 0f)) - Sample(position - new Vector3(Step, 0f, 0f)),
                    Sample(position + new Vector3(0f, Step, 0f)) - Sample(position - new Vector3(0f, Step, 0f)),
                    Sample(position + new Vector3(0f, 0f, Step)) - Sample(position - new Vector3(0f, 0f, Step))
                )
            );
        }
    }

    sealed class Paint : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => new(0.5f, 0.25f, 0.125f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => new(2f, 1f, 0.5f);
    }

    /// <summary>What <c>line.vert</c> reads: a world position and a colour.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex(Vector3 position, Vector3 colour) {
        public const int Stride = 28;

        public Vector3 Position = position;
        public Vector4 Colour = new(colour, 1f);
    }

    static void AssertClean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The capture produced validation errors, so what it read back is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device is available");

        return false;
    }
}
