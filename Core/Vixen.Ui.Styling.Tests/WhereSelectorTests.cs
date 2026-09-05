// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Where <c>:where()</c> actually stops, which is a whole stage earlier than doc 43 says.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 43 § F9 and <c>ChildScopedFamilyTests</c> both said <c>SelectorCompiler</c>
///         "charges a class for <c>:where()</c> exactly as it does for <c>:is()</c>", and that the
///         cure was three lines there. It is not true and the sizing that came from it is wrong.</b>
///         ExCSS 4.3.2 has no <c>:where()</c>: a selector containing one comes back as a single
///         <see cref="UnknownSelector" /> covering the <i>whole</i> selector — not a complex selector
///         with one unknown part — so <c>SelectorCompiler</c> never sees a <c>MatchesSelector</c> to
///         charge anything for, and the rule is refused entire. The difference between "lands at
///         (0,2,0)" and "does not land" is the difference between a specificity tweak and teaching
///         the front end a selector, which is why it is pinned here rather than left in prose.
///     </para>
///     <para>
///         <b>What this prints on the day nothing runs.</b> The three cases are one parse each and
///         take no device, document or clock. The first is the control — <c>:is()</c> compiles and is
///         charged — so a change that broke the compiler wholesale would fail here rather than making
///         the refusals below look like a passing test.
///     </para>
///     <para>
///         This goes red the day <c>:where()</c> is supported, which is the point of writing it: the
///         refusal is what has to be deleted, and it should be deleted deliberately.
///     </para>
/// </remarks>
public class WhereSelectorTests {
    /// <summary>The control: <c>:is()</c> is understood, and Vixen charges it one class.</summary>
    /// <remarks>
    ///     ⚠ One class flat, which is itself a divergence from Selectors 4 — CSS gives <c>:is()</c>
    ///     the specificity of its <i>most specific</i> argument, so <c>:is(#a, .b)</c> is an id in a
    ///     browser and a class here. Asserted rather than glossed, because it is the number the
    ///     paragraph about <c>:where()</c> is comparing against.
    /// </remarks>
    [Fact]
    public void An_is_selector_compiles_and_is_charged_a_class() {
        var fixture = new StyleFixture();
        var selector = fixture.Compile(".a > :is(:not(:last-child))");

        // `.a` and `:is(…)`, one class each. The `:not(:last-child)` inside contributes nothing
        // further because the nested selector's own specificity is never added to the outer one.
        Assert.Equal(new Specificity(0, 2, 0), selector.Specificity);
    }

    /// <summary>And <c>:where()</c> does not compile at all, in any position.</summary>
    /// <remarks>
    ///     ⚠ The whole selector is lost, not the wrapper. ExCSS hands the entire text over as one
    ///     unknown selector, so <c>.a > :where(:not(:last-child))</c> does not become "the same rule
    ///     at a different specificity" — it becomes no rule, and the diagnostic quotes the whole
    ///     selector rather than the part it could not read.
    /// </remarks>
    [Theory]
    [InlineData(":where(.b)")]
    [InlineData(".a :where(.b)")]
    [InlineData(".a > :where(:not(:last-child))")]
    public void A_where_selector_is_refused_whole(string selectorText) {
        var fixture = new StyleFixture();
        var compiled = fixture.Load(selectorText + " { color: red }");

        Assert.Empty(compiled);
        Assert.NotEmpty(fixture.Compiler.Diagnostics);

        // Quoted so that a reader can see what was dropped; the refusal is worth nothing if it names
        // something the author did not write.
        Assert.Contains(
            fixture.Compiler.Diagnostics,
            diagnostic => diagnostic.Text.Contains(":where", StringComparison.Ordinal)
        );
    }
}
