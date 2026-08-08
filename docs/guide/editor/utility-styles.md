---
title: Utility styles
slug: editor/utility-styles
kind: guide
area: Editor
summary: Styling an editor panel with Tailwind-shaped class names — the build step that compiles them, the one palette they resolve against, and which families the engine actually reads.
api: [T:Vixen.Editor.Ui.EditorStyles]
tags: [editor, styling, theming, utilities, vxml]
since: 0.2
status: preview
related: [editor/index, editor/inspectors-in-markup, ui/utility-composition]
---

## What it is

`EditorStyles` is the editor's utility stylesheet, compiled at build time. `Theming/vixen.ui.vcss` is
the design tokens — an `@theme { … }` block, layered over the palette the engine ships; every source file and every `.vxml` in the assembly is scanned for class names; the
sheet — inside `@layer utilities`, containing only the rules something actually refers to — is
generated into `obj/` before the compiler runs and carried in the binary as a constant.

There is no code behind it. `EditorStyles` is a name for that constant, and the machinery is
`Core/Vixen.Ui.Styling.Utilities/build/Vixen.Ui.Styling.Utilities.targets` plus `Tools/Vixen.StyleGen`.
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

Write the class names. In markup:

```vxml
<task-line>
    <task-title class="truncate min-w-0">@task.Title</task-title>
</task-line>
```

…or in C#, which the build step scans as well:

```csharp no-compile="a fragment against a panel's own element tree"
row.AddClass("flex");
row.AddClass("items-center");
```

**A class name assembled at run time is still invisible.** The scanner does not parse anything, but it
cannot see a name that is never written down: `$"level-{severity}"` is `level-` and a variable. Such a
name goes in the project's `@(VixenStyleSafelist)` item, or gets written out in full in a switch the
scanner can read. The editor has four such sites today — `ThemeService`'s `dark`, `ConsoleView`'s and
`MessageLogView`'s `level-*`, and whatever a plugin puts in `EditorCommand.ClassName` — and not one of
them names a utility, so the safelist is empty.

**One palette, not two.** Every colour in `vixen.ui.vcss` is a `var(--…)` reference to a custom
property `EditorTheme` already declares on the root, so `bg-surface` and a hand-written
`background: var(--surface)` are the same declaration. The light/dark toggle moves both, and a user's
theme file loaded through `ThemeService` moves both again. The two limitations that used to follow
from that are gone: `bg-accent/50` emits
`color-mix(in oklab, var(--accent) 50%, transparent)` and keeps its opacity, and `rounded-panel`,
`rounded-control` and `rounded-row` are real tokens now that a radius can hold a reference rather than
only a number.

**The editor clears two of the engine's namespaces, and both are decisions.** The engine ships
Tailwind v4's default `@theme` — twenty-six colour ramps in `oklch()`, a type scale, radii,
breakpoints — so a game writing `bg-blue-500` needs no theme file at all. The editor says
`--color-*: initial;`, because its palette is designed around four surfaces and a hairline darker than
the surface it edges, and twenty-six general-purpose ramps beside that would be twenty-six ways to
write a colour that is not in it. It says `--breakpoint-*: initial;` because a tool window is not a
page: a panel is sized by the dock that holds it, so `md:` asks the wrong question. It keeps the v4
radius scale, which collides with nothing.

**The spacing base is 4, which is Tailwind's.** It was 2 until the `@theme` change, justified by the
chrome being drawn on a two-pixel rhythm — but that made `p-4` mean 8px in the editor where it means
16px everywhere else in the Tailwind world, so every measurement a designer brought with them was half
size. The chrome is being redone and the exception went with it.

**Which families the engine reads is not obvious, and getting it wrong is silent.** The list below is
resolved against real elements by `UtilityFamilySupportTests`; anything in the second column emits a
property no consumer in the engine looks at, so the class name is correct, the rule is generated, the
cascade computes it, and nothing happens.

⚠ **`block` moved columns**, and it is the first family here to do so because an *algorithm* arrived
rather than because a property found a reader. It really is block layout — children stack down the
page, fill the line across it, and their vertical margins **collapse** into each other per CSS 2.1
§8.3.1, which is the one thing a flex column will never do for you. Two cards with `mb-4` and `mt-4`
between them are 16 points apart, not 32. `inline-block` and `inline-flex` stay inert on purpose:
they differ from their block-level twins only inside an inline formatting context, and there is not
one yet.

