// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     ⚠ A layer that overrides <see cref="UiElement.OnDraw" /> <em>can</em> write a word, and this
///     is the recipe.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1112">#1112</a>, and its premise is
///         wrong.</b> That issue says a drawn layer "cannot put a character on the screen" because
///         <see cref="DrawContext" /> has no text primitive, and offers two answers: build one, or
///         write down that a drawn layer is shapes only. Neither is right, because the absence is a
///         missing <em>shorthand</em> and not a missing capability — every piece is already public
///         and already reachable from a <c>DrawContext</c>:
///     </para>
///     <para>
///         <c>Element.Document.Fonts.Resolve</c> gives the face, <c>Document.Shaping.Shape</c> gives
///         the run — ⚠ through the same LRU the layout pass uses, which is the "cached per string so
///         a canvas of forty labels is not forty shapings" the issue asks a new primitive to
///         provide — <see cref="TextRun.Place" /> gives the glyphs at a baseline, and
///         <see cref="DrawList.AddGlyphs" />, <see cref="DrawList.AddFont" /> and
///         <see cref="DrawList.Add" /> put the command in the list. That is the whole of what
///         <c>DrawListBuilder</c> does per run, minus the layout pass a layer does not want.
///     </para>
///     <para>
///         ⚠ <b>So this file is the decision: a test rather than an API.</b> Adding
///         <c>DrawContext.DrawText</c> with no production caller is this workstream's most common
///         defect written on purpose, and writing "shapes only" into <c>DrawContext</c>'s remarks
///         would have been writing down something untrue. What was actually missing is that nobody
///         could find the recipe, so the recipe is here, executed, and <c>DrawContext</c>'s remarks
///         point at it. The day a second drawn layer wants a word, the shorthand is a wrap of this
///         method with a caller to justify it.
///     </para>
///     <para>
///         ⚠ <b>What a layer still does not get, and must not be sold as an oversight:</b> line
///         breaking, bidi reordering, font fallback across scripts, and the cascade. Those are
///         <see cref="UiElement" />'s layout pass. This is one run of one face on one baseline —
///         which is exactly what <c>NodePreview.Label</c> described ("a number, a width") and is not
///         a paragraph.
///     </para>
/// </remarks>
public class DrawnLayerTextTests {
    static readonly FontFace Font = LoadFont("TestShapeLana.ttf", "TestShapeLana");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>A layer with no style node, no layout box per item and no child element.</summary>
    /// <remarks>
    ///     The shape <c>NodePreviewLayer</c> and <c>NodeMinimap</c> are, and the reason they are: an
    ///     element per label is a style node, a layout box and a rebind per item.
    /// </remarks>
    sealed class Layer : UiElement {
        public Action<DrawContext>? Draws { get; set; }

        protected internal override void OnDraw(DrawContext context) => Draws?.Invoke(context);
    }

    /// <summary>The five calls. This is the whole recipe, and it is what the issue asked for.</summary>
    /// <param name="context">What the layer was handed.</param>
    /// <param name="text">One line, one run, no breaking.</param>
    /// <param name="x">Where the run starts, in document space.</param>
    /// <param name="baseline">⚠ The baseline, not the top — glyph origins sit on it.</param>
    /// <param name="size">The size in pixels the face is scaled to.</param>
    static void Write(DrawContext context, string text, float x, float baseline, float size) {
        var document = context.Element.Document;
        var face = document.Fonts.Resolve(null) ?? throw new InvalidOperationException("no default face");
        var run = new TextRun(face, document.Shaping.Shape(face, text, ParagraphDirection.LeftToRight), size);

        List<PositionedGlyph> placed = [];
        run.Place(placed);

        context.List.Add(
            new DrawCommand(DrawCommandKind.Text, x, baseline, run.Width, run.Height, context.Foreground, 0f, 0f) {
                Offset = context.List.AddGlyphs(placed),
                Length = placed.Count,
                Font = context.List.AddFont(face),
                FontSize = size
            }
        );
    }

