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
| Fields | `TextBox`, `TextArea`, `SearchBox`, `SecureTextBox`, `NumericInput` (with drag-scrub), `Stepper` (`NumericInput` with the two arrows) |
| Range | `Slider`, `RangeSlider`, `ProgressBar`, `Spinner` |
| Choice | `Select`, `MultiSelect`, `ComboBox` |
| Grouping | `Panel`, `Card`, `Separator`, `Tabs`, `Expander`, `Accordion`, `ScrollView`, `SplitView` |
| Application chrome | `Toolbar`, `StatusBar`, `SegmentedControl`/`Segment` — the three the editor drew out of bare elements. Each carries the behaviour a stylesheet cannot: one tab stop with roving arrows, a `status` live region, and an exclusive choice. ⚠ `AccessibleRole.Toolbar` had no carrier until `Toolbar` existed |
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

## A dialog that is a function of state, beside the one a command awaits

`ConfirmAsync`, `PromptAsync` and `ChooseAsync` are imperative on purpose: a command that has to have
an answer before it continues is exactly a call that returns one, and that is what every caller in
this tree does. ⚠ **This is not a replacement for them.** What had no spelling at all was SwiftUI's
other arrangement — `.alert(isPresented:)`, where the dialog is a function of state, so the panel
that shows one owns the flag and the presentation survives a rebuild because the flag does.

`DialogService.Present(dialog, asking)` is that, over a `<Dialog>` the panel wrote itself:

```vxml
<Dialog use="@(overlay => Dialogs.Present(overlay, Asking.Value))"
        on:openchanged="@((OpenChangedEvent args) => Dismissed(args))">
    <Button Label="Delete" on:click="@Confirm" />
</Dialog>
```

Three things it does that opening the dialog from an effect would not:

- **It takes its turn.** The ask goes into the same queue an awaited one goes into, so a panel's
  dialog waits behind a command's rather than appearing over it. Two backdrops over each other is a
  picture with no answer in it, and the lower one still holds the focus scope.
- **It does not take the element away.** ⚠ The service removes the dialogs it *made* and leaves the
  ones a panel owns, because a panel's `<Dialog>` belongs to that panel's region — removing it here
  would leave the `ref` pointing at a corpse and the next rebuild adding a second one beside it.
- **A withdrawn ask is dropped rather than flashed.** A panel whose state flips twice before its turn
  comes never sees the dialog at all.

**The answer is the panel's own business**, which is the other half of what makes this a different
shape: there is no `Task` to complete, so the buttons write the model with an `on:click` and the
dialog goes away because that signal is what `Present` reads.

## An overlay whose open state is a panel's own state

`Overlay.IsOpen` is deliberately not a `[UiProperty]` — opening measures, places, moves the focus and
may take a modal scope, so a settable property would be an invitation to write half of it — and that
is why `bind:IsOpen` does not exist and is not an oversight. What a panel that owns the flag writes
instead is two attributes:

```vxml
<Dialog use="@(overlay => overlay.Show(Wanted.Value))"
        on:openchanged="@((OpenChangedEvent args) => Wanted.Value = args.IsOpen)">
```

- **`Show(bool)`** is the forward leg. It is a call rather than an assignment, so `Open`'s
  measure-place-focus sequence is what actually happens, and it is idempotent — which it has to be,
  because `use` is an effect and re-runs on every change of every signal the expression read.
- **`on:openchanged`** is the write-back leg, and it is the half a forward-only implementation
  fails. ⚠ `OpenChangedEvent` was raised all along, by `Overlay.Restate` and by `Disclosure`, and no
  `.vxml` in the tree could hear it: the name was absent from the markup event table, so `on:` had
  no entry to bind. Without it an overlay closed by Escape or by a click outside leaves the model
  still saying `true`, and the effect puts it straight back up — a control the user cannot dismiss.

The two converge rather than chatter: the write-back writes the value the effect just produced, which
is a no-op, and a dismissal writes the one the effect then agrees with.

## What `ScrollView` reads out of the cascade

