---
title: Stylesheet diagnostics
slug: ui/stylesheet-diagnostics
kind: guide
area: Core
summary: What happens to CSS Vixen cannot read — the at-rules, selectors and @apply names it drops, the build step's two refusal channels, where each refusal is now reported, why a rule that does nothing used to be indistinguishable from a rule that was never written, and the three rules that apply and still warn.
api: [L:7004, L:7005, L:7006, L:7007, L:7009, L:7010, T:Vixen.Ui.Styling.Utilities.UtilityRefusal, T:Vixen.Ui.Styling.Utilities.UtilityRefusalKind]
tags: [ui, styling, vcss, diagnostics, logging, troubleshooting, apply]
since: 0.2
status: preview
related: [ui/cascade-layers, ui/utility-composition, editor/utility-styles, ui/document-diagnostics]
---

## What it is

Vixen's cascade recovers from a stylesheet it cannot read rather than refusing it. An at-rule it does
not implement is dropped and the rest of the sheet still applies; a selector it cannot compile
matches nothing and its neighbours still match; an `@apply` naming a utility that does not exist
contributes no declarations and the rest of the block stands. That is CSS's own error-recovery model
and it is the right one for a UI that must not disappear over a typo.

The cost of it is that **a rule which does nothing looks exactly like a rule nobody wrote.**

Three log events close that gap. Every refusal the cascade makes is now reported as it is made:

| Id | Level | What it means |
|---|---|---|
| `7004` | Warning | The stylesheet loader, the selector compiler or the layout bridge dropped something, and the text it names is the whole of what can be named. |
| `7005` | Warning | An `@apply` could not be expanded — a name that is not a utility, or one carrying a variant. |
| `7006` | Warning | The same refusal as `7004`, where what was refused is a *fragment* of a larger rule. The message names the fragment **and** the rule to go and change. |

They arrive through `ILogger`, so they land in `Vixen.Core.Diagnostics`' always-on `RingBufferSink`:
the editor's Console panel, a game's log overlay, a rolling log file, and a crash dump.

## What it is for

**Finding the rule that is not applying.** The messages themselves are not new — `StyleSheetLoader`
has answered an unknown at-rule with *"Vixen does not understand this rule"* since it was written,
and `SelectorCompiler` has named every selector it refused. What was new is a reader. Both lists sat
behind public properties that nothing outside the engine's own tests had ever looked at, so the
answer to "why is this rule doing nothing" existed, in memory, and no one could see it.

**`@apply` is the expensive member of that class.** It is expanded at install time by `UiDocument`,
which means a name it cannot resolve costs you declarations rather than a rule — and a block silently
missing three of its eight declarations is much harder to spot than a block missing entirely.

**Every mistyped at-rule is the cheap member.** `@suports`, `@meida`: all of them take the same path,
and all of them used to vanish.

> ⚠ This list used to include "a `@container` query Vixen has not implemented", and that was wrong
> twice over. ExCSS knows `@container` and hands it back as a parsed rule rather than an unknown one,
> so it never took this path and never produced a warning — it was dropped in silence. `@container`
> is implemented now; an unreadable *condition* inside one (`@container (prefers-color-scheme: dark)`)
> is refused at load with a diagnostic that does reach here. So are the two `style()` forms the
> cascade cannot keep current — a named one (`@container card style(--x: 1)`) and one mixed with a
> size feature — while the unnamed `@container style(--x: 1)`, which asks the parent, is answered.
> ⚠ Every `style()` query used to be refused as "'not all' is not a container feature": ExCSS does
> not parse the function and hands its prelude over as `not all`, so the loader reads the raw text.

## Using it

**Give the document a logger.** `UiDocument` takes one, and a document handed none reports into
`NullLogger` — the previous silence, with an extra step.

```vcss
/* Every line below is dropped, and every one of them now says so. */
@suports (display: grid) { .card { display: grid } }   /* 7004 — not an at-rule Vixen knows */
.card >>> .body { color: red }                          /* 7006 — not a combinator Vixen supports */
.card::before { content: '>' }                          /* 7006 — names `.card::before`, not `::before` */
.card { @apply p-4 hover:bg-accent notautility; }       /* 7005 twice: the variant, and the name */
```

