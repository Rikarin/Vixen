// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     That a real document's line boxes get a real strut — the half of CSS 2.1 §10.8 that lives on
///     this side of the layout bridge.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The arithmetic is <c>InlineStrutTests</c>'s in the layout store; what is under test
///         here is that anything <i>writes</i> the numbers.</b> This repository's commonest defect is
///         a finished thing nothing calls, and a strut is exactly that shape: the store would go on
///         laying every line out as tall as the boxes on it, silently and correctly, for ever — every
///         layout test green — if <c>UiDocument.ApplyStyle</c> never called
///         <c>LayoutTree.SetStrut</c>. So each test below reads a height out of a document built from
///         CSS, and the control is the same document with no font registered.
///     </para>
///     <para>
///         The declared <c>line-height</c> is what makes these numbers font-independent: §10.8.1 puts
///         half the leading on each side of the content area, so the strut's box is the declared line
///         height whatever the face's ascent and descent are. Only <c>middle</c> below depends on the
///         face, and it is asserted as an inequality against the baseline answer for that reason.
///     </para>
/// </remarks>
public class InlineStrutWiringTests {
    const float Tolerance = 0.001f;

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");

    /// <summary>A 30-point line box holding one 10-point inline-block, from a stylesheet.</summary>
    /// <remarks>
    ///     Without a strut the line is the box's own 10 points and the box sits at the top of it. With
    ///     one it is the declared 30, and the box hangs from a baseline the font put somewhere inside
    ///     it — which is why only the height is asserted exactly here.
    /// </remarks>
    [Fact]
    public void A_registered_font_gives_the_container_a_strut() {
        using var withFont = Documented(registerFont: true);
        var lined = Box(withFont);

        Assert.Equal(30f, lined.Parent!.Height, Tolerance);
        Assert.True(lined.Top > 0f, $"the box should hang from the strut's baseline, and sits at {lined.Top}");

        using var without = Documented(registerFont: false);
        var unlined = Box(without);

        // ⚠ The control, and it is the assertion that would catch this feature being deleted: with no
        // face to resolve, `StrutOf` writes nothing and the line is the box's own height again.
        Assert.Equal(10f, unlined.Parent!.Height, Tolerance);
        Assert.Equal(0f, unlined.Top, Tolerance);
    }

    /// <summary><c>vertical-align: middle</c> reaches the layout store from a stylesheet.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the value that was dropped at the bridge for the whole life of the
    ///     property</b>, so the assertion that matters is that it now <i>differs</i> from
    ///     <c>baseline</c> — which is what a silent fallback looked like and what
    ///     <c>A_font_relative_vertical_align_falls_back_to_the_baseline_with_no_strut</c> still pins
    ///     for a document with no font. The exact offset is half the face's x-height and belongs to
    ///     the closed-form tests in the layout store.
    /// </remarks>
    [Fact]
    public void Vertical_align_middle_reaches_the_store_and_moves_the_box() {
        using var middle = Documented(registerFont: true, extra: "vertical-align: middle;");
        using var baseline = Documented(registerFont: true);

        Assert.NotEqual(Box(baseline).Top, Box(middle).Top);
    }

    /// <summary>⚠ A face registered <i>after</i> the first layout still reaches the strut.</summary>
    /// <remarks>
    ///     The trap <c>UiElement.AppliedFontRevision</c> exists for. Registering a face changes
    ///     nothing about an element's computed style, its font size or its line height, so a strut
    ///     written under the old three-part test would be resolved once against a registry with
    ///     nothing in it and never again — an interface built before its font is installed would keep
    ///     lines as tall as their boxes for the life of the document. <c>Refont</c> repairs the same
    ///     fault for the measure function and cannot repair this one: it dirties nodes that measure
    ///     text, and a strut is written by the style pass rather than measured by a leaf.
    /// </remarks>
    [Fact]
    public void A_font_registered_after_the_first_layout_reaches_the_strut() {
        using var document = Documented(registerFont: false);

        Assert.Equal(10f, Box(document).Parent!.Height, Tolerance);

        document.Fonts.Register("Test", Lana);
        document.Update();

        Assert.Equal(30f, Box(document).Parent!.Height, Tolerance);
    }

    static UiDocument Documented(bool registerFont, string extra = "") {
        var document = new UiDocument(400f, 200f);

        if (registerFont) {
            document.Fonts.Register("Test", Lana);
        }

        document.Load(
            $$"""
              root { width: 400px; height: 200px; align-items: flex-start; }
              panel { display: block; width: 200px; font-family: Test; font-size: 20px; line-height: 30px; }
              chip { display: inline-block; width: 40px; height: 10px; {{extra}} }
              """
        );

        var panel = document.Root.Add("panel");
        panel.Add("chip");
        document.Update();

        return document;
    }

    static UiElement Box(UiDocument document) => document.Root.Children[0].Children[0];

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }
}
