# Vixen.Ui.Controls

The standard control set from [docs/plan/09](../../docs/plan/09-ui-framework.md) § "Control library",
and the user-agent stylesheet it comes with.

`Vixen.Ui` is an element tree, a cascade, a flexbox engine and a draw list. None of them is a button.
This is the assembly that turns them into one — and, more usefully, into forty-odd of them that agree
with each other about what Space does, what a focus ring looks like, and what happens when you press
Escape.

## What is in it

| | |
|---|---|
| Text | `TextBlock`, `Link`, `Badge`, `KeyboardShortcut`, `Avatar`, `Skeleton`, `Icon`, `Image` |
| Buttons | `Button`, `IconButton`, `ToggleButton` |
| Toggles | `CheckBox` (with indeterminate), `Switch`, `RadioButton`, `RadioGroup` |
| Fields | `TextBox`, `TextArea`, `SearchBox`, `NumericInput` (with drag-scrub) |
| Range | `Slider`, `RangeSlider`, `ProgressBar`, `Spinner` |
| Choice | `Select`, `MultiSelect`, `ComboBox` |
| Grouping | `Panel`, `Card`, `Separator`, `Tabs`, `Expander`, `Accordion`, `ScrollView` |
| Overlays | `Popover`, `Tooltip`, `Menu`, `ContextMenu`, `MenuBar`, `Dialog`, `Drawer`, `Toast` |
| Navigation | `Breadcrumb`, `Pagination` |
| Feedback | `Alert`, `EmptyState` |
| Not a control | `DialogService`, `DialogSession<T>` — the one service in here, and § below says why |

## The one thing in here that is not a control

`DialogService` has no tag, no theme rule and no place in the element tree. It is here because
`Dialog` gets modality right — a real backdrop element, `IsFocusScope` so Tab cannot walk out, the
focus restored to whatever had it — and **none of that answers a question**. The 376 lines that make
a dialog *answerable* were in `Vixen.Editor.Ui`, an assembly no application can reference, which is
[doc 46](../../docs/plan/46-what-an-application-needs.md) § A4's finding.

`ConfirmAsync`, `PromptAsync`, `ChooseAsync` and `ShowAsync<T>` hand back a `Task<T>` a caller
awaits. Four things about how they do it are the value, and each is a defect a re-implementation
has: answering removes nothing (the click was dispatched into the subtree being torn down); the
continuation runs on the frame loop rather than from the click handler; asks are queued one at a
time rather than refused; and `CancelAll` *answers* what is waiting rather than dropping it, so a
command awaiting a dialog during shutdown unwinds instead of never finishing.

**Nothing has to wire the pump.** The service subscribes to `UiDocument.Ticked`, so an application
that ticks its document has working dialogs. `Tick` and not `Update` for the same reason
`CommandsInvalidated` is raised there: `Update` returns early when nothing dirtied the document, and
asking a question dirties nothing. `Dispose` unsubscribes and then cancels, which is what stops a
queue of continuations outliving the document.

⚠ **Drawn, not native.** A modal that is an OS window cannot be screenshotted by a golden-image
suite or driven by a headless harness. A *file* picker is the opposite case and belongs to the
platform. See [the guide page](../../docs/guide/ui/dialogs.md).

## Three decisions the whole set rests on

**A control names its own tag.** `UiDocument.Create<T>` asks the element for `TagName` when the
caller does not pass one, so `parent.Add<Button>()` produces an element the theme's `button { … }`
rule matches. A caller that had to pass `"button"` alongside `Button` would eventually pass something
else, and the control would be silently unstyled.

**A control builds its parts in `OnCreated`.** A switch is a track and a knob; an element can only be
made by a document; so there is a hook that runs after the element is bound and attached and before
it is handed back. It is the constructor a control cannot have.

**Anything a stylesheet can decide, a stylesheet decides.** `Variant` and `Size` are written through
to classes rather than read here. A collapsed panel is `display: none`, not a removed subtree. A
ticked checkbox is `:checked`, not swapped geometry. What is left for code is the two or three
things the cascade genuinely cannot express — a slider's thumb at 37%, a caret at index 4 — and
those are drawn in `OnDraw` against custom properties the theme still owns.

