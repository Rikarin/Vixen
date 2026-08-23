---
title: Reactive collections
slug: ui/reactive-collections
kind: guide
area: Core
summary: The two collections that live in the signal graph as one node each — a list whose changes are reported one at a time for the keyed @for reconciler, and a map written in place so a counter arriving every frame costs nothing.
api: [T:Vixen.Ui.Reactive.CollectionSignal`1, T:Vixen.Ui.Reactive.CollectionChange, T:Vixen.Ui.Reactive.CollectionChangeKind, T:Vixen.Ui.Reactive.SignalDictionary`2]
tags: [ui, reactivity, signals, collections, vxml]
since: 0.2
status: preview
related: [ui/markup-panels]
---

## What it is

Two collections that are themselves nodes in the signal graph: `CollectionSignal<T>`, which is an
`IReadOnlyList<T>`, and `SignalDictionary<TKey, TValue>`, which is an
`IReadOnlyDictionary<TKey, TValue>`. Reading either one inside a binding subscribes to it; mutating
either one notifies whatever read it, without the collection being replaced.

That second half is the whole reason they exist, because the two obvious alternatives each fail in
one direction.

**A `Signal<List<T>>` is silently dead.** The signal compares the value it is given with the value it
holds, the list is the same instance either way, and so a write that appended a row propagates
nothing. This is the single commonest way a hand-built model draws its first answer for ever.

**A `Signal<ImmutableDictionary<K, V>>` is correct and costs a copy.** Replacing the map *is* the
notification, which works — and rebuilds a balanced tree's spine every time one number moves. At a
dozen entries nobody notices. At a per-frame counter, or at a thousand entries, it is the allocation
you are chasing.

## What it is for

**`CollectionSignal<T>` is what `@for` binds to**, and it earns its keep through a change log rather
than through the subscription. A plain signal can say that a list changed and nothing more, so the
only correct response is to reconcile the whole thing — which for the hierarchy panel of a scene with
ten thousand entities is the difference between appending one row and rebuilding ten thousand. So
mutations are recorded as `CollectionChange` entries in a bounded ring, and a reconciler reads the
ones it has not seen with `TryGetChangesSince`.

⚠ **The log being bounded is the interesting decision.** A consumer that has not reconciled for
`ChangeLogCapacity` changes is told to resynchronise from scratch rather than the collection
retaining an unbounded history on its behalf. For a UI drained every frame that never happens; for
one that has been off-screen for a minute, a full rebuild is the cheaper answer anyway.

**`SignalDictionary<TKey, TValue>` is for values that arrive by name and keep arriving** — live
counters, a per-key cache the interface shows, a set of named overrides. The remote inspector's
counter pane is the case that asked for it: a build reports its frame rate on every poll, and the
map it lands in used to be rebuilt to say so.

**When you do not want either.** A collection that is genuinely rebuilt wholesale — a snapshot
projected from something else once per change, which most panels' rows are — is better as a
`Signal<ImmutableArray<T>>`. The reconciler's keys come from the projection, the equality check is a
sequence comparison the signal can make, and there is nothing to mutate in place.

## Using it

⚠ **The dependency is the whole collection, for both of them.** Reading `Count`, one item, one key,
`ContainsKey`, or enumerating — all of it records one edge, to the collection. So a binding that read
`counters["fps"]` is woken when `counters["draws"]` is written. That over-approximates the dependency
and can never under-approximate it: the cost is a re-run, never a stale answer, and a re-run that
computes the same string stops there because equality blocks the propagation one level up. Nothing
here is per-key or per-index, and a design that needs that granularity needs a signal per key, held
by whatever owns the keys.

⚠ **Equality still stops propagation, per element and per key.** Writing a value the comparer already
agrees with does nothing at all — no version bump, no notification, no effect run. This is what makes
a per-frame poll affordable: a build reporting an unchanged number costs the panel nothing. For a
mutable reference value the default comparer is the wrong answer, for the reason `Signal<T>` gives,
and the answer is the same — hold an immutable value, or pass `SignalComparer.Never<T>()` and accept
that every write propagates.

