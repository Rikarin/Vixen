// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Ui.Markup.Binding;
using Xunit;

namespace Vixen.Ui.Markup.Tests;

/// <summary>What the binder resolves, and the mistakes only it can see.</summary>
public class BinderTests {
    [Fact]
    public void A_lowercase_tag_is_an_element_and_an_uppercase_one_is_a_component() {
        var content = BindClean("@component A\n<div><Callout /></div>").Content;

        var div = Assert.IsType<BoundElement>(Assert.Single(content));
        Assert.False(div.IsComponent);
        Assert.True(Assert.IsType<BoundElement>(Assert.Single(div.Children)).IsComponent);
    }

    [Fact]
    public void A_using_directive_survives_to_the_bound_component() {
        var component = BindClean("@component A\n@using Vixen.Ui.Controls\n<div />");
        Assert.Equal(["Vixen.Ui.Controls"], component.Usings);
    }

    /// <summary>
    ///     An alias is joined here rather than in the emitter, so that the one place that writes
    ///     <c>using {text};</c> does not have to know an alias exists.
    /// </summary>
    [Fact]
    public void A_using_alias_reaches_the_component_as_one_string() {
        var component = BindClean("@component A\n@using Prefab = Game.Assets.Prefab\n<div />");
        Assert.Equal(["Prefab = Game.Assets.Prefab"], component.Usings);
    }

    /// <summary>
    ///     ⚠ <b>And an alias with nothing after the <c>=</c> reaches nothing at all.</b> The parser
    ///     has already reported the missing name; carrying half a directive through would turn one
    ///     diagnostic on the markup into a second one on generated C#.
    /// </summary>
    [Fact]
    public void A_using_alias_with_no_target_is_dropped_rather_than_half_emitted() {
        var component = Binder.Bind(Vxml.Parse("@component A\n@using Prefab =\n<div />"), out var diagnostics);

        Assert.NotEmpty(diagnostics.Where(d => d.IsError));
        Assert.Empty(component!.Usings);
    }

    [Fact]
    public void A_tag_directive_reaches_the_bound_component_and_its_absence_is_null() {
        Assert.Equal("task-center", BindClean("@component A\n@tag task-center\n<div />").Tag);
        Assert.Null(BindClean("@component A\n<div />").Tag);
    }

    [Fact]
    public void Code_blocks_concatenate_in_source_order() {
        var component = BindClean("@component A\n@code { int a; }\n<div />\n@code { int b; }");
        Assert.Equal([" int a; ", " int b; "], component.Code.Select(c => c.Text));
    }

    [Fact]
    public void A_style_block_reaches_the_component_with_its_scoped_flag() {
        var component = BindClean("@component A\n<div />\n<style scoped>.a{}</style>");

        Assert.Equal(".a{}", component.Css);
        Assert.True(component.CssIsScoped);
    }

    /// <summary>
    ///     Both spellings of an event binding, and the closed alias list that keeps a parameter
    ///     called <c>online</c> from being mistaken for one.
    /// </summary>
    [Theory]
    [InlineData("on:click", BoundAttributeKind.Event, "click")]
    [InlineData("onclick", BoundAttributeKind.Event, "click")]
    [InlineData("onkeydown", BoundAttributeKind.Event, "keydown")]
    [InlineData("online", BoundAttributeKind.Parameter, "online")]
    [InlineData("bind:value", BoundAttributeKind.Bind, "value")]
    [InlineData("change:Value", BoundAttributeKind.Changed, "Value")]
    [InlineData("changed", BoundAttributeKind.Parameter, "changed")]
    [InlineData("key", BoundAttributeKind.Key, "key")]
    [InlineData("title", BoundAttributeKind.Parameter, "title")]
    public void An_attribute_name_resolves_to_what_it_means(string written, BoundAttributeKind kind, string name) {
        var attribute = Assert.Single(FirstElement($"@component A\n<div {written}=\"@x\" />").Attributes);

        Assert.Equal(kind, attribute.Kind);
        Assert.Equal(name, attribute.Name);
    }