## Content, and how a control that has parts takes some

A control with parts of its own cannot let a caller's children land beside them, so every one that
takes content says where it goes by overriding `ContentHost`: `ScrollView` answers its `Content`,
`KeyValueRow` its value half, `Disclosure` and `Overlay` their bodies. Nothing else is needed for
`<ScrollView><Expander>…</Expander></ScrollView>` to mean what it looks like, and markup never has to
name a part.

**`Tabs` is the case one `ContentHost` cannot express, and it is worth reading before adding a second
one like it.** A tab and the panel it shows are in different halves of the tree — `tab-strip` and
`tab-panels` — so the pairing cannot be parenthood, and a control with two places for content has one
property to say so with. `AddTab` existed because of that, and being imperative-only it was the last
thing keeping `PrefabView` out of markup.

```html
<Tabs class="document-tabs">
    <TabItem Label="Hierarchy" ref="@HierarchyTab" />
    <TabItem Label="Compiled">
        <CompiledSceneView ref="@Compiled" />
    </TabItem>
</Tabs>
```

The answer is **two hosts and a lifecycle hook**, not a second slot mechanism:

- `Tabs.ContentHost` is `Strip`, because the children a caller writes are headers.
- `TabItem.ContentHost` is `Panel`, so a tab's own content goes where it shows.
- `TabItem.OnCreated` is what pairs the two: a tab that finds itself in a `Tabs`'s strip takes a
  `tab-panel` and joins the list.

⚠ **The pairing moved into the hook rather than being duplicated.** `AddTab` could not be what markup
calls — a `.vxml` writes tags — so leaving the pairing where it was and adding a declarative
equivalent beside it would have been two ways to half-build a tab, and the day they disagreed the
symptom is a panel with nothing in it. `AddTab` is now three lines that do what the markup does:
add a `TabItem` to the strip, and let the tab join. `RemoveTab` went the same way — it removes the
element, and `TabItem.OnRemoved` is what unregisters — because **markup removes elements without
asking**. An `@if` whose arm leaves takes its `TabItem` with it, and a `Tabs` that only unregistered
from `RemoveTab` would keep a dead tab in `Items`, an orphaned panel in the tree, and a
`SelectedIndex` possibly pointing at the gap.

⚠ **`TabItem.Owner` is exactly two levels up and not an ancestor walk**, because a tab's panel can
hold another `Tabs` and "the nearest `Tabs` above me" is the wrong answer for every nested tab.

## What is in the framework because of this

Four things were missing from `Vixen.Ui` and are now there, because no control set works without
them:

- **Keyboard input.** `KeyEvent` and `TextInputEvent`, routed from the focus outwards, with Tab
  handled by the document *after* the route and only if nothing wanted it. The key is a physical
  position (`Vixen.Input.InputKey`); the character is a separate event, because on an AZERTY keyboard
  the key that types `a` is `Q`.
- **Hover, press and crossing.** `:hover` and `:active` on the whole ancestor chain, maintained as a
  difference so the path to the root is not restyled on every pointer move, plus `Entered`/`Exited`
  delivered `Direct` to each element that was actually crossed.
- **`:focus-visible`.** The document remembers whether the last input was a key, so a click focuses
  quietly and a Tab lights the ring. One heuristic, in one place, rather than a flag on every control.
- **`OffsetX`/`OffsetY`.** A translation applied when absolute positions are accumulated. It is what
  scrolling, popup placement and drag previews are made of, and it costs a walk rather than a cascade
  and a relayout.

## What `ScrollView` reads out of the cascade

⚠ **It reads no `overflow`, and it does read four scroll families.** The distinction is the whole of
doc 43 A18 and is worth stating here rather than only in the source. `overflow` on a box is what
*conjures* a scrollbar in CSS; here the bars are children this control creates, so which bars a view
offers is a property of the control and `overflow-x` on some other element is a clip and nothing more.
The consequence is blunt: `overflow-y: auto` on a plain `div` cuts its content off and offers no way
to reach it. Put a `ScrollView` there.

