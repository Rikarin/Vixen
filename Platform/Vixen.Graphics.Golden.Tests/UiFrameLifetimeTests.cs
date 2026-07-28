// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     What a <see cref="UiRenderer" /> does to resources a frame still on the device is using.
/// </summary>
/// <remarks>
///     <para>
///         No picture, and that is the point. Everything the golden fixtures next door check is a
///         property of one frame rendered on its own — and the mistakes this file is about need a
///         <i>second</i> frame to exist at all, because they are about touching something the first
///         one has not finished with. A fixture that submits and waits cannot see them: waiting is
///         exactly what makes them safe.
///     </para>
///     <para>
///         ⚠ <b>The assertion is the validation layers' error count</b>, which is the only observer
///         there is. A descriptor set updated while a submitted command buffer references it is
///         undefined behaviour and not a failure — the picture is usually right, on this driver,
///         today. So the test drives frames the way a window does, with nothing between them, and
///         asks the layers whether anything was said.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiFrameLifetimeTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>
    ///     A frame whose geometry outgrows the box buffer, while earlier frames are still running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Growing replaces the storage buffer the shape records live in, which invalidates the
    ///         binding in every frame's descriptor set at once. Rewriting them all there is the
    ///         obvious repair and it is the bug: the other frames' sets are the ones the device is
    ///         reading through right now, and
    ///         <c>VUID-vkUpdateDescriptorSets-None-03047</c> says a set may not be updated while a
    ///         pending command buffer references it. The repair has to be deferred to the frame that
    ///         owns each set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Growth is what makes this reachable, so the geometry has to actually grow.</b> The
    ///         interface a window draws grows for ordinary reasons — a list expands, a tooltip opens,
    ///         a panel is dragged wider — and the counts here climb past the doubling in
    ///         <c>UiRenderer.Grow</c> more than once so that the replacement happens with frames in
    ///         flight rather than only on the first frame, where nothing is pending and everything is
    ///         safe.
    ///     </para>
    /// </remarks>
    [Fact]
    public void GrowingTheBoxBufferLeavesFramesInFlightAlone() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var renderer = new UiRenderer(
            device,
            new(
                owned.Shader("ui.vert.spv", ShaderStage.Vertex),
                owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        // ⚠ `CopySource` as well, because the exit state below is what the graph barriers into and a
        // layout a texture's usage flags do not allow is an error of its own — which would fail this
        // test for a reason that has nothing to do with what it is about.
        var target = owned.Owned("ui frames", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));

        VulkanDiagnostics.Reset();

        // A run long enough that the ring comes round: with two frames in flight, the third frame is
        // the first whose set another frame could still be reading. The counts double past the box
        // buffer's own doubling several times over that run.
        int[] counts = [1, 4, 16, 64, 256, 1024];

        foreach (var count in counts) {
            Frame(count);
        }

        device.WaitIdle();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the layers reported an error while frames were in flight: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        // The renderer did the growing this is about, rather than passing because every frame fitted
        // in what the first one allocated.
        Assert.True(renderer.Draws > 0);

        void Frame(int count) {
            var geometry = Boxes(count, cache);

            // ⚠ No wait anywhere in here, which is the whole fixture. `BeginFrame` waits on the fence
            // of the frame `FramesInFlight` ago and no other, so by the third pass round this loop
            // there is a submitted frame using the sets and buffers this one is about to touch —
            // which is the state a window is in every frame and no golden fixture is ever in.
            device.BeginFrame();

            var imported = owned.Graph.ImportTexture(
                target.Texture,
                target.View,
                target.Description,
                ResourceState.Undefined,
                ResourceState.CopySource
            );

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "ui")) {
                renderer.Upload(commands, geometry, cache.Atlas);

                owned.Graph.AddPass("ui", pass => {
                    pass.ColourAttachment(imported, LoadAction.Clear, new(0f, 0f, 0f, 1f));
                    pass.SideEffect();

                    pass.Execute(
                        context => renderer.Record(context.CommandList, geometry, new(Side, Side))
                    );
                });

                owned.Graph.Execute(commands);
                owned.Graph.Reset();

                commands.Finish();
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();
        }
    }

    /// <summary>A frame of that many rounded boxes, tiled across the surface.</summary>
    static UiGeometry Boxes(int count, GlyphFieldCache cache) {
        var list = new DrawList();
        list.BeginFrame();

        for (var index = 0; index < count; index++) {
            // Wrapped rather than laid out: what matters is the number of shape records, not where
            // they are, and a box off the surface is one the scissor throws away before it is drawn.
            var x = index % 16 * 8f;
            var y = index / 16 % 16 * 8f;

            list.Add(
                new(DrawCommandKind.Rectangle, x, y, 6, 6, new Color4(0.8f, 0.4f, 0.2f, 1f), 2, 0)
            );
        }

        list.EndFrame();
        return new UiGeometryBuilder().Build(list, cache, Viewport);
    }

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
}
