<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Ui.Desktop

The window, the device and the four steps of a frame, for an application whose whole content is a
user interface.

```csharp
static int Main(string[] arguments) =>
    UiApplication.Run(
        new UiApplicationOptions {
            Title = "Hello",
            Content = () => new Shell(),          // Shell.vxml
            Styles = { VixenUtilityStyles.Css }   // generated from your .vcss
        },
        arguments
    );
```

That is a titled, resizable, DPI-correct window with the control theme installed, the utility classes
loaded, a font found, all eight shader stages wired, `--frames N` understood and a second window
available the moment a docking host asks for one.

## Why this assembly exists

**It existed three times before it existed once.** `Samples/02-HelloUi`, the `vixen-app` template and
`Vixen.Editor.Host` each carried a Vulkan device, a swapchain, a render graph, an atlas upload,
resize coalescing and a suboptimal-present rule. Nothing failed, and the copies had already drifted
in three ways that produce a wrong picture rather than an error:

| | |
|---|---|
| The sample never called `UiRenderer.Compose` | Every translucent subtree drew at full strength, so a disabled control — which `ControlTheme.vcss` fades with an `opacity` — came out opaque. |
| The sample wired four of the eight shader stages | `filter`, `mask-image` and `backdrop-filter` cascaded, resolved, and did nothing. |
| Only the editor cached its tessellation | The other two flattened every path in the draw list sixty times a second for a window where nothing had moved. |

What is here is the union of the correct halves.

**And it is what makes the `Vixen.Ui` ⇸ `Vixen.Engine` boundary cheap to keep.** `Tools/Vixen.App` is
the engine's application host and it references `Vixen.Engine`, so an interface-only application that
wanted a window had to choose between writing four hundred lines and dragging a scene graph, an ECS
world and a fixed-step accumulator behind it. `CheckArchitecture` asserts `Samples/02-HelloUi` still
makes that choice; this assembly is why making it costs nothing.

There is no engine here, no scene, no world and no fixed step. There is a `UiDocument`, a
`VulkanDevice` and a loop.

## The four types

### `UiApplication`

The loop. **Four steps, and only the last knows what a GPU is** — which is what makes `--frames N`
meaningful on a machine with no Vulkan at all: everything above the RHI still runs.

- `Run(options)` opens the window, runs the loop, returns an exit code.
- `Run(options, arguments)` reads `--frames N` first.
- `Started`, `Frame` and `Stopping` are the hooks. They exist as events *and* as properties on the
  options, because the short form constructs the application itself and hands a caller nothing to
  subscribe to.
- `Document` is public: a hot-reload watcher is built over it, a test drives it with no window. What
  an application should *not* do through it is build its interface — that is `Content`, and a `.vxml`.

⚠ **It draws every frame rather than when something changes.** Redrawing only on input is the right
end state for a desktop application and it is not free: every animation, every timer and every
background task's progress has to say that it moved, and one that forgets leaves a progress bar
frozen at forty per cent. The *tessellation* is skipped for a window whose drawing did not change,
which is most of the cost of a still frame.

### `UiWindowSurface`

One per window: a swapchain, a `UiRenderer` and the geometry between them. This is the editor's
`EditorPane`, lifted — it was the only one of the three copies that could open a second window,
publish its granted colour gamut per surface, and skip tessellating a frame that had not changed.

⚠ **A renderer each, rather than one shared.** The renderer rings its vertex and box buffers across
the device's frames in flight and advances a region per `Upload`, so two uploads in one device frame
consume two regions — and after as many frames as there are regions the second window writes over
geometry the GPU is still reading. Sharing it is a validation-clean way to draw yesterday's frame.

### `UiShaderLibrary`

All eight modules, embedded, compiled on demand. **The whole set, because half of it is optional in
the way that hurts**: `UiShaders` degrades a missing stage to a picture rather than to a failure, so
an application that names four of eight has four features that cascade, resolve, and silently do
nothing. A host with a shader table to fill is a host where somebody fills half of it.

**They are Raven's, from one `Shaders/Ui.rvn`** — compiled by this repository's own compiler and
gated by `./build.sh CheckShaders`, rather than by whatever `glslc` was on the machine of whoever
last touched them. That matters more than it sounds: the same eight were committed three times as
GLSL, and `SharedUiShaderTests` was written after two of the copies had already lost the whole shadow
path. [`Shaders/README.md`](Shaders/README.md) has the detail, including the three numbers a host has
to get right — the vertex attribute locations, the push-constant offsets, and the pipeline layout's
single range — and how each is checked.

### `SystemFonts`

Borrows a face from the operating system. A starting point, not a shipping answer — an application
that ships decides what it looks like and registers its own asset. Nothing found is not a failure:
the document has no face, every label measures zero, and the controls draw their boxes exactly as
before. Text is a thing an element has, not a thing the layout requires.

## The user-agent stylesheet

`UiApplication` loads four declarations of its own, at `StyleOrigin.UserAgent`, so anything an
application writes beats them. **A browser does not make an author write `body { height: 100% }`
before a page can fill a window, and neither should this.** Both of them were found by looking at a
picture, and both read as an engine bug rather than as a missing line:

```css
root { flex-direction: column; align-items: stretch; }
.ui-window-content { flex-grow: 1; min-height: 0px; }
```

⚠ **The root is a column and CSS's initial value is `row`.** A window's content stacks — a menu bar,
then a body — and a root left as a row lays those out side by side, each as wide as its own text.

⚠ **`ui-window-content` is the class `UiApplication` puts on the mounted component's host element**,
which is neither the root nor the markup's first tag: a component draws into a host of its own,
`<app-shell>` for a `Shell.vxml`, and that element is one no file mentions and nothing styles.
Without `flex-grow` it is content-sized. With the root a row that made the whole of
`Samples/02-HelloUi` render in a strip down the left of a black window.

⚠ **The same trap is one level down and this sheet cannot reach it.** A component mounted into a
panel — a `DockPanel`, a card, anything — has a host element too, and it needs the same two
declarations. `Samples/02-HelloUi`'s `Theme/shell.vcss` names its three panel tags for exactly this,
and the symptom there was a blank hierarchy panel: the tree had realised its rows correctly, into a
box with no height.

## What it does not do

- **It does not redraw on demand.** See above.
- **It does not own the interface.** `Content` is a factory for a `Component`, which in practice is
  what a `.vxml` compiled to. Everything about what the application *is* lives there.
- **It does not know about docking.** A torn-off panel becomes a second window because
  `PlatformWindowHost` fills `IUiWindowHost` and the document asks for one; nothing here names
  `Vixen.Ui.Controls.Advanced`.
- **It does not hot-reload.** `Vixen.Ui.HotReload` is a development tool and is neither trimmable nor
  AOT-compatible; wiring it is six lines in `Started`, which `Samples/02-HelloUi/Program.cs` is the
  worked example of.

## Regenerating the shaders

```bash
./build.sh CheckShaders --update-shaders
```

That recompiles `Shaders/Ui.rvn` and rewrites what differs. The same gate, run without the flag,
fails when a committed module is not what the compiler produces — so a `.rvn` edited and not
recompiled cannot sit in a commit. [`Shaders/README.md`](Shaders/README.md) has the one-source
command and the reasons `--emit-reflection` is not optional.
