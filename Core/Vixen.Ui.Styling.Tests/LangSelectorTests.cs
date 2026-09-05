// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>:lang()</c>, which is not the attribute selector it is usually described as.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The claim these were written against — and it is half wrong.</b> #606 says
///         <c>:lang(de)</c> is "a spelling, not a mechanism", because CSS Selectors 4 defines it as
///         the <c>|=</c> prefix match <c>AttributeOperator.DashMatch</c> already implements. The
///         <i>comparison</i> is indeed the same. The <i>subject</i> is not: an attribute selector
///         asks what this element declares, and <c>:lang()</c> asks what language this element's
///         content is in — which is the nearest declaration at or above it.
///         <see cref="A_span_inside_a_German_paragraph_is_German_and_only_one_of_the_two_spellings_knows_it" />
///         is that difference, and it is why this landed as a kind rather than as a rewrite.
///     </para>
///     <para>
///         The other half of the claim holds and is recorded in
///         <see cref="The_Selectors_4_list_and_wildcard_forms_are_refused_by_the_parser_not_by_the_matcher" />:
///         the comma list and <c>*</c> wildcards are not free. They are also not ours — ExCSS 4.3.2
///         does not parse either, so they are refused a layer below with a diagnostic.
///     </para>
/// </remarks>
public class LangSelectorTests {
    /// <summary>Selectors 4 § 9.1's own worked pair, and the one everybody gets wrong.</summary>
    [Fact]
    public void A_region_subtag_matches_the_language_and_a_longer_word_does_not() {
        var fixture = new StyleFixture();
        var austrian = fixture.Tree.CreateElement("p");
        var dendi = fixture.Tree.CreateElement("p");
        var german = fixture.Tree.CreateElement("p");

        fixture.Tree.SetAttribute(austrian, "lang", "de-AT");
        fixture.Tree.SetAttribute(dendi, "lang", "den");
        fixture.Tree.SetAttribute(german, "lang", "de");

        Assert.True(fixture.Matches(":lang(de)", austrian));
        Assert.True(fixture.Matches(":lang(de)", german));

        // The whole reason the comparison is over subtags rather than over characters. `den` is
        // Dendi, and a `StartsWith` would call it German.
        Assert.False(fixture.Matches(":lang(de)", dendi));
    }

    /// <summary>The subject is the content language, not the declaration.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that separates <c>:lang(de)</c> from <c>[lang|="de"]</c></b>, and the
    ///     one that refutes #606's "this is a spelling". Both spellings are asked of the same
    ///     element, and they disagree.
    /// </remarks>
    [Fact]
    public void A_span_inside_a_German_paragraph_is_German_and_only_one_of_the_two_spellings_knows_it() {
        var fixture = new StyleFixture();
        var paragraph = fixture.Tree.CreateElement("p");
        var span = fixture.Tree.CreateElement("span", paragraph);

        fixture.Tree.SetAttribute(paragraph, "lang", "de");

        Assert.True(fixture.Matches(":lang(de)", span));
        Assert.False(fixture.Matches("[lang|=de]", span));

        // And the nearer declaration wins, which is what makes it inheritance rather than a search.
        fixture.Tree.SetAttribute(span, "lang", "fr");
        Assert.False(fixture.Matches(":lang(de)", span));
        Assert.True(fixture.Matches(":lang(fr)", span));
    }

    /// <summary>An empty tag is "no declaration here" and keeps climbing.</summary>
    /// <remarks>
    ///     What <c>UiElement.Language = null</c> writes, because <c>StyleTree</c> appends attributes
    ///     and never removes one. If an empty value read as a declaration, taking a declaration off
    ///     would strand the subtree in no language at all.
    /// </remarks>
    [Fact]
    public void An_empty_tag_is_not_a_declaration() {
        var fixture = new StyleFixture();
        var paragraph = fixture.Tree.CreateElement("p");
        var span = fixture.Tree.CreateElement("span", paragraph);

        fixture.Tree.SetAttribute(paragraph, "lang", "de");
        fixture.Tree.SetAttribute(span, "lang", string.Empty);

        Assert.True(fixture.Matches(":lang(de)", span));
    }