In the editor there is nothing to wire: `Vixen.Editor.App` builds its shell with a logger over the
same ring the Console panel reads, filed under the category `Vixen.Ui.Styling` so that "the styling
is wrong" is one filter rather than a search through the editor's own chatter.

**A refusal is reported once.** The loader's and the compiler's lists accumulate for the life of the
document, and the drain keeps a watermark per producer, so installing a fourth sheet does not replay
the first sheet's problems. A reload — a resize that flips a breakpoint, a saved `.vcss` — rebuilds
both producers and the refusals that survive it are reported again, which is what makes a hot reload
that fixes one rule and breaks another legible.

**A dropped declaration inside a rule Vixen *did* understand is a third list, and it is drained
too.** `LayoutStyleBuilder.Diagnostics` answers a different question — what parsed as CSS and then
meant nothing to the layout, `grid-template-columns: 4furlongs` being the canonical case — and is
produced inside the per-element pass rather than at load, so it is drained at the end of `Update`
rather than after a load. It reports on `7004`, because it is the same event with a different
source.

⚠ **A refusal names the fragment; `7006` also names the rule.** The cascade stops on the smallest
thing it could not use — `::before`, a combinator, one declaration — and reports *that*, which is
right and is not enough on its own: a sheet with two `::before` rules used to produce two warnings
that were character-for-character identical, and neither said which rule to open. There are no line
numbers to fall back on, because the CSS parser does not carry source positions through to the nodes
the compiler walks, so the enclosing selector is the only locator there is. Where it is known you get
`7006` and the rule is in the message; where the fragment already *is* the whole rule you get `7004`
and it is named once.

The layout bridge's refusals are always `7004`. It is handed a `ComputedStyle` — interned property
and value ids, with the rule, origin, layer and specificity that produced them already resolved and
discarded — so there is no rule left to name. What it gives you instead is a text that is a locator
of its own: `grid-template-columns: 4furlongs` is the declaration as you wrote it, and greppable
across a project's sheets in a way a bare `::before` is not.

## The three events here that are not refusals

⚠ **`7009` reports a box that asked to scroll and got a clip.** `overflow: auto` and
`overflow: scroll` are understood — the layout reads both as a scroll container, so the box drops the
flex item's content-sized floor and reserves a scrollbar gutter, and the draw list clips at its edges.
What neither does is scroll. Nothing in `Vixen.Ui` moves content off that property; the one control
that scrolls is `ScrollView`, which is styled `overflow: hidden` and drives bars of its own. So a plain
element declaring `auto` is the CSS author's expectation met exactly half-way: the content is cut off,
and the half that would let a person reach it is absent — by pointer, by wheel and by keyboard, with
nothing on screen to say the rest exists.

```vcss
choice-list { max-height: 320px; overflow: auto; }   /* nineteen rows × 53 px, six of them reachable */
```

> `'choice-list' declares 'overflow: auto', and in this UI that clips and does not scroll: the box cuts
> its content off at its edges and what hangs outside it cannot be reached by pointer, wheel or
> keyboard. Put a ScrollView there, or write 'overflow: hidden' if the clip is what was meant.`

It is named by *element* — tag, `#id`, classes — rather than by declaration, because the declaration
is the same three words in every one of them and a line keyed on it would name none. One line per
distinct box, produced from the same walk that builds the layout style, so a list of a thousand rows
under one rule is one line. The cure is a `ScrollView`, and never a taller box: raising a
`max-height` moves the first unreachable row rather than reaching it (`Rikarin/Vixen#1275`). And it
is a log event only, not an entry in `UiDocument.Refusals()`: the declaration is understood and
applied, and a hot reload rolls back any sheet that adds to that ledger, so a save adding
`overflow: auto` would otherwise be undone and reported as an error (`Rikarin/Vixen#1396`).

