// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>:where()</c> — <c>:is()</c>'s matching, and none of its specificity.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file used to pin the opposite, and the thing it pinned was itself a refutation.</b>
///         Doc 43 § F9, doc 09 and three READMEs said <c>SelectorCompiler</c> "charges a class for
///         <c>:where()</c> exactly as it does for <c>:is()</c>" and that zeroing it was three lines
///         there. It charged nothing, because it never saw one: ExCSS 4.3.2 has no <c>:where()</c>,
///         and a selector containing one comes back as a single <c>UnknownSelector</c> covering the
///         <i>whole</i> text — so the rule was refused entire and there was no charge to remove.
///     </para>
///     <para>
///         What landed is therefore a repair rather than a subtraction: that unknown node still
///         carries the author's text, so the text is split on its top-level commas, each
///         <c>:where(</c> becomes <c>:is(</c>, each part is handed back to ExCSS, and one class per
///         top-level occurrence is taken off the result. The cases below are the ones that tell a
///         real implementation from that rewrite done carelessly.
///     </para>
///     <para>
///         <b>What this prints on the day nothing runs.</b> Each case is one parse and needs no
///         device, document or clock. The first is the control — <c>:is()</c> is charged a class —
///         so a change that broke the compiler wholesale fails there rather than making the numbers
///         below look deliberate.
///     </para>
/// </remarks>
public class WhereSelectorTests {
    /// <summary>The control: <c>:is()</c> is understood, and Vixen charges it one class.</summary>
    /// <remarks>
    ///     ⚠ One class flat, which is itself a divergence from Selectors 4 — CSS gives <c>:is()</c>
    ///     the specificity of its <i>most specific</i> argument, so <c>:is(#a, .b)</c> is an id in a
    ///     browser and a class here. Asserted rather than glossed, because it is the number every
    ///     <c>:where()</c> case below is measured against.
    /// </remarks>
    [Fact]
    public void An_is_selector_compiles_and_is_charged_a_class() {
        var fixture = new StyleFixture();
        var selector = fixture.Compile(".a > :is(:not(:last-child))");

        // `.a` and `:is(…)`, one class each. The `:not(:last-child)` inside contributes nothing
        // further because the nested selector's own specificity is never added to the outer one.
        Assert.Equal(new Specificity(0, 2, 0), selector.Specificity);
    }

    /// <summary>And the same selector written with <c>:where()</c> is charged one class less.</summary>
    /// <remarks>
    ///     ⚠ <b><c>(0,1,0)</c> is the number the utility generator needs and the reason this was
    ///     worth doing.</b> A child-scoped family emits <c>.space-y-4 &gt; :where(:not(:last-child))</c>
    ///     so that a child's own <c>mb-0</c> — also <c>(0,1,0)</c>, and later in the sheet — wins.
    ///     At <c>(0,2,0)</c> the container's rule beats the child and the override is unwritable.
    /// </remarks>
    [Theory]
    [InlineData(":where(.b)", 0, 0)]
    [InlineData(".a :where(.b)", 1, 0)]
    [InlineData(".a > :where(:not(:last-child))", 1, 0)]
    [InlineData(".a:where(.b, .c)", 1, 0)]
    [InlineData("div:where(.b)", 0, 1)]
    [InlineData(":where(.b) :where(.c)", 0, 0)]
    public void A_where_selector_compiles_and_is_charged_nothing(string selectorText, int classes, int types) {
        var fixture = new StyleFixture();
        var selector = fixture.Compile(selectorText);

        Assert.Empty(fixture.Compiler.Diagnostics);
        Assert.Equal(new Specificity(0, classes, types), selector.Specificity);
    }

    /// <summary>⚠ A nested <c>:where()</c> is already free, so subtracting for it would go negative.</summary>
    /// <remarks>
    ///     This compiler never adds a nested selector's specificity to the outer one, so
    ///     <c>:where()</c> inside <c>:not()</c> or <c>:is()</c> contributed nothing before the
    ///     rewrite and contributes nothing after it. Charging the subtraction there would make
    ///     <c>.a:not(:where(.b))</c> <i>less</i> specific than <c>.a</c> — the one arithmetic mistake
    ///     this repair can make that no compile failure would show.
    /// </remarks>
    [Theory]
    [InlineData(".a:not(:where(.b))", 2)]
    [InlineData(".a:is(:where(.b))", 2)]
    [InlineData(".a:has(:where(.b))", 2)]
    public void A_where_inside_another_pseudo_class_is_not_subtracted_twice(string selectorText, int expected) {
        var fixture = new StyleFixture();
        var selector = fixture.Compile(selectorText);

        Assert.Empty(fixture.Compiler.Diagnostics);
        Assert.Equal(expected, selector.Specificity.Classes);
    }

