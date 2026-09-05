---
title: Exit animations
slug: ui/exit-animations
kind: guide
area: Core
summary: A row removed from a keyed list can stay in the document long enough to animate away. Written exit="200ms" on the row, where its key already is; the runtime adds a class and holds the elements for the interval stated, the row's bindings die the moment it is let go of, and a key that comes back mid-flight ends the old row rather than standing beside it.
api: [T:Vixen.Ui.Composition.ExitSpec]
tags: [ui, reactivity, animation, vxml]
since: 0.2
status: preview
related: [ui/markup-panels, ui/reactive-collections]
---

## What it is

`ExitSpec` is how long a subtree stays in the document after the thing that built it stopped wanting
it, and what marks it while it does. It is the fifth argument to `BuildContext.For`, which is what a
keyed loop compiles to:

```csharp no-compile="a fragment; `Rows` is the panel below and `ctx` the build context"
ctx.For(
    null,
    () => Rows.Value,
    static row => row.Id,
    static (inner, parent, row) => inner.Element(parent, "li").Text = row.Title,
    new ExitSpec(TimeSpan.FromMilliseconds(200))
);
```

In a `.vxml` it is one attribute on the row, written where the row's `key` already is:

```html
@for (var row in Rows.Value) {
    <li key="@row.Id" exit="200ms">@row.Title</li>
}
```

A row the sequence stops containing is given the class the spec names — `leaving` unless it says
otherwise — and is kept where it was for `Duration`, measured on `UiDocument.Now`. The author writes
the transition against that class in a stylesheet, the way every other transition in this framework
is written:

```css
.row { opacity: 1; transition: opacity 200ms }
.row.leaving { opacity: 0 }
```

## What it is for

Everything a list does when it changes and nothing arrives instantly: a notification that slides out,
a row that collapses as it is deleted, a chip that fades when its filter is dropped.

⚠ **The entering half of that was always free and the leaving half was impossible.** An element that
arrives can animate from a class the cascade applies on its first frame, because it is in the document
to be cascaded over. An element that leaves has to still be in the document while it does — and
`Region.Clear`, which every `@for` row and every `@if` arm ends through, removed synchronously with
nothing anywhere able to delay it. Transitions, `@keyframes` and springs were all real, clock-driven
and used in the same documents; the one thing they could not do was see a row go.

## Using it

**The duration is stated rather than discovered, and that is a decision.** The obvious alternative is
to add the class, let the cascade resolve, and hold the elements until the animator reports nothing
running on them. It reads better and it is the wrong instrument: nothing has cascaded at the moment a
row leaves, so on that first frame "nothing is running" is the answer for a row whose transition is
about to start *and* for a row that has none. Waiting a frame to tell them apart makes removal depend
on when a style pass happened to run. A number the author states is deterministic and is the same
number already written in the stylesheet.

**A leaving row's bindings are dead.** They are disposed at the moment the row is let go of, not when
its elements go, because the row's item is already gone from the sequence — an effect that survived
would spend the fade reading a signal about something that no longer exists. What is on screen during
the exit is the last frame the model ever produced.

**A leaving row keeps its place.** It stays in the region's order, so the rows below it are positioned
after it and it fades where it stood rather than jumping to the end of the list to die.

⚠ **A key that comes back mid-exit ends the old row rather than reviving it.** Reviving is not
available — the bindings are gone — and letting both stand would put two subtrees in the document
under one identity, which is the one failure an exit can introduce that nothing else in the runtime
defends against. So the arriving row removes the leaving one at once and is built fresh. It snaps
rather than crossfading.

**The document owns the interval, so a host that does not call `UiDocument.Tick` never finishes an
exit.** That is the same requirement transitions and `@keyframes` already have, and it fails the same
way: not instantly, but stuck.

### In markup

⚠ **`exit` goes on the row's element and not on the `@for` header, and `key` is why.** An exit is a
property of the *loop* rather than of the element it is written on — which argues for the header —
and so is `key`, which has been written on the row's own element since the language had loops. A
second convention for the second member of the same pair would be the language disagreeing with
itself. Written where it is, the interval sits beside the identity it reconciles against and beside
the class list the transition is written for.

⚠ **The value is a literal, which no other directive on that list is.** `key`, `ref`, `use` and
`context-menu` all take expressions, because what they name has to be computed per row. A duration
cannot depend on the row — it is the same number already in `transition: opacity 200ms` — so it is
written the same way and read when the file is compiled:

| Written | Means |
|---|---|
| `exit="200ms"` | 200 milliseconds, class `leaving` |
| `exit="0.2s"` | the same |
| `exit="320ms closing"` | 320 milliseconds, class `closing` |
| `exit="200"` | **VXML2025** — a bare number is refused, on CSS's rule |
| `exit="@Duration"` | **VXML2025** — there is nothing per-row for an expression to read |

Two more refusals, both of which would otherwise be silence:

- **`VXML2024` — `exit` outside an `@for`.** The interval is the reconciler's, and an `@if` arm that
  is swapped out is cleared rather than reconciled. Left as an ordinary attribute the word `exit`
  would have gone into the style tree as selector data and the build would have called that success.
- **`VXML2026` — `exit` in a loop that also declares an index.** `BuildContext.For`'s indexed
  overload takes no `ExitSpec`: a leaving row is no longer in the sequence, so what its index signal
  should read while it animates out was never decided. Refused rather than dropped, because an
  `exit` that compiled and did nothing is a row that vanishes — which is indistinguishable from not
  having written it.

## Examples

A list whose deletions collapse, asserted the way this repository asserts anything time-shaped — on
frames given to the document's own clock, never on elapsed wall time:

```csharp no-compile="a fragment; the test body, against the `Rows` panel above"
using var document = new UiDocument(200f, 200f);
var panel = BuildContext.Build<Rows>(document, document.Root);

panel.Items.Value = ["a", "b", "c"];
document.Effects.Flush();

panel.Items.Value = ["a", "c"];
document.Effects.Flush();

// Still there, marked, and still between the rows it was between.
Assert.Equal(["a", "b", "c"], Texts(panel.Root));
Assert.True(panel.Root.Children[1].HasClass("leaving"));

document.Tick(TimeSpan.FromMilliseconds(120));
Assert.Equal(["a", "b", "c"], Texts(panel.Root));

document.Tick(TimeSpan.FromMilliseconds(200));
Assert.Equal(["a", "c"], Texts(panel.Root));
```

Naming a different class, for a panel with two lists that leave differently:

```csharp no-compile="a fragment"
new ExitSpec(TimeSpan.FromMilliseconds(320), "closing")
```

The same list in markup, with the stylesheet it is written against:

```html
@for (var chip in Filters.Value) {
    <span class="chip" key="@chip.Id" exit="320ms closing">@chip.Label</span>
}
```

```css
.chip { opacity: 1; transition: opacity 320ms }
.chip.closing { opacity: 0 }
```

⚠ The number appears twice — once here and once in the stylesheet — and that is the cost the stated
duration buys. When they disagree the failure is benign in one direction (the row is removed
mid-fade) and invisible in the other (it sits at its final appearance for the remainder). Neither is
a wrong picture that looks right.

Omitting the spec is the old behaviour exactly — the row is gone on the flush that removed it — and
that is the default, because deferring every removal would change what "the row is gone" means for
every caller in the tree.

## See also

- [Markup panels](/docs/guide/ui/markup-panels) — what `@for` compiles to, and the key rule that
  decides which row survives a change.
- [Reactive collections](/docs/guide/ui/reactive-collections) — the collection a keyed loop is usually
  reading.