⚠ **It reads no `overflow`, and it does read seven scroll families.** The distinction is the whole of
doc 43 A18 and is worth stating here rather than only in the source. `overflow` on a box is what
*conjures* a scrollbar in CSS; here the bars are children this control creates, so which bars a view
offers is a property of the control and `overflow-x` on some other element is a clip and nothing more.
The consequence is blunt: `overflow-y: auto` on a plain `div` cuts its content off and offers no way
to reach it. Put a `ScrollView` there.

The ones it does read say nothing about *whether* something scrolls — only where a scroll lands, how
it gets there and what happens at the end, which are questions that presuppose a scroll container:

| Property | Read off | What it does |
|---|---|---|
| `scroll-margin-*` | the **target** of `ScrollIntoView` | leaves that much room around it when the view stops |
| `scroll-padding-*` | the **view** | insets the viewport the same scroll is measured against |
| `scroll-behavior` | the view | `smooth` eases programmatic scrolls off `UiDocument.Ticked` |
| `overscroll-behavior`, `-x`, `-y` | the view | whether a wheel at the stop chains to what contains it |
| `scroll-snap-type` | the view | which axes snap, and whether `mandatory` or `proximity` |
| `scroll-snap-align` | any **descendant** | that it is a candidate at all, and which edge of it lines up |
| `scroll-snap-stop` | a candidate | `always`, meaning a scroll may not pass over it |

⚠ **The first two come off different elements and that is CSS, not a shortcut** (Scroll Snap §6). A
reader that took both off one element passes every test in which the two happen to be equal. The
logical edges are folded against `direction` here rather than by the layout, because a scroll offset
is not a layout pass and no pass ever sees them; `ScrollViewTests` asserts the `rtl` half.

⚠ **`scroll-behavior: smooth` is not applied to the wheel or to a drag on the bar**, and both of those
call `Settle()` to abandon an easing already in flight. Direct manipulation that lags the finger by a
time constant reads as a dropped frame, which is why browsers exempt it too.

⚠ **The snap families are the only ones here that needed a feature rather than a reader, and the
feature was mostly the *gesture*.** Doc 43 § Part 8 § 3 deferred the whole scroll block on "the
behaviour comes first" and was wrong about twenty-two of the twenty-three roots — those were property
reads inside a control that already scrolled. It was right about this one, and the hard half was not
the arithmetic: a snap position is one subtraction per candidate per axis, but a snap is defined at
the moment a scroll *comes to rest*, and neither of the two gestures had an end. `ScrollBar` raised
nothing when a thumb was released — it has `ScrollEnded` now — and a wheel is a stream of deltas with
no terminator in it at all, which `ScrollView.SnapIdleSeconds` answers with an idle measured on the
tick clock. ⚠ **Snapping on every wheel notch instead would pass every arithmetic test and be
unusable**, so `ScrollSnapTests` spends half its assertions watching a view stay unsnapped while a
flick is still running.

⚠ **`overscroll-contain` and `overscroll-none` no longer do the same thing.** They stop the chain
identically — that is the half the class is usually written for — and they differ over the boundary:
`contain` keeps the rubber band below and `none` makes the edge hard. They *were* identical here for
as long as there was no local effect for `none` to suppress, which the README and the enum both said;
a reader who remembers that is reading a note that has been overtaken.

**Scroll anchoring is here, and it is the four lines this file said it was.** CSS Scroll Anchoring
keeps the reader still when content above them grows: remember the deepest child the port can see and
its position in *content* space — `Bounds.Top` minus `Content`'s, and the scroll is an `OffsetY` on
`Content` that both terms carry equally, so the difference is independent of the offset — and one
frame's position minus the last's is exactly what appeared above it. Hung on `Refresh` from
`LayoutFinished`, the correction lands inside the same frame's settle loop.

⚠ **What this file had wrong was not the obstacle but where the discriminator lives.** The note here
said anchoring fights a row recycler and named the mechanism exactly — a virtualising panel *reuses*
one element for a different row, so the anchor moves for a reason that is not growth, and the
correction cancels the scroll that caused it, which
`EditorShellBudgetTests.Scrolling_one_row_restyles_the_rows_it_rebound_and_not_the_shell` catches. It
then looked for the discriminator among the children — a child count misses an image that finished
decoding, a content height fires on a realised row — and concluded a recycler protocol was needed.
**It is not a property of the children at all.** A recycler re-lays a row *because* the offset moved,
and anchoring exists for movement the reader did not ask for; so a frame whose offset changed since
the baseline is re-baselined and never corrected. No child count, no content height, no protocol, and
the budget test stays green.

