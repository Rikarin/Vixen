# Vixen.Ui.Testing

Automated interface testing for games: a chainable, self-retrying command API over a real
`UiDocument`, and visual regression against committed pictures rendered without a GPU.

Cypress's shape, because it is the right one — you name something, you say what should be true of it,
and the waiting is the framework's problem rather than yours. What is different is everything
underneath, and the differences are the interesting part.

```csharp
using var ui = UiTest.Create(800, 600);
ui.Load("""
    .btn   { width: 120px; height: 40px; background-color: #3b82f6; border-radius: 6px; }
    .toast { position: absolute; left: 0; top: 0; width: 200px; height: 48px; }
""");

var save = ui.Create("button", ui.Document.Root, "save", "btn");
save.Text = "Save";

ui.Ticked += () => game.Update(ui.Options.FrameDelta);   // your game drives the interface

ui.Get("#save").ShouldBeVisible().ShouldHaveText("Save").Click();
ui.Get(".toast").ShouldExist().ShouldContainText("Saved");   // runs frames until it appears
ui.Screenshot("save-pressed");
```

## Waiting is counted in frames

The one decision everything else follows from. Cypress waits on a wall clock because the browser is
somebody else's loop; here the test owns the loop, so *wait for the toast* means **run frames until
it appears**.

That is not a translation detail, it is a better property. The suite is deterministic, runs as fast
as the machine can go, behaves identically on a loaded CI runner and a quiet laptop, and can be
replayed. It also satisfies what [doc 12](../../docs/plan/12-build-ci-and-testing.md) asks of every
test in this repository by name: no `Thread.Sleep`, no ambient clock, no test that fails only when
the machine is busy.

`Options.RetryFrames` is the budget — sixty, a second of a sixty-hertz game. `Options.FrameDelta` is
what the clock advances by, and it is what the gesture recogniser reads, so a long press is
`Press()` then `Advance(TimeSpan.FromSeconds(1))` rather than a sleep.

⚠ **A condition already true costs no frames.** Otherwise every assertion would advance the clock and
a suite's gesture timings would depend on how many things it happened to assert.

## A subject is a recipe, not a snapshot

`ui.Get(".toast")` holds the *question*, and asks it again every time it is used. That is what makes
waiting work at all — an element the game creates three frames later is found by the assertion that
was written before it existed.

It also means a subject survives its elements. A list rebuilt between two commands hands the second
command the new elements rather than a fistful of removed ones, which is the *element is detached
from the DOM* failure Cypress has to warn about and this cannot produce.

The cost is that resolution is paid per command rather than once. That is the same order as one style
pass, in a harness that has already agreed to run frames.

## Selectors are the cascade's own

`Get(".panel > button:nth-child(odd)")` compiles through `SelectorCompiler` and runs through
`SelectorMatcher` — the same two objects a stylesheet goes through, against the same interned name
table.

A test framework with its own selector engine would agree with the stylesheets on `.panel button` and
disagree on `:nth-child(2n+1)`, `:not(.a, .b)` and what `:has()` means, and it would disagree
*silently*, so a test would pass against a selector that styles nothing.

⚠ **A selector that will not compile throws rather than matching nothing.** The compiler drops what it
does not support with a diagnostic, which is right for a stylesheet — one bad rule should not take the
sheet down — and wrong here, where it would report `expected 1, found 0` and send somebody looking at
the interface for an element that was never being asked for.

## Nothing reaches past the framework

A click is a `PointerEvent` at a real coordinate, dispatched through `UiDocument.Dispatch`,
hit-tested and routed. So pointer capture, `pointer-events`, the `:active` state and the gesture
recogniser are all under test rather than bypassed. Calling a control's handler directly would make
every test pass on an interface where the button is behind a modal.

### `ShouldBeHittable`, and why it is the assertion worth having

A modal backdrop, a full-screen overlay left up by a state machine, a tooltip that forgot
`pointer-events: none` — each leaves a button visible, laid out, and completely unclickable. A suite
that asserts visibility passes while the game is unplayable.

Every action checks this before it acts, and the failure names *what it hit instead*:

```
#save · click.
  Found: <div #backdrop .modal> is on top of <button #save .btn> at its centre (60, 20)
  Waited: 60 frames
```

`Click(force: true)` skips the check, for the tests that are deliberately about something being in
the way. It does not redirect the event — the click still lands on the backdrop, which is the point.

## Failures carry the log and the tree

Cypress's best idea, copied before any of the assertions. What you need when a test fails is not the
failing line but the twenty commands before it:

```
Commands:
  get "#save" · should be visible  → <button #save .btn>
  get "#save" · click              → <button #save .btn>
  get ".toast" · should exist      → failed — no elements [60 frames]
Interface:
  <root> 0,0 800×600
    <button #save .btn> 0,0 120×40 "Save"
```

The log answers *did the test get where it thought*, the tree answers *is my selector wrong*, and the
count answers *did it never appear*. A frame count on a line that passed after fifty-eight retries is
a test about to become flaky, visible without being looked for.

## Visual regression, without a GPU

`Screenshot(name)` renders the interface on the CPU and compares it with a committed PNG.

