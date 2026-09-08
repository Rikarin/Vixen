// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>A viewer's channel and colour-space toggles, on the device, against a closed form.</summary>
/// <remarks>
///     <para>
///         <b>The gap this closes.</b>
///         <see href="https://github.com/Rikarin/Vixen/issues/611">#611</see>:
///         <c>Vixen.Ui.Controls.Advanced.ImageView</c> shipped a channel picker and a colour-space
///         toggle and neither changed a pixel, because the image command carried a tint and a source
///         rectangle and a tint can only multiply. <c>DrawCommand.View</c> is the field that was
///         missing and this is the assertion that it arrives — at the *shader*, which is the only
///         place that can be true.
///     </para>
///     <para>
///         ⚠ <b>What the issue asked to be checked first is the instrument, and this is that
///         check.</b> "A test that asserts the toggle changed the picture must be able to tell a
///         swizzled draw from an unswizzled one. Comparing draw-command counts cannot." So no
///         assertion here counts anything: every one reads a texel out of the rendered picture and
///         compares it with a number worked out on paper from the texel that went in. The counts
///         that <i>are</i> asserted are asserted for the opposite reason — one draw, not four, is
///         what says the swizzle did not split the batch.
///     </para>
///     <para>
///         <b>Why the arithmetic is exact rather than a tolerance.</b> The target is
///         <c>Rgba8UNorm</c>, so what the fragment stage returns is what the readback holds; the
///         texture is <c>Rgba8UNorm</c> too and is sampled at a texel centre with an untinted white
///         colour, so an isolate is the input byte, unchanged, in all three colour channels. The one
///         place a tolerance is needed is the transfer curve, where a <c>pow</c> lands between two
///         bytes.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day it does not run.</b> It skips, loudly, like every other
///         file in this project — see <see cref="TryOpen" />, which turns a missing device into a
///         failure whenever <c>VIXEN_REQUIRE_VULKAN</c> promised one. A green run with a zero total
///         is the failure this repository has shipped before, and the skip count is what reveals it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiImageViewTests {
    const int Side = Fixture.Side;

    /// <summary>The number the draw list carries and the renderer registers.</summary>
    const ulong Texture = 11;

    /// <summary>The alpha of every texel: a partial coverage, so an isolate of it is not 0 or 255.</summary>
    /// <remarks>
    ///     ⚠ <b>64 and not 255, and that is the whole of what makes the alpha isolate an
    ///     assertion.</b> A fully opaque texture makes "show the alpha as a grey" and "draw nothing
    ///     at all differently" the same white rectangle, so a fixture that used 255 would pass
    ///     against a shader that ignored the channel entirely.
    /// </remarks>
    const byte Coverage = 64;

    /// <summary>
    ///     The red channel of every texel: mid-grey, which is where the transfer curve is steepest.
    /// </summary>
    /// <remarks>
    ///     Near 0 or near 1 the sRGB decode is close to the identity and a shader that skipped it
    ///     would be within a byte or two of one that applied it. At 128 the two answers are 128 and
    ///     55, which no rounding closes.
    /// </remarks>
    const byte Mid = 128;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>
    ///     Four draws of one texture — untouched, red, alpha, and stored values — read back and
    ///     checked against the numbers each one owes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One frame rather than four, for <c>UiImageTests.Images</c>' reason: the four quads
    ///         name one texture and sit next to each other, so a swizzle carried anywhere but on the
    ///         vertices would have to break the batch to be per-quad at all. They come out as
    ///         <b>one</b> draw and four different pictures, which is the claim
    ///         <see cref="UiImageView" />'s remark makes and the only way to prove it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The green and blue channels differ from the red, so an isolate that read the
    ///         wrong component is a wrong number rather than the right one.</b> A fixture whose texel
    ///         was grey could not tell <c>Red</c> from <c>Green</c> from a shader that ignored
    ///         <c>shape.y</c> and showed the colour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AToggleReachesThePicture() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui");

        var sampled = owned.Sampled("ui view", 1, Texel);

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(Frame(), cache, Viewport);

        var renderer = new UiRenderer(
            owned.Device,
            new(
                owned.Shader("ui.vert.spv", ShaderStage.Vertex),
                owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ) {
                Image = owned.Shader("ui-image.frag.spv", ShaderStage.Fragment)
            },
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);
        renderer.RegisterImage(Texture, sampled.View);

        owned.Graph.AddPass("ui", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();
            pass.Execute(graph => renderer.Record(graph.CommandList, geometry, new(Side, Side)));
        });

        var image = owned.Render(
            colour,
            commands => {
                renderer.Upload(commands, geometry, cache.Atlas);

                commands.Barrier(
                    new([], [new(sampled.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
                );

                commands.CopyBufferToTexture(sampled.Staging, 0, new(sampled.Texture), new(1, 1, 1));

                commands.Barrier(
                    new([], [new(sampled.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );
            }
        );

        // ⚠ One draw from four commands, and this is the *only* count in the file. Four quads over
        // one texture with four different views batch into one draw because the view rides the
        // vertices — a per-draw uniform would read four here, and the picture would still be right.
        Assert.Equal(geometry.Draws.Count, renderer.Draws);
        Assert.Single(geometry.Draws);

        // The colour, untouched: each channel is its own byte and the coverage is the alpha, so the
        // premultiplied red is 128 · 64/255 ≈ 32. This is the quad that must not have moved.
        Sample(image, 0, out var r0, out var g0, out var b0);
        Assert.InRange(r0, 30, 34);
        Assert.InRange(g0, 48, 52);
        Assert.InRange(b0, 6, 10);

        // Red alone, opaque: 128 in all three, and *not* premultiplied down to 32 — an isolate that
        // kept the image's own alpha would read a third of this.
        Sample(image, 1, out var r1, out var g1, out var b1);
        Assert.InRange(r1, Mid - 2, Mid + 2);
        Assert.Equal(r1, g1);
        Assert.Equal(r1, b1);

        // Green alone: 200, which is the assertion that `shape.y` selects rather than merely
        // switching something on. A shader that isolated a fixed channel passes the case above.
        Sample(image, 2, out var r2, out _, out _);
        Assert.InRange(r2, 198, 202);

        // Alpha alone: 64, the one number that is invisible to every tint — it is what the alpha
        // *is*, drawn opaque, rather than what it does to the colour.
        Sample(image, 3, out var r3, out var g3, out var b3);
        Assert.InRange(r3, Coverage - 2, Coverage + 2);
        Assert.Equal(r3, g3);
        Assert.Equal(r3, b3);

        // The stored values, with the colour left whole: the sRGB decode of 128/255 is 0.2159, so
        // 55 — and the premultiply by the coverage takes it to 55 · 64/255 ≈ 14. The number that
        // matters is that it is *not* the 32 the first quad reads.
        Sample(image, 4, out var r4, out _, out _);
        Assert.InRange(r4, 12, 16);
        Assert.True(
            r4 < r0 - 8,
            $"the stored-values quad read {r4} against the plain quad's {r0}: the transfer curve did "
            + "not reach the shader, and `shape.z` is being dropped somewhere between the draw "
            + "command and `UiImage`."
        );
    }

    /// <summary>The row of quads: one texture, five views, left to right.</summary>
    /// <returns>A finished draw list.</returns>
    /// <remarks>
    ///     ⚠ <b>Internal because a second suite draws it, and that is what
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1016">#1016</a> was about.</b> This file
    ///     renders it through the hand-written GLSL copy and reads texels against numbers worked out
    ///     on paper; <c>UiRavenAgreementTests</c> renders the same list through <em>both</em> tables
    ///     and compares the pictures with each other, which is what puts the two new branches through
    ///     the module every shipping application draws with. One frame builder rather than two, so
    ///     the arithmetic proof and the agreement proof cannot be about different pictures.
    /// </remarks>
    internal static DrawList Frame() {
        var list = new DrawList();
        list.BeginFrame();

        foreach (var (index, view) in Views()) {
            list.Add(
                new DrawCommand(DrawCommandKind.Image, Left(index), Top, Width, Height, Color4.White, 0, 0) {
                    Image = Texture,
                    View = view
                }
            );
        }

        list.EndFrame();

        return list;
    }

    /// <summary>The texel every quad samples: red 128, green 200, blue 32, alpha 64.</summary>
    /// <remarks>
    ///     Four distinct numbers, so every isolate has an answer no other isolate shares — a grey
    ///     texel could not tell <c>Red</c> from <c>Green</c> from a shader that ignored the request.
    /// </remarks>
    internal static byte[] Texel => [Mid, 200, 32, Coverage];

    /// <summary>The number the draw list names the texture by, which a renderer has to register.</summary>
    internal static ulong Registered => Texture;

    /// <summary>The five views drawn, left to right.</summary>
    /// <remarks>
    ///     A method rather than a field so the indices and the views cannot drift apart: the layout
    ///     below reads the same index this yields.
    /// </remarks>
    internal static (int Index, UiImageView View)[] Views() => [
        (0, default),
        (1, new(UiImageChannel.Red, false)),
        (2, new(UiImageChannel.Green, false)),
        (3, new(UiImageChannel.Alpha, false)),
        (4, new(UiImageChannel.All, true))
    ];

    /// <summary>How wide each quad is drawn.</summary>
    const int Width = 20;

    /// <summary>How tall.</summary>
    const int Height = 40;

    /// <summary>Where the row of quads starts.</summary>
    const int Top = 20;

    /// <summary>The left edge of the quad at an index.</summary>
    static int Left(int index) => 4 + (index * (Width + 4));

    /// <summary>The three colour bytes at the middle of the quad at an index.</summary>
    /// <remarks>
    ///     ⚠ The <i>middle</i>, and not a corner. The quad's edge is where the rasteriser's coverage
    ///     is partial, and a texel read there is a blend with the clear colour that would make every
    ///     range below have to be widened until it stopped being an assertion.
    /// </remarks>
    static void Sample(in Bitmap image, int index, out int red, out int green, out int blue) {
        var at = image.Offset(Left(index) + (Width / 2), Top + (Height / 2));

        red = image.Pixels[at];
        green = image.Pixels[at + 1];
        blue = image.Pixels[at + 2];
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device opened: {reason}");
        }

        // ⚠ Skip rather than return false, which is what `DeviceGuardTests` has been failing about on
        // all three of master's CI legs. Returning false made the caller `return`, and a test that
        // returns is a test that PASSED — so on a runner with no device this class reported a green
        // picture it had never drawn. That is the eighteen-passing-goldens failure, and this door was
        // the one class in the project still holding it open.
        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
