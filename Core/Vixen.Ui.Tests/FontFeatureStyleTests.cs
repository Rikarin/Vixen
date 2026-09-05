// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>font-variant-numeric</c> and <c>font-feature-settings</c>, from the cascade to the glyphs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One row per keyword, and the completeness is the point rather than thoroughness for
///         its own sake.</b> The consumption gate's verdict is per <i>property</i> and unions over
///         every value a family can emit, so one live keyword makes all nine green — which is how
///         <c>visibility</c> came to ship a <c>collapse</c> that parsed as a <c>box-shadow</c> and
///         painted normally. Nine keywords, nine OpenType tags, and a table with one wrong entry
///         would look exactly like a table with none.
///     </para>
///     <para>
///         ⚠ <b>The mapping is asserted without a font on purpose, and it has to be.</b> A keyword
///         only <i>shows</i> in a face that implements its feature: Open Sans has seven of the eight
///         and no embedded face has <c>afrc</c>, and even in Open Sans <c>lining-nums</c> and
///         <c>proportional-nums</c> are what the face already does, so they are correctly invisible.
///         A per-keyword test written against glyphs could therefore only ever cover four of the
///         nine, and would report the other five as failures of the engine rather than of the font.
///         What <i>can</i> be wrong per keyword is which tag it resolves to, and that is what these
///         rows pin.
///     </para>
///     <para>
///         The end-to-end half — that a resolved tag actually reaches HarfBuzz and changes the
///         picture — is <see cref="A_feature_setting_reaches_the_shaper" /> below, and the shaping
///         cache's key is <c>Vixen.Ui.Text.Tests.FontFeatureTests</c>.
///     </para>
/// </remarks>
public class FontFeatureStyleTests {
    static readonly FontFace Contextual = LoadFont("TestGSUBOne.otf");

    static FontFace LoadFont(string file) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{file}")
            ?? throw new InvalidOperationException($"the test font '{file}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: file);
    }

    static UiElement Styled(UiDocument document, string declarations) {
        document.Load($"root {{ width: 400px; height: 300px; }} label {{ {declarations} }}");

        var element = document.Root.Add("label");
        element.Text = "0123456789";
        document.Update();

        return element;
    }

    static string[] Tags(UiElement element) =>
        element.FontFeatures.Features.Select(feature => FontFeature.Unpack(feature.Tag)).ToArray();

    [Fact]
    public void Text_that_asks_for_nothing_carries_the_shared_empty_set() {
        using var document = new UiDocument(400f, 300f);

        Assert.Same(FontFeatureSet.None, Styled(document, "color: #000000;").FontFeatures);
    }

    [Theory]
    [InlineData("ordinal", "ordn")]
    [InlineData("slashed-zero", "zero")]
    [InlineData("lining-nums", "lnum")]
    [InlineData("oldstyle-nums", "onum")]
    [InlineData("proportional-nums", "pnum")]
    [InlineData("tabular-nums", "tnum")]
    [InlineData("diagonal-fractions", "frac")]
    [InlineData("stacked-fractions", "afrc")]
    public void Each_numeric_keyword_resolves_to_its_opentype_feature(string keyword, string tag) {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, $"font-variant-numeric: {keyword};");