The four it does read say nothing about *whether* something scrolls — only where a scroll lands, how
it gets there and what happens at the end, which are questions that presuppose a scroll container:

| Property | Read off | What it does |
|---|---|---|
| `scroll-margin-*` | the **target** of `ScrollIntoView` | leaves that much room around it when the view stops |
| `scroll-padding-*` | the **view** | insets the viewport the same scroll is measured against |
| `scroll-behavior` | the view | `smooth` eases programmatic scrolls off `UiDocument.Ticked` |
| `overscroll-behavior`, `-x`, `-y` | the view | whether a wheel at the stop chains to what contains it |

⚠ **The first two come off different elements and that is CSS, not a shortcut** (Scroll Snap §6). A
reader that took both off one element passes every test in which the two happen to be equal. The
logical edges are folded against `direction` here rather than by the layout, because a scroll offset
is not a layout pass and no pass ever sees them; `ScrollViewTests` asserts the `rtl` half.

⚠ **`scroll-behavior: smooth` is not applied to the wheel or to a drag on the bar**, and both of those
call `Settle()` to abandon an easing already in flight. Direct manipulation that lags the finger by a
time constant reads as a dropped frame, which is why browsers exempt it too.

⚠ **`overscroll-contain` and `overscroll-none` do the same thing here.** In CSS they differ only over
the rubber-band and pull-to-refresh at the boundary, and this engine has neither, so there is nothing
for `none` to additionally suppress. Both stop the chain, which is the half the class is written for.

## The theme

**The sheet is `ControlTheme.vcss`, a file beside the loader**, embedded by the `**/*.vcss` glob in
`Vixen.Ui.targets` and read back by `ControlTheme.Css`. It was 873 lines of CSS in a `const string`
until it was moved out byte for byte; `ControlTheme.cs` is now the loader and nothing else.

`ControlTheme.Install(document)` loads it as **`StyleOrigin.UserAgent`**, which is the point: a
game's own `button { … }` beats it at equal specificity, so restyling is one rule rather than a fork.
Colours go through custom properties — `--surface`, `--accent`, `--text`, and the rest — so a palette
is nine values on the root and a dark theme is a `dark` class.

Two things worth knowing before reading it. The layout defaults are **CSS's, not Yoga's**, so a
container with no `flex-direction` is a *row* and every rule that wants a column says so. And
`box-sizing: border-box` is set here on `*`, because `LayoutStyleBuilder` deliberately left that
property to a user-agent sheet rather than baking it in where an author could not see it.

## Icons, and the three places a colour comes from

`Icon.Geometry` is one `PathBuilder` drawn in the inherited `color`, which is what the editor's
chrome is and what thirty-four hand-drawn glyphs already say. `Icon.Art` is an `IconArt`: several
paths, each with its own fill, stroke and stroke width, on a view box of its own.

**Per-path paint was free and monochrome-plus-tint would not have been.** `DrawContext.Fill` and
`Stroke` each already take a `Color4`, so what was single-colour was `OnDraw` making exactly one
call — a line, not a rendering path. The tessellator, the draw list and the batching are untouched.

⚠ **The real decision is not how many colours but whether a colour follows the theme**, so a paint
has three cases and all three are needed:

| `IconPaint` | Follows a retheme | For |
|---|---|---|
| `Foreground` | yes, via `color` | the chrome — a toolbar glyph, a disclosure arrow |
| `Named("--icon-warning")` | yes, via the cascade | "the warning colour", "the accent" |
| `Of(colour)` | no | the brand colours a file-type glyph actually needs |

A set that only offers literals looks correct in the theme it was drawn for and wrong in the other
one. A token nothing answers falls back to `color` rather than disappearing — a plugin whose
stylesheet has not loaded must be a visible glyph in the wrong colour, never an invisible one.

**A stroke's width is in view-box units and scales with the art**, so one definition reads the same
weight at 16 pixels and at 32.

## Menus

**A submenu opens on hover, and a line that has one says so with an arrow.** Both are what every
desktop menu does, and the absence of either is felt rather than noticed: without the hover, reaching
a nested command costs a click per level and sliding off "3D Object" onto "Delete" leaves the shapes
hanging over the line the pointer is now on; without the arrow, a line that opens a menu and a line
that runs a command are the same shape, so there is no way to tell which one you are about to commit
to. Clicking still opens one, for the keyboard's Right arrow and for anybody who clicks anyway.