⚠ **A `ScrollView` under a tag of its own does not get the `scroll-view` user-agent rule**, which is
where its `overflow: hidden` and `position: relative` live — so a rule keyed on that tag has to
write both, or the scrolled-off rows draw over whatever is above the view.

⚠ **`7010` is that trap reported where it happens, for every control and not only `ScrollView`.**
`tag=` on a capitalised markup tag and `Add<T>("some-tag")` are the sanctioned way to put a control
under a name a sheet already knows, but a control's own user-agent rule is keyed on its `TagName`,
so the renamed control matches none of it. The style pass resolves a bare element under the
control's own tag and names every property that resolves and that the renamed box does not have:

```vcss
choice-scroller { min-width: 420px; max-height: 320px; }   /* <ScrollView tag="choice-scroller"> */
```

> `'choice-scroller' is a <scroll-view> under a tag of its own, so it matches none of the rules for
> <scroll-view> and has none of what they declare: flex-direction, overflow, position. Restate them on
> the new tag's rule — for a scroll view the clip and the bars' anchor are among them.`

It compares *presence*, not value: a rule that restates `overflow` as anything has decided about it.
The bare probe has no parent, classes or state, so a rule under an ancestor or a `var()` the probe
cannot resolve is not counted — it errs towards silence. And it is a log event only, not an entry in
`UiDocument.Refusals()`: a hot reload rolls back any sheet that adds to that ledger, and deleting the
rule that restated a control's declarations is a legitimate edit (`Rikarin/Vixen#1327`).

⚠ **`7007` reports a rule that applied and answered *late*, which is the opposite failure and needs
saying separately.** A `container-type` makes an element answerable about its own measured box, so
the cascade depends on the layout and the layout depends on the cascade. The loop through the
container's own contents is closed for you: `container-type: inline-size` applies inline-size
containment, as CSS Containment 3 § 3.1 says, so a flex item on its content basis, a float or a
`width: max-content` that declares it is sized as if it were empty on the axis it answers. ⚠ **That
is CSS's answer and it surprises people**: such a container is as narrow as its own padding and
border unless something outside it — a width, a stretch, a `flex-grow` — gives it room.

What is left is a loop through the container's *surroundings*. A verdict that changes its height can
move it onto another flex line or into another grid track, and that line or track can give it a
different width, so the next verdict differs:

```vcss
root    { height: 100px; flex-direction: column; flex-wrap: wrap; align-content: flex-start; }
.wide   { width: 500px; height: 60px; }
.seesaw { container-type: inline-size; container-name: seesaw; }  /* stretched to its column */
.body   { height: 10px; }
@container seesaw (min-width: 400px) { .body { height: 60px; } }
```

`UiDocument` bounds that with `SettlePasses` rather than spinning, and `UiDocument.Settled` has
reported the result since the wiring landed — as a boolean about the whole document. `7007` names the
container instead, by its `container-name` where it has one, with the box it measured on the last
pass:

> `The query container 'seesaw' never settled: it measured 0×60 on the last of 3 layout passes and its
> box was still moving. Its own @container verdicts are one pass stale: its contents cannot size it,
> so what moved it is its surroundings answering what the verdict did to its height — a flex line it
> wrapped onto, a track it resized. Give it a definite inline size.`

The cure is a definite inline size on the container, or a `width: auto` in normal flow, which the
containing block sizes. The frame is drawn one pass stale until then, and this is the report of it.

## Examples

**A sheet with one bad rule in it still works, and says what it lost.** The rule below installs
cleanly, `.card` gets its colour, and one warning appears:

```vcss
@nonsense pretend { color: red }
.card { color: red }
```

> `The stylesheet loader refused '@nonsense pretend { color: red }': Vixen does not understand this
> rule. It was dropped; the rest of the stylesheet still applies, so the visible effect is a rule that
> does nothing.`

**An `@apply` with one bad name keeps the good ones.** This block gets its padding; only the second
name is refused.

