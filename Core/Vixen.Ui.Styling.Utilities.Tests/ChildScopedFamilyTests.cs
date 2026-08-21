// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The two families whose rule is about the children rather than about the element.</summary>
/// <remarks>
///     <para>
///         <c>space-x-*</c>, <c>space-y-*</c> and the <c>divide-*</c> set are the only utilities in
///         Tailwind's index that are not property families at all. <c>space-x-4</c> does not set
///         anything on the element carrying it; it sets a margin on <i>every child but the last</i>,
///         and <c>divide-y</c> sets a border the same way. Doc 09 named both for 1.0 and neither was
///         written, and the reason is visible in the shape of the table they belong to: a
///         <c>Family</c> was a name, a value kind and a list of properties, with no way to say what
///         the rule is <i>about</i>. It has a <c>Scope</c> now.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the selector engine needed to change, and the design started from the
///         assumption that something would.</b> A child combinator, <c>:not()</c> and
///         <c>:last-child</c> are all compiled and matched by <c>SelectorCompiler</c> and
///         <c>SelectorMatcher</c> already — <see cref="A_scoped_family_reaches_the_children_and_not_the_last_one" />
///         is the proof, and it is first in the file because the whole design rests on it. What was
///         missing was one line in <c>UtilityGenerator</c> and a field on <c>Family</c>.
///     </para>
///     <para>
///         ⚠ <b>What these tests are for that the consumption gate is not.</b> Every property these
///         families emit was already emitted by some other family and already read —
///         <c>margin-inline-end</c> by <c>me-*</c>, <c>border-bottom-width</c> by <c>border-b-*</c> —
///         so the gate cannot fail on any of them, and a scoped family whose <i>selector</i> never
///         matched would be silently perfect by its measure. The end-to-end frames below are what
///         says the rule reaches a child at all.
///     </para>
/// </remarks>
public class ChildScopedFamilyTests {
    /// <summary>The mechanism: the rule matches every child but the last, in a real cascade.</summary>
    /// <remarks>
    ///     The load-bearing claim of the whole feature, asserted against the style engine rather than
    ///     against the emitted text — the generator could produce a perfectly reasonable selector that
    ///     <c>SelectorCompiler</c> declined, and the class would resolve to nothing with no diagnostic
    ///     anyone reads.
    /// </remarks>
    [Theory]
    [InlineData("space-x-2", "margin-inline-end", "8px")]
    [InlineData("space-y-2", "margin-bottom", "8px")]
    [InlineData("divide-x", "border-inline-end-width", "1px")]
    [InlineData("divide-y-2", "border-bottom-width", "2px")]
    public void A_scoped_family_reaches_the_children_and_not_the_last_one(
        string utility,
        string property,
        string expected
    ) {
        var fixture = new UtilityFixture();
        var engine = new StyleEngine();
        engine.Load(fixture.Generate(utility), StyleOrigin.Author);

        var container = engine.Tree.CreateElement("div", null, classNames: [utility]);
        var first = engine.Tree.CreateElement("div", container);
        var middle = engine.Tree.CreateElement("div", container);
        var last = engine.Tree.CreateElement("div", container);

        var styles = engine.ResolveAll();
        var id = engine.Properties.Lookup(property);

        Assert.NotEqual(NameTable.None, id);
        Assert.Equal(expected, Read(styles[first.Index]));
        Assert.Equal(expected, Read(styles[middle.Index]));
        Assert.Null(Read(styles[last.Index]));

        // ⚠ And nothing at all on the element that carries the class, which is the half a property
        // family would have got wrong. A `space-x-2` that put the margin on the container would look
        // right in a two-child column and be a trailing gap everywhere else.
        Assert.Null(Read(styles[container.Index]));

        string? Read(ComputedStyle style) =>
            style.TryGet(id, out var value) ? engine.Values.NameOf(value) : null;
    }

