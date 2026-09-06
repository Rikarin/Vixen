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
related: [editor/index, editor/inspectors-in-markup, ui/utility-composition, ui/text-decoration]
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
| `static`/`relative`/`absolute`, `inset*`, `top`/`right`/`bottom`/`left`, `start`/`end`, **`inset-s`/`-e`/`-bs`/`-be`**, `z-`, `box-border`/`box-content` | |
| `text-<align>`, `text-<size>`, `text-<colour>`, `font-`, `leading-`, `tracking-`, `whitespace-`, **`align-`** | `align-middle`/`-text-top`/`-text-bottom`/`-sub`/`-super` — the property is read, those five values are refused at the bridge for want of a font strut |
| **`underline`**, **`overline`**, **`line-through`**, **`no-underline`**, **`underline-offset-`**, **`decoration-<n>`**, **`decoration-auto`/`-from-font`**, **`decoration-solid`/`-double`/`-dashed`/`-dotted`**, **`decoration-<colour>`** | `decoration-wavy` — not registered: a wave is a stroked path where every other decoration is a rectangle, and CSS states neither its amplitude nor its period, so it would paint a straight line |
| `bg-`, `opacity-`, `shadow-`, `ring-`, `fill-`, `stroke-`, **`translate-x/y-`** | `blur-`, `scale-`, `rotate-` |
| `rounded-`, and the per-corner `rounded-t`/`-r`/`-b`/`-l`/`-tl`/`-tr`/`-br`/`-bl`; **the logical `rounded-s`/`-e`/`-ss`/`-se`/`-ee`/`-es`** | |
| **`outline`/`outline-<n>`**, **`outline-<colour>`**, **`outline-offset-<n>`**, **`outline-solid`/`-none`/`-dashed`/`-dotted`/`-double`**, **`outline-hidden`** | — all five keywords are drawn |
| `border`/`border-t`/`-r`/`-b`/`-l`/`-x`/`-y`, both widths and colours; **`border-bs`/`-be`**, both; `border-s`/`-e` widths; **`border-solid`/`-dashed`/`-dotted`/`-double`/`-none`/`-hidden`** | `border-s-<colour>` and `border-e-<colour>` — the *inline* logical pair never reached the draw list. `border-groove` and friends are not Tailwind classes and are not drawn: all four are two-tone and the border record carries one colour |
| **`divide-x-`**, **`divide-y-`**, **`divide-<colour>`**, **`divide-solid`/`-dashed`/`-dotted`/`-double`/`-none`** | `divide-x-reverse`, `divide-y-reverse` — the reverse pair needs `calc()` |
| **`overflow-visible`**, `overflow-hidden`, `overflow-scroll`, `overflow-auto`, **`overflow-clip`**, **`overflow-x-*`**, **`overflow-y-*`**, **`scrollbar-auto`/`-thin`/`-none`**, `truncate` | |
| `cursor-`, `caret-`, `pointer-events-`, `transition`, `duration-`, `ease-`, `aspect-` | `select-` (`user-select`) |
| **`bg-linear-*`**, **`bg-radial`**, **`bg-conic`** with `from-`/`via-`/`to-` and stop positions | |

⚠ **v4's logical families divide into two halves here, and the halves emit different spellings.**
`inset-s-*`/`inset-e-*` and `ps`/`pe`/`ms`/`me` keep CSS's logical longhands, because the layout
interns `-inline-start`/`-inline-end` and mirrors them under `direction: rtl` — which is the whole
point of writing them. `inset-bs-*`/`inset-be-*` and `border-bs-*`/`border-be-*` emit the *physical*
`top`, `bottom`, `border-top-*` and `border-bottom-*`, because nothing interns the block longhands
and `Vixen.Ui.Layout` has no writing mode for the block axis to be anything but top-to-bottom. Same
trade as `space-y-*`. ⚠ The six logical **radii** — `rounded-ss-*` and friends — are the third
case and neither of the first two: a radius corner is named on *both* axes at once, so a physical
corner would be right only under `direction: ltr`. They keep the logical longhands and
`DrawListBuilder.Corners` resolves them against `direction` at paint time, mirroring the inline
half of each name and leaving the block half alone. `rounded-ss` is therefore the top-left corner
under `ltr` and the top-right under `rtl`, which is what `ps-2` does one property over.

⚠ **`space-*` and `divide-*` are the only families whose rule is about the children.** The rule is
`:where(.space-y-4 > :not(:last-child))` — v4's own selector, wrapper included. `:where()` contributes
no specificity, so the rule lands at `(0,0,0)` and a child's own `mb-0` takes its margin back; without
the wrapper it would be `(0,2,0)` and the override would be unwritable, which is what Tailwind v3
shipped and what this emitted until `:where()` compiled. One divergence from v4 is left:
`space-y-*` writes
`margin-bottom` where v4 writes `margin-block-end`, because the block longhands are interned by
nobody here and there is no writing mode for the two to differ in. `@apply space-x-4` is refused, for
the same reason `@apply hover:bg-accent` is: it is a rule with a selector of its own.

