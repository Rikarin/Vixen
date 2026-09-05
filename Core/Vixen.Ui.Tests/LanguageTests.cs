// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What language an element's words are in, and how it reaches the shaper.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first blocker under <c>hyphens: auto</c>, and it is not the dictionary.</b>
///         #546 names a Liang pattern set as its dependency; #600's finding is that the pattern set
///         is the <i>second</i> blocker and nothing in <c>Vixen.Ui</c> knew what language anything
///         was written in. <c>TextItemizer</c> cuts a paragraph by <b>script</b>, and script does
///         not determine language: English, German and French are one script with three pattern
///         sets whose hyphenations disagree, and three different sets of <c>locl</c> substitutions
///         besides.
///     </para>
///     <para>
///         ⚠ <b>And the process locale is the wrong answer rather than a crude one.</b>
///         <c>TextShaper</c> leaves HarfBuzz's language unset on purpose, so that the same document
///         lays out the same way on every machine; a default taken from
///         <c>CultureInfo.CurrentCulture</c> would wrap a paragraph one way on a German developer's
///         laptop and another on CI, and would surface as a golden image red on one machine only.
///         <see cref="An_undeclared_language_is_undetermined_rather_than_a_default" /> is as much of
///         that as an assertion here can be; the rest is a guard in <c>TextShaper.ShapeRun</c>.
///     </para>
/// </remarks>
public class LanguageTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    static UiDocument Documented() {
        var document = new UiDocument(600f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            """
            root  { width: 600px; height: 300px; align-items: flex-start; }
            panel { width: 300px; flex-shrink: 0; }
            label { font-family: Test; font-size: 16px; width: 300px; flex-shrink: 0; }
            """
        );