    /// <summary>And the layout moves, which is the only claim worth making about a margin.</summary>
    /// <remarks>
    ///     ⚠ <b>A frame, not a computed value.</b> The lesson <c>InertProperties.txt</c> records four
    ///     times over is that "the cascade holds a value for it" is not "something acts on it", and a
    ///     family whose whole purpose is to move boxes apart should be asked whether the boxes moved.
    ///     Both axes, because they emit different longhands for different reasons — <c>space-x-*</c>
    ///     the logical <c>margin-inline-end</c> and <c>space-y-*</c> the physical <c>margin-bottom</c>
    ///     — and a mistake in either would be invisible in the other.
    /// </remarks>
    [Fact]
    public void Spacing_the_children_moves_them() {
        var fixture = new UtilityFixture();

        using var document = new UiDocument(200f, 200f);
        document.Load(
            fixture.Generate("space-x-2", "space-y-2") + """
            #row    { display: flex; flex-direction: row; }
            #column { display: flex; flex-direction: column; }
            .kid    { width: 20px; height: 10px; }
            """,
            StyleOrigin.Author
        );

        var row = document.Create("div", document.Root, "row", "space-x-2");
        document.Create("div", row, null, "kid");
        var second = document.Create("div", row, null, "kid");

        var column = document.Create("div", document.Root, "column", "space-y-2");
        document.Create("div", column, null, "kid");
        var below = document.Create("div", column, null, "kid");

        document.Update();

        // Twenty wide plus two spacing steps, rather than twenty.
        Assert.Equal(28f, second.AbsoluteLeft - row.AbsoluteLeft);
        Assert.Equal(18f, below.AbsoluteTop - column.AbsoluteTop);
    }

    /// <summary>Each of the five names resolves to itself rather than being eaten by a shorter one.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SplitName</c> takes the longest registered name and does not retry a shorter prefix
    ///     on failure</b> — doc 43 § F8 — so a family whose name is a prefix of another, or whose
    ///     name another is a prefix of, can shadow it silently and the class is reported as a typo. All
    ///     five names added here are short and two of them nest (<c>divide</c> inside
    ///     <c>divide-x</c>), which is exactly the arrangement that goes wrong. Asserted rather than
    ///     assumed.
    /// </remarks>
    [Theory]
    [InlineData("space-x-4", "space-x", "4")]
    [InlineData("space-y-4", "space-y", "4")]
    [InlineData("divide-x-2", "divide-x", "2")]
    [InlineData("divide-y-2", "divide-y", "2")]
    [InlineData("divide-x", "divide-x", "")]
    [InlineData("divide-y", "divide-y", "")]
    [InlineData("divide-accent", "divide", "accent")]
    public void The_new_names_are_not_shadowed(string whole, string name, string value) =>
        Assert.Equal((name, value), UtilityFamilies.SplitName(whole));

    /// <summary>The scope goes after the variants, so the variant is about the container.</summary>
    /// <remarks>
    ///     ⚠ <b>The one ordering mistake that compiles.</b>
    ///     <c>.hover\:space-x-2:hover &gt; :not(:last-child)</c> means "space the children while the
    ///     container is hovered", which is what the class name says;
    ///     <c>.hover\:space-x-2 &gt; :not(:last-child):hover</c> means "space whichever child the
    ///     pointer is over", which is a different rule that also parses, also matches, and would have
    ///     shipped. Asserted on the emitted text because it is a claim about the <i>selector</i>, and
    ///     a resolution test would pass under both readings for the hovered child.
    /// </remarks>
    [Theory]
    [InlineData("space-x-2", ".space-x-2 > :not(:last-child)")]
    [InlineData("hover:space-x-2", ".hover\\:space-x-2:hover > :not(:last-child)")]
    [InlineData("divide-y", ".divide-y > :not(:last-child)")]
    public void The_scope_is_appended_after_the_variants(string utility, string selector) =>
        Assert.Contains(selector, new UtilityFixture().Generate(utility), StringComparison.Ordinal);