⚠ **`mix-blend-*` and `origin-*` were once refused as measured-inert, and both refusals have since
expired** — they are registered families now, and the sequence is worth knowing because it is the
shape most of these take: the family was not missing, the *consumer* was, and writing the consumer is
what closed it. `transform-origin` needed `rotate` and `scale`; `mix-blend-mode` needed a blend
channel on `UiLayer` and the arithmetic to go with it. `isolate` and `isolation-auto` came with the
second, because `isolation` has no observable of its own — its only defined effect is on a
descendant's blend.

⚠ **`object-*` needs one line of C# per picture and does nothing without it.** The fourteen classes —
five fits and nine positions — are read by `Image`, and every keyword but `object-fill` is *defined*
as a relation between the picture's own shape and the box it is in. Nothing on the UI side can ask a
texture how big it is, so the application says: set `Image.IntrinsicSize` beside `Image.Texture`, in
the texture's own pixels. Left at zero it means "unknown", and an unknown picture stretches to the
box whatever the class says — which is CSS's own answer for content with no intrinsic dimensions, and
also why adding these classes to an existing screen changes nothing until somebody fills the size in.

⚠ And they place the **picture**, not the element. An `Image` with no `width` is still a zero-height
box: sizing a replaced element from its content is a separate thing this framework does not do.

⚠ **One half of `mix-blend-*` is still owed and the classes say so.** The blend is applied by
`SoftwareUiRasterizer` and not by `UiRenderer`, so on the device a blended group composites
source-over and looks as though the declaration were absent. `UiRenderer.Unblended` is what says it
happened. See `docs/guide/ui/compositing.md` and `docs/plan/43-web-styling-parity.md` § Part 9,
Bucket 2.

⚠ **The `scroll-*` set is written now, and every one of them only means something inside a
`<ScrollView>`.** `scroll-mt-4` on a `div` that nothing ever scrolls to resolves, computes a value and
does nothing, which is true of the CSS as well and is the reason these were held back until the
reader existed. What each is for:

- **`scroll-m-*`** goes on the **target** — the row, the field, the section heading — and says "leave
  this much room around me when something scrolls to me". A sticky header two rows deep is the usual
  cause: `scroll-mt-10` on the rows stops the header covering whichever one just took focus.
- **`scroll-p-*`** goes on the **`ScrollView`** and says the same thing from the container's side, so
  one declaration covers every target inside it. Where both apply they add up, as CSS does.
- **`scroll-smooth`** on a `ScrollView` eases its *programmatic* scrolls — `ScrollIntoView`, Page,
  Home, End — over about 75 ms. ⚠ It does **not** smooth the wheel or a drag on the bar, and neither
  does a browser: an eased wheel lags the finger by the whole time constant and reads as a dropped
  frame.
- **`overscroll-contain`** on an inner `ScrollView` stops the wheel chaining to the panel behind it
  once the inner has run out. ⚠ `overscroll-none` stops the chain the same way and additionally makes
  the boundary hard: `contain` keeps the elastic overscroll a drag gets at the end of the content and
  `none` refuses it. The two used to be identical here, for as long as there was no rubber-band for
  `none` to suppress.

The logical edges (`scroll-ms-*`, `scroll-pe-*`, …) mirror under `direction: rtl` exactly as `ms-*`
does. The **block** ones — `scroll-mbs-*`, `scroll-mbe-*`, `scroll-pbs-*`, `scroll-pbe-*` — are
written now and emit the *physical* `scroll-margin-top` and friends, for the reason `inset-bs-*` does
one section up: nothing interns a block longhand and there is no writing mode for the block axis to
be anything but top-to-bottom, so the physical name is the same declaration in a spelling
`ScrollView` can read. ~~`snap-*` and `scrollbar-*` are still deferred~~ — `scrollbar-*` landed with
`LayoutStyle.ScrollbarWidth` and is in the overflow row of the table above; `snap-*` is still
deferred. See `docs/plan/43-web-styling-parity.md` Part 8 § 3.

⚠ **`caret-*` is the one interactivity colour with a reader, and `accent-*` is not.** `caret-accent`
on a `<TextBox>`, a `<TextArea>` or a `<CodeEditor>` colours the insertion point: both controls ask
the standard `caret-color` first and fall back to Vixen's own `--caret-color` token, which is what
`ControlTheme.vcss` and `EditorTheme.vcss` declare on the root — so the class is the field's answer
and the token stays the document's. It inherits, so writing it on a form row reaches every field
inside. **`accent-*` is deliberately absent**: CSS applies `accent-color` to checkboxes, radios,
sliders and progress bars, and Vixen draws the last two in C# but the first three from the
stylesheet — a checkbox's tint is `checkbox:checked box { background-color: var(--accent) }`, a rule
on a *child part*, and `var()` reads custom properties only. Set `--accent` to recolour them.

