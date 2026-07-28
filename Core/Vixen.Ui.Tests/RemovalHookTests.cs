// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The hook an element gets on its way out, and what it is allowed to do in it.</summary>
/// <remarks>
///     ⚠ <b>The absence of this was a leak nothing could see.</b> An overlay is a child of the root
///     rather than of the control that opened it — forced by painting order — so removing the control
///     left the popup in the document, along with the two capture handlers <c>Overlay</c> puts on the
///     root, each closing over the removed overlay. Everything about that is invisible from the
///     screen: the popup is closed, so it draws nothing, and the only symptom is a document that
///     grows.
/// </remarks>
public class RemovalHookTests {
    sealed class Watcher : UiElement {
        public int Removals { get; private set; }

        public List<string> Order { get; } = [];

        public string Name { get; set; } = "";

        public Action<Watcher>? OnRemoving { get; set; }

        protected internal override void OnRemoved() {
            Removals++;
            Order.Add(Name);
            OnRemoving?.Invoke(this);
            base.OnRemoved();
        }
    }

    [Fact]
    public void It_is_called_once_for_every_element_in_the_subtree() {
        using var document = new UiDocument(200f, 200f);

        var parent = document.Root.Add<Watcher>();
        var child = parent.Add<Watcher>();
        var grandchild = child.Add<Watcher>();

        document.Update();
        document.Remove(parent);

        Assert.Equal(1, parent.Removals);
        Assert.Equal(1, child.Removals);
        Assert.Equal(1, grandchild.Removals);
    }

    [Fact]
    public void It_is_called_parents_first() {
        using var document = new UiDocument(200f, 200f);

        var shared = new List<string>();
        var parent = document.Root.Add<Watcher>();
        parent.Name = "parent";
        var child = parent.Add<Watcher>();
        child.Name = "child";

        parent.OnRemoving = w => shared.Add(w.Name);
        child.OnRemoving = w => shared.Add(w.Name);

        document.Update();
        document.Remove(parent);

        // ⚠ The opposite of a disposal order, and deliberate: a control's hook tears down what it
        // owns, and what it owns includes its own parts. A panel that closes its menu should run
        // before that menu is told anything.
        Assert.Equal(["parent", "child"], shared);
    }

    [Fact]
    public void The_element_is_still_in_the_document_when_it_is_called() {
        using var document = new UiDocument(200f, 200f);

        var parent = document.Root.Add<Watcher>();
        var child = parent.Add<Watcher>();
        UiElement? seenParent = null;
        var wasRemoved = true;

        child.OnRemoving = w => {
            seenParent = w.Parent;
            wasRemoved = w.IsRemoved;
        };

        document.Update();
        document.Remove(parent);

        // The whole purpose of the hook is to reach things, and a handler called after the subtree is
        // out of the stores can ask almost nothing.
        Assert.Same(parent, seenParent);
        Assert.False(wasRemoved);
    }

    [Fact]
    public void It_may_remove_something_else() {
        using var document = new UiDocument(200f, 200f);

        var owner = document.Root.Add<Watcher>();
        var elsewhere = document.Root.Add<Watcher>();

        owner.OnRemoving = _ => document.Remove(elsewhere);

        document.Update();
        document.Remove(owner);

        // The case this exists for: a select removing the popover it parented on the root.
        Assert.True(elsewhere.IsRemoved);
        Assert.Equal(1, elsewhere.Removals);
    }

    [Fact]
    public void It_may_not_remove_an_ancestor_of_what_is_being_removed() {
        using var document = new UiDocument(200f, 200f);

        var outer = document.Root.Add<Watcher>();
        var inner = outer.Add<Watcher>();

        inner.OnRemoving = _ => document.Remove(outer);

        document.Update();

        // The outer call is holding `outer` and is about to detach it; letting the inner one detach
        // it first leaves the outer one taking a node out of a parent it no longer has. Refused with
        // a message rather than left to surface as a null reference several frames later.
        var thrown = Assert.Throws<InvalidOperationException>(() => document.Remove(outer));
        Assert.Contains("ancestor", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nor_an_ancestor_further_up_than_the_element_being_removed() {
        using var document = new UiDocument(200f, 200f);

        var grandparent = document.Root.Add<Watcher>();
        var middle = grandparent.Add<Watcher>();
        var leaf = middle.Add<Watcher>();

        leaf.OnRemoving = _ => document.Remove(grandparent);

        document.Update();

        // ⚠ The transitive case, and the reason the guard walks rather than compares. Removing
        // `middle` puts *middle* in the pending list; the element the hook then asks to remove is
        // `middle`'s grandparent, which is only found by climbing. A guard that checked the pending
        // element alone would let this through, and the test above cannot tell the two apart because
        // there the pending element *is* the one being asked for.
        var thrown = Assert.Throws<InvalidOperationException>(() => document.Remove(middle));
        Assert.Contains("ancestor", thrown.Message, StringComparison.Ordinal);

        // ⚠ **And the exception alone does not distinguish them**, which cost a sabotage to find. A
        // guard that only compares the pending element lets the first attempt through, starts
        // announcing the grandparent's subtree, reaches the same leaf again, and *then* trips — on
        // the second pending entry, whose own first step does match. The exception arrives either
        // way. What differs is how much of the document was torn down before it did: the grandparent
        // must not have been told anything at all.
        Assert.Equal(0, grandparent.Removals);
        Assert.False(grandparent.IsRemoved);
    }

    [Fact]
    public void Removing_something_twice_is_quiet() {
        using var document = new UiDocument(200f, 200f);

        var owner = document.Root.Add<Watcher>();
        var shared = document.Root.Add<Watcher>();

        owner.OnRemoving = _ => document.Remove(shared);

        document.Update();
        document.Remove(shared);
        document.Remove(owner);

        // Two controls can name the same popup, and the second one to go should not throw about it.
        Assert.Equal(1, shared.Removals);
    }
}
