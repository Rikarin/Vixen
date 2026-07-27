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

        EffectScheduler.Default.Flush();
        Assert.Equal("0", label.Text);

        component.Count.Value = 7;
        EffectScheduler.Default.Flush();
        Assert.Equal("7", label.Text);
    }

    [Fact]
    public void A_class_binding_replaces_the_class_it_set_last_time() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Counter>(document, document.Root);
        var label = component.Root.Children[0];

        EffectScheduler.Default.Flush();
        Assert.True(label.HasClass("zero"));

        component.Count.Value = 1;
        EffectScheduler.Default.Flush();

        Assert.False(label.HasClass("zero"));
        Assert.True(label.HasClass("some"));
    }

    // ------------------------------------------------------------ Switch

    [Fact]
    public void A_branch_swaps_its_subtree_when_the_arm_changes() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);
        EffectScheduler.Default.Flush();

        Assert.Equal(["before", "none", "after"], Tags(component.Root));

        component.Which.Value = 0;
        EffectScheduler.Default.Flush();
        Assert.Equal(["before", "first", "after"], Tags(component.Root));

        component.Which.Value = 1;
        EffectScheduler.Default.Flush();
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
        EffectScheduler.Default.Flush();

        component.Which.Value = 0;
        EffectScheduler.Default.Flush();

        Assert.Equal(1, component.Root.Children.Single(child => child.Tag == "first").IndexInParent);
    }

    [Fact]
    public void An_arm_with_no_content_leaves_its_siblings_adjacent() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Branching>(document, document.Root);

        component.Which.Value = -1;
        EffectScheduler.Default.Flush();

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
        EffectScheduler.Default.Flush();
        var runs = component.Runs;

        component.Which.Value = 1;
        EffectScheduler.Default.Flush();

        // The first arm's effect is gone, so touching what it read runs nothing.
        component.Label.Value = "changed";
        EffectScheduler.Default.Flush();

        Assert.Equal(runs, component.Runs);
    }

    // ------------------------------------------------------------ For

    [Fact]
    public void A_loop_builds_one_subtree_per_item() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        EffectScheduler.Default.Flush();

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
        EffectScheduler.Default.Flush();
        var b = component.Root.Children.Single(child => child.Text == "b");

        component.Items.Value = ["c", "b", "a"];
        EffectScheduler.Default.Flush();

        Assert.Same(b, component.Root.Children.Single(child => child.Text == "b"));
        Assert.Equal(["c", "b", "a"], Texts(component.Root, "item"));
    }

    [Fact]
    public void An_item_that_leaves_takes_its_element_with_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "b", "c"];
        EffectScheduler.Default.Flush();
        var b = component.Root.Children.Single(child => child.Text == "b");

        component.Items.Value = ["a", "c"];
        EffectScheduler.Default.Flush();

        Assert.True(b.IsRemoved);
        Assert.Equal(["head", "item", "item", "tail"], Tags(component.Root));
    }

    [Fact]
    public void An_inserted_item_lands_where_the_sequence_puts_it() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Listing>(document, document.Root);

        component.Items.Value = ["a", "c"];
        EffectScheduler.Default.Flush();

        component.Items.Value = ["a", "b", "c"];
        EffectScheduler.Default.Flush();

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
        EffectScheduler.Default.Flush();
        Assert.Equal(component.Root.Children.Select(child => child.StyleNode), StyleChildren(document, component.Root));

        component.Items.Value = ["c", "a", "b"];
        EffectScheduler.Default.Flush();
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
        EffectScheduler.Default.Flush();
        document.Update();

        var firstChild = Styled(component, "a");
        Assert.NotSame(firstChild, Styled(component, "b"));

        component.Items.Value = ["b", "a"];
        EffectScheduler.Default.Flush();
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
        EffectScheduler.Default.Flush();

        component.Items.Value = ["b", "a"];
        component.Open.Value = true;
        EffectScheduler.Default.Flush();

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
        EffectScheduler.Default.Flush();

        component.Open.Value = true;
        EffectScheduler.Default.Flush();

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
}
