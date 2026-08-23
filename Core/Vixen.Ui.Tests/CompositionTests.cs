// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The runtime a compiled <c>.vxml</c> calls: elements, effects, branches and lists.</summary>
public class CompositionTests {
    [Fact]
    public void A_component_builds_its_elements_under_the_mount() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Panel>(document, document.Root);

        Assert.Equal("panel", component.Root.Tag);
        Assert.Same(document.Root, component.Root.Parent);
        Assert.Equal(["div"], component.Root.Children.Select(child => child.Tag));
    }

    [Fact]
    public void A_static_attribute_is_written_once_and_a_class_replaces_rather_than_appends() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Panel>(document, document.Root);
        var div = component.Root.Children[0];

        Assert.True(div.HasClass("row"));
        Assert.True(div.HasClass("lead"));
    }

    /// <summary>
    ///     One effect per dynamic expression, assigning one property. Nothing walks the tree.
    /// </summary>
    [Fact]
    public void A_bound_expression_follows_its_signal() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Counter>(document, document.Root);
        var label = component.Root.Children[0];

        document.Effects.Flush();
        Assert.Equal("0", label.Text);

        component.Count.Value = 7;
        document.Effects.Flush();
        Assert.Equal("7", label.Text);
    }

    [Fact]
    public void A_class_binding_replaces_the_class_it_set_last_time() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Counter>(document, document.Root);
        var label = component.Root.Children[0];

        document.Effects.Flush();
        Assert.True(label.HasClass("zero"));

        component.Count.Value = 1;
        document.Effects.Flush();

        Assert.False(label.HasClass("zero"));
        Assert.True(label.HasClass("some"));
    }

    // ------------------------------------------------------------ Switch

    [Fact]
    public void A_branch_swaps_its_subtree_when_the_arm_changes() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);
        document.Effects.Flush();

        Assert.Equal(["before", "none", "after"], Tags(component.Root));

        component.Which.Value = 0;
        document.Effects.Flush();
        Assert.Equal(["before", "first", "after"], Tags(component.Root));

        component.Which.Value = 1;
        document.Effects.Flush();
        Assert.Equal(["before", "second", "after"], Tags(component.Root));
    }

    /// <summary>
    ///     The reason regions exist. A branch in the middle of its siblings has to come back to the
    ///     middle, and the element tree only appends.
    /// </summary>
    [Fact]
    public void A_rebuilt_branch_lands_back_between_its_siblings() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);
        document.Effects.Flush();

        component.Which.Value = 0;
        document.Effects.Flush();

        Assert.Equal(1, component.Root.Children.Single(child => child.Tag == "first").IndexInParent);
    }

    [Fact]
    public void An_arm_with_no_content_leaves_its_siblings_adjacent() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);

        component.Which.Value = -1;
        document.Effects.Flush();

        Assert.Equal(["before", "after"], Tags(component.Root));
    }

    /// <summary>
    ///     ⚠ An effect that outlived its branch would keep the branch's elements alive through its
    ///     closure and keep assigning to elements that are no longer in the document.
    /// </summary>
    [Fact]
    public void A_branch_that_leaves_takes_its_effects_with_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);

        component.Which.Value = 0;
        document.Effects.Flush();
        var runs = component.Runs;

        component.Which.Value = 1;
        document.Effects.Flush();

        // The first arm's effect is gone, so touching what it read runs nothing.
        component.Label.Value = "changed";
        document.Effects.Flush();

        Assert.Equal(runs, component.Runs);
    }

    /// <summary>
    ///     ⚠ <b>And the effects a branch left one level down, which is where a branch puts them.</b>
    ///     An <c>@if</c> whose arm is a <c>&lt;div&gt;</c> with a loop inside it opens that loop's
    ///     region against the div, not against the arm — so the arm's own region never contained it,
    ///     and clearing the arm removed the div while every row went on reading signals.
    /// </summary>
    /// <remarks>
    ///     No component anywhere in this: the same defect that made a component leak is a property
    ///     of how a region finds its parent, and it reaches plain control flow first.
    ///     <c>A_branch_that_leaves_takes_its_effects_with_it</c> missed it by writing its effect at
    ///     the top of the arm, which is the one place the arm's region does cover.
    /// </remarks>
    [Fact]
    public void A_branch_that_leaves_stops_the_effects_one_level_inside_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<DeepBranching>(document, document.Root);

        component.Items.Value = ["a", "b"];
        document.Effects.Flush();

        var runs = component.RowRuns;
        Assert.True(runs >= 2);

        component.Shown.Value = false;
        document.Effects.Flush();

        component.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, component.RowRuns);
    }

    // ------------------------------------------------------------ For

    [Fact]
    public void A_loop_builds_one_subtree_per_item() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        document.Effects.Flush();

        Assert.Equal(["head", "item", "item", "item", "tail"], Tags(component.Root));
        Assert.Equal(["a", "b", "c"], Texts(component.Root, "item"));
    }

    /// <summary>
    ///     The whole reason keys exist: an item that is still there keeps its element, and therefore
    ///     its focus, its scroll offset and whatever else lives on it.
    /// </summary>
    [Fact]
    public void An_item_that_survives_a_change_keeps_the_element_it_had() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        document.Effects.Flush();
        var b = component.Root.Children.Single(child => child.Text == "b");

        component.Items.Value = ["c", "b", "a"];
        document.Effects.Flush();

        Assert.Same(b, component.Root.Children.Single(child => child.Text == "b"));
        Assert.Equal(["c", "b", "a"], Texts(component.Root, "item"));
    }

    [Fact]
    public void An_item_that_leaves_takes_its_element_with_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        document.Effects.Flush();
        var b = component.Root.Children.Single(child => child.Text == "b");

        component.Items.Value = ["a", "c"];
        document.Effects.Flush();

        Assert.True(b.IsRemoved);
        Assert.Equal(["head", "item", "item", "tail"], Tags(component.Root));
    }

    /// <summary>
    ///     The same one level down inside a row, which is the version that grows without bound: a
    ///     list that churns builds a region per row per nested element, and a row whose effects are
    ///     never stopped is one the list can never be rid of.
    /// </summary>
    [Fact]
    public void An_item_that_leaves_stops_the_effects_one_level_inside_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<DeepRows>(document, document.Root);

        component.Items.Value = ["a", "b"];
        document.Effects.Flush();

        var runs = component.CellRuns;
        Assert.True(runs >= 2);

        component.Items.Value = ["a"];
        document.Effects.Flush();

        var kept = component.CellRuns;
        component.Ping.Value++;
        document.Effects.Flush();

        // One row left, so exactly one cell effect may run. Two would mean the dropped row is still
        // subscribed.
        Assert.Equal(kept + 1, component.CellRuns);
    }

    /// <summary>
    ///     ⚠ A <c>refs</c> inside a branch inside a row files under that row's key, and the whole arm
    ///     is built.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The defect this pins down was silent in the worst way. <c>For</c> sets the iteration
    ///         key around the <i>synchronous</i> build of a new region and restores it in a
    ///         <c>finally</c>; <c>Switch</c> registers its own effect, which the scheduler runs after
    ///         that — so <c>Refs</c> found no key, threw the "only meaningful inside an @for" message
    ///         its own remark says nothing generated can reach, and the throw abandoned the arm's
    ///         builder wherever it happened. The row survived with the element created on the line
    ///         above the <c>refs</c> and nothing after it: no classes, no bindings, no children.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The markup compiler cannot catch it</b>: <c>VXML2013</c> reads the markup
    ///         lexically, and a <c>refs</c> in an <c>@if</c> arm inside a <c>@for</c> <i>is</i> inside
    ///         a loop. So the assertion has to be here. Wave 7's <c>ShapeVocabularyView</c> is where
    ///         it was met — seven empty boxes where the list should have been.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_refs_inside_a_branch_inside_a_row_is_filed_under_the_rows_key() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<BranchingRefs>(document, document.Root);

        component.Items.Value = ["a", "b"];
        document.Effects.Flush();

        // The arm builds whole: the element after the `refs` is there and says what it was told.
        Assert.Equal(["row", "row"], Tags(component.Root));
        Assert.Equal(["detail"], Tags(component.Root.Children[0]));
        Assert.Equal("a", component.Root.Children[0].Children[0].Text);

        // And the handle answers under the loop's own key rather than throwing on the way in.
        Assert.True(component.Cells.TryGet("a", out var first));
        Assert.Same(component.Root.Children[0].Children[0], first);

        Assert.True(component.Cells.TryGet("b", out var second));
        Assert.Same(component.Root.Children[1].Children[0], second);

        // Swapping the arm re-files the new element under the same key and drops the old one.
        component.Detailed.Value = false;
        document.Effects.Flush();

        Assert.Equal(["brief"], Tags(component.Root.Children[0]));
        Assert.True(component.Cells.TryGet("a", out var swapped));
        Assert.Same(component.Root.Children[0].Children[0], swapped);
    }

    [Fact]
    public void An_inserted_item_lands_where_the_sequence_puts_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "c"];
        document.Effects.Flush();

        component.Items.Value = ["a", "b", "c"];
        document.Effects.Flush();

        Assert.Equal(["a", "b", "c"], Texts(component.Root, "item"));
    }

    /// <summary>
    ///     A reorder has to reach the style tree too, or <c>:nth-child</c> and the sibling
    ///     combinators keep answering for the order the list used to be in — striped rows that
    ///     stripe wrongly, long after the list stopped changing.
    /// </summary>
    /// <remarks>
    ///     Asserted against the style store rather than against a resolved style, so that it is the
    ///     store saying where the element is and not a cascade that might have agreed by accident.
    /// </remarks>
    [Fact]
    public void A_reorder_moves_the_element_in_the_style_tree_as_well() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        document.Effects.Flush();
        Assert.Equal(component.Root.Children.Select(child => child.StyleNode), StyleChildren(document, component.Root));

        component.Items.Value = ["c", "a", "b"];
        document.Effects.Flush();
        Assert.Equal(component.Root.Children.Select(child => child.StyleNode), StyleChildren(document, component.Root));
        Assert.Equal(["c", "a", "b"], Texts(component.Root, "item"));
    }

    /// <summary>
    ///     ⚠ The child arena and <c>IndexInParent</c> are two different facts, and a move that
    ///     updated only the first would leave <c>:first-child</c> matching the element that used to
    ///     be first. Asserted through the cascade because that is the only thing that reads the
    ///     field.
    /// </summary>
    [Fact]
    public void A_reorder_moves_what_nth_child_reads_and_not_only_the_child_list() {
        using var document = new UiDocument(200f, 200f);
        document.Load("item { color: rgb(9, 9, 9); } item:first-child { color: rgb(1, 2, 3); }");

        var component = BuildContext.Build<Bare>(document, document.Root);
        component.Items.Value = ["a", "b"];
        document.Effects.Flush();
        document.Update();

        var firstChild = Styled(component, "a");
        Assert.NotSame(firstChild, Styled(component, "b"));

        component.Items.Value = ["b", "a"];
        document.Effects.Flush();
        document.Update();

        Assert.Same(firstChild, Styled(component, "b"));
        Assert.NotSame(firstChild, Styled(component, "a"));
    }

    /// <summary>
    ///     Control flow inside a loop item, which is the only thing that reads an item region's
    ///     position. A reorder that moved the elements but left the region chain pointing at the
    ///     old neighbours would rebuild the branch into somebody else's item.
    /// </summary>
    [Fact]
    public void A_branch_inside_a_reordered_item_rebuilds_inside_that_item() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Nested>(document, document.Root);

        component.Items.Value = ["a", "b"];
        document.Effects.Flush();

        component.Items.Value = ["b", "a"];
        component.Open.Value = true;
        document.Effects.Flush();

        Assert.Equal(["b", "mark", "a", "mark"], component.Root.Children.Select(Label));
    }

    /// <summary>
    ///     The same, with the branch first in the item — which is the case that actually reads the
    ///     item region's own position, because an empty region has no last element to follow.
    /// </summary>
    [Fact]
    public void A_branch_that_opens_an_item_rebuilds_inside_that_item() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<LeadingBranch>(document, document.Root);

        component.Items.Value = ["a", "b"];
        document.Effects.Flush();

        component.Open.Value = true;
        document.Effects.Flush();

        Assert.Equal(["mark", "a", "mark", "b"], component.Root.Children.Select(Label));
    }

    // ------------------------------------------------------------ Events and slots

    [Fact]
    public void An_event_binding_runs_its_handler_and_a_modifier_changes_what_happens() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Clickable>(document, document.Root);
        var button = component.Root.Children[0];

        button.Raise(new TapEvent { Count = 1 });
        Assert.Equal(1, component.Clicks);

        // `once` unsubscribed itself after the first one.
        button.Raise(new TapEvent { Count = 1 });
        Assert.Equal(1, component.Clicks);
    }

    /// <summary>
    ///     The markup compiler cannot catch this — it does not know what events the runtime has —
    ///     so the runtime does, loudly. A silently ignored <c>on:clcik</c> is a control that does
    ///     nothing for a reason nobody can see.
    /// </summary>
    [Fact]
    public void An_unknown_event_name_is_refused_rather_than_ignored() {
        using var document = new UiDocument(200f, 200f);

        var thrown = Assert.Throws<ArgumentException>(() => BuildContext.Build<BadEvent>(document, document.Root));
        Assert.Contains("clcik", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Children_are_projected_into_the_slot_the_component_declared() {
        using var document = new UiDocument(200f, 200f);
        var outer = BuildContext.Build<Outer>(document, document.Root);

        var host = outer.Root.Children.Single(child => child.Tag == "host");
        var box = host.Children.Single();
        var slot = box.Children.Single();

        Assert.Equal("slot", slot.Tag);
        Assert.Equal(["guest"], slot.Children.Select(child => child.Tag));
    }

    // ------------------------------------------------------------ Element tags

    /// <summary>
    ///     A capitalised tag may name an element type as well as a component, because the control
    ///     library is made of elements and the markup compiler resolves no types — so the two are
    ///     written identically and the C# compiler is what tells them apart.
    /// </summary>
    [Fact]
    public void A_capitalised_tag_that_names_an_element_type_builds_the_element() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Mixed>(document, document.Root);

        var gauge = Assert.IsType<Gauge>(component.Root.Children[0]);

        // Its own tag, not the type's name in lower case: `gauge-bar { … }` is what a stylesheet
        // for it says, exactly as it would for one built by hand.
        Assert.Equal("gauge-bar", gauge.Tag);
    }

    [Fact]
    public void An_element_tags_bindings_and_children_land_on_the_element_itself() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Mixed>(document, document.Root);
        var gauge = (Gauge) component.Root.Children[0];

        document.Effects.Flush();
        Assert.Equal(1, gauge.Level);

        component.Level.Value = 9;
        document.Effects.Flush();
        Assert.Equal(9, gauge.Level);

        // A component's children are projected into its slot; an element's children are its own.
        Assert.Equal(["mark"], Tags(gauge));
    }

    /// <summary>
    ///     The same teardown a component gets. An element created by a tag is registered against
    ///     the region that built it, so a branch leaving takes it with it — the alternative is a
    ///     control that stays in the tree after the <c>@if</c> that made it stopped being true.
    /// </summary>
    [Fact]
    public void An_element_tag_inside_a_branch_leaves_with_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Switching>(document, document.Root);

        document.Effects.Flush();
        Assert.Equal(["gauge-bar"], Tags(component.Root));

        component.Shown.Value = false;
        document.Effects.Flush();
        Assert.Empty(Tags(component.Root));
    }

    /// <summary>
    ///     A default taken from the type's name cannot produce a hyphen, and every compound tag in
    ///     the control library has one.
    /// </summary>
    [Fact]
    public void A_component_may_name_its_own_host_tag() {
        using var document = new UiDocument(200f, 200f);

        Assert.Equal("panel", BuildContext.Build<Panel>(document, document.Root).Root.Tag);
        Assert.Equal("mixed-up", BuildContext.Build<Mixed>(document, document.Root).Root.Tag);
    }

    // ------------------------------------------------------------ Teardown

    /// <summary>
    ///     ⚠ <b>A component's own build hangs off its host rather than off the branch that made
    ///     it</b>, so a <c>Clear</c> that removed the host used to leave everything inside it
    ///     running — assigning to elements that were no longer in the document. The plain-element
    ///     case above has been tested since regions existed; this is the same claim for the case
    ///     the markup channel actually produces.
    /// </summary>
    [Fact]
    public void A_component_inside_a_branch_takes_its_effects_with_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Hosting>(document, document.Root);

        document.Effects.Flush();
        var runs = Guest.Runs;

        component.Shown.Value = false;
        document.Effects.Flush();

        Assert.DoesNotContain(component.Root.Children, child => child.Tag == "guest");

        // Nothing it built is still watching anything.
        Guest.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, Guest.Runs);
    }

    /// <summary>
    ///     What the runtime gave, the runtime takes back; what a component subscribed to itself is
    ///     the component's, and <c>OnUnmounted</c> is where it says so.
    /// </summary>
    [Fact]
    public void A_component_is_told_when_it_leaves_and_can_still_see_what_it_built() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Hosting>(document, document.Root);

        document.Effects.Flush();
        Assert.Equal(0, Guest.Unmounts);

        component.Shown.Value = false;
        document.Effects.Flush();

        Assert.Equal(1, Guest.Unmounts);

        // The hook runs before the teardown, so a component saving a scroll offset or a selection
        // has something left to read.
        Assert.Equal(["box"], Guest.SawOnUnmount);
    }

    /// <summary>
    ///     ⚠ <b>The same claim for a loop the component wrote <i>inside</i> one of its own
    ///     elements</b>, which is where a real panel writes one — a <c>@for</c> is almost never a
    ///     component's first statement, it is inside the <c>&lt;div&gt;</c> that is its body.
    /// </summary>
    /// <remarks>
    ///     A region hangs off the element its content has as a parent, so this loop's region is
    ///     keyed on the <c>&lt;box&gt;</c> and not on the host. Before the fix, clearing the host's
    ///     region removed the box and left every row's effect subscribed — the loop stopped
    ///     reconciling and the rows went on assigning to elements that had left the document.
    ///
    ///     ⚠ Counted, not inferred from how the tree looks. Assigning to a removed element does not
    ///     complain, so "the elements are gone" would pass with the teardown deleted. What a leaked
    ///     effect actually does is <i>run</i>.
    /// </remarks>
    [Fact]
    public void A_component_stops_the_effects_inside_a_loop_written_in_a_nested_element() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<DeepHosting>(document, document.Root);

        document.Effects.Flush();
        var runs = DeepGuest.RowRuns;
        Assert.True(runs >= 2);

        component.Shown.Value = false;
        document.Effects.Flush();

        DeepGuest.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, DeepGuest.RowRuns);

        // ⚠ Once. Two things can end a component now — the region that built it and the removal of
        // its host — and both happen here, in that order. They share one spent-on-first-call
        // teardown so that `OnUnmounted` is not raised twice.
        Assert.Equal(1, DeepGuest.Unmounts);
    }

    /// <summary>
    ///     And the control that makes the nesting the cause rather than loops in general: the same
    ///     component with the loop written at its top level opens its region against the host, which
    ///     the teardown has always reached.
    /// </summary>
    [Fact]
    public void A_loop_at_a_components_top_level_opens_against_the_host_and_was_never_the_gap() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<ShallowHosting>(document, document.Root);

        document.Effects.Flush();
        var runs = ShallowGuest.RowRuns;
        Assert.True(runs >= 2);

        component.Shown.Value = false;
        document.Effects.Flush();

        ShallowGuest.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, ShallowGuest.RowRuns);
    }

    /// <summary>
    ///     ⚠ <b>And removing the host is an unmount, which it was not.</b> A component tracks its
    ///     teardown against the region that <i>built</i> it, so a root component — one built onto a
    ///     mount rather than inside a branch — had nothing above it to be cleared and went on
    ///     running for as long as the document lived. Every markup panel in the editor is mounted
    ///     that way, which made this the shape the leak actually took in practice rather than the
    ///     branch case.
    /// </summary>
    [Fact]
    public void Removing_a_root_components_host_stops_what_it_built() {
        using var document = new UiDocument(200f, 200f);
        DeepGuest.Reset();

        var component = BuildContext.Build<DeepGuest>(document, document.Root);
        document.Effects.Flush();

        var runs = DeepGuest.RowRuns;
        Assert.True(runs >= 2);

        component.Root.Remove();

        DeepGuest.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, DeepGuest.RowRuns);

        // And it was told, on the same terms the branch case gets: before anything was detached, so
        // a panel saving a scroll offset still has its elements to read.
        Assert.Equal(1, DeepGuest.Unmounts);
        Assert.Equal(["box"], DeepGuest.SawOnUnmount);
    }

    /// <summary>
    ///     ⚠ <b>A <see cref="Component" /> inside a composed element, which is the case the
    ///     element-flavoured teardown was never tested against.</b> <c>Compose</c> used to stop by
    ///     walking every region in its context; a component's teardown is a subscription in one of
    ///     them and it took entries <i>out</i> of that table as it ran, so the correctness of the
    ///     whole thing rested on <c>Dictionary</c> tolerating a removal mid-enumeration. It does,
    ///     which is why nothing caught fire — but the reach it needed is now a link rather than a
    ///     walk, and the table is not enumerated at all.
    /// </summary>
    [Fact]
    public void A_composed_element_stops_a_component_it_built_one_level_inside_it() {
        using var document = new UiDocument(200f, 200f);

        DeepGuest.Reset();
        var composed = document.Root.Add<Composed>(null, null, default);
        document.Effects.Flush();

        var runs = DeepGuest.RowRuns;
        Assert.True(runs >= 2);

        composed.Remove();

        DeepGuest.Ping.Value++;
        document.Effects.Flush();

        Assert.Equal(runs, DeepGuest.RowRuns);
        Assert.Equal(1, DeepGuest.Unmounts);
    }

    // ------------------------------------------------------------ Fixtures

    static IReadOnlyList<string> Tags(UiElement element) => [.. element.Children.Select(child => child.Tag)];

    static IReadOnlyList<string> Texts(UiElement element, string tag) =>
        [.. element.Children.Where(child => child.Tag == tag).Select(child => child.Text ?? string.Empty)];

    /// <summary>The parent's children as the style store lists them, in its order.</summary>
    static Styling.ComputedStyle Styled(Bare component, string text) =>
        component.Root.Children.Single(child => child.Text == text).Style;

    static string Label(UiElement element) => element.Text ?? element.Tag;

    static List<Styling.StyleNodeId> StyleChildren(UiDocument document, UiElement parent) {
        var tree = document.Styles.Tree;
        var children = new List<Styling.StyleNodeId>();

        for (var i = 0; i < tree.GetChildCount(parent.StyleNode); i++) {
            children.Add(tree.GetChild(parent.StyleNode, i));
        }

        return children;
    }

    sealed class Panel : Component {
        protected override void Build(BuildContext ctx) {
            var div = ctx.Element(null, "div");
            ctx.Attribute(div, "class", "row lead");
        }
    }

    sealed class Counter : Component {
        public Signal<int> Count { get; } = new(0);

        protected override void Build(BuildContext ctx) {
            var label = ctx.Text(null, () => Count.Value);
            ctx.Bind(label, "class", () => Count.Value == 0 ? "zero" : "some");
        }
    }

    sealed class Branching : Component {
        public Signal<int> Which { get; } = new(2);
        public Signal<string> Label { get; } = new("start");
        public int Runs { get; private set; }

        protected override void Build(BuildContext ctx) {
            ctx.Element(null, "before");

            ctx.Switch(
                null,
                () => Which.Value,
                (inner, parent, arm) => {
                    switch (arm) {
                        case 0: {
                            var first = inner.Element(parent, "first");
                            inner.Bind(() => {
                                // ⚠ Counted *before* the element is touched. An effect that
                                // outlives its branch throws on the removed element and the
                                // scheduler suspends it — so counting afterwards would record a
                                // leaked effect as a clean one.
                                Runs++;
                                first.Text = Label.Value;
                            });

                            break;
                        }

                        case 1: {
                            inner.Element(parent, "second");
                            break;
                        }

                        default: {
                            inner.Element(parent, "none");
                            break;
                        }
                    }
                }
            );

            ctx.Element(null, "after");
        }
    }

    sealed class Listing : Component {
        public Signal<string[]> Items { get; } = new([]);

        protected override void Build(BuildContext ctx) {
            ctx.Element(null, "head");

            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                static (inner, parent, item) => {
                    var element = inner.Element(parent, "item");
                    element.Text = item;
                }
            );

            ctx.Element(null, "tail");
        }
    }

    /// <summary>A list and nothing else, so that <c>:first-child</c> can mean an item.</summary>
    sealed class Bare : Component {
        public Signal<string[]> Items { get; } = new([]);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                static (inner, parent, item) => inner.Element(parent, "item").Text = item
            );
    }

    /// <summary>A branch whose arm is an element with a loop inside it.</summary>
    sealed class DeepBranching : Component {
        public Signal<bool> Shown { get; } = new(true);
        public Signal<string[]> Items { get; } = new([]);
        public Signal<int> Ping { get; } = new(0);
        public int RowRuns { get; private set; }

        protected override void Build(BuildContext ctx) =>
            ctx.Switch(
                null,
                () => Shown.Value ? 0 : -1,
                (inner, parent, _) => {
                    var box = inner.Element(parent, "box");

                    inner.For(
                        box,
                        () => Items.Value,
                        static item => item,
                        (deeper, host, item) => {
                            var row = deeper.Element(host, "row");

                            deeper.Bind(() => {
                                RowRuns++;
                                row.Text = item + Ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            });
                        }
                    );
                }
            );
    }

    /// <summary>A list whose rows each hold an element with a loop of their own inside it.</summary>
    sealed class DeepRows : Component {
        public Signal<string[]> Items { get; } = new([]);
        public Signal<int> Ping { get; } = new(0);
        public int CellRuns { get; private set; }

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                (inner, parent, item) => {
                    var row = inner.Element(parent, "row");

                    inner.For(
                        row,
                        static () => new[] { "one" },
                        static cell => cell,
                        (deeper, host, _) => {
                            var cell = deeper.Element(host, "cell");

                            deeper.Bind(() => {
                                CellRuns++;
                                cell.Text = item + Ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            });
                        }
                    );
                }
            );
    }

    /// <summary>A list whose items each contain a branch.</summary>
    sealed class Nested : Component {
        public Signal<string[]> Items { get; } = new([]);
        public Signal<bool> Open { get; } = new(false);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                (inner, parent, item) => {
                    inner.Element(parent, "item").Text = item;
                    inner.Switch(
                        parent,
                        () => Open.Value ? 0 : -1,
                        static (deeper, host, _) => deeper.Element(host, "mark")
                    );
                }
            );
    }

    /// <summary>A list whose items lead with a branch rather than with content.</summary>
    sealed class LeadingBranch : Component {
        public Signal<string[]> Items { get; } = new([]);
        public Signal<bool> Open { get; } = new(false);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                (inner, parent, item) => {
                    inner.Switch(
                        parent,
                        () => Open.Value ? 0 : -1,
                        static (deeper, host, _) => deeper.Element(host, "mark")
                    );

                    inner.Element(parent, "item").Text = item;
                }
            );
    }

    sealed class Clickable : Component {
        public int Clicks { get; private set; }

        protected override void Build(BuildContext ctx) {
            var button = ctx.Element(null, "button");
            ctx.On(button, "click", () => Clicks++, "once");
        }
    }

    sealed class BadEvent : Component {
        protected override void Build(BuildContext ctx) =>
            ctx.On(ctx.Element(null, "div"), "clcik", () => { });
    }

    sealed class Host : Component {
        protected override void Build(BuildContext ctx) {
            var box = ctx.Element(null, "box");
            ctx.Slot(box, BuildContext.DefaultSlot);
        }
    }

    sealed class Outer : Component {
        protected override void Build(BuildContext ctx) {
            var host = ctx.Child<Host>(null);
            ctx.Element(host.Content, "guest");
        }
    }

    /// <summary>Stands in for a control: an element type a capitalised tag can name.</summary>
    /// <remarks>
    ///     Not a real one, because <c>Vixen.Ui.Controls</c> is downstream of this assembly. What is
    ///     being tested is the shape a control has — a <see cref="UiElement" /> with a tag of its
    ///     own and properties to bind — and not any particular control.
    /// </remarks>
    sealed class Gauge : UiElement {
        protected internal override string TagName => "gauge-bar";

        public int Level { get; set; }
    }

    sealed class Mixed : Component {
        public Signal<int> Level { get; } = new(1);

        // `protected internal`, not `protected`, only because this assembly is a friend of the one
        // that declares it. An application overriding it writes `protected override`.
        protected internal override string TagName => "mixed-up";

        protected override void Build(BuildContext ctx) {
            var gauge = ctx.Child<Gauge>(null);

            ctx.Bind(() => gauge.Level = Level.Value);
            ctx.Element(BuildContext.Inner(gauge), "mark");
        }
    }

    /// <summary>A component built inside a branch, which counts what it is asked to do.</summary>
    /// <remarks>
    ///     Static counters because the point is what happens to an instance nobody holds any more:
    ///     a field on the test would be a reference keeping it alive and would prove less.
    /// </remarks>
    sealed class Guest : Component {
        public static readonly Signal<int> Ping = new(0);
        public static int Runs;
        public static int Unmounts;
        public static string[] SawOnUnmount = [];

        protected internal override string TagName => "guest";

        protected override void Build(BuildContext ctx) {
            var box = ctx.Element(null, "box");
            ctx.Bind(() => {
                Runs++;
                box.Text = Ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        }

        protected override void OnUnmounted() {
            Unmounts++;
            SawOnUnmount = [.. Root.Children.Select(child => child.Tag)];
        }
    }

    sealed class Hosting : Component {
        public Signal<bool> Shown { get; } = new(true);

        protected override void Build(BuildContext ctx) {
            Guest.Runs = 0;
            Guest.Unmounts = 0;
            Guest.SawOnUnmount = [];

            ctx.Switch(null, () => Shown.Value ? 0 : -1, (inner, parent, _) => inner.Child<Guest>(parent));
        }
    }

    /// <summary>
    ///     A component whose loop is written inside one of its own elements, which is where a panel
    ///     writes one.
    /// </summary>
    /// <remarks>
    ///     Static counters for the same reason <see cref="Guest" />'s are: what is being tested is
    ///     what happens to an instance nobody holds, and a field on the test would be a reference
    ///     keeping it alive.
    /// </remarks>
    sealed class DeepGuest : Component {
        public static readonly Signal<int> Ping = new(0);
        public static readonly Signal<string[]> Items = new([]);
        public static int RowRuns;
        public static int Unmounts;
        public static string[] SawOnUnmount = [];

        protected internal override string TagName => "deep-guest";

        public static void Reset() {
            RowRuns = 0;
            Unmounts = 0;
            SawOnUnmount = [];
            Ping.Value = 0;
            Items.Value = ["a", "b"];
        }

        protected override void OnUnmounted() {
            Unmounts++;
            SawOnUnmount = [.. Root.Children.Select(child => child.Tag)];
        }

        protected override void Build(BuildContext ctx) {
            var box = ctx.Element(null, "box");

            ctx.For(
                box,
                static () => Items.Value,
                static item => item,
                static (inner, parent, item) => {
                    var row = inner.Element(parent, "row");

                    inner.Bind(() => {
                        // ⚠ Counted before the element is touched: an effect that outlived its
                        // component throws on the removed element and the scheduler suspends it, so
                        // counting afterwards would record a leaked effect as a clean one.
                        RowRuns++;
                        row.Text = item + Ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    });
                }
            );
        }
    }

    /// <summary>The same, with the loop at the top level — the case that was never the gap.</summary>
    sealed class ShallowGuest : Component {
        public static readonly Signal<int> Ping = new(0);
        public static readonly Signal<string[]> Items = new([]);
        public static int RowRuns;

        protected internal override string TagName => "shallow-guest";

        public static void Reset() {
            RowRuns = 0;
            Ping.Value = 0;
            Items.Value = ["a", "b"];
        }

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                static () => Items.Value,
                static item => item,
                static (inner, parent, item) => {
                    var row = inner.Element(parent, "row");

                    inner.Bind(() => {
                        RowRuns++;
                        row.Text = item + Ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    });
                }
            );
    }

    /// <summary>
    ///     What an <c>@inherits</c> class compiles to, by hand: an element that composes its own
    ///     tree and disposes it from <c>OnRemoved</c>.
    /// </summary>
    sealed class Composed : UiElement {
        IDisposable? composition;

        protected internal override string TagName => "composed";

        protected internal override void OnCreated() =>
            composition = BuildContext.Compose(
                this,
                context => {
                    // Nested, and a component: the two things the composed teardown has to reach
                    // that its own slots do not contain.
                    var box = context.Element(null, "box");
                    context.Child<DeepGuest>(box);
                }
            );

        protected internal override void OnRemoved() => composition?.Dispose();
    }

    sealed class DeepHosting : Component {
        public Signal<bool> Shown { get; } = new(true);

        protected override void Build(BuildContext ctx) {
            DeepGuest.Reset();
            ctx.Switch(null, () => Shown.Value ? 0 : -1, (inner, parent, _) => inner.Child<DeepGuest>(parent));
        }
    }

    sealed class ShallowHosting : Component {
        public Signal<bool> Shown { get; } = new(true);

        protected override void Build(BuildContext ctx) {
            ShallowGuest.Reset();
            ctx.Switch(null, () => Shown.Value ? 0 : -1, (inner, parent, _) => inner.Child<ShallowGuest>(parent));
        }
    }

    sealed class Switching : Component {
        public Signal<bool> Shown { get; } = new(true);

        protected override void Build(BuildContext ctx) =>
            ctx.Switch(
                null,
                () => Shown.Value ? 0 : -1,
                (inner, parent, _) => inner.Child<Gauge>(parent)
            );
    }

    /// <summary>A list whose rows each hold a branch, and a <c>refs</c> inside the branch's arm.</summary>
    /// <remarks>
    ///     ⚠ <b>The shape that was silently broken until 2026-08-23.</b> <c>For</c> sets the iteration
    ///     key around a <i>synchronous</i> build; <c>Switch</c> registers its own effect, which runs
    ///     after the key has been put back — so the <c>refs</c> found none and threw, and the throw
    ///     abandoned the rest of the arm rather than reaching anybody. What that looked like was a row
    ///     that existed with no children and no classes while the rest of the panel was correct.
    /// </remarks>
    sealed class BranchingRefs : Component {
        public Signal<string[]> Items { get; } = new([]);

        public Signal<bool> Detailed { get; } = new(true);

        public ElementRefs<UiElement> Cells { get; } = new();

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Items.Value,
                static item => item,
                (inner, parent, item) => {
                    var row = inner.Element(parent, "row");

                    inner.Switch(
                        row,
                        () => Detailed.Value ? 0 : 1,
                        (arm, cell, index) => {
                            var element = arm.Element(cell, index == 0 ? "detail" : "brief");

                            arm.Refs(Cells, element);
                            element.Text = item;
                        }
                    );
                }
            );
    }
}
