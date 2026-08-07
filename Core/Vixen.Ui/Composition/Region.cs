// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Composition;

/// <summary>
///     A run of a parent's children that some piece of control flow owns, and can replace.
/// </summary>
/// <remarks>
///     <para>
///         The problem regions exist to solve is <b>where</b>. An <c>@if</c> in the middle of a
///         <c>&lt;div&gt;</c> has siblings on both sides; when its condition changes, the elements it
///         builds have to land back between them. The element tree only appends, so something has to
///         know the index — and it cannot be a fixed number, because a region above may have grown
///         or shrunk since.
///     </para>
///     <para>
///         So a region knows what it comes <i>after</i>, and asks. If that is an element, the answer
///         is one past it. If it is another region, the answer is that region's end — which recurses
///         through however many empty regions sit between, and bottoms out at the start of the
///         parent. Nothing is stored that can go stale.
///     </para>
///     <para>
///         ⚠ <b>The alternative was an anchor element</b>, which is what the DOM frameworks use — a
///         comment node or an empty text node holding the place. Here it would have to be a real
///         element in all three stores, and a real element is counted by <c>:nth-child</c>. Striped
///         rows that stripe wrongly because of a hidden marker is a worse bug than this is
///         complexity.
///     </para>
/// </remarks>
sealed class Region {
    /// <summary>What this region's contents hang from.</summary>
    readonly UiElement parent;

    /// <summary>What it follows within its host: an element, a sibling region, or nothing yet.</summary>
    object? after;

    /// <summary>
    ///     The region this one is a slot of, for when it follows nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A region, not the index it had when this one was created.</b> A branch that opens a
    ///     loop item has no element in front of it, and where that item starts is not known until
    ///     the list is in its final order — so the answer has to be asked for rather than
    ///     remembered. Snapshotting it put every leading branch at index zero of the parent, in
    ///     somebody else's item.
    /// </remarks>
    readonly Region? host;

    /// <summary>
    ///     Its own top-level contents in order, each either a <see cref="UiElement" /> or a nested
    ///     <see cref="Region" />.
    /// </summary>
    readonly List<object> slots = [];

    /// <summary>What was subscribed inside it: effects, and the handlers a two-way binding added.</summary>
    readonly List<IDisposable> subscriptions = [];

    /// <summary>The regions of the elements this one built, which it also ends.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="slots" /> rather than in it, because these belong to a different
    ///     parent element.</b> A slot is a run of <i>this</i> parent's children and is what
    ///     <see cref="Start" />, <see cref="End" /> and <see cref="Reposition" /> compute indices
    ///     from; a region hanging off a <c>&lt;div&gt;</c> this region created has nothing to say
    ///     about where this region's own content sits, and counting it would move elements into
    ///     each other's runs.
    ///
    ///     ⚠ <b>What it is for is reach.</b> A region hangs off the element its content has as a
    ///     parent, so an <c>@for</c> written inside a nested <c>&lt;div&gt;</c> opens against that
    ///     div — and before this list, nothing above it pointed at it. Clearing the branch that
    ///     built the div removed the div and left every row's effects subscribed, reading signals
    ///     and assigning to elements that had left the document.
    /// </remarks>
    readonly List<Region> linked = [];

    /// <summary>Takes this region out of the table it is keyed in, when it has ended.</summary>
    /// <remarks>
    ///     ⚠ <b>The table is strong and has to be, so something has to empty it.</b>
    ///     <c>BuildContext.regions</c> holds a region per parent element, and a region owns
    ///     <see cref="IDisposable" />s — so a weak table would not end anything: an effect is held by
    ///     every signal it read, and collecting the region that tracked it turns a leak that can be
    ///     found into one that cannot. What it costs instead is this: an entry that is never removed
    ///     pins its element, and a <c>@for</c> over a churning list makes one per row.
    /// </remarks>
    readonly Action? forget;

    internal Region(UiElement parent, object? after, Region? host = null, Action? forget = null) {
        this.parent = parent;
        this.after = after;
        this.host = host;
        this.forget = forget;
    }

    /// <summary>The element this region's contents start at.</summary>
    internal int Start =>
        after switch {
            UiElement element => element.IndexInParent + 1,
            Region region => region.End,
            _ => host?.Start ?? 0
        };

