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

## The theme

`ControlTheme.Install(document)` loads it as **`StyleOrigin.UserAgent`**, which is the point: a
game's own `button { … }` beats it at equal specificity, so restyling is one rule rather than a fork.
Colours go through custom properties — `--surface`, `--accent`, `--text`, and the rest — so a palette
is nine values on the root and a dark theme is a `dark` class.

Two things worth knowing before reading it. The layout defaults are **CSS's, not Yoga's**, so a
container with no `flex-direction` is a *row* and every rule that wants a column says so. And
`box-sizing: border-box` is set here on `*`, because `LayoutStyleBuilder` deliberately left that
property to a user-agent sheet rather than baking it in where an author could not see it.

## Known gaps

Said out loud rather than left to be discovered:

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
- **`VirtualizingPanel` is not here.** `ScrollView` keeps everything in the tree. Doc 09 makes
  virtualisation a first-class primitive and it is owed.
