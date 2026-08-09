---
title: Cascade layers
slug: ui/cascade-layers
kind: guide
area: Core
summary: Vixen's base / components / utilities ladder — why every theme sheet opens with the same @layer statement, what a game's or a plugin's own sheet should do, and why the whole ladder has to live inside one origin.
api: [T:Vixen.Ui.Styling.CascadeLayers, T:Vixen.Ui.Styling.CascadePrecedence, T:Vixen.Ui.Styling.CascadeRanks, T:Vixen.Ui.Styling.StyleOrigin]
tags: [ui, styling, vcss, cascade, layers, utilities, tailwind]
since: 0.2
status: preview
related: [editor/utility-styles, ui/utility-composition, ui/markup-panels]
---

## What it is

A cascade layer answers "which of these two rules wins" by **where the rule lives** rather than by how
specific its selector happens to be. Vixen has three, declared in this order and in this one line:

```vcss
@layer base, components, utilities;
```

- **`base`** — what an element is before anybody styled it. The universal `box-sizing` reset and the
  engine's default design tokens, both in `ControlTheme.vcss`. Nobody's decision.
- **`components`** — what a *thing* looks like. Every control's default appearance, every editor
  panel's chrome, and a game's own stylesheet if it wants to join in. Somebody's decision.
- **`utilities`** — what the generator emits from the class names in your markup: `p-2`, `min-w-0`,
  `truncate`.

**Later wins.** A class written on an element beats the sheet that styles its tag, whatever the
selectors say, which is the whole reason to have a ladder at all.

## What it is for

Without layers, a generated `.p-4` is one class and a hand-written `.card .body` is two, so the
utility loses every argument on specificity and the only remedy is `!important` on everything the
generator emits. `CascadePrecedence` compares **origin and importance**, then **layer**, then
**specificity**, then **source order** — specificity is *third*, which is the thing most people who
write CSS have backwards and the reason a utility layer works.

⚠ **A layer only wins if something else is in a lower one.** Vixen shipped `@layer utilities` for a
release while every theme sheet in the tree was unlayered, and that is strictly *worse* than no layers
at all: an unlayered rule beats every layer, so `<Button class="p-2">` lost to `button { padding: … }`
silently and always — a fight the utility would have won on specificity if neither had mentioned
layers. The ten theme sheets moved into `components`, and the utility layer started meaning something.

## Using it

**In a game's or a plugin's own stylesheet**, open with the statement and put your rules in
`components`:

```vcss
@layer base, components, utilities;

@layer components {
    .quest.ready > quest-title { color: #4c9e4c; }
}
```

Two things that line does, and only one of them is obvious. It puts your rules below your utilities,
so `class="p-2"` works in your markup the way it works in the engine's. And it **fixes the order**:
`CascadeLayers` gives a layer its position the first time anything names it and never moves it
afterwards, so a sheet that opened `@layer components { … }` and said nothing else would sort wherever
the load order happened to put it — after `utilities`, if a generated sheet reached the document
first. Every theme sheet in the tree restates the same statement for that reason, and re-declaring a
layer is a no-op. `Samples/14-Mmo/Mmo.Ui/Theme/hud.vcss` is the worked example.

**An unlayered author sheet still beats the entire engine**, and that is deliberate rather than an
oversight. A game's sheet loads as `StyleOrigin.Author`, the engine's ladder is all
`StyleOrigin.UserAgent`, and origin is compared before layer — so restyling a button is one rule that
names `button`, and you never have to learn the engine's layer names to do it. What you give up by
staying unlayered is the ordering *within your own sheet*: your `quest-title { color: … }` will beat
your own `class="text-danger"`, which is the same defect one origin up, where nothing the engine does
can fix it for you.

**A rule that genuinely must not be overridden by a class** says `!important`. In a layered cascade
that is a precise instrument rather than a blunt one: importance **reverses** the layer order, so an
important `components` rule beats an important `utilities` one, and an important `base` rule beats
both.

## Examples

The three questions, in the order the cascade asks them. A game's sheet wins on **origin** whatever
either says about layers:

```vcss
/* the game, StyleOrigin.Author, no layer named */
task-center { min-width: 280px; }
```

```vcss
/* the editor, StyleOrigin.UserAgent */
@layer utilities { .min-w-0 { min-width: 0; } }
```

`min-width` is `280px`. Inside one origin, the **layer** decides and specificity never enters into it —
a tag selector in `components` loses to a class in `utilities`:

```vcss
@layer base, components, utilities;

@layer components { task-center { min-width: 280px; } }
@layer utilities  { .min-w-0 { min-width: 0; } }
```

`min-width` is `0`, on `<task-center class="min-w-0">`. Take the `components` wrapper away and the
answer goes back to `280px`, because an unlayered rule beats every layer — which is exactly the state
the engine's sheets were in, and exactly what
`StylesheetTests.With_the_components_layer_gone_the_hand_written_rule_wins_again` keeps measured.

## See also

- [Utility styles in the editor](../editor/utility-styles.md) — the generated sheet, and what changed
  for a panel author when the chrome moved into `components`.
- [Composed utilities](utility-composition.md) — the `--tw-*` fragments, which the cascade assembles.
- `Core/Vixen.Ui.Styling/README.md` — the four tie-breaks, the importance mirror, and why `@layer` is
  Vixen's to parse rather than ExCSS's.