    /// <summary>One past this region's last element, or its start when it is empty.</summary>
    internal int End =>
        slots.Count == 0
            ? Start
            : slots[^1] switch {
                UiElement element => element.IndexInParent + 1,
                Region region => region.End,
                _ => Start
            };

    /// <summary>The last thing in this region, or null when nothing is in it yet.</summary>
    internal object? Last => slots.Count == 0 ? null : slots[^1];

    internal void Add(UiElement element) => slots.Add(element);

    internal void Add(Region region) => slots.Add(region);

    internal void Track(IDisposable subscription) => subscriptions.Add(subscription);

    /// <summary>Makes this region responsible for one opened against an element it built.</summary>
    internal void Link(Region region) => linked.Add(region);

    /// <summary>Points this region at a new predecessor after a reorder.</summary>
    internal void Rebind(object? predecessor) => after = predecessor;

    /// <summary>Replaces this region's contents list, keeping the regions themselves.</summary>
    internal void Reorder(IEnumerable<object> ordered) {
        slots.Clear();
        slots.AddRange(ordered);
    }

    /// <summary>Takes everything this region built back out.</summary>
    /// <remarks>
    ///     ⚠ <b>Subscriptions first, elements second.</b> An effect disposed after its element is
    ///     removed may already have run against a detached element; disposing it first means the
    ///     only thing that could still write to these elements has stopped before they go.
    ///
    ///     ⚠ And <see cref="linked" /> before either, for the same reason one step further down: a
    ///     region opened against an element this one built holds effects that write to elements
    ///     inside it, and those elements go when this region's slots do.
    /// </remarks>
    internal void Clear() {
        foreach (var subscription in subscriptions) {
            subscription.Dispose();
        }

        subscriptions.Clear();

        foreach (var region in linked) {
            region.Clear();
        }

        linked.Clear();
        forget?.Invoke();

        foreach (var slot in slots) {
            switch (slot) {
                case Region region:
                    region.Clear();
                    break;
                case UiElement element when !element.IsRemoved:
                    element.Remove();
                    break;
                default:
                    break;
            }
        }

        slots.Clear();
    }

    /// <summary>Stops everything this region subscribed, and leaves its elements alone.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Clear" /> without the removal, for the callers whose elements are already
    ///     going.</b> A markup-authored <see cref="UiElement" /> disposes its composition from
    ///     <c>OnRemoved</c>, which the document raises <i>while</i> it is taking the subtree out — so
    ///     the elements below it need no removing, and removing them anyway would mean a nested
    ///     <c>Document.Remove</c> per element inside the walk that is already removing them. What does
    ///     have to stop is the effects: an effect outliving its element keeps assigning to it and
    ///     keeps it alive through its closure, which is the whole reason regions track subscriptions.
    ///
    ///     ⚠ <b>And a <see cref="Component" />'s teardown, on the same terms.</b> Everything a
    ///     component built is under its host, and whatever ended the component — the enclosing region
    ///     clearing, or the document removing the host — is already taking that host out. See
    ///     <c>BuildContext.Ended</c>.
    /// </remarks>
    internal void Stop() {
        foreach (var subscription in subscriptions) {
            subscription.Dispose();
        }

        subscriptions.Clear();

        foreach (var region in linked) {
            region.Stop();
        }

        linked.Clear();
        forget?.Invoke();

        foreach (var slot in slots) {
            if (slot is Region region) {
                region.Stop();
            }
        }
    }

    /// <summary>Moves this region's contents into its place among its siblings.</summary>
    /// <remarks>
    ///     Building appends, so a rebuilt region's elements arrive at the end of the parent. Moving
    ///     them left one at a time in order works because they stay contiguous: taking the first out
    ///     of the tail and putting it at <c>Start</c> shifts everything between right by one and
    ///     leaves the rest of the tail where it was.
    /// </remarks>
    internal void Reposition() {
        var target = Start;

        foreach (var slot in slots) {
            foreach (var element in Elements(slot)) {
                parent.Document.Move(element, target++);
            }
        }
    }

    static IEnumerable<UiElement> Elements(object slot) {
        switch (slot) {
            case UiElement element:
                yield return element;
                break;

            case Region region:
                foreach (var nested in region.slots) {
                    foreach (var element in Elements(nested)) {
                        yield return element;
                    }
                }

                break;

            default:
                break;
        }
    }
}