    /// <summary>
    ///     ⚠ <b>A <c>bind:</c>'s dots are event names and an <c>on:</c>'s are filter words</b>, so
    ///     nothing here checks them against a list: which events exist is the runtime's table, and a
    ///     control library adds to it. That is the same bargain an <c>on:</c> event <i>name</i> is
    ///     already emitted under, and the failure is the same one at compose.
    /// </summary>
    [Fact]
    public void A_binding_keeps_the_events_that_commit_it_in_source_order() {
        var attribute = Assert.Single(FirstElement("@component A\n<div bind:Value.submit.blur=\"@x\" />").Attributes);

        Assert.Equal(BoundAttributeKind.Bind, attribute.Kind);
        Assert.Equal("Value", attribute.Name);
        Assert.Equal(["submit", "blur"], attribute.Modifiers);
    }

    /// <summary>
    ///     And a name an <c>on:</c> would have refused is passed straight through, because it is not
    ///     one of the same words. <c>stop</c> is a filter there and an event name that does not exist
    ///     here, which the runtime says loudly rather than this side saying it wrongly.
    /// </summary>
    [Fact]
    public void A_bindings_events_are_not_checked_against_the_event_modifiers() {
        var attribute = Assert.Single(FirstElement("@component A\n<div bind:Value.stop=\"@x\" />").Attributes);

        Assert.Equal(["stop"], attribute.Modifiers);
    }

    [Fact]
    public void An_event_keeps_its_modifiers_in_source_order() {
        var attribute = Assert.Single(FirstElement("@component A\n<div on:click.stop.once=\"@Go\" />").Attributes);

        Assert.Equal("click", attribute.Name);
        Assert.Equal(["stop", "once"], attribute.Modifiers);
    }

    [Fact]
    public void A_mixed_value_binds_as_its_parts_rather_than_as_one_string() {
        var attribute = Assert.Single(FirstElement("@component A\n<div class=\"btn @kind\" />").Attributes);

        Assert.True(attribute.IsDynamic);
        Assert.Collection(
            attribute.Value,
            part => Assert.Equal("btn ", Assert.IsType<BoundLiteralPart>(part).Text),
            part => Assert.Equal("kind", Assert.IsType<BoundExpressionPart>(part).Expression.Text)
        );
    }

    [Fact]
    public void The_escape_for_a_literal_at_sign_is_decoded() {
        var content = BindClean("@component A\n<div>a @@ b</div>").Content;
        var div = Assert.IsType<BoundElement>(Assert.Single(content));

        Assert.Equal("a @ b", Assert.IsType<BoundText>(Assert.Single(div.Children)).Text);
    }

    /// <summary>
    ///     <c>else if</c> nests in the tree, because that is what it is, and flattens here, because
    ///     that is what an emitter wants.
    /// </summary>
    [Fact]
    public void An_else_if_chain_flattens_into_branches_and_one_else() {
        var @if = Assert.IsType<BoundIf>(
            Assert.Single(BindClean("@component A\n@if (a) { <x /> } else if (b) { <y /> } else { <z /> }").Content)
        );

        Assert.Equal(["a", "b"], @if.Branches.Select(branch => branch.Condition.Text));
        Assert.Single(@if.Else.OfType<BoundElement>());
    }

    [Fact]
    public void A_loop_takes_its_key_from_the_body_root() {
        var @for = Assert.IsType<BoundFor>(
            Assert.Single(BindClean("@component A\n@for (var i in xs) { <p key=\"@i\">x</p> }").Content)
        );

        Assert.Equal("i", @for.Variable);
        Assert.Equal("xs", @for.Sequence.Text);
        Assert.Equal("i", @for.Key!.Text);
    }

    /// <summary>
    ///     A key identifies one element among its siblings, so the requirement is on the roots of a
    ///     loop body — everything below moves with the root it hangs from.
    /// </summary>
    [Fact]
    public void Only_the_roots_of_a_loop_body_are_asked_for_a_key() {
        Assert.Equal(
            ["VXML2004"],
            Ids("@component A\n@for (var i in xs) { <p><span /></p> }")
        );
    }