        Assert.Equal([tag], Tags(element));
        Assert.Equal(1u, Assert.Single(element.FontFeatures.Features).Value);
    }

    /// <summary><c>normal</c> is the initial value and asks for nothing, which is not a gap.</summary>
    [Fact]
    public void Normal_asks_for_no_features() {
        using var document = new UiDocument(400f, 300f);

        Assert.Same(FontFeatureSet.None, Styled(document, "font-variant-numeric: normal;").FontFeatures);
    }

    /// <summary>CSS's grammar takes a list, and so does the reader.</summary>
    /// <remarks>
    ///     ⚠ <b>The utility families reach this now, and the remark here used to say they could
    ///     not.</b> Each numeric class emitted the whole property, so
    ///     <c>class="tabular-nums slashed-zero"</c> kept the later declaration and the other class
    ///     silently did nothing; they compose through <c>--tw-*</c> fragments now, exactly as v4
    ///     does. What this pins is the half underneath that: the declaration itself takes a list.
    /// </remarks>
    [Fact]
    public void A_list_of_keywords_asks_for_all_of_them() {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, "font-variant-numeric: tabular-nums slashed-zero;");

        Assert.Equal(["tnum", "zero"], Tags(element));
    }

    /// <summary>⚠ And empty slots in the list are not keywords, which is what the composition emits.</summary>
    /// <remarks>
    ///     <b>The contract the utility layer's composition rests on, asserted where the reader
    ///     lives.</b> <c>UtilityComposition.NumericFigures</c> assembles five <c>var()</c>
    ///     references and an element carrying one class fills exactly one of them — the other four
    ///     resolve to the empty token stream, so what reaches this method is a keyword with runs of
    ///     spaces around it. A reader that treated the gaps as keywords would produce no tags at all
    ///     for every composed class, and a reader that refused the declaration would produce none
    ///     either; both failures look like the family never having been registered.
    /// </remarks>
    [Fact]
    public void Empty_slots_in_the_list_contribute_no_feature() {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, "font-variant-numeric:   tabular-nums    slashed-zero ;");

        Assert.Equal(["tnum", "zero"], Tags(element));

        // And a list that is nothing but slots asks for nothing rather than for a mistake.
        Assert.Empty(Tags(Styled(document, "font-variant-numeric:     ;")));
    }

    [Fact]
    public void A_feature_settings_list_parses_into_tags() {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, "font-feature-settings: \"liga\" 0, \"ss01\";");

        Assert.Equal(["liga", "ss01"], Tags(element));
        Assert.Equal([0u, 1u], element.FontFeatures.Features.Select(feature => feature.Value));
    }

    /// <summary>
    ///     ⚠ <b>The escape hatch wins over the friendly spelling, which is CSS Fonts 4 § 6.4's order
    ///     and not a choice.</b> <c>font-feature-settings</c> is defined as the low-level override, so
    ///     a hand-written <c>"tnum" 0</c> has to be able to switch off what <c>tabular-nums</c> asked
    ///     for. The other order would make the escape hatch the thing that gets escaped from.
    /// </summary>
    [Fact]
    public void A_hand_written_setting_overrides_the_keyword_that_asked_for_it() {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, "font-variant-numeric: tabular-nums; font-feature-settings: \"tnum\" 0;");

        Assert.Equal(["tnum"], Tags(element));
        Assert.Equal(0u, Assert.Single(element.FontFeatures.Features).Value);
    }

    /// <summary>
    ///     ⚠ <b>Both properties inherit, independently, and a child declaring one keeps the other.</b>
    ///     They share one slot on the element — <c>FontFeatures</c> — so an implementation that built
    ///     the set from the <i>parent's finished set</i> rather than from each element's own computed
    ///     style would let either property erase the other on the way down.
    /// </summary>
    [Fact]
    public void A_child_declaring_one_property_keeps_the_other_from_its_parent() {
        using var document = new UiDocument(400f, 300f);
        document.Load(
            """
            root    { width: 400px; height: 300px; }
            #outer  { font-variant-numeric: tabular-nums; }
            #inner  { font-feature-settings: "ss01"; }
            """
        );

        var outer = document.Root.Add("div", "outer");
        var inner = outer.Add("label", "inner");
        inner.Text = "0123456789";
        document.Update();

        Assert.Equal(["ss01", "tnum"], Tags(inner));
    }

    /// <summary>A malformed entry drops out and the ones beside it survive.</summary>
    [Fact]
    public void One_bad_tag_does_not_take_the_rest_of_the_list_with_it() {
        using var document = new UiDocument(400f, 300f);
        var element = Styled(document, "font-feature-settings: \"toolong\" 1, \"ss02\";");

        Assert.Equal(["ss02"], Tags(element));
    }

    /// <summary>
    ///     ⚠ <b>And the tags reach HarfBuzz, which is the half a computed-value assertion cannot
    ///     see.</b>
    /// </summary>
    /// <remarks>
    ///     <c>TextShaper.ShapeRun</c> ended <c>Shape(buffer, [])</c>, so every assertion above would
    ///     have held word for word against an engine that resolved the features perfectly and then
    ///     threw the array away. <c>calt</c> is on by default in this face and turns the first
    ///     <c>a</c> of <c>"a a"</c> into an alternate; switching it off makes the two <c>a</c>s the
    ///     same glyph, which no amount of correct cascading could do on its own.
    /// </remarks>
    [Fact]
    public void A_feature_setting_reaches_the_shaper() {
        using var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Alt", Contextual);
        document.Load("root { width: 400px; height: 300px; } label { font-family: Alt; }");

        var plain = document.Root.Add("label");
        plain.Text = "a a";

        document.Load("#off { font-family: Alt; font-feature-settings: \"calt\" 0; }");
        var suppressed = document.Root.Add("label", "off");
        suppressed.Text = "a a";

        document.Update();

        var untouched = Ids(plain);
        var switched = Ids(suppressed);

        Assert.NotEqual(untouched, switched);
        Assert.NotEqual(untouched[0], untouched[2]);
        Assert.Equal(switched[0], switched[2]);
    }

    static ushort[] Ids(UiElement element) {
        var line = element.Block()!.Lines[0];
        var placed = new List<PositionedGlyph>();
        line.Place(placed);

        return placed.Select(glyph => glyph.GlyphId).ToArray();
    }
}
