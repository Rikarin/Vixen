// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>::before</c> and <c>::after</c>: compiled, kept out of the element's cascade, and given one of their own.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 43's F6 is the failure every test here is shaped against.</b> A <c>::before</c>
///         once compiled into a field nothing read, so its declarations reached the paragraph. The
///         compiler accepting the selector is therefore worth nothing alone; what is asserted is where
///         the declarations go — into <see cref="StyleResolver.ResolvePseudo" />'s answer, and never
///         into the element's own.
///     </para>
///     <para>
///         The frame half — that something builds, paints and hit-tests the box — is
///         <c>Vixen.Ui.Tests.GeneratedContentTests</c>, because a test that reaches for the resolver
///         directly would pass with the box never generated.
///     </para>
/// </remarks>
public class PseudoElementCascadeTests {
    [Theory]
    [InlineData("p::before", PseudoElement.Before)]
    [InlineData("p:before", PseudoElement.Before)]
    [InlineData("p::after", PseudoElement.After)]
    [InlineData("p::AFTER", PseudoElement.After)]
    public void Before_and_after_compile_naming_their_box_and_weigh_one_type(string selector, PseudoElement expected) {
        var fixture = new StyleFixture();

        var compiled = Assert.Single(fixture.Load(selector + " { color: red }"));

        Assert.Empty(fixture.Compiler.Diagnostics);
        Assert.Equal(expected, compiled.PseudoElement);

        // Selectors 4 § 17: a pseudo-element is charged as a type selector.
        Assert.Equal(new Specificity(0, 0, 2), compiled.Specificity);
    }

    [Theory]
    [InlineData("p::marker")]
    [InlineData("p::first-letter")]
    [InlineData("p::first-line")]
    [InlineData("p::selection")]
    [InlineData("p::placeholder")]
    [InlineData("p::backdrop")]
    [InlineData("p::file-selector-button")]
    public void Every_other_pseudo_element_is_still_refused_with_F6s_reason(string selector) {
        var fixture = new StyleFixture();

        Assert.Empty(fixture.Load(selector + " { color: red }"));

        var refusal = Assert.Single(fixture.Compiler.Diagnostics);
        Assert.Contains("no box without an element behind it", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("p::before span", "must be the last thing")]
    [InlineData("p::before > span", "must be the last thing")]
    [InlineData("p::before:hover", "follows a pseudo-element")]
    [InlineData("p::before.x", "follows a pseudo-element")]
    // ExCSS itself cannot read this one, so it never reaches the compiler's own argument check.
    [InlineData("p:not(::before)", "not a selector")]
    [InlineData("p:is(span::after)", "argument")]
    [InlineData("div:has(p::before)", "argument")]
    public void A_pseudo_element_anywhere_but_the_end_of_the_selector_is_refused(string selector, string reason) {
        var fixture = new StyleFixture();

        Assert.Empty(fixture.Load(selector + " { color: red }"));
        Assert.Contains(fixture.Compiler.Diagnostics, diagnostic => diagnostic.Reason.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_before_rule_reaches_the_box_and_never_the_element() {
        var fixture = new CascadeFixture();
        // Custom properties as the probe, because ExCSS rewrites a colour keyword into `rgb()`.
        fixture.Load("p::before { --mark: box; content: 'x' } p { --mark: element }");

        var paragraph = fixture.Tree.CreateElement("p");
        var own = fixture.Engine.Resolver.Resolve(fixture.Tree, paragraph);

        // F6: the paragraph is green and carries no `content`.
        Assert.Equal("element", fixture.Read(own, "--mark"));
        Assert.Null(fixture.Read(own, "content"));

        var box = fixture.Engine.Resolver.ResolvePseudo(fixture.Tree, paragraph, PseudoElement.Before, own);

        Assert.NotNull(box);
        Assert.Equal("box", fixture.Read(box, "--mark"));
        Assert.NotNull(fixture.Read(box, "content"));

        // And no `::after` was written, so there is none — without a property being read.
        Assert.Null(fixture.Engine.Resolver.ResolvePseudo(fixture.Tree, paragraph, PseudoElement.After, own));
    }

    [Fact]
    public void The_box_inherits_from_the_element_that_generates_it() {
        var fixture = new CascadeFixture();
        fixture.Load(".card { --tone: teal } .card::after { content: '' }");

        var card = fixture.Tree.CreateElement("div", classNames: ["card"]);
        var own = fixture.Engine.Resolver.Resolve(fixture.Tree, card);
        var box = fixture.Engine.Resolver.ResolvePseudo(fixture.Tree, card, PseudoElement.After, own);

        Assert.NotNull(box);
        Assert.Equal("teal", fixture.Read(box, "--tone"));
    }

    [Fact]
    public void The_box_follows_the_originating_elements_state_and_ancestors() {
        var fixture = new CascadeFixture();
        fixture.Load(".dark .card:hover::before { content: 'on' }");

        var page = fixture.Tree.CreateElement("div", classNames: ["dark"]);
        var card = fixture.Tree.CreateElement("div", page, classNames: ["card"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Null(updater.PseudoStyleOf(card, PseudoElement.Before));

        // ⚠ Through the incremental pass, which is what a frame runs: the box's style has no slot
        // of its own, so it is only right if re-resolving the element recomputes it.
        fixture.Tree.SetState(card, ElementState.Hover);
        updater.StateChanged(card);

        Assert.Equal("'on'", fixture.Read(updater.PseudoStyleOf(card, PseudoElement.Before)!, "content")?.Replace('"', '\''));

        fixture.Tree.RemoveClass(page, "dark");
        updater.ClassChanged(page, "dark");

        Assert.Null(updater.PseudoStyleOf(card, PseudoElement.Before));
    }

    [Fact]
    public void An_element_no_pseudo_element_rule_names_costs_no_second_cascade() {
        // ⚠ The cost half, measured as cascades built rather than as time. One `::before` anywhere
        // in a sheet must not make every element of the document pay two more cascades a pass to
        // arrive at "no box": ten elements, ten cascades, and the one element the rule is about pays
        // its one extra.
        var fixture = new CascadeFixture();
        fixture.Load(".a { --x: 1 } .badge::before { content: '' }");

        // Each with an id of its own, so that the sharing cache cannot answer for nine of them and
        // hide the count this is about.
        for (var i = 0; i < 10; i++) {
            fixture.Tree.CreateElement("div", id: "e" + i, classNames: ["a"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        var before = fixture.Engine.Resolver.Cascades;
        updater.ResolveAll();

        Assert.True(fixture.Engine.Rules.HasPseudoElementRules);
        Assert.Equal(10, fixture.Engine.Resolver.Cascades - before);

        // And the element that has one does pay for it, which is what keeps the count honest.
        var badge = fixture.Tree.CreateElement("div", classNames: ["badge"]);
        before = fixture.Engine.Resolver.Cascades;
        updater.ResolveAll();

        Assert.Equal(12, fixture.Engine.Resolver.Cascades - before);
        Assert.NotNull(updater.PseudoStyleOf(badge, PseudoElement.Before));
    }
}
