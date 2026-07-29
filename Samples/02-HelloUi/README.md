# 02 — Hello UI

A small editor shell, on a window, with no engine underneath it.

```
dotnet run --project Samples/02-HelloUi
dotnet run --project Samples/02-HelloUi -c Release -- --frames 300
```

## What it proves

**An absence.** [docs/plan/02](../../docs/plan/02-repository-layout.md) § Samples describes this one
as *"Vixen.Ui only, no engine — proves the UI/Engine boundary"*, and
[doc 15](../../docs/plan/15-risks-and-open-questions.md) makes it the thing that proves the framework
standalone before the editor is allowed to depend on it.

So there is no `Vixen.App` here. The host that assembly would have provided — the window, the device,
the swapchain, the frame loop — is `Program.cs`, and it is about a hundred lines. Using `Vixen.App`
would have been shorter and would have pulled `Vixen.Engine` in behind it, at which point the sample
would demonstrate nothing. `CheckArchitecture` asserts the absence, so it cannot be undone by
accident: adding either reference fails the build with a message saying why.

## The four steps of a frame

Only the last one knows what a GPU is:

1. **Pump** — `platform.PumpEvents()` into `PlatformInput.Dispatch`, which turns a `PlatformEvent` into a
   `PointerEvent`, a `KeyEvent`, a `TextInputEvent` or a `WheelEvent`.
2. **Update** — the cascade, the font sizes, the layout style, flexbox.
3. **Draw** — the walk that turns the laid-out tree into a `DrawList`, diffed against last frame's.
4. **Present** — `UiGeometryBuilder` turns the list into vertices; `UiRenderer` uploads and records
   them; the swapchain presents.

`Shell.cs` builds the interface and touches none of that. It is a `UiDocument` and nothing else,
which is the point: the framework has to be usable, and testable, without any of the machinery that
eventually puts it on a screen.

## What is in it

A menu bar with submenus and shortcut labels, over a `DockingHost` holding three panels:

| | |
|---|---|
| **Hierarchy** | A `TreeView` of a thousand nodes, of which about thirty exist as elements. |
| **Controls** | A scrolling gallery: buttons in every variant, checkboxes including an indeterminate one, a switch, a radio group, text fields, a numeric input, a select, sliders, a progress bar, a spinner, an alert, tabs, an accordion, a breadcrumb and a paginator. |
| **Inspector** | A `PropertyGrid` over a hand-written type descriptor — the shape `Vixen.Core.Reflection.Generator` emits. |

Toasts appear over the lot. Panels can be dragged between groups, splitters dragged, tabs closed;
**View → Reset Layout** puts it back and **View → Toggle Dark** switches the theme by adding one
class to the root.

On exit it prints the docking arrangement as YAML, which is the round trip
[doc 14](../../docs/plan/14-roadmap.md) names as Phase 4e's exit criterion, demonstrated by the thing
it is about.

## `--frames N`

Runs exactly N frames and exits, so the whole stack can be proved to start, present and stop without
a validation error or a hang — the same argument Samples/01 makes for the flag it introduced.

⚠ **No CI step runs it yet.** This paragraph used to say the flag was how CI proved that, and it is
not: `ci.yml` builds and tests on all three platforms and invokes neither sample. The flag is what a
CI step would use; nothing uses it. Samples/01 heads its equivalent section "Running it in CI",
which is a recipe rather than a claim — but nothing runs that one either.

It also prints what the frame cost:

```
287 elements · 192 commands · first frame 590.5 ms · then over 270 frames: mean 0.427 ms, worst 4.901 ms
```

⚠ **The first frame is reported separately because it is not a frame.** It carries the JIT, the font
load, and the rasterisation of every glyph the interface uses into the MSDF atlas — half a second of
work that happens exactly once. Folding it into a mean over three hundred frames triples the answer
while hiding what the answer is about.

⚠ **What is timed is the UI frame, not the presented one**: steps 2, 3 and 4a above. Including the
swapchain would measure the display's refresh rate.

