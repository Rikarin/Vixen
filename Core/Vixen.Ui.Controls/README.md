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

- **`Image` reserves space and draws nothing.** The draw list has no texture command yet.
- **`TextArea` is a taller `TextBox`.** Nothing in the framework wraps a line, so there is no second
  line for Enter to start. The tag exists so the markup will not have to change when it does.
- **Timed behaviour needs a host tick.** `Tooltip.Tick` and `ToastHost.Tick` exist for the same
  reason `GestureRecognizer.Tick` does: nothing in this assembly is told what time it is except by an
  input event, and those stop arriving exactly when a pointer comes to rest.
- **An overlay outlives the control that made it.** A select's list is a root child, so removing the
  select leaves the list behind. What is missing is an `OnRemoved` hook, which is a change to
  `UiDocument` rather than to a control.
- **`VirtualizingPanel` is not here.** `ScrollView` keeps everything in the tree. Doc 09 makes
  virtualisation a first-class primitive and it is owed.