⚠ **A pool slot is still never the anchor, and that is true whatever the offset did.**
`VirtualizingPanel` and `VirtualizingGrid` document their rows as pool order rather than item order,
so the walk stops at the panel and anchors on the panel's own box. A nested `ScrollView` is excluded
the same way and for a nearer reason: its children move when *it* scrolls, which says nothing about
this view's content. `position: sticky`, `fixed` and `absolute` boxes are excluded too — a header
pinned to the top of the port sits at the scroll offset by definition, so anchoring on it would report
zero movement for ever, which is a feature that silently does nothing. `overflow-anchor: none` is
read on the view (refuses the whole correction) and on a candidate (excludes that candidate),
and at the start edge nothing is anchored, because content arriving above a view already at the top is
content the reader wants to see.

**Momentum is here, and what unblocked it was not the curve.** A
`ScrollView` used to scroll from the wheel, the keyboard and its bars and handle no `PointerEvent` or
`DragEvent` of its own — so there was no finger for a fling to continue and nothing for a velocity
tracker to track. `DragToScroll` is that finger: it takes the `DragEvent` the recogniser already
produced, moves the content under it, and lets go of it with whatever speed it had.

⚠ **It used to be opt-in because nothing in this engine could tell a finger from a mouse, and it is
not any more.** `DragEvent` now carries a `PointerType`, so the two cases are separable: a finger or
a pen drags the content with no opt-in at all, and `DragToScroll` is what a kiosk sets to get the
mouse to behave like one too. The mouse is still off by default because a mouse drag inside a scroll
view is a text selection or a marquee on every desktop. ⚠ `PointerType.Unknown` takes the mouse
branch and is *not* guessed into the touch one — a producer that has not said what it is has not said
it is a finger, which is the same reason the enum's default is not `Mouse`.

⚠ **The velocity is sampled per tick and not per drag event.** `DragEvent` carries no timestamp and
several can arrive between two frames, so a per-event velocity would divide by a zero interval or
invent one. Measuring the offset's change over the document's own clock means a fling can never be
faster than the frames that produced it, and a test that steps the clock gets the same number on
every machine. The decay is a time constant against real seconds for the same reason
`SmoothingConstant` is: a factor applied once a frame decelerates twice as fast at 120 fps as at 60,
which is the commonest way an inertial scroll is written wrong and is invisible on the machine it was
tuned on.

⚠ **The wheel deliberately has no fling.** AppKit generates a trackpad's momentum phase itself and
SDL forwards those events as ordinary wheel deltas, so a flick on macOS plausibly already coasts and
a second deceleration here would compound with it. That is reasoned from the two APIs and **has not
been measured on a device**; a drag is the platform-neutral gesture that carries no momentum of its
own, which is why the curve lives there and nowhere else.

**Rubber-band is here too, and the thing it was blocked on was an offset allowed to leave its range.**
`ScrollTop` coerces into `[0, MaximumTop]` and has to: it is what the bars show, what `ScrollIntoView`
computes against and what a snap position is measured in. So `OverscrollTop` is a *second* offset, and
the two are added in exactly one place — on the way to `Content.OffsetY`. Every other question this
control answers is still asked of the clamped one, which is what keeps a transient stretch out of all
of them.

⚠ **The pull accumulated is raw and the resistance is applied on the way out**, which is what makes a
drag past the edge and back arrive at exactly the offset it left. Damping the accumulation instead
needs no second number and is the obvious shortcut; it makes a pull-and-return end somewhere else, and
the content reads as having slipped under the finger. The curve is bounded by the viewport's own
dimension, so a determined drag cannot pull the content out of its window and hand back a blank box.

⚠ **A view let go of while stretched springs rather than flings.** The velocity tracker samples the
scroll offset, which is pinned at a stretched boundary — so the speed it holds is the speed of the last
pixel before the edge. A fling that *arrives* at an end is the other direction of the same seam: it
hands what is left of its speed to the spring, which is the bounce.