The measurement above is this machine, in Release, at the sample's own size. Doc 14's budget is
*5 000 elements under 2 ms*, and this interface is 287 — because the tree virtualises, which is what
it is there to show. The number at the roadmap's scale is `Vixen.Benchmarks.Ui`'s
`DocumentBenchmarks`, which has now been run: **8 001 elements, 0.230 ms, zero bytes allocated.**

⚠ **The same run found that an interaction costs a full cascade** — one class toggled on one row of
that document is 9.50 ms and 8.87 MB, because `UiDocument.Update` calls `StyleEngine.ResolveAll` and
Phase 4b's `StyleUpdater` has no production caller. It is invisible from this sample: 287 elements
put the same defect at about a third of a millisecond. See the benchmark's
[README](../../Benchmarks/Vixen.Benchmarks.Ui/README.md).

## The font

⚠ **Borrowed from the operating system rather than committed.** The repository has no Latin UI font
to commit: the fourteen files under `Vixen.Ui.Text.Tests/Fonts` are the Unicode Consortium's shaping
fixtures — Balinese, Kannada, Lanna — and a sample that drew its buttons in Lanna would be
demonstrating the shaper rather than the controls.

`Fonts.cs` looks for a plain TrueType face in the usual places on each platform. **Finding none is
not a failure**: every label measures zero, and the controls draw their boxes and their chrome
exactly as before — which is worth knowing about the framework as well as convenient here. Text is a
thing an element *has*, not a thing the layout requires.

A real application ships its own font as an asset. That is [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s
business.

## The shaders

Four SPIR-V modules, committed, and the GLSL beside them. `UiRenderer`'s own remarks say the modules
are *"supplied rather than compiled here"* because turning shader source into modules belongs to
Raven — which already carries `Ui/Msdf.rvn` and `Ui/RoundedRect.rvn` for exactly this. Until that
path is wired, a caller hands over what it has.

These are the same four the golden-image fixture drives the renderer with, so the sample and the
reference pictures cannot disagree about what the shaders do. Regenerating is
`glslc Shaders/ui.vert -o Shaders/ui.vert.spv`.

## Two spaces, and where they meet

⚠ **The document is laid out in device-independent points; the framebuffer is physical pixels.** On
this machine the window is 1280×800 points and 2560×1600 pixels, a DPI scale of two, and three things
have to agree about which space they are in:

| | |
|---|---|
| The document, the geometry and the pointer | **points** — `FramebufferSize / DpiScale`, and what SDL already reports positions in |
| `UiRenderer.Record`'s `surface` | **points** — it is the extent the projection maps onto clip space |
| `UiRenderer.Record`'s `scale` | how many pixels a point is — the scissor, and only the scissor, is in framebuffer pixels |

Getting the first two wrong draws the whole interface into the top-left quarter of the window;
getting the third wrong clips every scroll view to a quarter of its rectangle. Both did happen here,
and the second is the one that has no visible cause: the pointer goes on hitting the controls where
the *layout* says they are, so a mis-scaled projection reads as a renderer that is mysteriously
small rather than as a unit mismatch. `UiImageTests.Scaled` is the picture that catches it.

## Known gaps

- ~~**`UiInput` lives here and should not.**~~ Closed. `Vixen.Ui` is a `Core/` assembly and
  `Vixen.Platform` is not, so the framework cannot depend on what produces its events; the editor
  became the second consumer and `Vixen.Platform.Ui.PlatformInput` is where the fifty lines went.
  This sample now references that assembly and has no copy.
- **The web head is not built.** Doc 10 makes this sample the Web target's real goal; that is Phase
  10, and it needs `net10.0-browser` and the wasm workload.
- **Resizing costs an explicit `Refresh`.** Nothing tells an element that its box changed, so the
  virtualiser has to be told — see `Shell.Resize`. A "layout finished" callback on `UiDocument`
  closes it.