⚠ **Reading subscribes; peeking does not.** `CollectionSignal<T>.Peek()` hands back a
`ReadOnlySpan<T>` and `SignalDictionary<TKey, TValue>.TryPeek` reads one entry, both without
recording an edge. Use them for the read an effect wants the current value of but does not want to be
woken by — and *only* for that, because a binding that peeks is a binding that never updates.

⚠ **`Keys`, `Values` and `Peek()` are live views, not copies.** Reading the property records the
dependency, and enumerating what it returned some frames later reads whatever the collection holds
then — or throws, if it was written in between. Project inside the binding; do not store the view.

⚠ **Writes queue. They do not run.** A mutation marks dependents dirty and evaluates nothing, so the
effects it wakes run when `EffectScheduler.Flush` says so and not on the line that wrote the key.
That is ADR-007 and neither collection is an exception to it.

⚠ **Both assert `ReactiveGraph.OwningThread`,** once it is set. A plug-in that reports a counter from
a worker thread throws where the mistake was made rather than corrupting an edge list and failing
somewhere unrelated three frames later.

⚠ **`@for` cannot bind to a dictionary, and that is not a gap.** A dictionary's order is its hashing,
so a pane of live numbers bound directly to one would reorder itself as values arrived. Project it to
a sorted sequence inside the binding and key the rows on the whole row — a counter that moves from
59.5 to 61.25 must be a *different* key, or the row goes on showing the first reading. That is also
why `SignalDictionary` has no change log where `CollectionSignal` does: the projection is what gets
reconciled, so a map's log would be written on every update and read by nobody.

⚠ **The two names are the wrong way round from each other, and it is the analyzer's call.**
`DictionarySignal` is what symmetry asks for; CA1710 refuses it, because a type implementing
`IReadOnlyDictionary<TKey, TValue>` has to end in `Dictionary` or `Collection`. `CollectionSignal<T>`
escapes the same rule only because `IReadOnlyList<T>` is not on its list. Suppressing the analyzer was
the other option and is not worth it — the suffix is what tells a caller that `foreach` over the thing
yields `KeyValuePair`.

## Examples

A list a reconciler follows. `Move` is a distinct operation rather than a remove followed by an
insert because for the reconciler they are not the same thing: a move keeps the element, its focus and
its scroll position, and a remove-then-insert destroys and rebuilds it.

```csharp compile
using Vixen.Ui.Reactive;

public static class Outline {
    public static int Follow(CollectionSignal<string> rows) {
        var cursor = rows.Revision;

        rows.Add("Camera");
        rows.Insert(0, "Root");
        rows.Move(1, 0);

        // False means the caller has fallen further behind than the log goes and has to rebuild
        // from the current contents rather than replay.
        if (!rows.TryGetChangesSince(cursor, out var changes)) {
            return -1;
        }

        var moves = 0;

        foreach (var change in changes) {
            if (change.Kind == CollectionChangeKind.Moved) {
                moves++;
            }
        }

        return moves;
    }
}
```

A map written in place. Every path here is a hash lookup and a store; nothing allocates, and the
second write of the same reading notifies nobody.

```csharp compile
using Vixen.Ui.Reactive;

public static class Counters {
    public static SignalDictionary<string, double> Create() =>
        new(StringComparer.Ordinal);

    /// <summary>Called from a poll that runs every frame.</summary>
    public static void Report(SignalDictionary<string, double> counters, string name, double reading) =>
        counters[name] = reading;

    /// <summary>What a binding does: read, and be woken when it moves.</summary>
    public static string Sentence(SignalDictionary<string, double> counters) =>
        counters.TryGetValue("fps", out var fps)
            ? $"{counters.Count} counters, {fps:0.###} fps"
            : "no counters yet";
}
```

And the markup half — the projection, sorted, keyed on the whole row.

```vxml
<counter-pane>
    @for (var counter in CounterRows) {
        <counter-row key="@counter">
            <CounterLabel Text="@counter.Name" />
            <CounterValue Text="@counter.Reading" />
        </counter-row>
    }
</counter-pane>
```

## See also

* [Panels in markup](markup-panels.md) — `@for`, and the key rule that decides whether a row updates
* [Key/value lists](key-value-list.md) — the control a pane of named readings usually wants
