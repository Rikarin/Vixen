# Vixen.Editor.Testing

The editor, headless, driven by synthetic input against the real element tree.

```csharp
using var editor = EditorSession.Start();

editor.Step("rename the crate")
      .Open("hierarchy")
      .ExpandAll(editor.Hierarchy)
      .DoubleClickRow(editor.Hierarchy, "Crate");

editor.Ui.Get("text-box").Type("Barrel").PressKey(InputKey.Enter);
editor.Ui.Contains("Barrel").ShouldExist();
```

[doc 20](../../docs/plan/20-editor-parity.md)'s E6 asks for this and its ordering note says to build it
during E1 rather than after E5 — *a harness written last is a harness written against a frozen
target.* [doc 11](../../docs/plan/11-editor.md)'s testing table is what it has to satisfy.

## It is the real editor

`EditorSession` constructs `EditorApplication`, in a project directory of its own, and drives it.
Not a stand-in, not a shell with stub panels. Everything worth asserting about an editor — which
panel a click landed in, what the inspector was handed, which of the several selections won, whether
a saved scene comes back — is wiring that lives in the head and nowhere else, and a harness that
built its own would be a harness for a copy of the thing that breaks.

⚠ **The frame is `EditorHost`'s four steps in `EditorHost`'s order**: the shell's tick, the layout,
the application's update, the draw. The update sits between the layout and the draw because a
viewport measures itself in render pixels from a box the layout pass produces, and the axis cross it
draws comes from the camera the update brings up to date. That is why `UiTest` grew an `Updated`
event: with only the pre-layout hook, the harness would have to run the update on the wrong side of
the layout, and then every test that passed would be saying nothing about the loop that ships.

No GPU, no window, no platform — the host's first four steps are all above the RHI, the same reason
`--frames N` is a smoke test on a machine with no Vulkan. Everything up to and including the draw
list runs; only the submission does not.

## `Ui` is the vocabulary, this is the nouns

Selectors, waiting-counted-in-frames, assertions, screenshots, two-finger gestures — all of that is
[`Vixen.Ui.Testing`](../../Core/Vixen.Ui.Testing/README.md)'s and none of it is reimplemented here.
`EditorSession.Ui` is a `UiTest` adopted over the shell's document.

What this adds is everything that has to know what an editor *is*:

| | |
|---|---|
| `Open("console")` | brings a panel forward — necessary before a *click*, not merely before an assertion, because a tab that is not in front has no size and a click at its centre lands on whatever is behind it |
| `Run("edit.delete")` | runs a command and **refuses a disabled one**, saying whether it is the enablement or the context that said no |
| `Menu("File", "Save Scene")` | opens the menu on the bar and clicks the line, through the pointer |
| `ClickRow(Hierarchy, "Crate")` | finds the realised row saying that and clicks it |
| `Answer("Save")` | presses a button on whatever dialog is up |
| `Restart()` | closes the editor and opens it again over the same directories |

### Why `Menu` exists next to `Run`

`Run` proves a command *works*. `Menu` proves it is **reachable** — that the line exists, is spelled
the way the test says, is not disabled, and is not underneath a submenu that never opens. Doc 20's
first bar is "nothing they reach for is missing", and that is a claim about the menu rather than
about the command. A suite with only `Run` passes on an editor whose File menu is empty.

### `Restart`, and why nothing else will do

"Save, reopen, assert" is a claim about what reached the disk. An in-process reload that kept the
same objects alive would pass for a scene that was never written, so `Restart` tears the whole thing
down — world, document, plugins, asset database — and builds a new one over the same two directories.

⚠ **It persists first, because that is what closing does.** The host writes the layout and the keymap
on the way down rather than on every change (a splitter drag raises a layout change per mouse-move),
so a restart that skipped it would prove the arrangement is *not* restored, which is the opposite of
the truth.

⚠ **`Ui`, `Shell` and `Scene` are different objects afterwards.** A test that held one in a local
across a restart is driving an editor that has closed.

## Two directories, kept apart

One holds the user's layouts, keymap and theme; the other is the project being edited. The editor
treats them as separate and so does this, because a scenario that restarts with a fresh project and
the same preferences — or the reverse — is testing something real and cannot be written otherwise.

Both default to a temp directory per session, deleted on the way out. ⚠ **A session never deletes a
directory it was handed**: a harness that ate a project somebody wanted to look at afterwards would
be one nobody points at a real project twice.

## Failures carry the scenario

Doc 11's scenario is eight verbs long — create, import, drag into the scene, edit, undo, save,
reopen, assert — and `expected 1, found 0` on the last one says nothing about which of the first
seven did not happen. `Step` names them, and every failure this type raises carries the trail and the
element tree:

```
No row saying 'Crate.prefab' is on screen. Showing: Assets, Scenes, Main.vxscene.

Steps:
  1. import a crate
  2. drag it into the scene

Interface:
  <root> 0,0 1600×1000
    ...
```

`EditorSessionException` is deliberately not `UiTestException`. That one means an element was not
there or was not clickable — a question about the interface. This one means a command is not
registered, a panel is not open, or a step did not happen — questions about the *editor*, whose
answers are somewhere else entirely.

## Fonts

⚠ **On by default, and turning it off is almost always wrong.** A document with no font measures
every label at zero, and a row whose label is zero wide is one whose hit test lands somewhere a
person's click would not — so a suite that asserts nothing about text still needs the font in order
to click anything. It is a switch at all so that a machine with no usable face fails with a sentence
rather than with a hundred missed clicks.

The shortcut format is decided again after the face is installed, exactly as `EditorHost` does it,
because whether `⌘` can be drawn is a question about the face. A harness that skipped it would render
menus a way the product never does.

## Scope

This drives the **editor**. `Vixen.Ui.Testing` drives a document and knows nothing about projects or
panels; `TestApp` in [doc 12](../../docs/plan/12-build-ci-and-testing.md) is where a harness for the
*world* belongs. All three are different questions.

Licensed under Apache-2.0.
