// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The black-HUD defect on a real device, in a pass whose units are cd/m².</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#670's remaining half, and it is a different claim from the one the software
///         fixture makes.</b> <c>Vixen.Ui.Controls.Tests.HudLuminanceTests</c> runs the fragment
///         arithmetic on the CPU over the geometry the GPU would be given, so what it proves is that
///         <see cref="UiGeometryBuilder.WhiteLevel" /> reaches the pixels. It cannot prove that the
///         Vulkan path agrees, and its own remarks say so. This does — same fixture, same numbers,
///         <c>UiRenderer</c> and a float attachment instead.
///     </para>
///     <para>
///         ⚠ <b>The float attachment is the whole of what makes the picture possible, on either
///         executor.</b> An eight-bit target leaves a scene at a hundred candelas and a HUD at one
///         both clamped to 255, so the two frames this file is about are byte-identical through that
///         door — which is the standing photometric trap wearing its rendering hat: a pass lit by an
///         authored 0–1 tint is pixel-identical to a pass that never ran. <c>Rgba32Float</c> is the
///         device's <c>RenderLinear</c>.
///     </para>
///     <para>
///         ⚠ <b>The assertion is an order first and a magnitude second</b>, for this repository's
///         usual reason: no display transform is invented anywhere below. A white panel a hundredth
///         of the grey wall it covers is wrong under every monotone transform there is; a white panel
///         twice the wall is right under all of them. The magnitudes are asserted too, because an
///         order alone would hold for a panel that came out at 99 cd/m².
///     </para>
///     <para>
///         ⚠ <b>What this still does not reach is a production host.</b> Nothing in the tree mounts a
///         <c>UiDocument</c> in a world renderer's frame yet, so <c>UiRenderFeature.Dim</c> has a
///         test and no production reader — that is #627's remaining half, and #670 stays open on it.
///         What is closed here is the executor: the device draws the interface at the frame's white
///         and not at one candela.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class HudLuminanceDeviceTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>BT.2408's reference white, which is what <see cref="UiRenderer.WhiteLevelFor" /> hands a float pass.</summary>
    const float DiffuseWhite = 203f;

    /// <summary>The scene already in the buffer when the interface composites over it, in cd/m².</summary>
    /// <remarks>
    ///     ⚠ A mid-grey wall of a scene-referred frame, and deliberately not a highlight: at
    ///     3 000 cd/m² every claim below would be about a clamp instead of about the interface. It is
    ///     the pass's clear colour rather than a draw, because a wall the interface drew would be
    ///     scaled by the white level too and the ratio could never move.
    /// </remarks>
    static readonly Color4 Wall = new(100f, 100f, 100f, 1f);

    /// <summary>A white HUD panel is darker than the wall it covers until the frame carries a white level.</summary>
    /// <remarks>
    ///     <para>
    ///         The fixture is the smallest thing that can carry the defect: one opaque white
    ///         rectangle over the middle of a wall at 100 cd/m², built twice from one draw list and
    ///         differing only in <see cref="UiGeometryBuilder.WhiteLevel" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The wall is asserted unmoved in both frames.</b> Without that, "the panel got
    ///         brighter" would also be satisfied by a change that lit the whole buffer — and the
    ///         thing under test is a scale spent on the interface's colours alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the draw count is asserted before either pixel.</b> A frame that drew nothing
    ///         reads the wall at both points, which satisfies "the wall is unmoved" perfectly and
    ///         would leave the panel comparisons measuring the clear colour against itself.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AWhitePanelIsDarkerThanTheWallItCoversUntilTheFrameCarriesAWhiteLevel() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        var dark = Frame(owned, 1f, "hud-dark");
        var lit = Frame(owned, DiffuseWhite, "hud-lit");

        // The panel is opaque in both, so neither reading is of the wall showing through a panel that
        // drew nothing.
        Assert.Equal(1f, Alpha(dark, Side / 2, Side / 2), 3);
        Assert.Equal(1f, Alpha(lit, Side / 2, Side / 2), 3);

        // The scene is not the interface's to scale.
        Assert.Equal(100f, At(dark, 4, 4), 2);
        Assert.Equal(100f, At(lit, 4, 4), 2);

        // The defect, in the units the pass works in. A panel CSS calls white leaves the builder at
        // one candela, which is a hundredth of the wall; with the frame's white it is diffuse white,
        // twice the wall.
        Assert.Equal(1f, At(dark, Side / 2, Side / 2), 2);
        Assert.Equal(DiffuseWhite, At(lit, Side / 2, Side / 2), 1);

        Assert.True(
            At(dark, Side / 2, Side / 2) < At(dark, 4, 4),
            "a white panel came out darker than the wall on the device, which is the defect and not "
            + "the fix — see `UiGeometryBuilder.WhiteLevel` and #670."
        );

        Assert.True(
            At(lit, Side / 2, Side / 2) > At(lit, 4, 4),
            "a white panel came out no brighter than the wall on the device, so the white level "
            + "reached the geometry and not the pixels."
        );
    }

    /// <summary>One frame, at one white level, over the wall, rendered by the device.</summary>
    /// <remarks>
    ///     ⚠ <b>A colour target of its own per call, because the graph is reset between them.</b> Two
    ///     frames into one imported texture would have the second's pass load what the first stored,
    ///     which is a comparison of a frame against itself plus a frame.
    /// </remarks>
    static Vector4[] Frame(Fixture fixture, float white, string name) {
        var device = fixture.Device;

        fixture.Graph.Reset();

        var list = new DrawList();
        list.BeginFrame();

        // Opaque and white: the colour whose whole meaning is "as bright as this frame's white".
        list.Add(new(DrawCommandKind.Rectangle, Side / 4, Side / 4, Side / 2, Side / 2, Color4.White, 0, 0));
        list.EndFrame();

        var atlas = new GlyphAtlas(64, 64);
        var cache = new GlyphFieldCache(atlas);
        var geometry = new UiGeometryBuilder { Gamut = ColorGamut.Srgb, WhiteLevel = white }.Build(
            list,
            cache,
            Viewport
        );

        // ⚠ The number the frame was built at rides the frame, which is what makes this fixture's two
        // calls distinguishable by something other than their pixels. See `UiGeometry.WhiteLevel`.
        Assert.Equal(white, geometry.WhiteLevel);

        var target = fixture.Owned(
            name,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            PixelFormat.Rgba32Float
        );

        var colour = fixture.Graph.ImportTexture(
            target.Texture,
            target.View,
            target.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var renderer = new UiRenderer(
            device,
            new(
                fixture.Shader("ui.vert.spv", ShaderStage.Vertex),
                fixture.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                fixture.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                fixture.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ),
            new Rendering.RenderOutput([PixelFormat.Rgba32Float])
        );

        fixture.Owns(renderer.Dispose);

        fixture.Graph.AddPass(name, pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Wall);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var readback = device.CreateBuffer(
            new(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "hud readback")
        );

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "hud")) {
            renderer.Upload(commands, geometry, atlas);
            fixture.Graph.Execute(commands);
            commands.CopyTextureToBuffer(new(fixture.Graph.TextureOf(colour)), new(Side, Side, 1), readback, 0);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        // The instrument, before the pixels: the frame drew, and it drew the one quad the fixture
        // holds. A renderer that skipped it would leave a buffer full of wall, which reads the same
        // at both of the points this file compares.
        Assert.Equal(1, renderer.Draws);

        var floats = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(floats.AsSpan()));
        device.Destroy(readback);

        var pixels = new Vector4[Side * Side];

        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = new(
                floats[index * 4],
                floats[(index * 4) + 1],
                floats[(index * 4) + 2],
                floats[(index * 4) + 3]
            );
        }

        return pixels;
    }

    /// <summary>One pixel's green channel, which is grey here and so is the luminance.</summary>
    static float At(Vector4[] frame, int x, int y) => frame[(y * Side) + x].Y;

    /// <summary>And its coverage, so a reading can be told apart from the wall behind it.</summary>
    static float Alpha(Vector4[] frame, int x, int y) => frame[(y * Side) + x].W;

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
