// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The user interface, drawn.
/// </summary>
/// <remarks>
///     <para>
///         Everything above <see cref="UiRenderer" /> is a pure function of a draw list and is
///         already checked without a device. What a picture adds is the only thing those checks
///         cannot reach: <b>whether the shaders agree with the geometry</b>. A signed distance
///         evaluated with the wrong sign draws the outside of every box; a projection that flips y
///         draws the frame upside down; an atlas sampled as sRGB draws text that is too thin; a
///         border taken as a second shape rather than as the difference of two coverages draws a
///         seam. Every one of those passes every unit test in <c>Vixen.Ui</c>.
///     </para>
///     <para>
///         ⚠ The properties are asserted before the picture is trusted, as this suite's README asks
///         for wherever the arithmetic is beyond hand-checking. Committing the first reference that
///         came out is committing whatever came out first.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiImageTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>
    ///     One of each kind: a filled rounded box, a border, a stroked path and a line of text.
    /// </summary>
    /// <remarks>
    ///     One fixture rather than four, and deliberately: the thing most likely to be wrong is not
    ///     any one shader but the <i>switching</i> between them. Three pipelines share one vertex
    ///     buffer and one push-constant range, and a descriptor set bound for the text survives into
    ///     whatever is drawn next unless the layout change invalidates it — which is a mistake that
    ///     needs two kinds in one frame to show at all.
    /// </remarks>
    [Fact]
    public void Interface() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui");

        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var geometry = Paint(cache);

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
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.08f, 0.09f, 0.11f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var image = owned.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));

        // Every draw reached the command list — a batch silently dropped would still produce a
        // plausible picture, just one missing a thing nobody counted.
        Assert.Equal(geometry.Draws.Count, renderer.Draws);

        // The atlas was uploaded once, not once per glyph.
        Assert.Equal(1, renderer.AtlasUploads);

        AssertTheBoxIsRounded(image);
        AssertTheBorderIsHollow(image);
        AssertTheTextIsThere(image);

        GoldenImage.Verify("ui-interface", image, Tolerance.Edges);
    }

    /// <summary>
    ///     ⚠ A clip is a scissor, and a scissor that is not set is a clip that does not clip.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Its own fixture rather than a corner of the one above, because what it asserts is an
    ///         absence: the part of a box outside the clip must be background. Folded into a busier
    ///         picture that would be one more region nobody looks at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The box is deliberately not symmetric about the clip's edge, and the first version
    ///         of this fixture was.</b> A centred box under a scissor at the halfway line looks
    ///         identical whether or not y is flipped — so it passed while the whole frame was being
    ///         drawn upside down, and it was the busier fixture next door that noticed. A clip test
    ///         whose picture is its own mirror image is a clip test that cannot see the most common
    ///         mistake in the file it is testing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Clipped() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui-clipped");

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));

        var list = new DrawList();
        list.BeginFrame();
        list.Add(new(DrawCommandKind.ClipPush, 0, 0, Side, Side / 2f, default, 0, 0));

        list.Add(new(DrawCommandKind.Rectangle, 16, 24, 96, 72, new Color4(0.2f, 0.8f, 0.5f, 1f), 0, 0));

        list.Add(new(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0, 0));

        // ⚠ A box under a clip that is entirely off the surface. The geometry builder still emits it —
        // it does not cull — so this is what makes the renderer's "skip a draw whose scissor is
        // empty" reachable at all, and the draw count below is what sees it. Without this the skip
        // could be deleted and no picture would change, because there was never an empty scissor.
        list.Add(new(DrawCommandKind.ClipPush, Side + 40, Side + 40, 20, 20, default, 0, 0));
        list.Add(new(DrawCommandKind.Rectangle, Side + 44, Side + 44, 12, 12, Color4.White, 0, 0));
        list.Add(new(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0, 0));

        list.EndFrame();

        var geometry = new UiGeometryBuilder().Build(list, cache, Viewport);

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

        owned.Graph.AddPass("ui-clipped", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var image = owned.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));

        // Two boxes were emitted and one was drawn: the second's clip does not intersect the surface,
        // so there is nothing to scissor it to.
        Assert.Equal(2, geometry.Draws.Count);
        Assert.Equal(1, renderer.Draws);

        // The box runs from y = 24 to y = 96 and the clip cuts it at 64.
        Assert.True(Green(image, Side / 2, 10) < 20, "the box drew above its own top edge");
        Assert.True(Green(image, Side / 2, 28) > 100, "the box is missing just below its top edge");
        Assert.True(Green(image, Side / 2, 56) > 100, "the box is missing just above the clip");
        Assert.True(Green(image, Side / 2, 72) < 20, "the box leaked below the clip");

        GoldenImage.Verify("ui-clipped", image, Tolerance.Edges);
    }

    // --------------------------------------------------------------- Content

    /// <summary>Builds the frame both the geometry and the picture are about.</summary>
    static UiGeometry Paint(GlyphFieldCache cache) {
        var font = Font();
        var list = new DrawList();
        list.BeginFrame();

        // A filled box with a radius large enough that the corners are unmistakable.
        list.Add(new(DrawCommandKind.Rectangle, 8, 8, 54, 40, new Color4(0.25f, 0.55f, 0.95f, 1f), 14, 0));

        // A border, whose middle has to stay background.
        list.Add(new(DrawCommandKind.Border, 70, 8, 50, 40, new Color4(0.95f, 0.45f, 0.2f, 1f), 10, 4));

        // A stroked path with a corner in it, which is where a join either exists or does not.
        var path = new PathBuilder()
            .MoveTo(new Vector2(12, 66))
            .LineTo(new Vector2(44, 88))
            .LineTo(new Vector2(76, 62))
            .LineTo(new Vector2(114, 84));

        list.Add(
            new DrawCommand(DrawCommandKind.PathStroke, 0, 0, 0, 0, new Color4(0.4f, 0.9f, 0.5f, 1f), 0, 4) {
                Offset = list.AddPath(path),
                Length = path.Count
            }
        );

        // Real glyphs from a real font, laid out along their runs.
        Text(list, font, "AB", 14, 116);

        // ⚠ A box *between* two text runs, so the frame alternates rather than ending on the text.
        // Two earlier versions of this fixture could not see the atlas re-bind at all: with the text
        // last, the atlas is bound once and it never matters whether a pipeline change disturbed it;
        // with only one text run, there is no second bind to skip. Deleting the re-bind broke
        // nothing both times, while the comment next to it claimed it would fail on "every real
        // frame". It takes box → text → box → text to find out.
        list.Add(new(DrawCommandKind.Rectangle, 74, 106, 14, 14, new Color4(0.9f, 0.8f, 0.3f, 1f), 4, 0));

        Text(list, font, "C", 94, 116);

        list.EndFrame();

        return new UiGeometryBuilder().Build(list, cache, Viewport);
    }

    /// <summary>One run of glyphs, positioned along the run rather than on the surface.</summary>
    static void Text(DrawList list, FontFace font, string text, float x, float y) {
        const float Size = 26f;

        var glyphs = new List<PositionedGlyph>();
        var pen = 0f;

        foreach (var character in text) {
            glyphs.Add(new(font.GlyphFor(character), pen, 0));
            pen += 24f;
        }

        list.Add(
            new DrawCommand(DrawCommandKind.Text, x, y, pen, Size, Color4.White, 0, 0) {
                Offset = list.AddGlyphs(glyphs),
                Length = glyphs.Count,
                Font = list.AddFont(font),
                FontSize = Size
            }
        );
    }

    // -------------------------------------------------------------- Oracles

    /// <summary>
    ///     ⚠ The corner is outside the shape and the middle is inside it, which is the one thing a
    ///     distance field can get exactly backwards while still drawing a rectangle.
    /// </summary>
    static void AssertTheBoxIsRounded(in Bitmap image) {
        Assert.True(Blue(image, 35, 28) > 200, "the box did not fill");
        Assert.True(Blue(image, 9, 9) < 60, "the rounded corner was filled in");
        Assert.True(Blue(image, 35, 4) < 60, "the box drew outside its own quad");
    }

    /// <summary>
    ///     ⚠ A border is a band, so its middle is background. Drawn as a filled shape it would pass
    ///     every assertion about its edge.
    /// </summary>
    static void AssertTheBorderIsHollow(in Bitmap image) {
        Assert.True(Red(image, 95, 10) > 150, "the border's top edge is missing");
        Assert.True(Red(image, 95, 28) < 60, "the border filled its middle");
    }

    /// <summary>
    ///     ⚠ Something bright is on the text row and nothing is on the row below it. The second half
    ///     is what catches an atlas sampled at the wrong scale, which draws a grey wash rather than
    ///     letters.
    /// </summary>
    static void AssertTheTextIsThere(in Bitmap image) {
        var text = 0;
        var below = 0;

        for (var x = 0; x < Side; x++) {
            for (var y = 96; y < 120; y++) {
                if (Red(image, x, y) > 180) {
                    text++;
                }
            }

            if (Red(image, x, Side - 2) > 180) {
                below++;
            }
        }

        Assert.True(text > 40, $"only {text} pixels of text were drawn");
        Assert.Equal(0, below);
    }

    // -------------------------------------------------------------- Helpers

    static int Red(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y)];

    static int Green(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y) + 1];

    static int Blue(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y) + 2];

    static FontFace? loaded;

    static FontFace Font() {
        if (loaded is not null) {
            return loaded;
        }

        using var stream = typeof(UiImageTests).Assembly
                               .GetManifestResourceStream("Vixen.Graphics.Golden.Tests.TestShapeLana.ttf")
                           ?? throw new InvalidOperationException("no test font is embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        loaded = FontFace.Load(memory.ToArray(), name: "golden");

        return loaded;
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
