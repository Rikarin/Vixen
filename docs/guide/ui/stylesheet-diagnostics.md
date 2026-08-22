---
title: Stylesheet diagnostics
slug: ui/stylesheet-diagnostics
kind: guide
area: Core
summary: What happens to CSS Vixen cannot read — the at-rules, selectors and @apply names it drops, the build step's two refusal channels, where each refusal is now reported, and why a rule that does nothing used to be indistinguishable from a rule that was never written.
api: [L:7004, L:7005, T:Vixen.Ui.Styling.Utilities.UtilityRefusal, T:Vixen.Ui.Styling.Utilities.UtilityRefusalKind]
tags: [ui, styling, vcss, diagnostics, logging, troubleshooting, apply]
since: 0.2
status: preview
related: [ui/cascade-layers, ui/utility-composition, editor/utility-styles]
---

## What it is

Vixen's cascade recovers from a stylesheet it cannot read rather than refusing it. An at-rule it does
not implement is dropped and the rest of the sheet still applies; a selector it cannot compile
matches nothing and its neighbours still match; an `@apply` naming a utility that does not exist
contributes no declarations and the rest of the block stands. That is CSS's own error-recovery model
and it is the right one for a UI that must not disappear over a typo.

The cost of it is that **a rule which does nothing looks exactly like a rule nobody wrote.**

Two log events close that gap. Every refusal the cascade makes is now reported as it is made:

| Id | Level | What it means |
|---|---|---|
| `7004` | Warning | The stylesheet loader or the selector compiler dropped something. The message names the source, the text as it was written, and why. |
| `7005` | Warning | An `@apply` could not be expanded — a name that is not a utility, or one carrying a variant. |

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

**Every mistyped at-rule is the cheap member.** `@suports`, `@meida`, a `@container` query Vixen has
not implemented: all of them take the same path, and all of them used to vanish.

## Using it

**Give the document a logger.** `UiDocument` takes one, and a document handed none reports into
`NullLogger` — the previous silence, with an extra step.

```vcss
/* Every line below is dropped, and every one of them now says so. */
@suports (display: grid) { .card { display: grid } }   /* 7004 — not an at-rule Vixen knows */
.card >>> .body { color: red }                          /* 7004 — not a combinator Vixen supports */
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

⚠ **A dropped declaration inside a rule Vixen *did* understand is a third list and is not drained
yet.** `LayoutStyleBuilder.Diagnostics` answers a different question — what parsed as CSS and then
meant nothing to the layout, `grid-template-columns: 4furlongs` being the canonical case — and is
produced inside the per-element pass rather than at load. Tracked as issue #56; it will report on
`7004` when it lands, because it is the same event with a different source.

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
- `docs/manual/log-events.md` — the register these two ids are allocated in, and the rules that keep
  a number in a bug report meaning something.
- `Core/Vixen.Ui/StyleDiagnostics.cs` — the drain itself, and why its watermark is keyed on the
  producer rather than on a count.