    /// <summary>The document's language is the bottom of the climb.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that would have made this a feature nothing could use.</b> A host declares
    ///     the interface's language once, on <c>UiDocument.Language</c>, and nothing in the tree
    ///     carries a <c>lang</c> attribute at all — which is the commonest configuration there is. A
    ///     <c>:lang()</c> that only read attributes would match nothing in it.
    /// </remarks>
    [Fact]
    public void An_element_that_declares_nothing_takes_the_documents_language() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("p");

        Assert.False(fixture.Matches(":lang(de)", element));

        fixture.Tree.Language = "de-CH";

        Assert.True(fixture.Matches(":lang(de)", element));
        Assert.False(fixture.Matches(":lang(fr)", element));
    }

    /// <summary>Undetermined matches nothing, which is a state rather than a default.</summary>
    [Fact]
    public void An_undetermined_language_is_in_no_range() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("p");

        Assert.False(fixture.Matches(":lang(de)", element));
        Assert.False(fixture.Matches(":lang(en)", element));
    }

    /// <summary>RFC 4647 § 3.3.2 extended filtering, which is what Selectors 4 cites.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that "prefix match at a hyphen" gets wrong in the other direction.</b> A
    ///     range's subtags need not be consecutive in the tag: a script subtag somebody wrote does
    ///     not stop <c>de-Latn-AT</c> being Austrian German. The singleton rule is what keeps the
    ///     skipping from running into an extension namespace.
    /// </remarks>
    [Theory]
    [InlineData("de-AT", "de", true)]
    [InlineData("de", "de", true)]
    [InlineData("DE-at", "de", true)]
    [InlineData("den", "de", false)]
    [InlineData("de", "de-AT", false)]
    [InlineData("de-Latn-AT", "de-AT", true)]
    [InlineData("de-AT", "de-Latn-AT", false)]
    [InlineData("de-a-value-AT", "de-AT", false)]
    [InlineData("", "de", false)]
    public void Extended_filtering_compares_subtags(string tag, string range, bool expected) =>
        Assert.Equal(expected, SelectorMatcher.Filters(tag, range));

    /// <summary>The forms Selectors 4 adds are refused, and one layer down from here.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured against ExCSS 4.3.2 rather than assumed.</b> <c>:lang(de)</c> and
    ///     <c>:lang(de-AT)</c> arrive as a <c>PseudoClassSelector</c>; <c>:lang(de, fr)</c>,
    ///     <c>:lang(*-CH)</c>, <c>:lang(*)</c> and <c>:lang()</c> all come back as an
    ///     <c>UnknownSelector</c> carrying the whole compound. So the list and the wildcard wait on a
    ///     parser and not on the matcher, and no unreachable branch was written for them.
    /// </remarks>
    [Fact]
    public void The_Selectors_4_list_and_wildcard_forms_are_refused_by_the_parser_not_by_the_matcher() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("p");
        fixture.Tree.SetAttribute(element, "lang", "de-CH");

        fixture.Load("p:lang(de, fr) { color: red } p:lang(*-CH) { color: red } p:lang() { color: red }");

        Assert.NotEmpty(fixture.Compiler.Diagnostics);
        Assert.Empty(fixture.MatchBruteForce(element));
    }

    /// <summary>A rule that can select on an ancestor's language cannot be shared sideways.</summary>
    /// <remarks>
    ///     ⚠ <b>The soundness half, and it is not about ancestors.</b> A sharing key carries the
    ///     parent, so two siblings agree on every declaration above them — but not on their own
    ///     <c>lang</c>, which is an attribute the key does not hold. Without this the second span
    ///     below would take the first's computed style and be styled German.
    /// </remarks>
    [Fact]
    public void A_lang_rule_blocks_style_sharing_the_way_an_attribute_rule_does() {
        var fixture = new CascadeFixture();
        fixture.Load(":lang(de) { color: red }");

        var parent = fixture.Tree.CreateElement("p");
        var german = fixture.Tree.CreateElement("span", parent);
        var neighbour = fixture.Tree.CreateElement("span", parent);

        fixture.Tree.SetAttribute(german, "lang", "de");

        // In this order, because sharing is a cache: the second element is the one that would be
        // handed the first's computed style, and only if the rule set said sharing was sound.
        Assert.Equal("rgb(255, 0, 0)", fixture.Value(german));
        Assert.Null(fixture.Value(neighbour));
    }
}
