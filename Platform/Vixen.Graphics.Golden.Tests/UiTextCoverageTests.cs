// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>What <c>ui-text.frag</c> draws when the colour it is given is white, measured on the device.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle for <a href="https://github.com/Rikarin/Vixen/issues/232">#232</a>'s
///         missing surface, and a correction to its price.</b> That issue names "a pass that binds
///         <c>ui-text.frag</c> with the colour forced to white" as part of a render-pass capability,
///         and five audits have repeated it. Half of that is not a capability and is not missing:
///         the module already writes <c>varying_colour.rgb · α</c> premultiplied with
///         <c>α = varying_colour.a · coverage</c>, so a white opaque colour makes every one of the
///         four channels the glyph's coverage exactly. There is no shader to write, no binding to
///         change and no pass kind to add — a run of text emitted white <i>is</i> its own coverage
///         surface, and what remains is draw-list shaped.
///     </para>
///     <para>
///         ⚠ <b>Measured rather than read off the source, because the claim is about what the device
///         produces.</b> The target is <c>Rgba8UNorm</c> and holds linear values, so the four
///         channels quantise identically and the identity is exact rather than approximate — which
///         is what makes this a closed-form oracle and not an eyeballed picture. A module that
///         decoded the atlas as sRGB, took the median of the wrong channels or dropped the
///         premultiply would break it in a way no reference image of white-on-black could show.
///     </para>
///     <para>
///         <b>What #232 still waits on is the other half</b>, and this does not touch it: a
///         <c>UiLayer</c> that names a coverage source separately from its colour source, and a
///         builder that emits the subtree's glyph runs a second time in white into it.
///         <c>bg-clip</c> stays unregistered.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiTextCoverageTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>
    ///     ⚠ White text is its own coverage: r, g, b and a are one number at every pixel, that
    ///     number is the alpha the same run drawn in a colour produces, and the same run drawn
    ///     transparent produces nothing at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three frames, and each answers a different half of #232.</b> The white frame is the
    ///         coverage surface the issue says has to be built. The red frame is the oracle for it —
    ///         its alpha channel is the same coverage, so the white frame carries the <i>right</i>
    ///         number rather than merely a self-consistent one, which a frame that drew nothing would
    ///         also be. And the transparent frame is the issue's own central argument, measured
    ///         rather than argued: the idiom draws the glyphs with no colour, so a group holding them
    ///         composites to an empty surface and using that surface as a mask multiplies the
    ///         background by zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fixture each, because <c>Fixture.Render</c> executes the graph and a graph is
    ///         one frame.</b> The same arrangement <c>UiImageTests.Scaled</c> uses, for its reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WhiteTextIsItsOwnCoverageAndTransparentTextIsNothing() {
        if (!Render(Color4.White, out var white)) {
            return;
        }

        var covered = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var offset = white.Offset(x, y);
                var r = white.Pixels[offset];
                var g = white.Pixels[offset + 1];
                var b = white.Pixels[offset + 2];
                var a = white.Pixels[offset + 3];

                Assert.True(
                    r == g && g == b && b == a,
                    $"({x}, {y}) is ({r}, {g}, {b}, {a}), which is not a coverage: white text premultiplied "
                    + "by its own alpha has to leave all four channels equal"
                );

                if (a > 0) {
                    covered++;
                }
            }
        }

        // The glyphs were actually drawn, so the agreement above is not a blank frame agreeing with
        // itself — and they did not fill the surface either, which a mis-sampled atlas can do.
        Assert.True(covered > 200, $"only {covered} pixels were covered at all; the text did not draw");
        Assert.True(covered < Side * Side / 2, $"{covered} pixels were covered; that is not a run of glyphs");

        // ⚠ The same run in an opaque colour. Its alpha is the coverage and nothing else — the colour
        // multiplies the three chromatic channels and leaves the fourth alone — so this is what says
        // the white frame carries the right number rather than a consistent one.
        Assert.True(Render(new Color4(1f, 0f, 0f, 1f), out var red), "the second fixture did not open");

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var offset = white.Offset(x, y);

                Assert.Equal(white.Pixels[offset + 3], red.Pixels[offset + 3]);

                // And it really is a different picture, so the equality above is not two identical
                // frames: red has no green in it anywhere.
                Assert.Equal(0, red.Pixels[offset + 1]);
            }
        }

        // ⚠ And the idiom this issue is about: `text-transparent` over a gradient. The glyphs are
        // drawn with no colour at all, so the surface a group holding them composites to is empty —
        // which is exactly why `bg-clip-text` cannot be built out of a layer surface, and is the
        // claim #232 rests on. Measured here so the argument is a red line rather than prose.
        Assert.True(Render(new Color4(1f, 1f, 1f, 0f), out var invisible), "the third fixture did not open");

        foreach (var channel in invisible.Pixels) {
            Assert.Equal(0, channel);
        }
    }

    /// <summary>Draws one line of glyphs in a colour, into a transparent surface, and reads it back.</summary>
    /// <param name="colour">What the run is drawn in.</param>
    /// <param name="image">The picture.</param>
    /// <returns>Whether there was a device. False has already skipped or failed the test.</returns>
    static bool Render(Color4 colour, out Bitmap image) {
        image = default;

        if (!TryOpen(out var fixture, out _)) {
            return false;
        }

        using var owned = fixture!;
        var target = owned.ColourTarget("ui text coverage");

        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var geometry = Paint(cache, colour);

        var renderer = new UiRenderer(
            owned.Device,
            new(
                owned.Shader("ui.vert.spv", ShaderStage.Vertex),
                owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass("ui", pass => {
            // ⚠ Cleared to transparent black rather than to a colour, because the claim is about a
            // coverage surface and a coloured clear would make every uncovered pixel disagree with
            // itself. It is also the clear a real coverage pass would use.
            pass.ColourAttachment(target, LoadAction.Clear, new(0f, 0f, 0f, 0f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        image = owned.Render(target, commands => renderer.Upload(commands, geometry, cache.Atlas));

        return true;
    }

    /// <summary>A line of glyphs from a real font, and nothing else in the frame.</summary>
    static UiGeometry Paint(GlyphFieldCache cache, Color4 colour) {
        const float Size = 40f;

        var font = Font();
        var list = new DrawList();

        list.BeginFrame();

        var glyphs = new List<PositionedGlyph>();
        var pen = 0f;

        foreach (var character in "ABC") {
            glyphs.Add(new(font.GlyphFor(character), pen, 0));
            pen += 36f;
        }

        list.Add(
            new DrawCommand(DrawCommandKind.Text, 8, 80, pen, Size, colour, 0, 0) {
                Offset = list.AddGlyphs(glyphs),
                Length = glyphs.Count,
                Font = list.AddFont(font),
                FontSize = Size
            }
        );

        list.EndFrame();

        return new UiGeometryBuilder().Build(list, cache, Viewport);
    }

    static FontFace? loaded;

    static FontFace Font() {
        if (loaded is not null) {
            return loaded;
        }

        using var stream = typeof(UiTextCoverageTests).Assembly
                               .GetManifestResourceStream("Vixen.Graphics.Golden.Tests.TestShapeLana.ttf")
                           ?? throw new InvalidOperationException("no test font is embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        loaded = FontFace.Load(memory.ToArray(), name: "coverage");

        return loaded;
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so this may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
