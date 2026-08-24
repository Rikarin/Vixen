// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     Which of <c>font-variant-numeric</c>'s nine keywords can be <i>seen</i> with the only face in
///     this repository that implements any of them, measured one keyword at a time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because the consumption gate's verdict is a union and cannot answer
///         the question it looks like it answers.</b> The gate says "some value of
///         <c>font-variant-numeric</c> moved a channel", which one live keyword satisfies for all
///         nine — the <c>visibility: collapse</c> shape that <c>docs/plan/43</c> Part 10 opens by
///         warning about. <c>UtilityFamilySupportTests</c> pins that each class resolves, and
///         <c>Vixen.Ui.Tests.FontFeatureStyleTests</c> pins which OpenType tag each keyword becomes.
///         Neither of those can fail for a keyword whose tag is right and whose picture never changes.
///     </para>
///     <para>
///         ⚠ <b>Every row below is a measurement and three of them contradicted the obvious guess,
///         which is the reason to write the table down rather than reason about it.</b> The guess was
///         that <c>tabular-nums</c> and <c>slashed-zero</c> move and that <c>proportional-nums</c> and
///         <c>lining-nums</c> are the defaults. It is the other way round in Open Sans: its figures
///         are already lining <i>and</i> tabular, so <c>tnum</c> and <c>lnum</c> are what it already
///         draws and <c>pnum</c> and <c>onum</c> are the ones that substitute. And <c>ordn</c> does
///         nothing at all to <c>"1st"</c> — Open Sans implements the Spanish and Italian ordinal
///         indicators, so it fires on <c>"1o 2a"</c> and on nothing an English speaker would try.
///     </para>
///     <para>
///         ⚠ <b>None of these is the <c>text-balance</c> shape, and the distinction is the whole
///         point of the file.</b> <c>text-balance</c> is unregistered because <c>LineWrapper</c> is
///         greedy first-fit by an argued decision, so no font, no text and no future short of a new
///         algorithm can make it differ from the default — the <i>engine</i> cannot express the
///         difference. Every keyword here resolves to a real OpenType tag that HarfBuzz applies; the
///         three that show nothing show nothing <i>because of the font</i>, exactly as
///         <c>font-style: italic</c> correctly resolves to an upright for a family with no italic.
///         That is a fact about this repository's fonts, and the rows are written so that it fails
///         and says so when the fonts change.
///     </para>
/// </remarks>
public class NumericFigureVisibilityTests {
    /// <summary>Open Sans: <c>tnum</c>, <c>pnum</c>, <c>onum</c>, <c>lnum</c>, <c>zero</c>, <c>ordn</c>, <c>frac</c>.</summary>
    static readonly FontFace Figures = UtilityConsumptionProbe.FiguredFace;

    static ushort[] Glyphs(string text, string? tag, uint value = 1u) {
        var features = tag is null
            ? FontFeatureSet.None
            : FontFeatureSet.Of([new FontFeature(FontFeature.Pack(tag), value)]);

        return TextShaper.Shape(Figures, text, features: features).Placements().Select(p => p.GlyphId).ToArray();
    }

    /// <summary>The guard the whole file rests on: the face really does implement features.</summary>
    /// <remarks>
    ///     ⚠ Without this, every "no change" row would pass against a font with no feature table at
    ///     all — which is the state all twenty-two Consortium faces are in, and is why this one had to
    ///     be linked in. A <c>Fact</c> rather than an assertion inside the theories, so its failure
    ///     says what is wrong instead of failing eight rows obscurely.
    /// </remarks>
    [Fact]
    public void The_face_implements_numeric_features() {
        Assert.NotEqual(Glyphs("0123456789", null), Glyphs("0123456789", "onum"));
        Assert.NotEqual(Glyphs("0123456789", null), Glyphs("0123456789", "pnum"));
    }

