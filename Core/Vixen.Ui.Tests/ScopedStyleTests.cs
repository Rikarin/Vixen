// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What <c>&lt;style scoped&gt;</c> means, and how often a component's rules are loaded.</summary>
/// <remarks>
///     <para>
///         <c>scoped</c> was parsed by the markup binder, carried through the emitter and put on the
///         generated class as <c>StyleIsScoped</c> — and then nothing read it. The stylesheet was
///         loaded whole, for every instance, and reached every element in the document. Both halves
///         of that are fixed here and they are one method's worth of change, because they are the
///         same fact: a component's stylesheet belongs to its <i>type</i>.
///     </para>
///     <para>
///         Verified by sabotage, seven of seven landing: prefixing the scope as a descendant rather
///         than welding it fails 4, not trimming the trailing space before welding fails 4, scoping
///         by instance rather than by type fails 1, loading the sheet per instance fails 1, scoping
///         a <c>@keyframes</c> body fails 1, splitting a selector list at a comma inside a
///         functional pseudo-class fails 1, and leaving the component's own root untagged fails 1.
///     </para>
///     <para>
///         ⚠ <b>The last needed a test that did not exist.</b> Every rule in the fixture named a
///         child, so the root — the element a component's stylesheet most often wants, and the one
///         element its build did not create — was scoped by a line nothing checked.
///     </para>
/// </remarks>
public class ScopedStyleTests {
    sealed class Scoped : Component {
        protected override string? Style => ".row { width: 40px; height: 10px; } .host { padding-left: 9px; }";

