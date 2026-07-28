# Vixen.Editor.App

The editor's executable: a platform, a window, a device, and a frame loop over a `UiDocument`.

```bash
dotnet run --project Editor/Vixen.Editor.App -- --frames 5
```

`--project PATH` opens a project. With none, a scratch one under the user's data directory is used,
so a first run with no arguments still opens something real — a directory that does not exist yet is
the ordinary way to start one.

`--frames N` runs exactly N frames and exits, which is how CI proves the whole stack starts,
presents and stops without a validation error or a hang — the flag `Samples/01` introduced and for
the same reason. With no `--frames` it runs until the window closes.

## Three files, three jobs

| | |
|---|---|
| `Program.cs` | the platform and the window |
| `EditorHost.cs` | the device and the four steps of a frame |
| `EditorApplication.cs` | the project, the scene, which panels exist, which layouts, and what persists |
| `SceneEntity.cs` | the join: one entity as a row of editors and as something a gizmo can drag |

The third is the one a game team would fork. The first two are the same hundred lines
`Samples/02-HelloUi` has, and the loop is four steps worth naming: pump the platform's events into
the document, run the layout and draw passes, turn the draw list into geometry, record that geometry
into a frame. Only the last knows what a GPU is — which is why `--frames` means something on a
machine with no Vulkan at all.

## Not Vixen.App, and the reason is the frame loop

`Tools/Vixen.App` exists to run a game: it builds an ECS world, a fixed-step accumulator and a
systems graph, and it is the right answer for a game and for the editor's play mode. The editor's own
loop is an interface. It has no world, and the moment it has one it has it because a viewport is
hosting a game rather than because the editor is one. Doc 17's "the app is the executable" is the
same argument from the other side.

## What persists, and when

The user's — not the project's, which is `ProjectSettings/` and `Vixen.Editor.Core`'s business:

| File | What |
|---|---|
| `current.vxlayout` | the arrangement the editor was left in |
| `<name>.vxlayout` | arrangements the user saved by name |
| `keybindings.yaml` | only the bindings that differ from the defaults |
| `theme.yaml` | token overrides, if the user wrote any |

They live in the platform's data directory — `%APPDATA%`, `~/Library/Application Support`,
`$XDG_DATA_HOME` — because `IFileSystemHost` is what knows which, and the editor should not have a
second opinion.

⚠ **Written on the way down, not on every change.** A splitter drag raises `LayoutChanged` per
mouse-move, and an editor that wrote a file per frame of a drag would have the noisiest thing on the
disk be its window layout.

⚠ **Persisting happens before the document is disposed**, which is why it is at the end of `Run`
rather than in `Dispose`: reading the arrangement out of a disposed docking host would write an empty
layout over the one the user spent the afternoon arranging.

## Load order, which is not arbitrary

1. Register panels, layouts and commands.
2. Load the keymap — **after** the commands that own its defaults, or every override in the file
   lands on a command with no default and the file rewrites itself with the whole map in it.
3. Load the theme tokens.
4. Apply the saved layout — **after** the panels are registered, or a saved arrangement names panels
   the workspace cannot build.

A first run has none of the three files and opens on the Default preset in dark.

## The panels, and which of them are real now

`Vixen.Editor.Inspector` and `Vixen.Editor.SceneView` have landed, so three of the five panels are
looking at a real model:

| Panel | What it is |
|---|---|
| Hierarchy | a `TreeView` over the scene's entities; selecting drives the shared selection, and renaming a row is an undo entry |
| Inspector | an `InspectorView` over the selection, recording every edit on the scene document's stack |
| Scene | a `SceneViewport`: orbit, pan, zoom, the axis cross, gizmo modes and snapping — with nothing rendered in it yet, for the reason below |
| Project | still `TreeView` over three made-up folders; listing the asset database is the project browser's own job |
| Console | still a line of text |

The scene lives at `Assets/Scenes/Main.vxscene` and is opened on launch. A project that has none gets
the seeded one written immediately — the only time the editor saves without being asked, so that a new
project contains the scene you are looking at rather than something that exists until the window
closes. `Ctrl+S` saves; the menu item greys itself out from the document's own dirty signal.

⚠ **The scene panel draws no scene yet, and the reason has moved.** The draw list carries a texture
now and `Viewport` draws one — what is missing is something to put *in* the texture: a `RenderSystem`
and a `GraphicsCompositor` in this host, rendering into an offscreen target that gets handed to
`UiRenderer.RegisterImage`. Everything around it already works: the camera, the gizmo arithmetic, the
hit-testing and the undo are all driven and all correct, and the corner axis cross is drawn as
ordinary UI paths so the camera is legible while you orbit it.

## The world, and why the editor has one

`Program.cs` says the editor's loop is an interface and has no world. It now owns one, and that is
not a contradiction: nothing here ticks systems, runs a fixed step or updates behaviours. The world
is a **document** — the thing the hierarchy lists, the inspector edits and the gizmo drags — and it
starts being a running game only when play mode says so.

`SceneEntity` is the join, and it lives here rather than in either library on purpose. An entity is a
handle and a set of chunk rows; an inspector shows the members of an *object*. Something has to be
that object, and putting it in the application is what keeps `Vixen.Editor.Inspector` from knowing
what an ECS is and `Vixen.Editor.SceneView` from knowing what a property drawer is. It is also the
gizmo's target, so a drag and a typed number cannot disagree about what "position" means.

⚠ **The selection is polled once a frame rather than subscribed to.** `Selection<T>` is
signal-backed and an `Effect` would be the better wiring, but nothing in this loop flushes the
reactive scheduler and adding one changes the loop's contract for notifications and background tasks
as well. Comparing a handful of handles once a frame is not a cost.

⚠ **A panel's factory runs again when it is reopened**, so nothing durable may live in one. The
camera is kept as a `ViewBookmark` on `EditorApplication` and restored when the scene panel is
rebuilt; without it, closing and reopening the viewport puts the user back at the origin.

## Known gaps

- **No plugin loading.** Doc 11 puts it here, and `Vixen.Editor.Plugin` — the contract, the manifest,
  the `AssemblyLoadContext` — does not exist yet. It is the reason this project is not NativeAOT, and
  the `PublishAot` property says so already.
- **No file dialog, so no "open project…".** A project comes from `--project` or is the scratch one;
  choosing one at run time needs a dialog, which is `Vixen.Platform`'s and not built.
- **Creating and deleting entities is not offered.** An `Entity` is a slot and a version and the ECS
  cannot reissue one, so a redo would hand back a different handle and every reference to the old one
  would be stale. Handle reservation in `Vixen.Ecs` is what unblocks it; until then the scene is
  seeded and edited rather than built.
- **Clicking in the viewport does not select.** Picking needs the id target the missing texture
  command also blocks; the gizmo can be dragged, and what it drags comes from the hierarchy.
- **It redraws every frame.** Redrawing only on change is the right end state and is not free — every
  animation, toast expiry and task progress has to say so, and one that forgets leaves a progress bar
  frozen at forty per cent.
- **One scene per project, chosen by path rather than by a dialog.** `Assets/Scenes/Main.vxscene`,
  because picking another needs a file dialog that `Vixen.Platform` does not have.
- **The four SPIR-V modules are committed here and in `Samples/02-HelloUi`**, byte for byte. They
  belong in one place once Raven's `Ui/*.rvn` path is wired; until then a caller hands the renderer
  whatever it has.
- **`PlatformInput.cs` is the second copy of the same file.** The sample's copy says a
  `Vixen.Platform.Ui` assembly is where it goes "when the editor becomes the second one". It now has.