`SoftwareUiRasterizer` consumes `UiGeometry` — the same vertices, indices, shape records and
scissored draws `UiRenderer` would submit — and does the three fragment shaders' arithmetic itself.
So the draw list, the geometry builder, the clip resolution, the path tessellator and the glyph atlas
are all under test; only the driver is not.

**Why not the real renderer.** A suite that needs a Vulkan device runs on the machines that have one,
which in practice means it does not run: not on a laptop without the SDK, not in a container, not on
the CI leg with no GPU. A game developer's UI suite has to run everywhere their unit tests run or it
is the suite nobody has seen pass.

⚠ **And it buys something a GPU cannot: an exact comparison.**
[`Vixen.Graphics.Golden.Tests`](../../Platform/Vixen.Graphics.Golden.Tests/README.md) compares
perceptually because MoltenVK and lavapipe round the same sRGB conversion differently and both are
conformant — a bitwise suite there is red from the day it is written. The arithmetic here is the same
on every machine, so `ImageTolerance.Exact` is the default and a one-pixel shift in a glyph is a
failure rather than something under a threshold.

⚠ **This does not replace the golden-image suite.** It cannot catch what lives below `UiGeometry`: a
wrong descriptor binding, a vertex layout that disagrees with the shader, a projection that flips y.
Those are what that suite is for. The two answer different questions and both are worth asking.

⚠ **The port is of the shaders as written, including their approximations.** `BoxDistance` turns an
elliptical corner into a circle by scaling and scales the distance back by the smaller semi-axis,
because that is what `ui-box.frag` does. Improving it here would make this renderer disagree with the
one that ships, which is the one failure mode a testing renderer must not have.

### When one fails

Three files land in `Options.ArtifactDirectory`, which CI can upload without knowing what is in it:

| File | What it is |
|---|---|
| `<name>.rendered.png` | what this run drew |
| `<name>.expected.png` | what is committed |
| `<name>.diff.png` | the differing pixels in red, over a dimmed reference |

### Accepting a change

```bash
VIXEN_UPDATE_SCREENSHOTS=1 dotnet test
```

Then **look at what it wrote** before committing. An accepted picture is written into the source tree
rather than beside the binary: rewriting the output copy makes the run pass and changes nothing
anybody can commit, and the next clean build restores the old picture.

⚠ **A missing reference fails rather than recording one.** The obvious behaviour — write it and pass —
makes the first run of every screenshot green, which means nobody ever looks at the picture everything
later is measured against. It writes what it drew where a human can open it, says how to accept it,
and fails.

## What is different from Cypress, and why

**Commands run eagerly.** Cypress enqueues and returns a chainable promise because it is JavaScript.
Translating that faithfully into C# would buy a debugger that never stops on the line that failed.
`Get("#save").Click()` has clicked by the time it returns; each command does its own waiting inside
itself. The chaining reads the same and the stack traces are real.

**Assertions are methods, not strings.** `ShouldBeVisible()` rather than `should("be.visible")`: the
set is discoverable by typing a dot, a typo is a compile error rather than a test that passes, and
each takes the type its subject actually has.

**`Type` takes text; `PressKey` takes a key.** Cypress packs key names into the string because it has
nothing better. Here a key is an `InputKey` — a physical position by its US-QWERTY legend — and text
is what a keyboard layout produced, which is the distinction `KeyEvent` and `TextInputEvent` already
make. `Type` sends one event per rune, so a text box that maintains a caret or an undo stack is
exercised rather than one that appends whatever it is handed.

**An action refuses to pick one of several.** A selector that matched three buttons and clicked the
first is a test that keeps passing after somebody adds a fourth in front, having silently changed what
it tests. `First()` says so out loud when that is what is wanted.

## `Ticked`, and where the game goes

Almost nothing an interface does happens on the frame that caused it: a click starts a request, a
state machine advances, an animation finishes, and three frames later a toast appears. `Ticked` runs
once per frame, after the gesture tick and before the passes — the order a game loop has — so the
game under test drives the interface exactly as it does when it ships.

A harness with no per-frame hook can only test interfaces that change synchronously inside an event
handler, which is the easy half and not the half that breaks.

## Scope

This tests **interfaces**. It drives a `UiDocument`: elements, styles, layout, input, drawing. It is
not a harness for the world — entities, physics, netcode — and does not pretend to be; `TestApp` in
[doc 12](../../docs/plan/12-build-ci-and-testing.md) is where that belongs, and `Ticked` is the seam
it would plug into.

## Two fingers

`Pinch(scale, rotation)` puts two pointers either side of the element's centre and moves them
symmetrically, so the midpoint stays put and the gesture is a pure scale and rotation. Degrees go in;
`TransformEvent` reports radians, because that is what a transform matrix consumes.

```csharp
ui.Get("#map").Pinch(2f, rotation: 90f);
```

`MovePointer`, `PressPointer` and `ReleasePointer` all take a pointer id for the cases the shorthand
does not cover — two fingers on two different elements, or a second finger arriving mid-drag.

