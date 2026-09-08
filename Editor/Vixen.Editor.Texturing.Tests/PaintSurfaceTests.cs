// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What a surface does about a canvas that is not there, which is not the same in both cases.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/976">#976</a>: a missing canvas and an
///         unmade one look identical to <c>PaintCanvasStore.Open</c> and are opposite situations.</b>
///         A layer that names no file has not been painted yet and its first stroke has to make one;
///         a layer that names one and does not have it is a stack that was moved without its
///         canvases, and an artist needs telling. Inventing a canvas for both was silent in the
///         second case.
///     </para>
///     <para>
///         ⚠ <b>And the damage was not confined to this type, which is why the assertion below is
///         about the store rather than about the refusal.</b> An invented canvas is <em>adopted</em>
///         into the shared store, so refreshing the paint pane created the entry
///         <c>TextureExternalImages</c>'s own refusal tests for — and the layers pane then drew the
///         layer as empty instead of saying the file was missing. A test that only read the returned
///         sentence would not have seen that half.
///     </para>
///     <para>
///         ⚠ <b>No device, and none of this needs one.</b> <c>PaintSurface.Open</c> resolves a path
///         and reads a file; the pane that draws what it resolved is the device's half and is
///         asserted in <c>LayerStackPanelDeviceTests</c>.
///     </para>
/// </remarks>
public class PaintSurfaceTests : IDisposable {
    readonly TexturingFixture fixture = new();

    /// <inheritdoc />
    public void Dispose() {
        GC.SuppressFinalize(this);
        fixture.Dispose();
    }

    /// <summary>A layer naming a canvas that is not beside the stack is refused, and nothing is invented.</summary>
    [Fact]
    public void A_layer_that_names_a_missing_canvas_is_refused_rather_than_given_a_fresh_one() {
        var document = Stack("Hull", "Hull-rust.vxpaint");

        PaintCanvasStore store = new();

        Assert.Null(PaintSurface.Open(document, "rust", store, out var refusal));
        Assert.Contains("Hull-rust.vxpaint", refusal, StringComparison.Ordinal);
        Assert.Contains("no such file", refusal, StringComparison.Ordinal);

        // ⚠ The half the sentence cannot show: the store is still empty, so the layers pane's own
        // resolver reaches its refusal instead of finding an invented canvas and drawing nothing.
        Assert.Equal(0, store.Count);
        Assert.Null(store.Open(Path.Combine(fixture.Paths.Assets, "Hull-rust.vxpaint")));
    }

    /// <summary>⚠ And a layer that names nothing still gets one, because that is a first stroke.</summary>
    /// <remarks>
    ///     <b>The half a refusal must not swallow.</b> Every paint layer starts naming no file —
    ///     <c>LayerPaint.NameFor</c> derives the name and only the first stroke writes it down — so a
    ///     refusal that fired on "no canvas here" rather than on "no canvas here and the layer says
    ///     there is one" would refuse every layer an artist had just added.
    /// </remarks>
    [Fact]
    public void A_layer_that_names_nothing_is_given_a_canvas_to_paint_into() {
        var document = Stack("Hull", "");

        PaintCanvasStore store = new();

        var surface = PaintSurface.Open(document, "rust", store, out var refusal);

        Assert.NotNull(surface);
        Assert.Equal("", refusal);
        Assert.True(surface.NeedsNaming);

        // Adopted rather than merely returned, which is what makes the second `Open` of one drag —
        // the refresh at pointer-up — find the canvas the stroke went into.
        Assert.Equal(1, store.Count);
        Assert.Same(surface.Canvas, store.Open(surface.Absolute));
        Assert.Equal(0, store.Reads);
    }

    /// <summary>
    ///     ⚠ The brush paints into the set the panel chose, and no longer into <c>Sets[0]</c> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fixture is the mis-aim the issue describes rather than a convenient one.</b>
    ///         Both sets carry a paint layer with the id <c>rust</c> — which is what a duplicated
    ///         material slot really looks like — so aiming by <c>PaintTool.LayerId</c> alone finds a
    ///         layer in either, and the wrong one is a plausible answer rather than a refusal. What
    ///         separates them is the <c>.vxpaint</c> each names, so the assertion is about which file
    ///         a stroke would reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because the fallback is what makes this change nothing for a
    ///         single-set stack.</b> An empty <c>PaintSet</c> has to keep meaning the first set;
    ///         a test that only set the name could not tell a working resolver from one that had
    ///         stopped falling back.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_stroke_lands_in_the_set_the_document_chose_and_an_unchosen_one_is_still_the_first() {
        var document = TwoSets();

        PaintCanvasStore store = new();

        var first = PaintSurface.Open(document, "rust", store, out var refusal);

        Assert.NotNull(first);
        Assert.Equal("", refusal);
        Assert.Equal("Body", first.Set.Name);
        Assert.Equal("Hull.Body.rust.vxpaint", first.Relative);

        document.PaintSet = "Head";

        var chosen = PaintSurface.Open(document, "rust", store, out refusal);

        Assert.NotNull(chosen);
        Assert.Equal("", refusal);
        Assert.Equal("Head", chosen.Set.Name);
        Assert.Equal("Hull.Head.rust.vxpaint", chosen.Relative);

        // ⚠ And a name no set answers to falls back rather than refusing — a stack whose set was
        // renamed under an open panel has to go on being paintable.
        document.PaintSet = "Torso";

        Assert.Equal("Body", PaintSurface.Open(document, "rust", store, out _)?.Set.Name);
    }

    /// <summary>A stack with one paint layer whose canvas is named, or not.</summary>
    LayerStackDocument Stack(string name, string paint) {
        var document = new LayerStackDocument(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, name),
            fixture.Paths.Absolute("Assets/" + name + LayerStackDocument.Extension)
        );

        var stack = LayerStackDocument.Starter(name) with { BaseWidth = 32, BaseHeight = 32 };

        stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint, Paint = paint });
        document.Document = stack;

        return document;
    }

    /// <summary>Two sets carrying the same layer id and different canvases.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither layer names a file, so each resolves to <c>LayerPaint.NameFor</c>'s
    ///     default</b> — which puts the <em>set's</em> name in it. That is what makes the two
    ///     distinguishable without writing a byte to the disk, and it is the ordinary state of a
    ///     paint layer whose first stroke has not happened.
    /// </remarks>
    LayerStackDocument TwoSets() {
        var document = Stack("Hull", "");
        var stack = document.Document;

        // ⚠ Replaced rather than renamed: `TextureSetAsset.Name` is init-only, and `with` shares the
        // collection members with the value it copied — which is what keeps the layer added above.
        stack.Sets[0] = stack.Sets[0] with { Name = "Body" };

        stack.Sets.Add(
            new() {
                Name = "Head",
                Channels = LayerStackDocument.DefaultChannels(),
                Layers = [new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint }]
            }
        );

        return document;
    }
}