⚠ **`Refresh` clears the stretch only when the ends have actually moved.** It runs on every
`LayoutFinished`, so clearing whenever there was a pull erased it on the very next layout — the spring
never ran, the bounce lasted one frame, and the result looked exactly like the hard edge it replaced.

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

### The menu bar is drawn, and stays drawn — decided 2026-09-05

`MenuBar` is a `Control` and there is no `IUiMenuHost`, no `NSMenu`, no `SetMenu` on `IWindow` and no
seam in `Core/Vixen.Ui` for one. That is the state; this is why it is not being changed yet.

⚠ **The urgency argument for native menus is built on a claim that is false.** Doc 49 § Part 5 and
issue #652 both say that a macOS application with no `NSMenu` "has no ⌘Q and no About, and the OS
draws the previous application's bar". SDL builds one. The library this engine actually loads on a
Mac — `sdl2-compat` over SDL3 — carries the whole default application menu in its binary: `strings`
on `libSDL3.0.dylib` finds `About `, `Services`, `Hide `, `Hide Others`, `Show All`, `Quit `,
`Window`, `Minimize`, `Zoom` and `Toggle Full Screen`, alongside the `setMainMenu:`, `setAppleMenu:`,
`hideOtherApplications:` and `orderFrontStandardAboutPanel:` selectors that install and drive them. A
Vixen application on macOS therefore has ⌘Q, About, Hide, Services and a working Window menu today,
without a line of code.

**So what is actually missing is narrower than it was filed as**: an application's *own* menus —
File, Edit, View, Help — appear inside the window rather than in the system bar. That is a
platform-convention gap, not a broken quit.

**Three reasons to leave it.**

- **The test suite can drive a drawn menu and cannot drive an `NSMenu`.** A drawn bar is in the draw
  list, so it is screenshotted by the golden-image suite and clicked by a headless run; a native one
  is in another process's compositor and its items are reachable only through the accessibility API.
  Making the native path the default would move the menu bar out of every test that covers it.
- **A seam is cheap and the implementations are not.** `IUiMenuHost` is an afternoon. Three native
  implementations that keep enablement, checkmarks, key equivalents, dynamic items and the responder
  chain's answers in step with a live `MenuBar` are not, and a half-built one is worse than none: a
  greyed item that should be live reads as a broken command.
- **Nothing above is blocked on it.** Every capability the menus carry is reachable through the drawn
  bar and through `Commands`; this is about where the strip is painted.

**What changes the answer**, and these are the conditions to reopen on rather than a preference:

1. A shipped application whose users are on macOS full-time — the convention gap is felt daily there
   and barely at all on Windows or Linux, where an in-window menu bar is ordinary.
2. Global shortcuts that must work while a modal native panel is up: an `NSMenu` key equivalent does,
   a drawn one does not, because the drawn bar's window is not in the responder chain then.
3. The accessibility work landing. A native bar is read by VoiceOver for free; the drawn one needs
   `Core/Vixen.Ui`'s accessibility layer to have a consumer, which it does not yet have.

Recorded here rather than in the issue because a decision that lives only in a tracker is a decision
the next reader re-derives. See `docs/plan/49-responder-chain-and-appkit-parity.md` § Part 5 for the
proposed `IUiMenuHost` shape, which stands unchanged if the answer becomes yes.

## What the set says to a screen reader

Every control here carries a role, and every one with words of its own carries a name. It costs the
control a **virtual member** rather than a field — see
[docs/guide/ui/accessibility](../../docs/guide/ui/accessibility.md) — so a `CheckBox` reads its own
`IsChecked` when asked and there is no second copy to keep in step.

⚠ **A layout element is deliberately not in the tree.** `Panel`, `Card`, `Expander`, `Accordion`,
`ScrollView`, `Tabs`, `KeyValueList`, `Popover`, `Icon`, `TextBlock`, `Skeleton`,
`KeyboardShortcut` and both virtualisers answer `AccessibleRole.None`, and a bridge reads through
them. A tree that reported all of those would announce a four-field form as thirty nested groups.

