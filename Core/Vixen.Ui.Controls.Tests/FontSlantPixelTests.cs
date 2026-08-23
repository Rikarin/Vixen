// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>font-style</c>, as the pixels the software rasteriser produced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The fixture is the argument, so read it before the assertions.</b> What
///         <c>font-style</c> decides is <i>which face of a family draws</i> — nothing else. So a
///         fixture that registers one face at both slants renders identical pixels either way and
///         would pass with the property unread, which is the shape of test this repository keeps
///         finding it wrote by mistake. Two <i>distinct</i> faces is the smallest thing that can tell
///         the two apart, and it is the same measurement <c>UtilityConsumptionProbe.Typeset</c> makes
///         for <c>font-weight</c> one axis over.
///     </para>
///     <para>
///         ⚠ <b>Two different fonts rather than an upright and its italic, because this repository has
///         no italic font in it.</b> That is also why the property was reported dead: the whole
///         matcher is finished — <c>FontRegistry.Slanted</c> implements CSS Fonts 4 § 5.2's italic →
///         oblique → upright search — and its last resort correctly and invisibly resolves
///         <c>italic</c> to the upright of a family that has no italic. Vixen does not synthesise a
///         slant either, and says so. So the honest question a test can ask here is not "do the
///         glyphs lean" but "did the slant reach the registry and pick the face registered under it",
///         and any two faces answer that.
///     </para>
///     <para>
///         ⚠ <b>The upright face does not cover the text and the italic one does, which is what makes
///         the direction assertable rather than only the difference.</b> "The two pictures differ" is
///         satisfied by a slant that picked the wrong face, or no face, or drew nothing at all. So
///         <see cref="Italic_draws_with_the_face_registered_under_the_slant" /> compares the italic
///         frame against a reference rendered with the italic face as the family's <i>only</i>
///         registration: equal there means the glyphs came from that face and from nowhere else.
///     </para>
/// </remarks>
public class FontSlantPixelTests {
    /// <summary>Latin, which <see cref="Lana" /> covers and <see cref="Kannada" /> does not.</summary>
    const string Text = "Agil";

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");
    static readonly FontFace Kannada = LoadFont("NotoSerifKannada-Regular.ttf", "kannada");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Controls.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"{resource} is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>The frame, as the set of lit pixels.</summary>
    /// <remarks>
    ///     A set rather than a hash, so that a failure can say how many pixels were involved and an
    ///     empty frame is distinguishable from a frame that merely differs.
    /// </remarks>
    static HashSet<(int X, int Y)> Render(
        string declarations,
        bool slantedIsLana,
        bool upright = true,
        string inner = ""
    ) {
        using var ui = UiTest.Create(240f, 120f);

        // ⚠ No `AddFallback`, and it is load-bearing. `FontRegistry.Chain` puts the fallbacks behind
        // the family's chosen face, and a Latin fallback would draw the text whichever slant was
        // picked — the picture would stop depending on the property and every assertion here would
        // hold against an engine that ignored it.
        if (upright) {
            ui.Document.Fonts.Register("Test", slantedIsLana ? Kannada : Lana);
        }

        ui.Document.Fonts.Register("Test", slantedIsLana ? Lana : Kannada, 400, FontStyle.Italic);

        ui.Load(
            $$"""
            root   { width: 240px; height: 120px; background-color: #000000; }
            .label { position: absolute; left: 20px; top: 20px;
                     font-family: Test; font-size: 48px; color: #ffffff; {{declarations}} }
            .text  { {{inner}} }
            """
        );

        // Two elements, because half of what is under test is the inheritance: `font-style` is in
        // `InheritedProperties`, and a `.vxml` interpolation emits its text as a child element, so
        // the class is written on the container essentially always.
        var label = ui.Create("div", null, "label", "label");
        ui.Create("span", label, "text", "text").Text = Text;
        ui.Frame();

        var image = ui.Capture();
        var lit = new HashSet<(int, int)>();

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                if (image.Pixels[image.Offset(x, y)] >= 24) {
                    lit.Add((x, y));
                }
            }
        }

        return lit;
    }

    /// <summary>The two faces differ about this text, which is what the rest of the file rests on.</summary>
    /// <remarks>
    ///     ⚠ The guard <c>TextDecorationPixelTests</c> keeps, pointed at the fixture rather than at
    ///     the font. If the Kannada face ever gained Latin coverage, or the Lana face lost it, every
    ///     assertion below would go on passing or start failing for a reason that has nothing to do
    ///     with <c>font-style</c>, and this is what would say so.
    /// </remarks>
    [Fact]
    public void The_two_faces_disagree_about_the_text_this_file_draws() {
        foreach (var letter in Text) {
            Assert.True(Lana.Supports(letter), $"the Lana face has no glyph for '{letter}'");
            Assert.False(Kannada.Supports(letter), $"the Kannada face has gained a glyph for '{letter}'");
        }
    }

    /// <summary><c>italic</c> draws with the face registered under the italic slant.</summary>
    /// <remarks>
    ///     Equal to the reference and not merely different from the upright, for the reason the type's
    ///     remarks give: a difference is satisfied by picking any wrong face, and equality with the
    ///     face-alone rendering is the claim that the right one was picked.
    /// </remarks>
    [Fact]
    public void Italic_draws_with_the_face_registered_under_the_slant() {
        var slanted = Render("font-style: italic", slantedIsLana: true);
        var reference = Render(string.Empty, slantedIsLana: true, upright: false);

        Assert.NotEmpty(slanted);
        Assert.Equal(reference, slanted);
    }

    /// <summary>Without the declaration the upright face draws, and it draws something else.</summary>
    /// <remarks>
    ///     ⚠ <b><c>NotEmpty</c> before <c>NotEqual</c>, and the order is the point.</b> The upright
    ///     face has no glyph for this text and shapes it to <c>.notdef</c>, which is a visible box —
    ///     so the frame is different <i>and</i> not blank. A test that only asserted a difference
    ///     would also pass for a slant that drew nothing, which is a failure and not a feature.
    /// </remarks>
    [Fact]
    public void The_upright_face_draws_when_no_slant_is_asked_for() {
        var upright = Render(string.Empty, slantedIsLana: true);
        var slanted = Render("font-style: italic", slantedIsLana: true);

        Assert.NotEmpty(upright);
        Assert.NotEqual(slanted, upright);
    }

    /// <summary><c>normal</c> on the text escapes an <c>italic</c> on its container.</summary>
    /// <remarks>
    ///     What <c>not-italic</c> is for. <c>font-style</c> is in <c>InheritedProperties</c>, so the
    ///     class is the only way a descendant of an italicised container gets its upright back — and
    ///     on a bare element it emits CSS's initial value and is correctly indistinguishable from
    ///     silence, which is how the consumption gate measures it.
    /// </remarks>
    [Fact]
    public void Normal_on_the_text_escapes_an_italic_container() {
        var italic = Render("font-style: italic", slantedIsLana: true);
        var escaped = Render("font-style: italic", slantedIsLana: true, inner: "font-style: normal");
        var upright = Render(string.Empty, slantedIsLana: true);

        Assert.NotEqual(italic, escaped);
        Assert.Equal(upright, escaped);
    }

    /// <summary><c>oblique</c> takes the italic face, which is CSS Fonts 4 § 5.2's second choice.</summary>
    /// <remarks>
    ///     Not a utility — Tailwind has no <c>oblique</c> class — but it is a value of the property
    ///     the engine reads, and the fallback order is the part of <c>FontRegistry.Slanted</c> that a
    ///     family registration cannot reach. Asserted here so that the order is pinned by something
    ///     other than its own comment.
    /// </remarks>
    [Fact]
    public void Oblique_falls_back_to_the_italic_face() =>
        Assert.Equal(
            Render("font-style: italic", slantedIsLana: true),
            Render("font-style: oblique", slantedIsLana: true)
        );
}