    [Fact]
    public void A_slot_takes_its_name_and_the_second_one_of_a_name_is_reported() {
        Assert.Equal("footer", Assert.IsType<BoundSlot>(BindClean("@component A\n<slot name=\"footer\" />").Content[0]).Name);
        Assert.Equal("default", Assert.IsType<BoundSlot>(BindClean("@component A\n<slot />").Content[0]).Name);
        Assert.Equal(["VXML2005"], Ids("@component A\n<div><slot name=\"a\" /><slot name=\"a\" /></div>"));
    }

    /// <summary>The consumer's half: <c>slot="footer"</c> is an attribute of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason it is a kind rather than a parameter is that a parameter is what it
    ///     used to be.</b> Left as one, <c>&lt;p slot="footer" /&gt;</c> inside a component tag became
    ///     an assignment to a property called <c>slot</c>, so the error was Roslyn's "no such member"
    ///     on generated code, naming a problem the author could not see from the markup.
    /// </remarks>
    [Fact]
    public void A_slot_attribute_names_the_slot_a_child_fills() {
        var attribute = Assert.Single(
            BindClean("@component A\n<Shell><p slot=\"footer\" /></Shell>")
                .Content.OfType<BoundElement>()
                .Single()
                .Children.OfType<BoundElement>()
                .Single()
                .Attributes
        );

        Assert.Equal(BoundAttributeKind.Slot, attribute.Kind);
        Assert.Equal("footer", attribute.Literal);
    }

    /// <summary>What makes it legal is the parent, so what refuses it is the parent.</summary>
    /// <remarks>
    ///     ⚠ Both halves, because either alone passes against a rule that checked the wrong thing: a
    ///     rule keyed on the attribute's name refuses the legal case too, and one that checks nothing
    ///     accepts the illegal one. The second string differs from the first only in the parent tag.
    /// </remarks>
    [Fact]
    public void A_slot_attribute_belongs_on_a_component_tags_own_child() {
        Assert.Empty(Ids("@component A\n<Shell><p slot=\"footer\" /></Shell>"));
        Assert.Equal(["VXML2016"], Ids("@component A\n<div><p slot=\"footer\" /></div>"));

        // ⚠ A grandchild is refused too, and refused once — by its own parent rather than by the
        // component two levels up, which is the element the author has to move it out of.
        Assert.Equal(["VXML2016"], Ids("@component A\n<Shell><div><p slot=\"footer\" /></div></Shell>"));
    }

    /// <summary>A region's body is not a direct child either, and it was accepted and dropped.</summary>
    /// <remarks>
    ///     ⚠ <b>The half the position rule missed, and it failed silently rather than loudly.</b>
    ///     <c>RefuseSlotAttributes</c> walked an element's own <c>ElementSyntax</c> children, so a
    ///     <c>slot</c> written inside an <c>@if</c> under a component tag was reported by nobody —
    ///     and the emitter's partition reads the attribute off <c>BoundElement</c> children, which a
    ///     <c>BoundIf</c> is not, so the whole region including the slotted child went to the default
    ///     host. Bound clean, ran, and drew the header's content in the body.
    /// </remarks>
    [Fact]
    public void A_slot_attribute_inside_a_control_flow_region_is_refused() {
        Assert.Equal(["VXML2016"], Ids("@component A\n<Shell>@if (x) { <p slot=\"footer\" /> }</Shell>"));
        Assert.Equal(["VXML2016"], Ids("@component A\n<Shell>@if (x) { } else { <p slot=\"footer\" /> }</Shell>"));
        Assert.Equal(
            ["VXML2016"],
            Ids("@component A\n<Shell>@for (var i in xs) { <p key=\"@i\" slot=\"footer\" /> }</Shell>")
        );

        Assert.Equal(
            ["VXML2016"],
            Ids("@component A\n<Shell>@switch (x) { case 1: <p slot=\"footer\" /> }</Shell>")
        );

        // ⚠ Once, not twice. A region inside a plain tag is swept by the region, and the tag's own
        // sweep sees an `IfSyntax` rather than an element — so neither reports the other's children.
        Assert.Equal(["VXML2016"], Ids("@component A\n<div>@if (x) { <p slot=\"footer\" /> }</div>"));

        // ⚠ And the top level, which was the third place it was accepted and dropped. A component's
        // own markup builds into its root; there is no parent above it to publish a name.
        Assert.Equal(["VXML2016"], Ids("@component A\n<p slot=\"footer\" />"));
    }