⚠ **Three kinds of control report *no name*, and that is the design.** `TextField` and its
subclasses, `Slider` and `RangeSlider` have no words of their own; a placeholder is a
hint that vanishes once there is a value, and a number is not a name. An unlabelled one answers
`null` so that a gate can fail it, and `AddAccessibleRelation(AccessibleRelation.LabelledBy, caption)`
is how one gets a name. A `KeyValueList` row does it for whatever `Content<T>()` puts in it.

⚠ **`ComboBox` is the one control whose role is not on the control.** ARIA 1.2's editable combo box
puts `role="combobox"` on the *text input* — the input takes the focus and the input is what
`aria-expanded` is read from — so the outer element is `None` and the editor is a derived `TextBox`
that or-s the two expansion states into the base's `Editable`. Assigning the role instead would have
meant writing `aria-expanded` into `DeclaredAccessibleState` from an `OpenChanged` handler, which is
a second copy of "is the list open". `TextBox` is unsealed for exactly this and nothing else derives
from it.

⚠ **`Tooltip.Attach` also says the tooltip *describes* what it is attached to.** A tooltip is shown
by hovering, and a hover is a gesture a screen-reader user does not make — so without the relation a
sentence written for one kind of user is withheld from another. It is read on demand, so it is right
before the tooltip has ever opened.

The gate is `Core/Vixen.Ui.Controls.Tests/AccessibilityTreeTests.cs`, and two of its tests are about
the class rather than the instances: a reference window asserted with `AccessibilitySnapshot.Unnamed`,
and a **reflection sweep over the assembly's own type list** — every public control with a
parameterless constructor built and held to "a tab stop must be in the accessibility tree". A window
is a list somebody has to remember to add to; a type list is not.

## The words the set says

`ControlStrings` is twenty `StringId` declarations — "Clear" in a search box, "Dismiss" on a toast,
"Previous tab" on a docked group, "Search" in a property grid — plus an `All` list for a translator's
template. Thirteen of them were English literals in a control constructor until doc 46 § A3 counted
them: a localised window had an untranslatable seam in the one place a user cannot avoid looking, and
the only party who could close it was this repository.

⚠ **Five of them are never drawn.** A scroll bar, a colour picker's hex field and a gradient
editor's colour-space select and opacity slider have no caption on screen at all, so their only
words are the ones a screen reader says — and a literal there is an English announcement in a
localised window that nobody can see to report. `ScrollBar` reads its two in a *virtual*, which makes
it the one name in the set that follows a language change on a control already on screen. See
[docs/guide/ui/strings](../../docs/guide/ui/strings.md).

⚠ **One class covering `.Advanced` as well**, which references this assembly. A second declaration
class over there would put the boundary between "a control" and "an advanced control" — an assembly
split about compile cost — into a translator's workflow, where it means nothing.

⚠ **Two ids for the two `"Close"`s**, the dialog's and the dock tab's. They are the same English word
and are not the same string, and an id that says *where* a string is used is what lets a language
distinguish them. Merging them saves a line and cannot be undone without a translator's file changing
shape.

⚠ **`DialogConfirm` and `DialogCancel` are defaults rather than labels.** `DialogService.Confirm`
falls back to them when the caller does not name its buttons, and it reads them *per call* rather
than caching them in a static — a static initialiser runs once and would freeze the words in
whichever language happened to open the first dialog. They were the literals `"OK"` and `"Cancel"`
until the catalogue became reachable from here, which is the row doc 46 § A3 recorded as owed to
itself.

⚠ **The labels are read in `OnCreated`, so a control shows the language it was built in.** That is
not a live binding: `Strings.Catalog` is a signal and an *expression* that reads it re-runs, but an
assignment in a constructor is not an expression. Re-labelling a live control set would need an
effect per label and somewhere to dispose it — listed under the gaps below.

⚠ **The class is checked rather than trusted**, by `StringDeclarationAnalyzer` in
`Vixen.Ui.Generators`, which this project names as an analyzer: a declaration missing from `All` is
`VXS0310`, two declarations under one id are `VXS0311`, and a `StringId` built anywhere else in this
assembly is `VXS0312`. The half that needs the whole tree — a declaration used nowhere, which cannot
be decided here because ten of the twenty are used only from `.Advanced` — is `nuke CheckStrings`.

