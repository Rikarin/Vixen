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
        AssertTheBoxIsGraded(image);
        AssertTheBorderIsHollow(image);
        AssertTheTextIsThere(image);

        GoldenImage.Verify("ui-interface", image, Tolerance.Edges);
    }

    /// <summary>
    ///     A shadow, which is the one primitive whose whole point is what happens *between* covered
    ///     and uncovered.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The geometry tests can say the quad is big enough and the blur reached the shape
    ///         record. What they cannot say is whether the shader turned it into a falloff — a blur
    ///         lane read from the wrong component, or a <c>smoothstep</c> with its edges the wrong
    ///         way round, gives a hard-edged box or nothing at all, and every test in
    ///         <c>Vixen.Ui</c> still passes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The card is offset from the shadow, and deliberately.</b> A shadow directly under
    ///         an opaque box of the same size is invisible, so a fixture with the two concentric
    ///         would pass with the shadow not drawn at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Shadowed() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui-shadowed");
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));

        var list = new DrawList();
        list.BeginFrame();

        // A shadow the size of the card, pushed down and right, then the card on top of it.
        list.Add(new(DrawCommandKind.Shadow, 40, 44, 48, 40, new Color4(0f, 0f, 0f, 0.8f), 8, 6));
        list.Add(new(DrawCommandKind.Rectangle, 32, 32, 48, 40, new Color4(0.9f, 0.9f, 0.95f, 1f), 8, 0));

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

        owned.Graph.AddPass("ui-shadowed", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(1f, 1f, 1f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var image = owned.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));

        // One draw: a shadow shares the box pipeline, so it batches with the card rather than
        // splitting the frame in two.
        Assert.Single(geometry.Draws);

        AssertTheShadowFadesOutwards(image);
        AssertTheCardIsOverTheShadow(image);

        GoldenImage.Verify("ui-shadowed", image, Tolerance.Edges);
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

    /// <summary>
    ///     ⚠ A frame with more boxes than the last one replaces the buffer they live in, and the
    ///     descriptor set still points at the old one until it is rewritten.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two uploads into one renderer, which is what makes the regrow reachable at all.</b>
    ///     A fixture that uploads once allocates the buffer and never grows it, so deleting the
    ///     rewrite broke nothing — the descriptor was written by the atlas path on the way past and
    ///     was correct by accident. The second upload is the whole test: it frees the buffer the set
    ///     names and puts a larger one somewhere else.
    /// </remarks>
    [Fact]
    public void Regrown() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("ui-regrown");

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var small = Grid(cache, 2);
        var large = Grid(cache, 10);

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

        owned.Graph.AddPass("ui-regrown", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, large, new(Side, Side)));
        });

        var image = owned.Render(
            colour,
            commands => {
                renderer.Upload(commands, small, cache.Atlas);
                renderer.Upload(commands, large, cache.Atlas);
            }
        );

        Assert.Equal(100, large.Shapes.Count);

        // Every cell of the grid is filled, including the ninety-six the first upload had no room
        // for. A stale descriptor reads another allocation's bytes as half-sizes, and the boxes past
        // the fourth come out the wrong size or not at all.
        for (var row = 0; row < 10; row++) {
            for (var column = 0; column < 10; column++) {
                var x = (column * 12) + 9;
                var y = (row * 12) + 9;

                Assert.True(Green(image, x, y) > 150, $"the box at ({column}, {row}) is missing");
            }
        }

        GoldenImage.Verify("ui-regrown", image, Tolerance.Edges);
    }

    // --------------------------------------------------------------- Content

    /// <summary>A square grid of small rounded boxes, for a frame whose box count is the point.</summary>
    static UiGeometry Grid(GlyphFieldCache cache, int side) {
        var list = new DrawList();
        list.BeginFrame();

        for (var row = 0; row < side; row++) {
            for (var column = 0; column < side; column++) {
                list.Add(
                    new(
                        DrawCommandKind.Rectangle,
                        (column * 12) + 5,
                        (row * 12) + 5,
                        8,
                        8,
                        new Color4(0.2f, 0.85f, 0.4f, 1f),
                        2,
                        0
                    )
                );
            }
        }

        list.EndFrame();

        return new UiGeometryBuilder().Build(list, cache, Viewport);
    }

    /// <summary>Builds the frame both the geometry and the picture are about.</summary>
    static UiGeometry Paint(GlyphFieldCache cache) {
        var font = Font();
        var list = new DrawList();
        list.BeginFrame();

        // ⚠ A filled box with a *different radius on every corner*, and an elliptical one at the top
        // left — 20 across by 8 down. A shader that read one radius, or read the four in the wrong
        // order, still draws a rounded rectangle; only a box whose corners disagree says which.
        list.Add(
            new DrawCommand(DrawCommandKind.Rectangle, 8, 8, 54, 40, new Color4(0.25f, 0.55f, 0.95f, 1f), 0, 0) {
                Offset = list.AddBox(
                    new BoxStyle(
                        new CornerRadii(new Vector2(20, 8), new Vector2(2, 2), Vector2.Zero, new Vector2(16, 16)),
                        new Color4(0.1f, 0.15f, 0.5f, 1f),
                        new Vector2(0, 1)
                    )
                ),
                Length = 1
            }
        );

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

    /// <summary>
    ///     ⚠ A display with two pixels per device-independent one draws the same picture, larger.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The regression test for a unit mismatch that had no picture.</b>
    ///         <see cref="UiRenderer.Record" /> takes the geometry's extent for the projection and a
    ///         scale for the scissor, and until it took the second the two were one number — which
    ///         is correct at 1:1 and only at 1:1. An interface laid out in device-independent units
    ///         on a retina display was drawn into the top-left quarter of the window, and the pointer
    ///         went on hitting the controls where the layout said they were, so it read as a renderer
    ///         that was mysteriously small rather than as a unit mismatch.
    ///     </para>
    ///     <para>
    ///         <b>Two renders compared against each other rather than a new reference image</b>, and
    ///         that is what makes it a property rather than a snapshot: the same interface, once at
    ///         1:1 into a surface of <c>Side</c>, once from a half-size geometry at scale two into a
    ///         surface of <c>Side</c>. Changing the pixel density must change the density and nothing
    ///         else, so the two pictures have to agree — where a reference PNG could only have said
    ///         that the second looked like whatever it looked like on the day it was committed.
    ///     </para>
    ///     <para>
    ///         ⚠ There is a clip in it deliberately. The projection is half the bug: a scissor left in
    ///         geometry units cuts at half the right place, and the busiest fixture in this file
    ///         cannot see that because nothing in it is clipped. Verified by sabotage — passing
    ///         <c>1f</c> to the scissor fails this and nothing else in the suite.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Scaled() {
        // ⚠ A fixture each. `Render` executes the graph and a graph is one frame, so the two pictures
        // cannot come out of the same one — which is the harness saying, correctly, that these are
        // two frames rather than two passes.
        if (!TryOpen(out var first, out _)) {
            return;
        }

        Bitmap reference;

        using (var owned = first!) {
            reference = Draw(owned, "ui-scale-1", Side, 1f);
        }

        if (!TryOpen(out var second, out _)) {
            return;
        }

        Bitmap scaled;

        using (var owned = second!) {
            scaled = Draw(owned, "ui-scale-2", Side / 2, 2f);
        }

        var differences = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (Math.Abs(Green(reference, x, y) - Green(scaled, x, y)) > 24) {
                    differences++;
                }
            }
        }

        // ⚠ Not zero. The two are rasterised from different vertex coordinates, so the antialiased
        // edges land on subtly different subpixel positions and a handful of pixels along every
        // boundary differ. What the count catches is the failure this exists for, which is not
        // subtle: a quarter-size picture differs over the three quarters it does not cover, and both
        // sabotages above land at about a quarter of the surface.
        Assert.True(
            differences < Side * Side / 50,
            $"{differences} of {Side * Side} pixels differ, which is more than an edge's worth"
        );

        // And the picture is actually there, so that two blank surfaces cannot agree their way to a
        // pass.
        Assert.True(Green(scaled, Side / 4, Side / 4) > 100, "the scaled picture is empty");
        Assert.True(Green(scaled, (Side / 2) + 8, Side / 4) > 100, "the scaled picture stops early");
    }

    /// <summary>Draws one clipped box into a <c>Side</c>-square surface.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="name">What to call the target.</param>
    /// <param name="extent">The geometry's own extent — half the surface at scale two.</param>
    /// <param name="scale">How many framebuffer pixels one geometry unit is.</param>
    /// <remarks>
    ///     Everything is a fraction of the extent, so the two runs describe the same picture in
    ///     different units — which is the whole point: if they described different pictures the
    ///     comparison would be measuring the arithmetic in this method.
    /// </remarks>
    static Bitmap Draw(Fixture fixture, string name, int extent, float scale) {
        var colour = fixture.ColourTarget(name);
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));

        var list = new DrawList();
        list.BeginFrame();

        list.Add(new(DrawCommandKind.ClipPush, 0, 0, extent, extent * 0.5f, default, 0, 0));
        list.Add(
            new(
                DrawCommandKind.Rectangle,
                extent * 0.125f,
                extent * 0.125f,
                extent * 0.75f,
                extent * 0.75f,
                new Color4(0.2f, 0.8f, 0.5f, 1f),
                0,
                0
            )
        );

        list.Add(new(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0, 0));
        list.EndFrame();

        var geometry = new UiGeometryBuilder().Build(list, cache, new Rectangle(0, 0, extent, extent));

        var renderer = new UiRenderer(
            fixture.Device,
            new(
                fixture.Shader("ui.vert.spv", ShaderStage.Vertex),
                fixture.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                fixture.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                fixture.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        fixture.Owns(renderer.Dispose);

        fixture.Graph.AddPass(
            name,
            pass => {
                pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
                pass.SideEffect();
                pass.Execute(
                    context => renderer.Record(context.CommandList, geometry, new(extent, extent), scale)
                );
            }
        );

        return fixture.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));
    }

    // -------------------------------------------------------------- Oracles

    /// <summary>
    ///     ⚠ The corner is outside the shape and the middle is inside it, which is the one thing a
    ///     distance field can get exactly backwards while still drawing a rectangle.
    /// </summary>
    static void AssertTheBoxIsRounded(in Bitmap image) {
        Assert.True(Blue(image, 35, 28) > 120, "the box did not fill");
        Assert.True(Blue(image, 35, 4) < 60, "the box drew outside its own quad");

        // ⚠ Every corner separately, because that is the only thing that says the four radii arrived
        // in the right order. The box runs from (8, 8) to (62, 48); its top left is cut 20 across by
        // 8 down, its top right barely at all, its bottom right square, its bottom left by 16.
        Assert.True(Blue(image, 10, 9) < 60, "the top-left corner was not cut");
        Assert.True(Blue(image, 60, 9) > 100, "the top-right corner was cut when it is nearly square");
        Assert.True(Blue(image, 60, 46) > 100, "the bottom-right corner was cut when it is square");
        Assert.True(Blue(image, 10, 46) < 60, "the bottom-left corner was not cut");

        // ...and elliptical rather than circular. The cut is 20 across and 8 down, so it stops much
        // sooner going down than going along: (18, 10) is inside this corner and would be outside a
        // circular 20px one. A shader that read only `radiiX` draws exactly that circle.
        Assert.True(Blue(image, 18, 10) > 100, "the top-left corner was cut as a circle rather than an ellipse");
    }

    /// <summary>
    ///     ⚠ The gradient runs down the box, so the top is the command's colour and the bottom is the
    ///     style's. A shader that ignored the axis, or normalised it against the wrong extent, still
    ///     draws a blue box.
    /// </summary>
    static void AssertTheBoxIsGraded(in Bitmap image) {
        var top = Blue(image, 35, 12);
        var middle = Blue(image, 35, 28);
        var bottom = Blue(image, 35, 44);

        Assert.True(top > middle, $"the gradient did not darken downwards ({top} then {middle})");
        Assert.True(middle > bottom, $"the gradient stopped part way ({middle} then {bottom})");
        Assert.True(top - bottom > 40, $"the gradient is barely there ({top} to {bottom})");
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

    /// <summary>The shadow gets lighter the further out it goes, and never gets darker again.</summary>
    /// <remarks>
    ///     ⚠ Monotonicity rather than a value at a point. A hard-edged box passes any single sample
    ///     taken inside it, and a falloff running the wrong way passes any sample taken outside — the
    ///     property that separates a blur from both of those is that it only ever lightens as it
    ///     leaves the box, which needs the whole row to say.
    /// </remarks>
    static void AssertTheShadowFadesOutwards(in Bitmap image) {
        // A column below the card, running from inside the shadow out into the white surface. The
        // card ends at y = 72 and the shadow's own edge is at 84, so this starts below the card and
        // covers the whole falloff.
        const int column = 64;
        var previous = -1;

        for (var y = 74; y < 108; y++) {
            var value = Red(image, column, y);

            if (previous >= 0) {
                Assert.True(
                    value >= previous - 2,
                    $"the shadow darkens again at y = {y}: {previous} then {value}"
                );
            }

            previous = value;
        }

        // And it is a gradient rather than a step: solid where the shadow is, white well away from
        // it, and neither of those at the same value.
        Assert.True(Red(image, column, 76) < 100, "there is no shadow just below the card");
        Assert.True(Red(image, column, 107) > 240, "the shadow never reaches the background");
    }

    /// <summary>The card is drawn over its own shadow rather than under it.</summary>
    static void AssertTheCardIsOverTheShadow(in Bitmap image) {
        // The card's own middle, which the shadow lies under and must not darken.
        Assert.True(Red(image, 56, 52) > 200, "the card is darker than it should be, or is not there");
    }

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