        return document;
    }

    /// <summary>A declared language inherits down the tree, and the document is the floor.</summary>
    /// <remarks>
    ///     ⚠ <b>By tree rather than by cascade</b>, which is how <c>lang</c> inherits in HTML and
    ///     the only way it can inherit here: it is not a style property, so the cascade has nothing
    ///     to carry.
    /// </remarks>
    [Fact]
    public void A_language_inherits_down_the_tree_and_the_document_is_the_floor() {
        using var document = Documented();

        var panel = document.Root.Add("panel");
        var label = panel.Add("label");

        Assert.Null(panel.Language);
        Assert.Equal(string.Empty, label.ResolvedLanguage);

        panel.Language = "de";

        Assert.Equal("de", panel.ResolvedLanguage);
        Assert.Equal("de", label.ResolvedLanguage);

        // The element's own beats the ancestor's, which is the whole point of inheriting.
        label.Language = "tr";
        Assert.Equal("tr", label.ResolvedLanguage);

        // ⚠ And taking it off falls back rather than sticking: the attribute cannot be removed from
        // the style tree, so an empty tag has to read as "declares none".
        label.Language = null;
        Assert.Null(label.Language);
        Assert.Equal("de", label.ResolvedLanguage);

        document.Language = "cs";
        Assert.Equal("de", label.ResolvedLanguage);

        panel.Language = null;
        Assert.Equal("cs", label.ResolvedLanguage);
    }

    /// <summary>Two subtrees under one document shape in two different languages.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Stated as work rather than as a picture, because in this font and this script
    ///         the picture is the same one.</b> What has to be true is that the two paragraphs are
    ///         shaped <i>separately</i> — the language is part of the shaping request and part of
    ///         the cache key — and the shaping cache's miss counter is the deterministic instrument
    ///         for that. A language that never reached the shaper would make the second paragraph a
    ///         cache hit on the first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The control is the half that makes it an assertion.</b> Two labels with the
    ///         <i>same</i> language and the same text must shape once between them, or the miss
    ///         above says nothing about the language and everything about there being two labels.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_subtrees_under_one_document_shape_in_two_languages() {
        using var document = Documented();
        const string Words = "Fugit inexorabile tempus";

        var german = document.Root.Add("panel");
        var turkish = document.Root.Add("panel");

        german.Language = "de";
        turkish.Language = "tr";

        var first = german.Add("label");
        first.Text = Words;

        document.Update();
        _ = first.Block(300f);

        var afterFirst = document.Shaping.Misses;

        var second = turkish.Add("label");
        second.Text = Words;

        document.Update();
        _ = second.Block(300f);

        Assert.Equal("de", first.ResolvedLanguage);
        Assert.Equal("tr", second.ResolvedLanguage);

        Assert.True(
            document.Shaping.Misses > afterFirst,
            "the same words under two languages were served from one shaping, so the language never "
            + "reached the shaper or is missing from the cache key"
        );

        // The control: a third label in the German subtree shapes nothing new.
        var alsoGerman = german.Add("label");
        alsoGerman.Text = Words;

        var afterSecond = document.Shaping.Misses;

        document.Update();
        _ = alsoGerman.Block(300f);

        Assert.Equal(afterSecond, document.Shaping.Misses);
    }

    /// <summary>Undetermined is a state of its own, and it is what a document starts in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of "nothing reads the process locale" that can be made red here.</b>
    ///         Undeclared is not a synonym for some default: the same words in an undeclared subtree
    ///         and in a German one shape separately, which is what says the empty tag travels to the
    ///         shaper as "leave HarfBuzz's language unset" rather than being filled in on the way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The culture-swap half is deliberately not written, because it could not fail.</b>
    ///         This assembly's tests run in globalization-invariant mode —
    ///         <c>CultureInfo.GetCultureInfo("tr-TR")</c> throws here — so
    ///         <c>CultureInfo.CurrentCulture.Name</c> is the empty string whatever the machine is,
    ///         and a default seeded from it would be indistinguishable from the correct answer. A
    ///         predicate that cannot be false is worse than the gap it papers over, so what is
    ///         asserted is what is observable, and the guarantee itself lives where it is enforced:
    ///         <c>TextShaper.ShapeRun</c> assigns the buffer's language only inside a
    ///         <c>string.IsNullOrEmpty</c> guard, and nothing in <c>Vixen.Ui.Text</c> mentions
    ///         <c>CultureInfo</c> at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_undeclared_language_is_undetermined_rather_than_a_default() {
        using var document = Documented();
        const string Words = "Fugit inexorabile tempus";

        Assert.Equal(string.Empty, document.Language);

        var undeclared = document.Root.Add("label");
        undeclared.Text = Words;

        Assert.Equal(string.Empty, undeclared.ResolvedLanguage);

        document.Update();
        _ = undeclared.Block(300f);

        var afterUndeclared = document.Shaping.Misses;

        var panel = document.Root.Add("panel");
        panel.Language = "de";

        var declared = panel.Add("label");
        declared.Text = Words;

        document.Update();
        _ = declared.Block(300f);

        Assert.True(
            document.Shaping.Misses > afterUndeclared,
            "declaring a language shaped nothing new, so an undeclared subtree and a German one are "
            + "being served the same shaping — which means the tag never reached the shaper"
        );
    }

    /// <summary>A stylesheet reads the language, which is what <c>:lang()</c> is defined as.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no <c>lang</c> property in CSS and there is not meant to be.</b> A language
    ///     is a fact about the document that a stylesheet selects on; a property would let a theme
    ///     assert what language somebody's words are in, which is the one thing font fallback,
    ///     locale-aware casing and hyphenation must not let it do. CSS Selectors 4 defines
    ///     <c>:lang(de)</c> as the BCP-47 prefix match <c>[lang|="de"]</c> — so <c>de-AT</c> matches
    ///     and <c>den</c> does not — and this engine already has that operator.
    /// </remarks>
    [Fact]
    public void A_stylesheet_selects_on_the_language_by_prefix() {
        using var document = Documented();
        document.Load("""[lang|="de"] { width: 111px; }""");

        var austrian = document.Root.Add("panel");
        var danish = document.Root.Add("panel");

        austrian.Language = "de-AT";
        danish.Language = "den";

        document.Update();

        Assert.Equal(111f, austrian.Width, 0.001f);
        Assert.Equal(300f, danish.Width, 0.001f);
    }

    static List<GlyphPlacement> Placements(TextLayout? block) {
        Assert.NotNull(block);

        var placements = new List<GlyphPlacement>();

        foreach (var line in block.Lines) {
            foreach (var run in line.Runs) {
                placements.AddRange(run.Shaped.Placements());
            }
        }

        Assert.NotEmpty(placements);

        return placements;
    }
}