```vcss
.card { @apply p-4 notautilityatall; }
```

> `An @apply could not be expanded: 'notautilityatall' is not a utility Vixen knows. The declarations
> it stood for are missing from the rule it was written in.`

**A variant is refused rather than approximated**, and this is the one refusal that is a design
decision rather than a gap. `@apply hover:bg-accent` would have to invent a rule whose selector
differs from the block it sits in, which is not what *apply this here* means. Write the hover rule.

```vcss
/* ⛔ refused, on 7005 */
.card { @apply hover:bg-accent; }

/* ✅ what to write instead */
.card:hover { @apply bg-accent; }
```

**Nothing is logged for a sheet the engine understood**, including `@layer` and `@media` — which
matters, because a channel that spoke on every load would be a channel nobody reads.

### The build step's own refusals

The cascade's refusals reach the log at run time. The *generator*'s refusals happen at build time and
have their own two channels on `UtilityGenerator`, reported into
`obj/…/<Assembly>.unrecognised.txt` and summarised on the build line.

⚠ **The split exists because one `false` was standing for two situations, and the useless one drowns
the useful one by three orders of magnitude.** `UtilityFamilies.TryResolve` returns `false` both for
"there is no such family" and for "that family has no such value", and everything it refused went
into one list — which for `Vixen.Editor.Ui` is seven thousand entries, because the scanner is
over-inclusive on purpose and reads every English word in every comment. So `bg-clip-text`, a real
Tailwind class against a root Vixen registers, sat among them unmarked. Indistinguishable from a typo
is the failure mode.

| | |
|---|---|
| `Unrecognised` | The candidate named no family at all. Prose, overwhelmingly — and a misspelt *family*, like `flexx`. |
| `Unresolved` | The candidate named a **registered** family and still emitted nothing. Each is a `UtilityRefusal` carrying the family that was consulted, the value or variant it had nothing for, and a `UtilityRefusalKind` saying which. |

```csharp no-compile="A fragment: `tokens` is whatever `ThemeTokens` the caller already has, and the trailing comment is an assertion in prose rather than code."
var generator = new UtilityGenerator(tokens);
generator.Generate(["bg-clip-text", "however"]);

// "however" is prose; "bg-clip-text" is a class whose root exists.
UtilityRefusal refusal = generator.Unresolved[0];
// refusal.Family == "bg", refusal.Detail == "clip-text", refusal.Kind == UtilityRefusalKind.Value
```

**A `UtilityRefusalKind.Variant` refusal is the same defect one field over**: the utility itself
resolved and one of its variants did not, so `wednesday:p-4` emits nothing while `p-4` is perfectly
fine. Nothing that has survived `TryResolve` is prose, so it belongs in this channel and not the
other.

⚠ **A refusal is not a to-do list entry, because it names the family that *was* consulted rather than
the one that should have been.** `UtilityFamilies.SplitName` takes the longest registered prefix, so
`rounded-ss-lg` is refused by `rounded` when what it wants is a `rounded-ss` nobody has registered.
There is no shorter prefix to retry either — `docs/plan/43-web-styling-parity.md` § F8 has the
measurement, and `ShadowedFamilyTests` re-takes it on every build.

## See also

- [Cascade layers](cascade-layers.md) — where a rule sits in the ladder, and why a rule that applies
  can still lose.
- [Utility styles](../editor/utility-styles.md) — the build step, the palette, and `@apply`'s place
  in it.
- `docs/manual/log-events.md` — the register these ids are allocated in, and the rules that keep a
  number in a bug report meaning something.
- `Core/Vixen.Ui/Containers.cs` — the container-scope walk `7007` is reported from, and why the
  containers it names are measured rather than predicted.
- `Core/Vixen.Ui.Controls/README.md` § What ScrollView reads out of the cascade — the control
  `7009` is telling you to use, and why it reads no `overflow` of its own.
- `Core/Vixen.Ui/StyleDiagnostics.cs` — the drain itself, and why its watermark is keyed on the
  producer rather than on a count.