    static UiDocument Drawn(Action<DrawContext> draws) {
        var document = new UiDocument(200f, 100f);
        document.Fonts.Register("Test", Font);
        document.Load("root { width: 200px; height: 100px; } layer { width: 200px; height: 100px; }");

        document.Root.Add<Layer>("layer").Draws = draws;

        document.Update();
        document.Draw();

        return document;
    }

    /// <summary>A drawn layer puts a word in the list, with its glyphs and its face.</summary>
    /// <remarks>
    ///     ⚠ <b>The glyph count is the assertion and the command is the precondition.</b> A command
    ///     of kind <see cref="DrawCommandKind.Text" /> with an empty run is what a layer emits when
    ///     the shaping did not happen — the renderer draws nothing and the list looks correct — so
    ///     asserting the kind alone would pass against exactly the state this file exists to say is
    ///     avoidable.
    /// </remarks>
    [Fact]
    public void A_drawn_layer_can_write_a_word() {
        using var document = Drawn(static context => Write(context, "1024", 4f, 20f, 16f));

        var command = Assert.Single(document.Drawing.Commands, static one => one.Kind == DrawCommandKind.Text);

        Assert.Equal(4, command.Length);
        Assert.Equal(4, document.Drawing.Glyphs.Count);
        Assert.Equal(16f, command.FontSize);
        Assert.True(command.Width > 0f, "a run of four digits has a width, or nothing was shaped");

        // ⚠ Placed from the run's own origin rather than in document space — the command carries
        // where the run is. A layer that added `x` to each glyph would draw the word twice as far
        // right at four times the distance, which is the trap DrawListBuilder documents.
        Assert.Equal(0f, document.Drawing.Glyphs[0].X);
    }

    /// <summary>
    ///     ⚠ And two layers writing the same word shape it once, because the document's cache is
    ///     what a layer is asked to go through.
    /// </summary>
    /// <remarks>
    ///     The half that answers the issue's own design requirement. A primitive "cached per string
    ///     so a canvas of forty labels is not forty shapings per frame" would have been a second
    ///     cache in front of <see cref="ShapingCache" />; the layout pass already goes through that
    ///     one, and so does a layer that asks the document rather than the face.
    /// </remarks>
    [Fact]
    public void Two_layers_writing_one_word_shape_it_once() {
        var document = new UiDocument(200f, 100f);
        document.Fonts.Register("Test", Font);
        document.Load("root { width: 200px; height: 100px; } layer { width: 200px; height: 40px; }");

        using var owner = document;

        var drawn = 0;

        foreach (var baseline in (float[])[20f, 60f]) {
            var layer = document.Root.Add<Layer>("layer");
            var y = baseline;

            layer.Draws = context => {
                drawn++;
                Write(context, "1024", 4f, y, 16f);
            };
        }

        document.Update();
        document.Draw();

        var misses = document.Shaping.Misses;
        var hits = document.Shaping.Hits;

        document.Draw();

        Assert.Equal(2, document.Drawing.Commands.Count(static one => one.Kind == DrawCommandKind.Text));

        // The instrument, and without it the assertion below is vacuous: a second `Draw` that
        // reused the previous frame's list would shape nothing for the same reason a cache would,
        // and the two are indistinguishable from the miss count alone.
        Assert.Equal(4, drawn);

        // ⚠ Over a second frame rather than within the first: the two layers already shared one
        // entry on the way in, so the first frame's miss count cannot tell a cache that works from
        // one that was asked once. A frame that shapes again is what a per-frame layer would cost.
        Assert.Equal(misses, document.Shaping.Misses);

        // ⚠ And the hits are the other direction, without which the line above is satisfied by a
        // layer that shapes through anything *except* this cache — a `TextShaper` call of its own,
        // a cache it made itself — which costs a shaping per label per frame and leaves the miss
        // count of the document's cache exactly where it was.
        Assert.Equal(hits + 2, document.Shaping.Hits);
    }
}
