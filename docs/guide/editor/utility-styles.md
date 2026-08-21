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

It works on something the hand-written sheet already styles, and **it did not used to**. A generated
utility is in `@layer utilities`; `EditorTheme` used to be in no layer at all, and an unlayered rule
wins whatever the specificity and whatever the order — so `task-row { padding: 6px }` beat `p-3` with
neither of them saying `!important`, and a utility only ever took effect on a property no rule in
`EditorTheme` set for that element. Retro-fitting a class onto existing chrome began by deleting a
rule.

`EditorTheme` is now `@layer components` and the ladder is `base, components, utilities`, so
`class="p-3"` does what somebody writing it plainly meant. See
[the cascade layers page](../ui/cascade-layers.md) for the ladder and for what a rule that genuinely
must not be overridden should say instead.

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

⚠ **`block` moved columns**, and it was the first family here to do so because an *algorithm* arrived
rather than because a property found a reader. It really is block layout — children stack down the
page, fill the line across it, and their vertical margins **collapse** into each other per CSS 2.1
§8.3.1, which is the one thing a flex column will never do for you. Two cards with `mb-4` and `mt-4`
between them are 16 points apart, not 32. `inline`, `inline-block` and `inline-flex` followed it when
the inline formatting context landed, and so did `vertical-align`.

⚠ **The Inert column is short now, and every line in it is measured rather than argued.**
`UtilityConsumptionGateTests` runs the whole family surface past a real document and compares four
observables either side of each declaration; the six properties it currently reports unread —
`--blur`, the two logical border colours, `rotate`, `scale` and `user-select` — are the whole of what
is left, each with a line in `InertProperties.txt` naming the task that closes it. A row here that
disagrees with that ledger is this page being stale, not the ledger being wrong.

| Read | Inert |
|---|---|
| `flex` / `hidden` / `block` / `grid`, **`inline`**, **`inline-block`**, **`inline-flex`**, `flex-row`/`-col`/`-wrap`/`-1`, `items-`, `self-`, `justify-`, `content-` | |
| `grow`, `shrink`, `basis-`, `order-`, `grid-cols-`, `col-span-`, **`grid-rows-`**, **`row-span-`**, **`col-start-`/`-end-`**, **`row-start-`/`-end-`**, **`auto-cols-`**, **`auto-rows-`**, **`grid-flow-`**, **`justify-items-`**, **`justify-self-`** | |
| `gap-`, `gap-x-`, `gap-y-`, `p*`, `m*` including the logical `ps`/`pe`/`ms`/`me`, **`space-x-`**, **`space-y-`** | `space-x-reverse`, `space-y-reverse` — not registered; they need `calc()`, which `StyleValueParser` has not got |
| `w-`, `h-`, `size-`, `min-w-`, `min-h-`, `max-w-`, `max-h-` | |
| `static`/`relative`/`absolute`, `inset*`, `top`/`right`/`bottom`/`left`, `start`/`end`, `z-`, `box-border`/`box-content` | |
| `text-<align>`, `text-<size>`, `text-<colour>`, `font-`, `leading-`, `tracking-`, `whitespace-`, **`align-`** | `align-middle`/`-text-top`/`-text-bottom`/`-sub`/`-super` — the property is read, those five values are refused at the bridge for want of a font strut |
| `bg-`, `opacity-`, `shadow-`, `ring-`, `fill-`, `stroke-`, **`translate-x/y-`** | `blur-`, `scale-`, `rotate-` |
| `rounded-`, and the per-corner `rounded-t`/`-r`/`-b`/`-l`/`-tl`/`-tr`/`-br`/`-bl` | |
| `border`/`border-t`/`-r`/`-b`/`-l`/`-x`/`-y`, both widths and colours; `border-s`/`-e` widths | `border-s-<colour>` and `border-e-<colour>` — the logical pair never reached the draw list |
| **`divide-x-`**, **`divide-y-`**, **`divide-<colour>`** | `divide-solid`/`-dashed`/`-dotted`/`-double`, `divide-x-reverse`, `divide-y-reverse` — not registered; nothing reads `border-style`, and the reverse pair needs `calc()` |
| `overflow-hidden`, `overflow-scroll`, `overflow-auto`, **`overflow-x-*`**, **`overflow-y-*`**, `truncate` | |
| `cursor-`, `pointer-events-`, `transition`, `duration-`, `ease-`, `aspect-` | `select-` (`user-select`) |
| **`bg-linear-*`**, **`bg-radial`**, **`bg-conic`** with `from-`/`via-`/`to-` and stop positions | |

⚠ **`space-*` and `divide-*` are the only families whose rule is about the children**, and the two
things worth knowing before reaching for them are both divergences from Tailwind v4. The rule is
`.space-y-4 > :not(:last-child)`, emitted without v4's `:where()` wrapper because Vixen's selector
compiler charges a class for `:where()` as it does for `:is()` — so the rule is two classes of
specificity and beats a child's own `mb-0`, exactly as Tailwind v3 did. And `space-y-*` writes
`margin-bottom` where v4 writes `margin-block-end`, because the block longhands are interned by
nobody here and there is no writing mode for the two to differ in. `@apply space-x-4` is refused, for
the same reason `@apply hover:bg-accent` is: it is a rule with a selector of its own.

