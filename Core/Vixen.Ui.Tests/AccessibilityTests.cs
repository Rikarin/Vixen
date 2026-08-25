// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The tree an element carries, and the one event that says it changed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both directions are asserted, on <c>CommandInvalidationTests</c>' terms and for its
///         reason.</b> "At most once per frame" is satisfied perfectly by an event that never fires,
///         so every test that counts a coalesced raise is paired with one that would fail if the
///         raise stopped happening.
///     </para>
///     <para>
///         The other half of this file is the cost claim. A tree is 10⁴ elements and almost none of
///         them declare anything, so the design is one nullable reference plus four virtual members
///         — and <see cref="An_element_that_declares_nothing_allocates_nothing" /> is what keeps that
///         true rather than aspirational.
///     </para>
/// </remarks>
public class AccessibilityTests {
    /// <summary>Counts raises, and drives the clock the way a host's frame loop does.</summary>
    sealed class Counter {
        readonly UiDocument document;
        TimeSpan clock;

        public Counter(UiDocument document) {
            this.document = document;
            document.AccessibilityInvalidated += _ => Count++;
        }

        public int Count { get; private set; }

        public bool Dirtied { get; private set; }

        public void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
            Dirtied = document.Update();
        }
    }

    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    [Fact]
    public void Fifty_mutations_in_one_tick_raise_it_once() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);
        var counter = new Counter(document);

        counter.Frame();
        var before = counter.Count;

        for (var index = 0; index < 50; index++) {
            var child = view.Add("div");
            child.AccessibleName = $"item {index}";
            child.Role = AccessibleRole.ListItem;
        }

        counter.Frame();

        Assert.Equal(before + 1, counter.Count);
    }

    [Fact]
    public void A_frame_in_which_nothing_changed_raises_nothing() {
        using var document = new UiDocument(100f, 100f);

        _ = View(document.Root);
        var counter = new Counter(document);

        counter.Frame();
        var before = counter.Count;

        counter.Frame();
        counter.Frame();

        Assert.Equal(before, counter.Count);
    }

    [Fact]
    public void It_is_raised_from_the_tick_and_not_from_the_pass() {
        using var document = new UiDocument(100f, 100f);

        _ = View(document.Root);
        var counter = new Counter(document);

        counter.Frame();
        var before = counter.Count;

        // ⚠ Nothing here dirties the document — that is the whole point of the assertion below. A
        // surface hung on `Update` would go stale for exactly as long as the interface was still,
        // which is most of the time.
        document.InvalidateAccessibility();
        counter.Frame();

        Assert.Equal(before + 1, counter.Count);
        Assert.False(counter.Dirtied);
    }

    [Fact]
    public void The_focus_moving_raises_it_even_where_the_command_route_stays_put() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);

        var menu = document.Root.Add("div");
        menu.IsCommandTransparent = true;

        var item = View(menu);

        var accessibility = new Counter(document);
        var commands = 0;
        document.CommandsInvalidated += _ => commands++;

        accessibility.Frame();
        var before = accessibility.Count;
        var commandsBefore = commands;

        document.Focus(view);
        accessibility.Frame();

        Assert.Equal(before + 1, accessibility.Count);
        Assert.Equal(commandsBefore + 1, commands);

        // ⚠ **The one place the two events deliberately disagree.** Focusing into a
        // command-transparent surface cannot have changed which view answers a verb, so the command
        // route stays where it was and says nothing. A screen reader's question is the other one —
        // "what has the focus" — and the answer is now the menu item.
        document.Focus(item);
        accessibility.Frame();

        Assert.Equal(before + 2, accessibility.Count);
        Assert.Equal(commandsBefore + 1, commands);
    }

    [Fact]
    public void Adding_and_removing_an_element_raises_it() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);
        var counter = new Counter(document);

        counter.Frame();
        var before = counter.Count;

        // ⚠ The structural half, which no property setter can cover: the shape of the tree is the one
        // thing a bridge caches that nothing else would tell it about.
        var child = view.Add("div");
        counter.Frame();
        Assert.Equal(before + 1, counter.Count);

        child.Remove();
        counter.Frame();
        Assert.Equal(before + 2, counter.Count);
    }

    [Fact]
    public void A_handler_that_invalidates_again_is_honoured_on_the_next_frame() {
        using var document = new UiDocument(100f, 100f);

        _ = View(document.Root);

        var raises = 0;
        var clock = TimeSpan.Zero;

        document.AccessibilityInvalidated += invalidated => {
            raises++;

            if (raises == 1) {
                invalidated.InvalidateAccessibility();
            }
        };

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
            document.Update();
        }

        document.InvalidateAccessibility();
        Frame();
        Assert.Equal(1, raises);

        // The flag is cleared before the handlers run, so the handler's own ask survives. Clearing
        // afterwards would swallow it, leaving the interface a frame stale with nothing to notice.
        Frame();
        Assert.Equal(2, raises);

        Frame();
        Assert.Equal(2, raises);
    }

    [Fact]
    public void A_disposed_document_lets_go_of_its_subscribers() {
        var document = new UiDocument(100f, 100f);
        var element = View(document.Root);

        document.AccessibilityInvalidated += _ => element.AccessibleName = "kept alive";

        // ⚠ The field is looked up and asserted *non-null first*, because `GetField` returning null
        // would make the assertion below pass without reading anything — a renamed backing field
        // would silently turn this test into a test of nothing.
        var field = typeof(UiDocument).GetField(
            "AccessibilityInvalidated",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );

        Assert.NotNull(field);
        Assert.NotNull(field.GetValue(document));

        document.Dispose();

        // A subscriber is a control, a control reaches its subtree, and a host that kept a disposed
        // document in a field would otherwise keep the whole tree that hung off it.
        Assert.Null(field.GetValue(document));
    }

    [Fact]
    public void An_element_that_declares_nothing_allocates_nothing() {
        using var document = new UiDocument(100f, 100f);

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");

        Assert.Equal(AccessibleRole.None, first.Role);
        Assert.False(first.IsInAccessibilityTree);
        Assert.Null(first.AccessibleValue);
        Assert.Empty(first.AccessibleRelationships);

        // ⚠ The same instance from both, which is the closest a test can get to "no allocation" and
        // is exactly what would stop being true if the empty case ever built a list.
        Assert.Same(first.AccessibleRelationships, second.AccessibleRelationships);
    }

    [Fact]
    public void A_role_set_on_an_element_wins_over_the_type_and_can_be_handed_back() {
        using var document = new UiDocument(100f, 100f);

        var element = document.Root.Add("div");
        Assert.Equal(AccessibleRole.None, element.Role);

        element.Role = AccessibleRole.Navigation;
        Assert.Equal(AccessibleRole.Navigation, element.Role);
        Assert.True(element.IsInAccessibilityTree);

        // ⚠ Assigning `None` is an answer — "read straight through this" — and it is not the same as
        // taking the native role back, which is what `ClearRole` is for. On a bare element the two
        // land in the same place; on a `Button` they do not.
        element.Role = AccessibleRole.None;
        Assert.Equal(AccessibleRole.None, element.Role);

        element.Role = AccessibleRole.Navigation;
        element.ClearRole();
        Assert.Equal(AccessibleRole.None, element.Role);
    }

    [Fact]
    public void A_name_comes_from_the_declaration_then_the_label_then_the_content() {
        using var document = new UiDocument(100f, 100f);

        var caption = document.Root.Add("div");
        caption.Text = "Project name";

        var field = document.Root.Add("div");
        field.Text = "some content";

        // Content first, because there is nothing else.
        Assert.Equal("some content", field.AccessibleName);

        field.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);
        Assert.Equal("Project name", field.AccessibleName);

        field.AccessibleName = "Explicitly this";
        Assert.Equal("Explicitly this", field.AccessibleName);

        field.AccessibleName = null;
        Assert.Equal("Project name", field.AccessibleName);
    }

    [Fact]
    public void The_derived_states_are_the_elements_and_never_a_controls_to_forget() {
        using var document = new UiDocument(100f, 100f);

        var element = View(document.Root);
        document.Update();

        Assert.True((element.AccessibleState & AccessibleStates.Focusable) != 0);
        Assert.False((element.AccessibleState & AccessibleStates.Focused) != 0);

        document.Focus(element);
        Assert.True((element.AccessibleState & AccessibleStates.Focused) != 0);

        element.State |= ElementState.Disabled;
        Assert.True((element.AccessibleState & AccessibleStates.Disabled) != 0);

        // And the declared half is or'd on top rather than replacing any of it.
        element.DeclaredAccessibleState = AccessibleStates.Required;
        Assert.True((element.AccessibleState & AccessibleStates.Required) != 0);
        Assert.True((element.AccessibleState & AccessibleStates.Disabled) != 0);
    }

    [Fact]
    public void A_relation_added_twice_is_one_relation_and_an_element_cannot_relate_to_itself() {
        using var document = new UiDocument(100f, 100f);

        var tab = document.Root.Add("div");
        var panel = document.Root.Add("div");

        tab.AddAccessibleRelation(AccessibleRelation.Controls, panel);
        tab.AddAccessibleRelation(AccessibleRelation.Controls, panel);

        // ⚠ A control that re-establishes its relations when something about it changes would
        // otherwise grow a list for as long as the user held an arrow key down.
        Assert.Single(tab.AccessibleRelationships);
        Assert.Same(panel, tab.AccessibleRelationTarget(AccessibleRelation.Controls));

        Assert.Throws<ArgumentException>(() => tab.AddAccessibleRelation(AccessibleRelation.Owns, tab));

        Assert.Equal(1, tab.ClearAccessibleRelations(AccessibleRelation.Controls));
        Assert.Empty(tab.AccessibleRelationships);
        Assert.Null(tab.AccessibleRelationTarget(AccessibleRelation.Controls));
    }

    [Fact]
    public void A_detached_element_takes_a_name_without_reaching_for_a_document() {
        // Half of what markup declares is declared before the element is bound — `UiElement.Document`
        // throws for exactly that case, so the invalidation reads the field rather than the property.
        var element = new UiElement();

        element.AccessibleName = "Not in a document yet";
        element.Role = AccessibleRole.Button;

        Assert.Equal("Not in a document yet", element.AccessibleName);
        Assert.Equal(AccessibleRole.Button, element.Role);
    }
}
