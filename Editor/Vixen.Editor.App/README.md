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
| Hierarchy | a `TreeView` over the scene's entities; selecting drives the shared selection, and renaming, creating and deleting are all undo entries |
| Inspector | an `InspectorView` over the selection, recording every edit on the scene document's stack |
| Scene | a `SceneViewport`: orbit, pan, zoom, the axis cross, gizmo modes and snapping, drawn into the panel — as lines, for the reason below |
| Project | `ProjectBrowser`: the asset database as a tree, with a search box, over the real `Assets/` directory |
| Console | still a line of text |

The scene lives at `Assets/Scenes/Main.vxscene` and is opened on launch. A project that has none gets
the seeded one written immediately — the only time the editor saves without being asked, so that a new
project contains the scene you are looking at rather than something that exists until the window
closes. `Ctrl+S` saves; the menu item greys itself out from the document's own dirty signal.

⚠ **The seeded scene is scanned a second time, and has to be.** `EditorProject.Open` indexes the
project before that file is written, so without the rescan a first run shows a browser with no scene
in it — the one file the editor is certain exists, because it just made it.

## The Scene menu

Everything the viewport can be told to do is an `EditorCommand`, so it is on `W`/`E`/`R`, in the
palette, rebindable, and greyed out when the scene panel is closed. What it was *not* was reachable
with a mouse: the shell's default bar is File, Edit, View and Help, and none of them is where a
gizmo mode belongs. `MenuModel.InsertMenu` puts a Scene menu third, with the modes and the space,
pivot and snap toggles under a Gizmo submenu, the numpad views and the projection under a Camera one,
and focus, frame-all and the grid alongside.

- **The three modes are ticked, not merely listed.** A menu of Translate, Rotate and Scale with
  nothing saying which is current is one where the only way to find out what a drag will do is to
  drag. Every toggle carries a `Checked` predicate for the same reason, and the same predicate draws
  its toolbar button pressed — one answer, three views.
- ⚠ **`Checked` is null for a command that is not a toggle.** `MenuPresenter` grows the tick column
  only for commands that have one, so a lambda returning false on every line would indent the whole
  menu by an empty tick.
- ⚠ **The model is described after the commands are registered.** An entry naming a command nothing
  has registered is skipped when the bar is built — which is what lets the shell name `file.save`
  without owning it, and what would silently swallow every line of this menu the other way round.
  The bar is then rebuilt once explicitly, because the last `Commands.Add` already rebuilt it against
  a model that did not yet have this menu in it.

## The project browser

`ProjectBrowser` is the Project panel: a `SearchBox` and a `TreeView` over `AssetTree.Build`. The
shape — folder synthesis, ordering, search — is `Vixen.Editor.Core`'s and is tested there without a
document; what is left here is rows, selection and when to rebuild. `Ctrl+R` rescans, saves the index
and rebuilds the reverse-reference index with it, and reports what the scan repaired rather than only
how many assets it found.

⚠ **Not watched.** A file added outside the editor appears on the next refresh. A file-system watcher
needs debouncing, a rename heuristic and a way not to fight the editor's own writes; one that missed
half the events while claiming to be live would be worse than a Refresh that says what it does.

⚠ **Only indexed nodes reach the selection.** A folder scanned read-only has no sidecar and so no
GUID, and putting `AssetId.Empty` in `EditorProject.Selection` would make every such folder select the
same nothing and look like one asset.

⚠ **Untested at the panel level**, in common with every other panel here — the app is an executable
with no test project. The model underneath it has 16.

## Importing and building content

`Import Assets` and `Build Content` (`Ctrl+Shift+B`) run `ContentPipeline` on the shell's background
task manager — the same call `vixen import` and `vixen content build` make. The orchestration moved
into `Vixen.Editor.Assets` so there is one of it: two would drift, and the way that drift shows up is
the editor and the CLI producing different output for one project.

⚠ **One at a time**, and the guard is `Interlocked.CompareExchange` rather than a bool. Two imports
write the same sidecars, artefact store and cache file at once; the second does not produce a worse
build, it produces a corrupt `Library/`. A menu item and a keybinding dispatched in one frame would
both see a plain flag unset.

⚠ **Build imports first.** The plan reads the import cache, so building without importing packs the
previous import's artefacts — a build that succeeds and ships yesterday's content.

⚠ **The workspace has its own `AssetDatabase`.** `Scan` clears and repopulates its dictionaries and
the import runs on a pool thread, so sharing the one the panels read would be a race. The editor
rescans afterwards, on the frame thread, which is what `ContentTasks.Rescan` is for.

⚠ **The progress bar fills at the end.** `ImportAllAsync` returns when it is finished, so what drives
the bar is a walk over what happened rather than a live feed. Honest, and fixed by giving the import
pipeline a progress callback of its own.

`--run ID` executes one editor command on the first frame, which is how CI proves an import or a
build through the *editor's* path — enablement, background task and notification — rather than
through the pipeline the CLI already covers. It exits 2 for a command that is not there or not
enabled.

**The scene panel is live.** `ScenePresenter` renders into an offscreen colour target, registers it
with `UiRenderer.RegisterImage`, and the viewport control draws it — so the scene arrives in the
interface as an ordinary element that panels can be drawn over.

⚠ **What it draws is lines**: the grid, a three-axis marker per entity, a line to each parent, and the
gizmo. There is no material system wired to an editor viewport and no model importer feeding one, so
there is nothing to draw as a mesh yet — and a grid, markers and a gizmo is what any editor shows for
a scene of empties regardless. A mesh pass is a second `SceneRenderer` into the same target.

⚠ **Resizing re-registers the number rather than surrendering it.** A dragged splitter resizes the
pane once a frame, and `UnregisterImage` destroys the number's descriptor sets — which the Vulkan
backend cannot reclaim, because its pools are deliberately created without `FreeDescriptorSetBit`. So
a set per resize is a leak that neither the picture nor the validation layers ever mention.
`UiRenderer` holds one set per frame in flight per registered number and repoints each when that
frame comes round; `UiRenderer.ImageSets` is what makes the claim checkable.

⚠ **The interface's pass declares that it reads the scene's target**, and has to. The interface
samples it through a descriptor set, which the render graph cannot see — it orders passes and places
barriers from what they *say* they touch. Without the declaration the target is still a colour
attachment when the fragment shader reads it, which validation reports as a layout mismatch and which
on a driver that does not check is a scene drawn from memory nothing had finished writing.

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
- **Reparenting is not undoable.** Dragging in the hierarchy is not wired up either; the primitive
  undo was waiting on — `Hierarchy.SetParentAfter`, which puts a child back where it was rather than
  at the head — now exists, so what is missing is the command.
- **Clicking in the viewport does not select.** ⚠ Not for want of a texture command any more — the
  draw list has one, and `Viewport` draws a `RenderTarget` through it. What picking needs is the
  *id* target and the readback: `PickingBuffer` and `PickingRenderer` in `Vixen.Editor.SceneView` are
  the pieces, and nothing in this application drives them. The gizmo can be dragged meanwhile, and
  what it drags comes from the hierarchy.
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