    /// <summary>Each part of a list is split, rewritten and charged on its own.</summary>
    /// <remarks>
    ///     ⚠ The whole list arrives as one unknown node, commas included, so the split is this
    ///     compiler's rather than ExCSS's — and a per-selector count that was really a per-list count
    ///     would take the class off <c>.c</c> as well.
    /// </remarks>
    [Fact]
    public void A_list_is_split_on_its_own_top_level_commas() {
        var fixture = new StyleFixture();
        var compiled = fixture.Load(".a:where(.b), .c { color: red }");

        Assert.Empty(fixture.Compiler.Diagnostics);
        Assert.Equal(2, compiled.Count);
        Assert.Equal(new Specificity(0, 1, 0), compiled[0].Specificity);
        Assert.Equal(new Specificity(0, 1, 0), compiled[1].Specificity);

        // A comma inside the argument is not a top-level one, so this stays a single selector.
        var single = new StyleFixture();
        Assert.Single(single.Load(".a:where(.b, .c) { color: red }"));
    }

    /// <summary>And it matches what <c>:is()</c> matches, because that is all <c>:where()</c> is.</summary>
    [Fact]
    public void A_where_selector_matches_exactly_what_the_same_is_selector_matches() {
        var fixture = new StyleFixture();
        var page = fixture.Tree.CreateElement("div", classNames: ["page"]);
        var first = fixture.Tree.CreateElement("div", page, classNames: ["row"]);
        var last = fixture.Tree.CreateElement("div", page, classNames: ["row"]);

        Assert.True(fixture.Matches(".page > :where(:not(:last-child))", first));
        Assert.False(fixture.Matches(".page > :where(:not(:last-child))", last));
        Assert.True(fixture.Matches(":where(.row, .column)", last));
        Assert.False(fixture.Matches(":where(.column, .cell)", last));
    }

    /// <summary>⚠ And the repair is attempted only where a repair is needed.</summary>
    /// <remarks>
    ///     The word appears inside a quoted attribute value in text ExCSS reads perfectly well.
    ///     Treating that as a selector to rewrite would send a working selector down the re-parse
    ///     path and lose it — the string is skipped, so this compiles as it always did.
    /// </remarks>
    [Fact]
    public void The_word_inside_a_quoted_attribute_value_is_not_a_selector() {
        var fixture = new StyleFixture();
        var selector = fixture.Compile(""".a[data-q=":where(x)"]""");

        Assert.Empty(fixture.Compiler.Diagnostics);
        Assert.Equal(new Specificity(0, 2, 0), selector.Specificity);
    }

    /// <summary><c>:open</c> rides the same repair, and it is a name the matcher can act on.</summary>
    /// <remarks>
    ///     ⚠ <b>A third name on this scan, and the first one that is a <i>prefix</i> of other names
    ///     CSS spells.</b> ExCSS 4.3.2 hands <c>expander:open</c> back as one
    ///     <c>UnknownSelector</c> covering the whole compound — measured, not assumed — so the
    ///     variant was recorded for years as needing a parser upgrade, when what it needed was the
    ///     rewrite <c>:user-valid</c> already had.
    /// </remarks>
    [Fact]
    public void An_open_pseudo_class_compiles_through_the_repair() {
        var fixture = new StyleFixture();
        var open = fixture.Tree.CreateElement("expander");
        fixture.Tree.SetState(open, ElementState.Open);
        var shut = fixture.Tree.CreateElement("expander");

        Assert.True(fixture.Matches("expander:open", open));
        Assert.False(fixture.Matches("expander:open", shut));
        Assert.Empty(fixture.Compiler.Diagnostics);

        // A pseudo-class costs one class, exactly as `:hover` does.
        Assert.Equal(new Specificity(0, 1, 1), fixture.Compile("expander:open").Specificity);
    }

    /// <summary>⚠ And a longer name that merely starts with it is left for the parser to refuse.</summary>
    /// <remarks>
    ///     <b>The rewrite is five characters wide and the name is not</b>, so a scan that stopped at
    ///     the length of <c>:open</c> would turn <c>:opened</c> into a marker followed by the letters
    ///     <c>ed</c> — an attribute selector next to a fragment, which parses, matches the open
    ///     elements, and says nothing about the word the author wrote. That is the failure mode this
    ///     file is least able to see, because the result is valid CSS rather than a diagnostic.
    /// </remarks>
    [Fact]
    public void An_open_prefixed_name_is_left_alone() {
        Assert.Empty(new StyleFixture().Load("expander:opened { color: red }"));
        Assert.Empty(new StyleFixture().Load("expander:popover-open { color: red }"));
    }
}