    /// <summary>A media variant still wraps the whole thing rather than sitting inside it.</summary>
    [Fact]
    public void A_breakpoint_variant_wraps_a_scoped_rule() {
        var css = new UtilityFixture().Generate("md:divide-y-2");

        Assert.Contains("@media (min-width: 768px)", css, StringComparison.Ordinal);
        Assert.Contains(".md\\:divide-y-2 > :not(:last-child)", css, StringComparison.Ordinal);
    }

    /// <summary><c>@apply</c> refuses a scoped family, for the reason it refuses a variant.</summary>
    /// <remarks>
    ///     ⚠ <b>The silent-failure this replaces is worse than an unknown class.</b> Without the
    ///     refusal, <c>@apply space-x-4</c> writes <c>margin-inline-end: 16px</c> into the block it was
    ///     written in — a trailing margin on the container, which is neither what the class means nor
    ///     obviously wrong when you look at it.
    /// </remarks>
    [Theory]
    [InlineData("space-x-4")]
    [InlineData("divide-y")]
    [InlineData("divide-accent")]
    public void Apply_refuses_a_family_that_styles_children(string utility) {
        var expander = new ApplyExpander(new UtilityFixture().Tokens);
        var expanded = expander.Expand($".card {{ @apply {utility}; }}");

        Assert.DoesNotContain("margin", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("border", expanded, StringComparison.Ordinal);
        Assert.Contains(expander.Diagnostics, diagnostic => diagnostic.Contains(utility, StringComparison.Ordinal));
    }

    /// <summary>The colour is <c>divide-*</c> and the widths are <c>divide-x</c>/<c>divide-y</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         Tailwind writes a divider's colour <c>divide-accent</c>, never <c>divide-x-accent</c>,
    ///         and <c>divide-x</c> is registered with no colour longhands at all so the unwritten
    ///         spelling is reported as unknown rather than resolving to something nobody asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>divide-x-[red]</c> is <i>not</i> in this list, and finding out why is worth the
    ///         line.</b> <c>LooksLikeColor</c> is a shape test over <c>#</c>, <c>rgb</c> and
    ///         <c>hsl</c> — a named colour does not look like one — so an arbitrary <c>red</c> takes
    ///         the width branch and emits <c>border-inline-end-width: red</c>. That is the escape
    ///         hatch behaving as <c>IsPlausibleValue</c>'s remark says it must ("a token-shape test
    ///         and never a value parser"), it is exactly what <c>border-x-[red]</c> has always done,
    ///         and changing it here would be a different family's decision made in the wrong file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_divider_colour_is_the_unaxed_family_and_the_axes_are_widths_only() {
        var fixture = new UtilityFixture();

        Assert.Equal(["border-color: #4f7cff"], fixture.Emits("divide-accent"));
        Assert.Equal(["border-inline-end-width: 2px"], fixture.Emits("divide-x-2"));
        Assert.Equal(["border-bottom-width: 3px"], fixture.Emits("divide-y-[3px]"));

        Assert.Null(fixture.Declarations("divide-x-accent"));
        Assert.Null(fixture.Declarations("divide-y-accent"));
        Assert.Null(fixture.Declarations("divide-x-[#ff0000]"));
    }

    /// <summary>A negative space is a real class and pulls the children together.</summary>
    /// <remarks>
    ///     <c>-space-x-2</c> is Tailwind's idiom for an overlapping avatar stack. It works here for
    ///     free because <c>TryNegate</c> flips the sign of whatever the family resolved to, which is
    ///     the whole reason negation is applied to the result rather than threaded through the kinds.
    /// </remarks>
    [Fact]
    public void A_negative_space_resolves() {
        var fixture = new UtilityFixture();

        Assert.Equal(["margin-inline-end: -8px"], fixture.Emits("-space-x-2"));
        Assert.Equal(["margin-bottom: -8px"], fixture.Emits("-space-y-2"));
        Assert.Equal(["margin-inline-end: 1px"], fixture.Emits("space-x-px"));
    }