    /// <summary>The name is read once, so it has to be one that cannot change.</summary>
    /// <remarks>
    ///     ⚠ <b>An interpolated name was read as the <i>default</i> slot.</b> The emitter asks the
    ///     attribute for its literal and falls back to <c>default</c> when there is none, which is
    ///     right for the bare <c>slot</c> an author writes to mean the default and wrong for
    ///     <c>slot="@Which"</c> — where it silently put the content somewhere the author did not ask
    ///     for. Both halves here: the bare one still binds, the expression does not.
    /// </remarks>
    [Fact]
    public void A_slot_name_has_to_be_a_literal() {
        Assert.Equal(["VXML2018"], Ids("@component A\n<Shell><p slot=\"@Which\" /></Shell>"));
        Assert.Equal(["VXML2018"], Ids("@component A\n<Shell><p slot=\"foot@Which\" /></Shell>"));
        Assert.Empty(Ids("@component A\n<Shell><p slot /></Shell>"));
        Assert.Empty(Ids("@component A\n<Shell><p slot=\"footer\" /></Shell>"));
    }

    /// <summary>Content inside a slot was discarded in silence, and is now refused out loud.</summary>
    [Fact]
    public void A_slot_cannot_carry_fallback_content() {
        Assert.Equal(["VXML2017"], Ids("@component A\n<slot name=\"footer\">Nothing yet</slot>"));
        Assert.Empty(Ids("@component A\n<slot name=\"footer\" />"));
    }

    /// <summary>
    ///     A component whose whole markup sits inside an <c>@if</c> builds something, and an
    ///     emptiness check that only looked at the top level would say it did not.
    /// </summary>
    [Fact]
    public void Markup_that_only_exists_inside_control_flow_still_counts_as_markup() {
        Assert.Empty(Ids("@component A\n@if (x) { <div /> }"));
        Assert.Empty(Ids("@component A\n@switch (x) { case 1: <div /> }"));
        Assert.Empty(Ids("@component A\n@for (var i in xs) { <div key=\"@i\" /> }"));
    }

    [Theory]
    [InlineData("", "VXML2001")]
    [InlineData("@component A\n<div a=\"1\" a=\"2\" />", "VXML2002")]
    [InlineData("@component A\n<div on:click=\"go\" />", "VXML2003")]
    [InlineData("@component A\n<div bind:value=\"x\" />", "VXML2003")]
    [InlineData("@component A\n<div at:click=\"@Go\" />", "VXML2006")]
    [InlineData("@component A\n<div on:click.stopp=\"@Go\" />", "VXML2007")]

    // ⚠ `slot-` with nothing after it is not a slot modifier. A nameless slot reaches
    // `NamedHost("")`, which every control answers with null, so the run-time "publishes no slot
    // named ''" would be a worse report than this one — and it would arrive a build later.
    [InlineData("@component A\n<Foldout on:click.slot-=\"@Go\" />", "VXML2007")]
    [InlineData("@component A\n<Callout data-id=\"1\" />", "VXML2008")]
    [InlineData("@component A\n@code { int a; }", "VXML2009")]
    [InlineData("@component A\n<div><self on:click=\"@Go\" /></div>", "VXML2015")]
    [InlineData("@component A\n@if (x) { <self on:click=\"@Go\" /> }", "VXML2015")]
    [InlineData("@component A\n@for (var i in xs) { <self key=\"@i\" on:click=\"@Go\" /> }", "VXML2015")]
    public void The_structural_mistakes_a_C_sharp_compiler_would_never_see(string source, string expected) =>
        Assert.Contains(expected, Ids(source));

    /// <summary>
    ///     A slot modifier is carried like any other and read by the emitter, which is what keeps
    ///     the binder out of the business of knowing what a control's parts are.
    /// </summary>
    [Fact]
    public void A_slot_modifier_is_accepted_and_carried() {
        var attribute = FirstElement("@component A\n<Foldout on:dragstart.slot-header.stop=\"@Go\" />")
            .Attributes.Single();

        Assert.Equal(BoundAttributeKind.Event, attribute.Kind);
        Assert.Equal("dragstart", attribute.Name);
        Assert.Equal(["slot-header", "stop"], attribute.Modifiers);
    }