⚠ **A submenu closes when a *sibling* is hovered, not when its own line is left.** A submenu is
placed beside the item that opens it, so reaching into it means leaving that item — a close-on-exit
rule would shut the menu the user is reaching for, every time, and no nested command would be
reachable with the mouse at all.

## The words the set says

`ControlStrings` is thirteen `StringId` declarations — "Clear" in a search box, "Dismiss" on a toast,
"Previous tab" on a docked group, "Search" in a property grid — plus an `All` list for a translator's
template. Every one of them was an English literal in a control constructor until doc 46 § A3 counted
them: a localised window had an untranslatable seam in the one place a user cannot avoid looking, and
the only party who could close it was this repository. See
[docs/guide/ui/strings](../../docs/guide/ui/strings.md).

⚠ **One class covering `.Advanced` as well**, which references this assembly. A second declaration
class over there would put the boundary between "a control" and "an advanced control" — an assembly
split about compile cost — into a translator's workflow, where it means nothing.

⚠ **Two ids for the two `"Close"`s**, the dialog's and the dock tab's. They are the same English word
and are not the same string, and an id that says *where* a string is used is what lets a language
distinguish them. Merging them saves a line and cannot be undone without a translator's file changing
shape.

⚠ **The labels are read in `OnCreated`, so a control shows the language it was built in.** That is
not a live binding: `Strings.Catalog` is a signal and an *expression* that reads it re-runs, but an
assignment in a constructor is not an expression. Re-labelling a live control set would need an
effect per label and somewhere to dispose it — listed under the gaps below.

## Known gaps

Said out loud rather than left to be discovered:

- **A control does not re-label itself when the language changes.** `ControlStrings` makes the words
  translatable and a control built after `Strings.Use` is built in the new language, but one already
  on screen keeps its old label until it is rebuilt. Markup does not have this problem — an `@expr`
  is an effect and follows the signal — so the gap is exactly the labels this set assigns in C#.

- **`Image` draws a texture the application registered**, and reserves the space when it has none.
  Turning a `Source` name into a `Texture` number is the application's half: it loads the asset and
  calls `UiRenderer.RegisterImage`. An unset texture draws nothing, which is what an image whose
  asset has not arrived should do.
- ~~**`TextArea` is a taller `TextBox`.**~~ The framework wraps a line now, and the theme is where
  the difference lives: `field-text` is `white-space: nowrap` so a field's long value scrolls
  sideways, and `textarea field-text` is `normal` so its text stays inside and the box grows
  downwards. Still owed is the *editing* half — a caret that moves between lines, and Enter starting
  one — which is the text editor's item.
- ~~**Timed behaviour needs a host tick.**~~ `Tooltip` and `ToastHost` subscribe to
  `UiDocument.Ticked` and unsubscribe in `OnRemoved`, so a host that drives `UiDocument.Tick` gets
  the delay and the lifetime without knowing that either control exists. Nothing here is told what
  time it is except by an input event, and those stop arriving exactly when a pointer comes to rest —
  which is the same reason `GestureRecognizer` is on that clock. Both `Tick` methods stay public for
  a caller that wants to drive one directly.
- ~~**An overlay outlives the control that made it.**~~ `UiElement.OnRemoved` exists and `Overlay`,
  `Menu`, `MenuBar` and `SelectBase` use it. A select's list is still a root child — painting order
  forces that — but removing the select now takes it, and the two capture handlers with it.
- ~~**`VirtualizingPanel` is not here.**~~ It is, and it is the primitive doc 09 asks for: a count, a
  row height, a factory and a binder. A hundred thousand items is a hundred thousand of the caller's
  own objects and about a dozen elements. ⚠ Fixed row heights only — virtualisation has to know where
  row 40 000 is without measuring the 39 999 above it, and variable heights need a running-sum index
  that is a different control. Nothing has to call `Realise`: it runs on `UiDocument.LayoutFinished`,
  which is the only place that knows how tall the viewport ended up.