        protected override bool StyleIsScoped => true;

        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(null, "div");
            row.AddClass("row");
            ctx.Slot(null, BuildContext.DefaultSlot);
        }
    }

    sealed class Loose : Component {
        protected override string? Style => ".row { width: 70px; height: 10px; }";

        protected override void Build(BuildContext ctx) => ctx.Element(null, "div").AddClass("row");
    }

    static UiDocument Documented() {
        var document = new UiDocument(400f, 200f);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; }");

        return document;
    }

    [Fact]
    public void A_scoped_rule_reaches_the_components_own_element() {
        using var document = Documented();
        var component = BuildContext.Build<Scoped>(document, document.Root);

        document.Update();

        var row = component.Root.Children[0];

        Assert.True(row.HasClass("row"));
        Assert.Equal(40f, row.Width, 0.5f);
    }

    [Fact]
    public void A_scoped_rule_reaches_the_components_own_root() {
        using var document = Documented();
        var component = BuildContext.Build<Scoped>(document, document.Root);

        // ⚠ The root is the element a component's stylesheet most often wants — it is what a `class`
        // on the component's tag styles — and it is the one element the build did not create, so it
        // has to be given the scope separately. A descendant-prefixed scope misses it entirely,
        // which is half of why the class is welded rather than prefixed.
        component.Root.AddClass("host");
        document.Update();

        Assert.Equal(9f, document.Layout.GetComputedPadding(component.Root.LayoutNode, Vixen.Ui.Layout.Edge.Left), 0.5f);
    }

    [Fact]
    public void And_not_an_identically_classed_element_outside_it() {
        using var document = Documented();
        BuildContext.Build<Scoped>(document, document.Root);

        var stranger = document.Root.Add("div", classNames: "row");
        stranger.SetStyle("height", "10px");

        document.Update();

        // ⚠ The whole content of `scoped`. Before this, a component that called something `.row`
        // styled every `.row` in the application — which is a bug that appears when a second
        // component picks the same word, and looks like the *other* component being broken.
        Assert.True(stranger.HasClass("row"));
        Assert.NotEqual(40f, stranger.Width);
    }

    [Fact]
    public void Nor_a_callers_element_projected_into_its_slot() {
        using var document = Documented();
        var component = BuildContext.Build<Scoped>(document, document.Root);

        var projected = component.Content.Add("div", classNames: "row");
        projected.SetStyle("height", "10px");

        document.Update();

        // ⚠ **Inside the component and not the component's.** A descendant-prefixed scope —
        // `.v-x .row` — matches this, which is precisely the case scoping exists to stop: what a
        // caller passed in is the caller's, wherever it ends up. The scope class comes from who
        // *built* the element, not from where it sits.
        Assert.NotEqual(40f, projected.Width);
    }

    [Fact]
    public void An_unscoped_component_still_reaches_everything() {
        using var document = Documented();
        BuildContext.Build<Loose>(document, document.Root);

        var stranger = document.Root.Add("div", classNames: "row");
        stranger.SetStyle("height", "10px");

        document.Update();

        // Scoping is opt-in and the default is the old behaviour, so a component without the keyword
        // is not quietly narrowed.
        Assert.Equal(70f, stranger.Width, 0.5f);
    }

    [Fact]
    public void Every_instance_of_a_type_shares_one_scope_and_one_stylesheet() {
        using var document = Documented();

        var first = BuildContext.Build<Scoped>(document, document.Root);
        var sheets = document.Styles.SheetCount;

        var second = BuildContext.Build<Scoped>(document, document.Root);
        document.Update();

        // ⚠ Once per *type*. Loading per instance is correct and costs a rule set per row of a list,
        // which is a linear cost on exactly the thing virtualisation exists to make constant.
        Assert.Equal(sheets, document.Styles.SheetCount);

        // And both instances are styled, which is what makes the shared sheet a saving rather than a
        // second component going unstyled.
        Assert.Equal(40f, first.Root.Children[0].Width, 0.5f);
        Assert.Equal(40f, second.Root.Children[0].Width, 0.5f);
    }

    [Fact]
    public void Two_components_with_the_same_short_name_do_not_share_a_scope() {
        // ⚠ From the full name, not the short one. Two `Row`s in two namespaces are two components,
        // and a scope they shared would let one's styles reach the other's elements — the bug
        // scoping exists to prevent, reintroduced by the naming of the thing that prevents it.
        Assert.NotEqual(ScopedStyles.ScopeOf(typeof(Outer.Row)), ScopedStyles.ScopeOf(typeof(Inner.Row)));
        Assert.Equal(ScopedStyles.ScopeOf(typeof(Scoped)), ScopedStyles.ScopeOf(typeof(Scoped)));
    }

    [Fact]
    public void The_scope_is_welded_to_the_end_of_every_selector() {
        const string scope = "v-test";

        Assert.Equal(".row.v-test{color:red}", ScopedStyles.Scope(".row{color:red}", scope));

        // Each entry of a list, and the *rightmost* compound of each complex selector.
        Assert.Equal(".a.v-test,.b.v-test{x:1}", ScopedStyles.Scope(".a,.b{x:1}", scope));
        Assert.Equal(".a > .b.v-test{x:1}", ScopedStyles.Scope(".a > .b{x:1}", scope));

        // ⚠ Trailing whitespace trimmed first, or `.b ` becomes `.b .v-test` — a descendant selector
        // that stops matching the element the rule was written for and starts matching its children.
        Assert.Equal(".a > .b.v-test {x:1}", ScopedStyles.Scope(".a > .b {x:1}", scope));

        // A comma inside a functional pseudo-class is not a list separator.
        Assert.Equal(":is(.a, .b).v-test{x:1}", ScopedStyles.Scope(":is(.a, .b){x:1}", scope));
    }

    [Fact]
    public void An_at_rules_contents_are_scoped_and_a_keyframes_contents_are_not() {
        const string scope = "v-test";

        Assert.Equal(
            "@media (min-width: 10px){.a.v-test{x:1}}",
            ScopedStyles.Scope("@media (min-width: 10px){.a{x:1}}", scope)
        );

        // ⚠ `from`, `to` and `50%` are offsets rather than selectors. Appending a class to one
        // produces a rule that parses and never matches, and the animation quietly loses its middle.
        Assert.Equal(
            "@keyframes spin{from{x:0}to{x:1}}",
            ScopedStyles.Scope("@keyframes spin{from{x:0}to{x:1}}", scope)
        );
    }

    static class Outer {
        internal sealed class Row;
    }

    static class Inner {
        internal sealed class Row;
    }
}