    /// <summary>
    ///     ⚠ <b><c>&lt;self /&gt;</c> at the top level is the whole of where it is allowed, and the
    ///     loop case is why the rule is an error.</b> It emits against the host, so a copy inside an
    ///     <c>@for</c> subscribes the same element once per row — and the duplicate count follows
    ///     the data, so it is right in the test with two rows and wrong in the panel with forty. It
    ///     also needs no <c>key</c>, because it creates nothing for a reconciler to keep.
    /// </summary>
    [Fact]
    public void Self_belongs_at_the_top_level_and_needs_no_key() {
        Assert.Empty(Ids("@component A\n<self on:click=\"@Go\" />\n<div />"));

        // Not VXML2011 either — the loop's missing-key rule is about elements a reconciler has to
        // keep, and `<self />` is not one.
        Assert.DoesNotContain("VXML2011", Ids("@component A\n@for (var i in xs) { <self on:click=\"@Go\" /> }"));
    }

    /// <summary>
    ///     <c>class</c> is universal: it names style classes, which a component's root element has
    ///     as much as a <c>&lt;div&gt;</c> does. It is also a C# keyword, so it could not be a
    ///     parameter even if one wanted it to be.
    /// </summary>
    [Fact]
    public void Class_on_a_component_is_not_a_parameter_name_error() =>
        Assert.Empty(Ids("@component A\n<Callout class=\"warn\" />"));

    /// <summary>
    ///     A parameter may be a <i>path</i>, because the control library has properties that are
    ///     objects — a button's icon is <c>LeadingIcon.Geometry</c> and there is no flat name for
    ///     it. Whether the path exists is Roslyn's question; what is checked here is only that it
    ///     will parse.
    /// </summary>
    [Theory]
    [InlineData("@component A\n<IconButton LeadingIcon.Geometry=\"@Icons.Close\" />")]
    [InlineData("@component A\n<Callout Kind=\"warn\" />")]
    public void A_parameter_may_name_a_property_path(string source) => Assert.Empty(Ids(source));

    [Theory]
    [InlineData("@component A\n<Callout LeadingIcon..Geometry=\"@X\" />")]
    [InlineData("@component A\n<Callout data-id=\"1\" />")]
    public void A_parameter_that_could_not_be_written_in_C_sharp_is_refused(string source) =>
        Assert.Contains("VXML2008", Ids(source));

    // ================================================================== @inherits and ref

    [Fact]
    public void An_inherits_directive_reaches_the_bound_component_and_its_absence_is_null() {
        Assert.Equal("Panel", BindClean("@component A\n@inherits Panel\n<div />").Inherits!.Text);
        Assert.Equal("Vixen.Ui.Controls.Control", BindClean("@component A\n@inherits Vixen.Ui.Controls.Control\n<div />").Inherits!.Text);
        Assert.Null(BindClean("@component A\n<div />").Inherits);
    }

    /// <summary>
    ///     A <c>ref</c> is a reference to a member, so it takes an expression for the same reason
    ///     <c>key</c> and <c>on:</c> do — and the value goes to Roslyn untouched.
    /// </summary>
    [Fact]
    public void A_ref_is_an_expression_and_a_quoted_name_is_refused() {
        var attribute = Assert.Single(FirstElement("@component A\n<div ref=\"@Tree\" />").Attributes);

        Assert.Equal(BoundAttributeKind.Ref, attribute.Kind);
        Assert.Equal("Tree", attribute.Expression!.Text);
        Assert.Contains("VXML2003", Ids("@component A\n<div ref=\"Tree\" />"));
    }