    /// <summary>The three v4 spellings this engine deliberately does not have, pinned as absent.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Absence recorded as a test, because the alternative is a family that computes a
    ///         value and does nothing.</b> Each of these needs something the engine has not got, and
    ///         each would resolve cleanly if it were registered:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>space-x-reverse</c> and <c>divide-x-reverse</c> — v4 emits both edges of the
    ///         axis and multiplies each by a <c>--tw-*-reverse</c> flag, which needs
    ///         <c>calc()</c>. <c>StyleValueParser</c> has no <c>calc()</c>, so the flag would have
    ///         nothing to multiply and the reverse class would be a custom property nobody reads.</item>
    ///         <item><c>divide-solid</c> and the other four style keywords — <c>border-style</c> is
    ///         emitted by no family here and read by nothing either. Measured, not assumed: it
    ///         resolves into four longhands and moves no channel in any of the probe's scenes. A
    ///         <c>divide-dashed</c> that drew a solid line is the inert family
    ///         <c>UtilityConsumptionGateTests</c> exists to keep out.</item>
    ///     </list>
    /// </remarks>
    [Theory]
    [InlineData("space-x-reverse")]
    [InlineData("space-y-reverse")]
    [InlineData("divide-x-reverse")]
    [InlineData("divide-y-reverse")]
    [InlineData("divide-solid")]
    [InlineData("divide-dashed")]
    [InlineData("divide-dotted")]
    [InlineData("divide-double")]
    public void The_spellings_that_need_something_the_engine_has_not_got_are_absent(string utility) =>
        Assert.Null(new UtilityFixture().Declarations(utility));

    /// <summary>The known divergence from v4, pinned so that fixing it fails here rather than silently.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>v4 wraps the scope in <c>:where()</c> and this cannot, so a container's
    ///         <c>space-y-*</c> beats a child's own margin utility.</b> <c>:where()</c> contributes no
    ///         specificity in CSS, which is precisely why Tailwind adopted it in v4: the rule lands at
    ///         one class, so <c>&lt;div class="mb-0"&gt;</c> inside <c>space-y-4</c> wins. Vixen's
    ///         <c>SelectorCompiler</c> compiles <c>:where()</c> as <c>:is()</c> and charges a class for
    ///         it either way, so there is no spelling available here that reaches zero — the emitted
    ///         rule is <c>(0,2,0)</c> and the child's <c>.mb-0</c> is <c>(0,1,0)</c>.
    ///     </para>
    ///     <para>
    ///         <b>This is v3's behaviour, and it shipped for four major versions.</b> It is written
    ///         down in the guide and in doc 43 rather than left to be discovered, and the escape is the
    ///         one v3 users used: put the exception on the container, or do not use <c>space-*</c> on a
    ///         list whose items set their own margins. Closing it is three lines in
    ///         <c>SelectorCompiler</c> — charge nothing for <c>:where()</c> — and the day that lands
    ///         this test fails, which is the point of writing it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_child_margin_utility_loses_to_the_containers_space_and_that_is_the_v3_behaviour() {
        var fixture = new UtilityFixture();
        var engine = new StyleEngine();
        engine.Load(fixture.Generate("space-y-4", "mb-0"), StyleOrigin.Author);

        var container = engine.Tree.CreateElement("div", null, classNames: ["space-y-4"]);
        var child = engine.Tree.CreateElement("div", container, classNames: ["mb-0"]);
        engine.Tree.CreateElement("div", container);

        var styles = engine.ResolveAll();
        var id = engine.Properties.Lookup("margin-bottom");

        Assert.True(styles[child.Index].TryGet(id, out var value));
        Assert.Equal("16px", engine.Values.NameOf(value));
    }
}
