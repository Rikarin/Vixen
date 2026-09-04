// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary><c>text-transform</c>'s character mapping and the index map that comes with it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The premise this file exists to hold up was measured, not assumed, and half of it was
///         wrong.</b> doc 43 and issue #237 say a case mapping changes the UTF-16 length. That is
///         true of Unicode's <i>full</i> mappings, which is what CSS Text 3 § 2.1 specifies — and
///         false of every case API .NET has: <c>string.ToUpperInvariant</c>, <c>ToUpper</c> in every
///         culture, and <c>Rune.ToUpperInvariant</c> over all 1 112 064 scalars are the <i>simple</i>
///         mappings, one code point to one, and not one of them moves an index. So an implementation
///         written on .NET's casing alone would have been caret-safe and would have drawn
///         <c>STRAßE</c> — a different defect, and a visible one rather than a silent one.
///     </para>
///     <para>
///         <see cref="Straße_uppercases_to_seven_characters_where_six_were_written" /> is the
///         assertion that keeps that from being re-decided by accident: if the expansions ever stop
///         arriving, the map has nothing to map and this file goes red rather than the caret going
///         quietly wrong.
///     </para>
/// </remarks>
public class TransformedTextTests {
    [Fact]
    public void No_transform_returns_the_same_string_instance() {
        var source = "Hello";
        var transformed = TransformedText.Of(source, TextTransform.None);

        // Reference equality, and it is load-bearing rather than tidy: `UiElement.Block` keys its
        // cache on the identity of the string it shaped, and the shaping cache hashes the contents.
        Assert.Same(source, transformed.Text);
        Assert.True(transformed.IsIdentity);
    }

    [Fact]
    public void A_transform_that_moves_no_index_needs_no_map() {
        var transformed = TransformedText.Of("hello", TextTransform.Uppercase);

        Assert.Equal("HELLO", transformed.Text);
        Assert.True(transformed.IsIdentity);
    }

    /// <summary>
    ///     ⚠ <b>The whole reason this type exists.</b> Six characters in, seven out — so every index
    ///     after the <c>ß</c> means a different character in the two strings.
    /// </summary>
    [Fact]
    public void Straße_uppercases_to_seven_characters_where_six_were_written() {
        var transformed = TransformedText.Of("straße", TextTransform.Uppercase);

        Assert.Equal("STRASSE", transformed.Text);
        Assert.Equal(6, transformed.Source.Length);
        Assert.Equal(7, transformed.Text.Length);
        Assert.False(transformed.IsIdentity);
    }

    /// <summary>
    ///     ⚠ <b>And .NET on its own does not.</b> Stated as a test rather than as a comment because
    ///     it is the claim the whole design rests on, and because a framework that started doing full
    ///     casing would make <see cref="SpecialCasingTable" /> a second opinion rather than the only
    ///     one — which would show up here first.
    /// </summary>
    [Fact]
    public void The_frameworks_own_uppercase_would_not_have_expanded_it() {
        Assert.Equal("STRAßE", "straße".ToUpperInvariant());
        Assert.Equal(6, "straße".ToUpperInvariant().Length);
    }

    [Fact]
    public void The_ligature_expands_too() {
        Assert.Equal("FINE", TransformedText.Of("ﬁne", TextTransform.Uppercase).Text);
    }

    [Fact]
    public void Every_source_index_survives_the_round_trip_through_an_expansion() {
        var transformed = TransformedText.Of("straße", TextTransform.Uppercase);

        for (var i = 0; i <= transformed.Source.Length; i++) {
            Assert.Equal(i, transformed.ToSource(transformed.ToDrawn(i)));
        }
    }

    /// <summary>
    ///     ⚠ <b>The other direction is not a round trip, and that is the behaviour a field wants.</b>
    ///     The second <c>S</c> of the expanded <c>ß</c> is not a place a caret can be: the author
    ///     typed one character there.
    /// </summary>
    [Fact]
    public void An_index_inside_an_expansion_snaps_to_the_character_it_came_from() {
        var transformed = TransformedText.Of("straße", TextTransform.Uppercase);

        // s t r a ß e  ->  S T R A S S E
        Assert.Equal(4, transformed.ToDrawn(4));
        Assert.Equal(4, transformed.ToSource(4));
        Assert.Equal(4, transformed.ToSource(5));
        Assert.Equal(5, transformed.ToSource(6));
        Assert.Equal(6, transformed.ToSource(7));
    }

    [Fact]
    public void The_map_is_monotonic_and_covers_both_strings_completely() {
        var transformed = TransformedText.Of("aßbﬁc", TextTransform.Uppercase);

        Assert.Equal("ASSBFIC", transformed.Text);
        Assert.Equal(0, transformed.ToDrawn(0));
        Assert.Equal(transformed.Text.Length, transformed.ToDrawn(transformed.Source.Length));
        Assert.Equal(transformed.Source.Length, transformed.ToSource(transformed.Text.Length));

        var previous = -1;

        for (var i = 0; i <= transformed.Source.Length; i++) {
            var drawn = transformed.ToDrawn(i);
            Assert.True(drawn > previous, "the drawn index never goes backwards");
            previous = drawn;
        }
    }

