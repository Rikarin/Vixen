---
title: Utility styles
slug: editor/utility-styles
kind: guide
area: Editor
summary: Styling an editor panel with Tailwind-shaped class names, the one palette they resolve against, and which families the engine actually reads.
api: [T:Vixen.Editor.Ui.EditorStyles]
tags: [editor, styling, theming, utilities, vxml]
since: 0.2
status: preview
related: [editor/index, editor/inspectors-in-markup]
---

## What it is

`EditorStyles` is the utility half of the editor's stylesheet. It reads the editor's design tokens
from `Theming/vixen.ui.yaml`, scans the editor's embedded `.vxml` for every class name that could be
a utility, and generates a VCSS sheet — inside `@layer utilities` — containing only the rules
something in the editor actually refers to.

`EditorTheme.Install` loads it, immediately after the hand-written sheet and at the same origin, so
one call installs the whole stack.

## What it is for

`class="flex min-w-0 truncate"` on an element in an editor panel, instead of a new rule in a 1 500-line
stylesheet nobody can find the top of. The argument is the one that made Tailwind win, and it applies
more strongly to the editor than it ever did to a website: the editor has around two hundred distinct
visual components, all in one repository with all of the tokens, and there is no page-weight budget
to trade against.

You do not want it for something the hand-written sheet already styles. A generated utility is in
`@layer utilities` and `EditorTheme` is not, so **an unlayered rule wins whatever the specificity and
whatever the order** — `task-row { padding: 6px }` beats `p-3` with neither of them saying
`!important`. That is the whole reason the layer exists, and the practical consequence is worth
stating plainly: a utility takes effect on a property no rule in `EditorTheme` sets for that element.
New panels get the whole vocabulary; retro-fitting one onto chrome the sheet already styles means
deleting the hand-written rule first.

## Using it

Write the class names in the markup. Nothing else is needed — the panel's document already has the
sheet in it, because `EditorShell` installed it.

```vxml
<task-line>
    <task-title class="truncate min-w-0">@task.Title</task-title>
</task-line>
```

**One palette, not two.** Every colour in `vixen.ui.yaml` is a `var(--…)` reference to a custom
property `EditorTheme` already declares on the root, so `bg-surface` and a hand-written
`background: var(--surface)` are the same declaration. The light/dark toggle moves both, and a user's
theme file loaded through `ThemeService` moves both again. Two consequences follow: an opacity
modifier such as `bg-accent/50` silently does nothing, because the generator makes a translucent
colour by parsing a hex triple and `var(--accent)` is not one; and `rounded-panel` does not exist,
because `ThemeTokens.Radius` holds numbers and `--radius-panel` is a reference. Write `opacity-50` and
`rounded-[var(--radius-panel)]`.

**The spacing base is 2, not Tailwind's 4.** The editor's chrome is drawn on a two-pixel rhythm — 2,
4, 6, 8 and 10 all appear in `EditorTheme`, and a 6px gutter is the commonest — so `p-3` is six
pixels and every measurement the sheet already uses is a whole number of steps.

**A class name assembled at run time is invisible.** The scanner is deliberately over-inclusive and
does not parse anything, but it cannot see a name that is never written down: `$"level-{severity}"`
is `level-` and a variable. Such a name goes in `EditorStyles.Safelist`, or gets written out in full
in a switch the scanner can read.

**Which families the engine reads is not obvious, and getting it wrong is silent.** The list below is
resolved against real elements by `UtilityFamilySupportTests`; anything in the second column emits a
property no consumer in the engine looks at, so the class name is correct, the rule is generated, the
cascade computes it, and nothing happens.

| Read | Inert |
|---|---|
| `flex` / `hidden`, `flex-row`/`-col`/`-wrap`/`-1`, `items-`, `self-`, `justify-`, `content-` | `block`, `inline`, `inline-block`, `inline-flex`, `grid` — the layout's table has `flex` and `none` and nothing else |
| `grow`, `shrink`, `basis-` | `order-`, `grid-cols-`, `col-span-` — the layout is flexbox only |
| `gap-`, `gap-x-`, `gap-y-`, `p*`, `m*` including the logical `ps`/`pe`/`ms`/`me` | |
| `w-`, `h-`, `size-`, `min-w-`, `min-h-`, `max-w-`, `max-h-` | |
| `static`/`relative`/`absolute`, `inset*`, `top`/`right`/`bottom`/`left`, `start`/`end`, `z-`, `box-border`/`box-content` | |
| `text-<align>`, `text-<size>`, `text-<colour>`, `font-`, `leading-`, `tracking-`, `whitespace-` | `align-` (`vertical-align`) |
| `bg-`, `opacity-`, `shadow-`, `rounded-` | `ring-`, `fill-`, `stroke-`, `blur-`, `translate-x/y-`, `scale-`, `rotate-` |
| `border`/`border-t`/`-r`/`-b`/`-l`/`-x`/`-y`/`-s`/`-e` **widths** | the per-edge border **colours** — the draw list reads `border-top-color` only, so `border-<colour>` works and `border-b-<colour>` does not |
| `overflow-hidden`, `overflow-scroll`, `truncate` | **`overflow-x-*` and `overflow-y-*`** — nothing interns either property |
| `cursor-`, `pointer-events-`, `transition`, `duration-`, `ease-`, `aspect-` | `select-` (`user-select`) |

⚠ **`overflow-auto` is in neither column.** The draw list clips on any value that is not `visible`, so
it clips; the layout's keyword table has `visible`, `hidden` and `scroll` and not `auto`, so the
layout goes on treating the box as visible. Write `overflow-scroll` when the layout is meant to hear
about it — and note that scrolling itself is `ScrollView`, not a property.

## Examples

Compiling the sheet outside a document, which is what a "show me what my theme does" button would do,
and which is how the diagnostics are read:

```csharp no-compile="reads the editor assembly's embedded markup, which needs the editor built"
var css = EditorStyles.Compile(out var unrecognised);
```

`unrecognised` is not a diagnostic to log and forget. The scanner is over-inclusive, so most of what
lands in it is prose out of a comment — but a *misspelt* utility lands in it too, and a misspelt
utility is a style that silently does nothing, which neither the compiler nor the markup binder can
see. `StylesheetTests` asks the narrower question instead: every class name actually written in a
`class` attribute in the editor's markup is either a utility the theme can emit or a rule `EditorTheme`
wrote, and anything else is a typo.

## See also

- [The editor shell](index.md) — `EditorShell`, which installs the sheet stack.
- [Inspectors in markup](inspectors-in-markup.md) — the other place editor UI is written in VXML.
- `EditorTheme`, `ThemeService` — the hand-written half of the sheet, and the tokens both halves
  resolve against.