| Read | Inert |
|---|---|
| `flex` / `hidden` / **`block`** / **`grid`**, `flex-row`/`-col`/`-wrap`/`-1`, `items-`, `self-`, `justify-`, `content-` | `inline`, `inline-block`, `inline-flex` |
| `grow`, `shrink`, `basis-`, `order-`, **`grid-cols-`**, **`col-span-`** | `grid-rows-`, `row-span-`, `col-start-`/`-end-`, `row-start-`/`-end-`, `auto-cols-`, `auto-rows-`, `grid-flow-` — the engine reads all of these properties; the families are not registered |
| `gap-`, `gap-x-`, `gap-y-`, `p*`, `m*` including the logical `ps`/`pe`/`ms`/`me` | |
| `w-`, `h-`, `size-`, `min-w-`, `min-h-`, `max-w-`, `max-h-` | |
| `static`/`relative`/`absolute`, `inset*`, `top`/`right`/`bottom`/`left`, `start`/`end`, `z-`, `box-border`/`box-content` | |
| `text-<align>`, `text-<size>`, `text-<colour>`, `font-`, `leading-`, `tracking-`, `whitespace-` | `align-` (`vertical-align`) |
| `bg-`, `opacity-`, `shadow-` | `ring-`, `fill-`, `stroke-`, `blur-`, `translate-x/y-`, `scale-`, `rotate-` |
| `rounded-`, and the per-corner `rounded-t`/`-r`/`-b`/`-l`/`-tl`/`-tr`/`-br`/`-bl` | |
| `border`/`border-t`/`-r`/`-b`/`-l`/`-x`/`-y`/`-s`/`-e`, both **widths** and **colours** | |
| `overflow-hidden`, `overflow-scroll`, `truncate` | **`overflow-x-*` and `overflow-y-*`** — nothing interns either property |
| `cursor-`, `pointer-events-`, `transition`, `duration-`, `ease-`, `aspect-` | `select-` (`user-select`) |
| | `bg-linear-*` with `from-`/`via-`/`to-` — the gradient assembles correctly and the draw list has no `background-image` channel to paint it |

⚠ **`overflow-auto` is in neither column.** The draw list clips on any value that is not `visible`, so
it clips; the layout's keyword table has `visible`, `hidden` and `scroll` and not `auto`, so the
layout goes on treating the box as visible. Write `overflow-scroll` when the layout is meant to hear
about it — and note that scrolling itself is `ScrollView`, not a property.

## Examples

Turning the step on in another project is two lines and a file. The `.targets` arrives with the
package; inside this repository it is imported by hand, for the same reason the VXML generator is.

```xml
<PropertyGroup>
    <VixenUtilityStylesClass>MyStyles</VixenUtilityStylesClass>
</PropertyGroup>

<ItemGroup>
    <!-- A run-time class name the scanner cannot see, and the hand-written sheets when they exist. -->
    <VixenStyleSafelist Include="text-rare" />
    <VixenStyleBase Include="Theme/panels.vcss" />
</ItemGroup>
```

The theme file is found rather than declared: one `**/vixen.ui.vcss` per project, and two is an error
because two would be two palettes. It is also optional — a project without one gets the engine's
shipped `@theme` and nothing else.

⚠ **The generated sheet is not the whole story about whether a class name works.** A misspelt utility
is a style that silently does nothing — neither the compiler nor the markup binder can see one,
because `class` is a string and every string parses. The build step writes every candidate it did not
recognise to `obj/…/<Assembly>.unrecognised.txt`, and with the C# scanned that file is mostly ordinary
English out of comments, so it cannot be a warning list. `StylesheetTests` asks the narrow question
instead: every class name actually written in a `class` attribute in the editor's markup is either a
utility the theme can emit or a rule `EditorTheme` wrote, and anything else is a typo.

## See also

- [The editor shell](index.md) — `EditorShell`, which installs the sheet stack.
- [Inspectors in markup](inspectors-in-markup.md) — the other place editor UI is written in VXML.
- [Composed utilities](../ui/utility-composition.md) — the families that set a `--tw-*` fragment
  instead of a declaration, and why the cascade is what assembles them.
- `EditorTheme`, `ThemeService` — the hand-written half of the sheet, and the tokens both halves
  resolve against.