    /// <summary>The five keywords whose feature changes the glyphs of the text it applies to.</summary>
    /// <remarks>
    ///     ⚠ Each row carries the string the feature is <i>for</i>, and two of them are not the string
    ///     anybody would guess. <c>frac</c> needs a solidus; <c>ordn</c> needs a Spanish ordinal
    ///     letter, because that is what Open Sans implements. Both are correctly invisible on the bare
    ///     run of digits the probe's <c>figured</c> scene holds — so the gate sees neither of them
    ///     even though both work, which is exactly the coverage hole this file is here to fill.
    /// </remarks>
    [Theory]
    [InlineData("proportional-nums", "pnum", "0123456789")]
    [InlineData("oldstyle-nums", "onum", "0123456789")]
    [InlineData("slashed-zero", "zero", "0123456789")]
    [InlineData("ordinal", "ordn", "1o 2a")]
    [InlineData("diagonal-fractions", "frac", "1/2")]
    public void The_keyword_changes_the_glyphs(string keyword, string tag, string text) =>
        Assert.True(
            !Glyphs(text, null).SequenceEqual(Glyphs(text, tag)),
            $"`{keyword}` asks for `{tag}`, and `{text}` shapes identically with it and without — "
            + "either the face stopped implementing the feature or the array stopped reaching HarfBuzz"
        );

    /// <summary>
    ///     ⚠ And the three that cannot be seen here, each with the measured reason — recorded rather
    ///     than skipped, because a skipped keyword and a broken one look the same.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>tabular-nums</c> and <c>lining-nums</c> name what Open Sans <i>already draws</i>:
    ///         its default figures are lining and tabular, so both features are applied and produce
    ///         the glyphs that were already there. That is CSS working rather than failing — both
    ///         keywords exist to override an inherited <c>oldstyle-nums</c> or
    ///         <c>proportional-nums</c>, which is the arrangement
    ///         <see cref="Switching_a_feature_off_restores_the_default" /> measures instead.
    ///     </para>
    ///     <para>
    ///         <c>stacked-fractions</c> asks for <c>afrc</c>, which no face in this repository has, so
    ///         HarfBuzz correctly ignores a tag the font has never heard of — the same shape as
    ///         <c>FontRegistry.Slanted</c> resolving <c>italic</c> to an upright for a family with no
    ///         italic. Registered anyway, and for that reason.
    ///     </para>
    ///     <para>
    ///         The ninth keyword, <c>normal-nums</c>, asks for no features at all: it is the
    ///         property's initial value and has nothing to measure here.
    ///         <c>Vixen.Ui.Tests.FontFeatureStyleTests.Normal_asks_for_no_features</c> pins that it
    ///         resolves to the empty set, and it is not a no-op — the property inherits, so it is how
    ///         a descendant escapes a <c>tabular-nums</c> on its container.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("tabular-nums", "tnum", "0123456789")]
    [InlineData("lining-nums", "lnum", "0123456789")]
    [InlineData("stacked-fractions", "afrc", "1/2")]
    public void The_keyword_is_invisible_in_this_repositorys_fonts(string keyword, string tag, string text) =>
        Assert.True(
            Glyphs(text, null).SequenceEqual(Glyphs(text, tag)),
            $"`{keyword}` asks for `{tag}`, and `{text}` now shapes DIFFERENTLY with it — which is "
            + "good news and expires this row. A face that can show the keyword has been added, so "
            + "move it up to `The_keyword_changes_the_glyphs` and give the probe's `figured` scene "
            + "text that triggers it"
        );

    /// <summary>
    ///     ⚠ <b>A feature's <i>value</i> reaches HarfBuzz as well as its tag, and nothing else in the
    ///     numeric path asserts that.</b>
    /// </summary>
    /// <remarks>
    ///     Every row above sets a feature to one. A <c>Feature</c> whose value was dropped — or built
    ///     from the tag alone — would satisfy all of them and would silently ignore
    ///     <c>font-feature-settings: "onum" 0</c>, which is CSS Fonts 4's own way of undoing a
    ///     high-level keyword and is what <c>FontFeatureStyleTests</c> asserts the cascade produces.
    ///     Asked as "switch it off and the glyphs come back", which is only true if the zero arrived.
    /// </remarks>
    [Theory]
    [InlineData("onum", "0123456789")]
    [InlineData("pnum", "0123456789")]
    [InlineData("zero", "0123456789")]
    [InlineData("frac", "1/2")]
    public void Switching_a_feature_off_restores_the_default(string tag, string text) {
        var plain = Glyphs(text, null);

        Assert.NotEqual(plain, Glyphs(text, tag));
        Assert.Equal(plain, Glyphs(text, tag, 0u));
    }
}