⚠ **And a translated label is not by itself a translated control.** What a screen reader says is the
accessible name, which is *computed*: `ButtonBase` answers with its `Label`, so eleven of the thirteen originals
declarations reach a screen reader with nothing written for them. The two that did not — a string
that went to a `Placeholder`, which is deliberately not a name, and a caption element no control had
related a slider to — showed the translation and announced nothing at all, and neither a localisation
test nor an accessibility test could see it because each asserts its own half.
`AccessibilitySnapshot.Untranslated(root, ControlStrings.All)` is the assertion that spans them, and
`Core/Vixen.Ui.Controls.Tests/AccessibleNameLocalisationTests.cs` is a reference window held to it.

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
  downwards. ~~Still owed is the *editing* half — a caret that moves between lines, and Enter starting
  one — which is the text editor's item.~~ ⚠ **That sentence outlived the work**: `AcceptsNewlines`,
  Up/Down carrying the caret affinity, line-relative Home/End, Enter inserting a `\n` and Ctrl-Enter
  still submitting are all in `TextField`, and `MarkupTests` pins the last of those because it is the
  one collision between two claimants for the same key.
- ~~**The caret does not blink.**~~ It does, and ⚠ **without a subscription**, which is the part worth
  knowing. `UiDocument.Draw` rebuilds the draw list and diffs it every frame, so a caret only has to
  read `Document.Now` where it is already being painted — and `OnDraw` returns before that on a field
  without the focus, so an interface with forty fields on it costs exactly what it did. A
  `UiDocument.Ticked` handler, which is what `Tooltip` needs and what this looked like it needed,
  would have made every one of those frames differ. `CaretBlink` is the half period, the phase is
  measured from the last time the caret moved so typing holds it solid, and `TimeSpan.Zero` is a solid
  caret for a reduced-motion setting.
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
  own objects and about a dozen elements. ~~⚠ Fixed row heights only — virtualisation has to know where
  row 40 000 is without measuring the 39 999 above it, and variable heights need a running-sum index
  that is a different control.~~ The index is here, and ⚠ **it is not a different control**: `TreeView`
  delegates its pool to this one, so a second would have duplicated the pool, the parking, the
  `LayoutFinished` subscription, `RowOf` and `ScrollIntoView` to gain one array — and a caller wanting
  one row taller than the rest would have had to choose a control rather than set a property. A
  Fenwick tree over each item's difference from `--row-height` makes an offset, the total and "which
  item is at this offset" logarithmic; the uniform path is untouched and is what runs until something
  calls `SetRowHeight` or turns `MeasureRows` on. Nothing has to call `Realise`: it runs on
  `UiDocument.LayoutFinished`, which is the only place that knows how tall the viewport ended up.
  - ⚠ **The heights are absolute and the tree holds the differences**, not the other way round. The
    estimate comes out of the cascade, so a caller setting heights before the first style pass sets
    them against `--row-height`'s *fallback* — and a delta-only index cannot be re-based when the
    estimate arrives, because a stored zero is indistinguishable from an item nobody measured.
  - ⚠ **`MeasureRows` is an estimate being corrected, so the scroll is anchored across a correction.**
    Learning that the rows above the viewport are taller than the estimate moves everything below them
    down on a frame the reader did nothing. It also has to *settle*: heights come from a layout that
    depends on the heights, and a virtualiser that asks for another pass for ever draws a frame
    permanently one pass stale with no sign of it but `UiDocument.Settled`.
  - ⚠ **Nothing in the tree turns either on yet, and that is worth saying rather than hiding.** The
    three virtualised lists here are uniform on purpose: a tree node is one line, an asset tile is a
    square, and the console's long message goes to its *detail pane* rather than to a taller row —
    which is what Unity does and what the pane exists for. So this is a primitive without a caller,
    which is this repository's commonest defect; it is written down here and filed rather than left
    to be discovered by somebody grepping for callers. What would change the answer is a list whose
    rows genuinely differ — a diff view, an inspector with expandable rows, a chat log.