⚠ **Starting a pinch cancels the drags those fingers had begun**, and neither finger produces a tap
or a long press afterwards. Without that, two fingers both pan and pinch and a map moves twice as far
as either gesture asked for.

⚠ **The gesture goes to the nearest element containing both fingers.** Two fingers on one tile target
the tile and bubble from there; one finger on each of two tiles targets what contains them, because a
pinch belongs to neither half.

## What `ShouldBeVisible` checks

Four ways to be invisible, and it checks all four: removed from the tree, an empty rectangle on the
element or any ancestor (which is how `display: none` and a collapsed container both arrive),
`visibility: hidden`, and an `opacity` of zero anywhere above it. The failure says which:

```
Found: <div #buried .child> is inside <div #panel .panel>, which has opacity 0
```

⚠ **It agrees with the picture because the picture agrees with it.** These are the same four tests
`DrawListBuilder` applies when deciding whether to emit anything — `visibility` and `opacity` were
implemented there in the same change, because an assertion reading a property the renderer ignored
would be worse than one that never checked it.

⚠ **What it still does not check:** whether the element is inside the viewport, and whether anything
is on top of it. `ShouldBeHittable` answers the second.

## Computed style

`ShouldHaveStyle` compares the resolved value as text, which is right for keywords.
`ShouldHaveColor` and `ShouldHaveLength` parse, because ExCSS normalises on the way in — `#3b82f6`
comes back as `rgba(59, 130, 246, 1)` — and a test should be able to write what it means.

⚠ **Shorthands do not survive to the cascade.** ExCSS expands `margin`, `border-color` and
`border-radius` on parse, exactly as a browser does, so ask for `margin-left` and
`border-top-left-radius`. A test written against the shorthand is told the property is absent.

## The accessibility tree, as text and as two audits

`AccessibilitySnapshot` is here rather than in a test project because there are two control
assemblies and they cannot see each other; a renderer copied into both would be two renderers
producing two formats the day one of them was improved.

- `Render(root)` — the tree as `role "name" = "value" [state state]`, one line per node, indented by
  accessibility-tree depth. Elements that are not nodes are walked *through*, so a control that
  grows a wrapper does not move in the snapshot; owned elements are emitted under their owner rather
  than where the tree has them.
- `Unnamed(root)` — every element that is a widget with no accessible name, and every focusable
  element with no role. ⚠ **Assert this first, always.** `Render` of a document with no
  accessibility at all is the empty string, and an expectation of the empty string matches it
  perfectly — a snapshot cannot fail vacuously and this can.
- `Untranslated(root, declarations)` — every element still announcing the `Source` text of a
  declaration the catalogue has a translation for. The one defect neither an accessibility test nor
  a localisation test can see: an accessible name is *computed*, so a control that answers from what
  it already shows goes through the catalogue for free and one that answers with a literal does not,
  and in English the two are identical. A declaration with no translation loaded is skipped, because
  the source text is the right answer when nothing translates it.

`Untranslated` is written against the declarations rather than against a list of controls, which is
what keeps it meaningful as controls are populated. Pair it with a check that the window actually
says the strings it was built to exercise: a window that announces nothing satisfies it.

## Owed

- <s>**Group opacity.** `opacity` is carried down the tree as a multiplier rather than composited
  into an offscreen surface, so two overlapping children of a half-opaque panel show through each
  other where CSS says they should not.</s> **Done, and this renderer composites.**
  `SoftwareUiRasterizer.Frame` gives each `UiGeometry.Layers` entry a viewport-sized float target of
  its own, and `filter: blur()` is a separable Gaussian over that target — the same kernel the device
  runs, taken from `UiLayer.KernelRadius` rather than written twice. ⚠ The claim that the two agree
  is not this file's to make: `Vixen.Graphics.Golden.Tests.UiCompositingTests` renders one frame both
  ways and requires the pictures to match, which is the only thing that can say so.
- **A third finger.** ⚠ **Not this assembly's, and refused where it does live.** `MovePointer`,
  `PressPointer` and `ReleasePointer` already take any pointer id, so a test can put three fingers
  down today; what ignores the third is `GestureRecognizer`, in `Vixen.Ui`, and it says why —
  three-finger gestures have no agreed meaning across platforms, and averaging an arbitrary number
  of pointers into one scale is an approximation worse than the gap. A harness cannot recognise a
  gesture the framework does not.
- <s>**Assertions on the layout box.**</s> **Done.** `ShouldHaveSize` and `ShouldHavePosition`, and
  what they buy over reading `UiElement.Width` is two things rather than sugar. ⚠ **The cascade
  cannot answer the question**: `flex-grow`, a percentage, `min-width` and a flex line's free space
  are all decided by the layout pass, so `ShouldHaveLength("width", …)` on a flexed child reports
  *no such property* rather than the wrong number. ⚠ **And layout is a pass, not a property setter**
  — an element created by the line above is zero by zero until a frame runs, so a test reading the
  property directly either sees the previous arrangement or carries a hand-written `Frames(n)`
  guessing at *n*. The position pair is **absolute**, because the relative one is an offset inside
  whatever the parent turned out to be and passes on a panel that slid sideways carrying the element
  with it.

Licensed under Apache-2.0.