⚠ **A `cursor-*` class resolves and is read, and no window shows it yet.** `UiDocument.Cursor`
answers correctly for whatever is hovered; nothing in the tree turns that answer into a call on the
window. This is family-wide — `cursor-pointer` is in exactly the same position as `cursor-help` —
and it is a gap in the host rather than in the cascade.

⚠ **`ring-*` is a `box-shadow`, not an outline, and it used to emit `outline-color` — a property no
version of Tailwind has ever emitted for it.** `ring-2` is `box-shadow: 0 0 0 2px currentcolor`: a
spread with no offset and no blur, which the draw list paints as a rounded box just outside the
border box. It costs the layout nothing, per CSS UI 4 § 2.1. The width and the colour are separate
classes that compose — `ring-2 ring-accent` — and a bare `ring` is one pixel, which is v4's meaning
(v3's three-pixel `ring` is `ring-3`). ⚠ **The limit this said it had is gone**: `ring-2 shadow-lg`
on one element used to write `box-shadow` twice and let the cascade keep one, and a comma list was
refused outright anyway. Both closed — the draw list paints a list, a command each, and all four
families now fill slots of one assembled declaration.

⚠ **`inset-shadow-*` and `inset-ring-*` are the inner twins, and one keyword's difference is a whole
other draw path.** An outer shadow is the border box grown by the spread and painted *below* the
background; an inner one is the region between the border box and that box shrunk by the spread,
painted *above* it — CSS Backgrounds 3 § 7.1.1's other answer, which is why an inner shadow shows on
an opaque element at all. `inset-shadow-*` reads a scale of its own, `--inset-shadow-*`: three steps
against the outer scale's seven, and much tighter, because every pixel of an inner shadow is seen
where an outer one is seen at its edge. `inset-ring-*` is `ring-*`'s width and colour with the
keyword in front. All four compose: `inset-shadow-sm inset-ring-2 ring-2 shadow-lg` is four things.

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

⚠ **`overflow-auto` is read, and it means what CSS means by it.** This page used to say it was in
neither column — that the draw list clipped on it and the layout did not hear about it, so an author
should write `overflow-scroll` instead. That was true and is not: `Vixen.Ui.OverflowReader` is the
single place all three names resolve, for the clip stack and the hit test alike, and
`LayoutStyleBuilder` maps `auto` onto `Overflow.Scroll` — which is the layout CSS gives it, since the
only thing `auto` and `scroll` disagree about is whether the gutter is always there. Following the
old advice now buys a permanent scrollbar where `auto` was wanted.

⚠ **The gutter is `scrollbar-*`, and it is what separates the two names here.** `LayoutStyle.ScrollbarWidth`
is a length rather than CSS's keyword, and `StyleResolution` reserves it only where the overflow is
`Scroll` — so `overflow-auto scrollbar-none` is the pair that clips and scrolls and reserves nothing,
and `overflow-scroll scrollbar-auto` is the one that always leaves room. Writing neither leaves the
gutter at zero.

⚠ **A named axis beats the shorthand, and the two axes do not coerce each other.** `overflow-x-*` and
`overflow-y-*` are each read on their own; an element with `overflow-hidden overflow-y-scroll` clips
horizontally and scrolls vertically, which is not CSS's rule about a `visible`/non-`visible` pair and
is deliberate — see `Vixen.Ui.Styling.Utilities/README.md`. And note that scrolling itself is
`ScrollView`, not a property: a clip is not a scrollbar.

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
because `class` is a string and every string parses. The build step writes every candidate that
emitted no rule to `obj/…/<Assembly>.unrecognised.txt`, and with the C# scanned that file is mostly
ordinary English out of comments, so it cannot be a warning list. `StylesheetTests` asks the narrow
question instead: every class name actually written in a `class` attribute in the editor's markup is
either a utility the theme can emit or a rule `EditorTheme` wrote, and anything else is a typo.

⚠ **The report has two sections and the first one is short.** A class whose *family* is registered
and whose value is not — `bg-surfaces` for `bg-surface`, `bg-clip-text` for a keyword set nobody has
registered — is a different failure from a word out of a comment, and is the one worth reading: it
means somebody wrote a class whose root exists. Those come first, each naming the family and the
value it had nothing for; the several thousand candidates that matched no family at all follow. The
build line prints both counts.

## See also

- [The editor shell](index.md) — `EditorShell`, which installs the sheet stack.
- [Inspectors in markup](inspectors-in-markup.md) — the other place editor UI is written in VXML.
- [Composed utilities](../ui/utility-composition.md) — the families that set a `--tw-*` fragment
  instead of a declaration, and why the cascade is what assembles them.
- `EditorTheme`, `ThemeService` — the hand-written half of the sheet, and the tokens both halves
  resolve against.