    [Fact]
    public void Lowercase_uses_the_full_mapping_as_well() {
        // U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE lowercases to `i` plus a combining dot, which
        // is the one unconditional row in the file's lowercase column.
        var transformed = TransformedText.Of("İ", TextTransform.Lowercase);

        Assert.Equal("i̇", transformed.Text);
        Assert.False(transformed.IsIdentity);
    }

    [Fact]
    public void Capitalize_titlecases_the_first_letter_of_every_word() {
        Assert.Equal("Ag Jq Wm Il", TransformedText.Of("ag jq wm il", TextTransform.Capitalize).Text);
    }

    /// <summary>
    ///     ⚠ <b>The rest of the word is untouched</b>, which is CSS's rule and surprises people:
    ///     <c>capitalize</c> says nothing about the characters after the first.
    /// </summary>
    [Fact]
    public void Capitalize_leaves_the_rest_of_a_word_alone() {
        Assert.Equal("IPhone", TransformedText.Of("iPhone", TextTransform.Capitalize).Text);
    }

    /// <summary>
    ///     ⚠ <b>The first <i>letter</i>, not the first character</b> — which is the whole reason
    ///     UAX#29 is consulted rather than "the character after a space".
    /// </summary>
    [Fact]
    public void Capitalize_skips_punctuation_to_reach_the_letter() {
        Assert.Equal("“Hello”", TransformedText.Of("“hello”", TextTransform.Capitalize).Text);
    }

    /// <summary>
    ///     ⚠ <b>Titlecase is a third case and not a synonym for uppercase.</b> If the titlecase
    ///     column were dropped and uppercase used for both, this comes out <c>Ǆ</c>.
    /// </summary>
    [Theory]
    [InlineData("ǆ", "ǅ", "Ǆ")]
    [InlineData("ǉ", "ǈ", "Ǉ")]
    [InlineData("ǌ", "ǋ", "Ǌ")]
    [InlineData("ǳ", "ǲ", "Ǳ")]
    public void Capitalize_uses_titlecase_rather_than_uppercase_on_a_digraph(
        string source,
        string title,
        string upper
    ) {
        Assert.Equal(title, TransformedText.Of(source, TextTransform.Capitalize).Text);
        Assert.Equal(upper, TransformedText.Of(source, TextTransform.Uppercase).Text);
    }

    /// <summary>
    ///     ⚠ <b>And a Greek iota-subscript letter, whose titlecase both differs from its uppercase
    ///     <i>and</i> expands.</b> Together with the digraphs above this is the pair of reasons the
    ///     title column is not the upper column: one is in the generated table and one is not.
    /// </summary>
    [Fact]
    public void A_titlecase_that_expands_comes_from_the_generated_table() {
        Assert.Equal("ᾈ", TransformedText.Of("ᾀ", TextTransform.Capitalize).Text);
        Assert.Equal("ἈΙ", TransformedText.Of("ᾀ", TextTransform.Uppercase).Text);
    }

    [Fact]
    public void An_astral_character_keeps_its_pair_together() {
        // Deseret, whose case mappings are astral at both ends — so the surrogate pair survives the
        // walk and both of its code units map to the same index.
        var transformed = TransformedText.Of("\U00010428", TextTransform.Uppercase);

        Assert.Equal("\U00010400", transformed.Text);
        Assert.True(transformed.IsIdentity);
    }

    /// <summary>
    ///     A field mid-keystroke holds one, and replacing it would change what the field reads back.
    /// </summary>
    [Fact]
    public void A_lone_surrogate_is_copied_through() {
        var transformed = TransformedText.Of("a\uD801b", TextTransform.Uppercase);

        Assert.Equal("A\uD801B", transformed.Text);
        Assert.True(transformed.IsIdentity);
    }

    [Fact]
    public void An_empty_string_is_the_identity() {
        Assert.True(TransformedText.Of(string.Empty, TextTransform.Uppercase).IsIdentity);
        Assert.True(TransformedText.Of(null, TextTransform.Uppercase).IsIdentity);
    }

    [Fact]
    public void Indices_outside_either_string_clamp_rather_than_throw() {
        var transformed = TransformedText.Of("straße", TextTransform.Uppercase);

        Assert.Equal(0, transformed.ToDrawn(-5));
        Assert.Equal(0, transformed.ToSource(-5));
        Assert.Equal(transformed.Text.Length, transformed.ToDrawn(99));
        Assert.Equal(transformed.Source.Length, transformed.ToSource(99));
    }
}