    /// <summary>
    ///     ⚠ Refused at every depth of the body, not only at its roots. The body runs once per item
    ///     and there is one member to assign, and a <c>ref</c> three elements down is assigned as
    ///     many times as one on the root.
    /// </summary>
    [Theory]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\" ref=\"@Row\" /> }")]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\"><span ref=\"@Row\" /></p> }")]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\">@if (i > 0) { <b ref=\"@Row\" /> }</p> }")]
    public void A_ref_inside_a_loop_is_refused(string source) => Assert.Contains("VXML2010", Ids(source));

    [Fact]
    public void A_ref_outside_a_loop_is_fine_including_under_an_if() {
        Assert.Empty(Ids("@component A\n<div ref=\"@Panel\" />"));
        Assert.Empty(Ids("@component A\n@if (x) { <div ref=\"@Panel\" /> }"));
    }

    /// <summary>
    ///     <c>refs</c> is an expression for <c>ref</c>'s reason, and it is what <c>VXML2010</c> now
    ///     points a reader at.
    /// </summary>
    [Fact]
    public void A_refs_is_an_expression_and_a_quoted_name_is_refused() {
        var loop = Assert.IsType<BoundFor>(
            Assert.Single(BindClean("@component A\n@for (var i in xs) { <p key=\"@i\" refs=\"@Rows\" /> }").Content)
        );

        var row = Assert.Single(loop.Body.OfType<BoundElement>());
        var attribute = Assert.Single(row.Attributes.Where(a => a.Kind == BoundAttributeKind.Refs));

        Assert.Equal("refs", attribute.Name);
        Assert.Equal("Rows", attribute.Expression!.Text);
        Assert.Contains("VXML2003", Ids("@component A\n@for (var i in xs) { <p key=\"@i\" refs=\"Rows\" /> }"));
    }

    /// <summary>
    ///     ⚠ The mirror of <c>VXML2010</c>, and refused at every depth for the mirror reason: a
    ///     <c>refs</c> handle is keyed on the loop's identity, and outside a loop there is none.
    /// </summary>
    [Theory]
    [InlineData("@component A\n<div refs=\"@Rows\" />")]
    [InlineData("@component A\n@if (x) { <div refs=\"@Rows\" /> }")]
    public void A_refs_outside_a_loop_is_refused(string source) => Assert.Contains("VXML2013", Ids(source));

    /// <summary>Accepted at every depth of a loop body, which is where <c>ref</c> is refused.</summary>
    [Theory]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\" refs=\"@Rows\" /> }")]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\"><span refs=\"@Rows\" /></p> }")]
    [InlineData("@component A\n@for (var i in xs) { <p key=\"@i\">@if (i > 0) { <b refs=\"@Rows\" /> }</p> }")]
    public void A_refs_inside_a_loop_is_fine(string source) => Assert.Empty(Ids(source));

    /// <summary>
    ///     A <c>change:</c> names a property and takes an expression, and — unlike <c>on:</c> — has
    ///     no modifier list, because there is no route to stop and no leg to listen on.
    /// </summary>
    [Fact]
    public void A_change_is_an_expression_naming_a_property() {
        var attribute = Assert.Single(FirstElement("@component A\n<Dial change:Ratio=\"@Set\" />").Attributes);

        Assert.Equal(BoundAttributeKind.Changed, attribute.Kind);
        Assert.Equal("Ratio", attribute.Name);
        Assert.Equal("Set", attribute.Expression!.Text);
        Assert.Empty(attribute.Modifiers);
        Assert.Contains("VXML2003", Ids("@component A\n<Dial change:Ratio=\"Set\" />"));
    }

    /// <summary>An unknown directive still names the ones there are, all three of them.</summary>
    [Fact]
    public void An_unknown_directive_is_refused_and_names_the_three_that_exist() {
        _ = Binder.Bind(Vxml.Parse("@component A\n<div when:x=\"@y\" />"), out var diagnostics);

        var reported = Assert.Single(diagnostics.Where(d => d.Descriptor.Id == "VXML2006")).ToString();
        Assert.Contains("'on:', 'bind:' and 'change:'", reported, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The one rule about <c>@for</c> that is the opposite of what <c>VXML2004</c>
    ///     teaches.</b> A key that survives keeps its region and its body is not re-run, so a row
    ///     keyed on a member of the item is a row frozen at the values the item had when that key
    ///     first appeared. Syntax is all the evidence there is — whether the item holds signals is
    ///     type resolution — and this is the shape the mistake always takes.
    /// </summary>
    [Theory]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@row.Label\" /> }")]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@row.Id.Value\" /> }")]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@row?.Label\" /> }")]
    public void A_key_that_projects_the_item_is_warned_about(string source) =>
        Assert.Contains("VXML2011", Ids(source));

    /// <summary>
    ///     ⚠ <b>And deliberately silent where the evidence runs out.</b> A compound key is a correct
    ///     answer to the same problem, so a rule that guessed at anything mentioning the variable
    ///     would fire on the fix it recommends.
    /// </summary>
    [Theory]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@row\" /> }")]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@(row, generation)\" /> }")]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@rows[row]\" /> }")]
    [InlineData("@component A\n@for (var row in xs) { <p key=\"@rowIndex\" /> }")]
    public void A_key_that_is_the_item_or_a_compound_is_not_warned_about(string source) =>
        Assert.DoesNotContain("VXML2011", Ids(source));

    /// <summary>
    ///     A <c>UiElement</c> has one place for content and a <c>Component</c> has as many as it
    ///     declares, so a second name on an <c>@inherits</c> file is an element nothing can address.
    /// </summary>
    [Fact]
    public void A_named_slot_needs_a_component_and_the_default_one_does_not() {
        Assert.Contains("VXML2012", Ids("@component A\n@inherits Panel\n<slot name=\"footer\" />"));
        Assert.Empty(Ids("@component A\n@inherits Panel\n<slot />"));
        Assert.Empty(Ids("@component A\n<slot name=\"footer\" />"));
    }

    /// <summary>
    ///     ⚠ <b>Refused on a lowercase tag, because the language already says it.</b>
    ///     <c>&lt;fact-row&gt;</c> is the element name written out, so <c>&lt;div tag="fact-row"&gt;</c>
    ///     is the same tree with the answer somewhere a reader has to go and look for it — and two
    ///     ways to name one thing is how a stylesheet comes to be checked against the wrong one.
    ///     On a capitalised tag there is no other spelling, which is where it is allowed.
    /// </summary>
    [Fact]
    public void A_tag_attribute_belongs_on_a_capitalised_tag_and_nowhere_else() {
        Assert.Contains("VXML2014", Ids("@component A\n<div tag=\"fact-row\" />"));
        Assert.Empty(Ids("@component A\n<Callout tag=\"fact-row\" />"));

        var callout = FirstElement("@component A\n<Callout tag=\"fact-row\" />");
        var tag = Assert.Single(callout.Attributes.Where(a => a.Kind == BoundAttributeKind.Tag));

        Assert.Equal("fact-row", tag.Literal);
    }

    /// <summary>
    ///     A <c>use</c> is a lambda, so a quoted value is a mistake worth naming here rather than
    ///     letting Roslyn report "cannot convert string to Action&lt;T&gt;" against generated code.
    /// </summary>
    [Fact]
    public void A_use_wants_an_expression() {
        Assert.Contains("VXML2003", Ids("@component A\n<Roster use=\"Inspect\" />"));

        var roster = FirstElement("@component A\n<Roster use=\"@(v => v.Inspect())\" />");
        var use = Assert.Single(roster.Attributes.Where(a => a.Kind == BoundAttributeKind.Use));

        // Parentheses and all — `@(…)` hands the binder what is between the `@` and the end of the
        // expression, and a parenthesised lambda still converts at the call.
        Assert.Equal("(v => v.Inspect())", use.Expression!.Text);
    }

    /// <summary>
    ///     And on a plain element, which is what makes it shape 5's escape as well: an element's own
    ///     <c>Text</c> has no other markup spelling.
    /// </summary>
    [Fact]
    public void A_use_is_allowed_on_a_plain_element() {
        Assert.Empty(Ids("@component A\n<fact-name use=\"@(cell => cell.Text = Name)\" />"));
    }

    static BoundComponent BindClean(string source) {
        var component = Binder.Bind(Vxml.Parse(source), out var diagnostics);

        Assert.Empty(diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return component!;
    }

    static BoundElement FirstElement(string source) =>
        BindClean(source).Content.OfType<BoundElement>().First();

    static ImmutableArray<string> Ids(string source) {
        _ = Binder.Bind(Vxml.Parse(source), out var diagnostics);
        return [.. diagnostics.Select(d => d.Descriptor.Id)];
    }
}