⚠ **`mix-blend-*`, `origin-*` and the `scroll-*` set are deliberately not families**, and each is a
measured verdict rather than an omission. Nothing in the engine reads `mix-blend-mode` (there is no
blend channel on a `DrawCommand` and no offscreen target to blend into) or `transform-origin` (which
needs a transform whose fixed point matters, and `translate` — the only one implemented — is
origin-independent). `scroll-m-*`, `scroll-p-*` and `scroll-behavior` are deferred rather than
refused: scrolling here is `ScrollView`, a control, so the behaviour lands first and the utilities
become properties it reads. See `docs/plan/43-web-styling-parity.md` § F9.

⚠ **`ring-*` is a `box-shadow`, not an outline, and it used to emit `outline-color` — a property no
version of Tailwind has ever emitted for it.** `ring-2` is `box-shadow: 0 0 0 2px currentcolor`: a
spread with no offset and no blur, which the draw list paints as a rounded box just outside the
border box. It costs the layout nothing, per CSS UI 4 § 2.1. The width and the colour are separate
classes that compose — `ring-2 ring-accent` — and a bare `ring` is one pixel, which is v4's meaning
(v3's three-pixel `ring` is `ring-3`). ⚠ One limit: a `ring-*` and a `shadow-*` on the same element
write the same property, so the cascade picks one. CSS layers them by comma and the draw list refuses
a comma list outright rather than painting the first and dropping the rest.

⚠ **`fill-*` and `stroke-*` reach `Icon`, and they inherit** — which is what makes them useful, since
the class goes on the button and the `<icon>` is a child. They override the paints an icon declared
as *foreground* (SVG's `currentColor`) and deliberately leave a literal colour alone, so the
brand-coloured file-type glyphs stay themselves. There is no `stroke-width` family, so CSS cannot add
a stroke to art that declared none.

⚠ **`select-*` is inert, and not for want of a reader.** There *is* a text selection model —
`TextField` and `CodeEditor` each have one — but both are per-control: each captures the pointer for
its own drag and hit-tests only its own text. The document-wide selection `user-select` governs, the
one that would let a drag cross a label or two sibling elements, does not exist. So `select-none` on
a button has nothing to suppress, because nothing there would have been selectable.

⚠ **`overflow-auto` is in neither column.** The draw list clips on any value that is not `visible`, so
it clips; the layout's keyword table has `visible`, `hidden` and `scroll` and not `auto`, so the
layout goes on treating the box as visible. Write `overflow-scroll` when the layout is meant to hear
about it — and note that scrolling itself is `ScrollView`, not a property.

## Examples

**Another editor assembly is one line, and the line shares the tokens rather than copying them.**

```xml
<Import Project="..\Vixen.Editor.Ui\build\Vixen.Editor.Ui.Styling.targets" />
```

That import names `Vixen.Editor.Ui`'s `@theme` as this project's token source, brings in the utility
build step, and names the tool that runs it. The project then scans only its own sources and emits
only its own sheet, which its `…Theme.Install` loads next to the hand-written one it already loads:

```csharp no-compile="two members of an assembly's own theme class; VixenUtilityStyles is generated into that assembly by the build step"
public static int Install(UiDocument document) {
    var sheet = document.Load(Css, StyleOrigin.UserAgent);

    document.Load(Utilities, StyleOrigin.UserAgent);

    return sheet;
}

public static string Utilities => VixenUtilityStyles.Utilities;
```

⚠ **Do not give the second project a `vixen.ui.vcss` of its own.** That is what this page used to
recommend, and it is a second palette — the precise failure the token model exists to prevent, and
two files that agree until the day somebody edits one. The unit of a palette is the *theme*, not the
project, and the editor is one theme across a dozen assemblies. The argument is in
`docs/plan/43-web-styling-parity.md` Part 3.

Every generated sheet is entirely inside `@layer utilities`, where document order decides nothing, so
a dozen of these loaded in whatever sequence the modules install behaves at runtime as one sheet.
Each project keeps its own scan and its own output, so incremental build and project independence
both survive.

**A project outside the editor** — a game, a plugin with a design of its own — declares its own
tokens, and that is the case the found-rather-than-declared glob is for:

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

One `**/vixen.ui.vcss` per project, and two is an error because two would be two palettes.
`VixenStyleTokens` is the other half of the same rule: a theme *found* in the project is that
project's own, a theme *named* there belongs to another one, and a named path that no longer resolves
fails the build rather than reverting the assembly to silence.

⚠ **A project that names no token source at all generates nothing, not a default palette.** The
generation target is conditioned on there being one, so the step does not run — the sheet is absent
rather than wrong. `ThemeTokens.CreateDefault()` is what a project gets when the step *does* run with
no `@theme` to read, which is why `SharedThemeTests` asserts `bg-blue-500` is absent from every
editor sheet: that class resolves under the shipped palette and under nothing else, so its presence
would mean an assembly had been wired to Tailwind's colours instead of the editor's.

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
