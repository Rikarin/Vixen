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
| `Program.cs` | the platform and the main window |
| `EditorHost.cs` | the device, the windows and the four steps of a frame |
| `EditorPane.cs` | one window's half of a frame: a swapchain, a renderer and the geometry between them |
| `EditorApplication.cs` | the project, the scene, which panels exist, which layouts, and what persists |
| `SceneEntity.cs` | the join: one entity as a row of editors and as something a gizmo can drag |

The fourth is the one a game team would fork. The loop is four steps worth naming: pump the
platform's events into the document, run the layout and draw passes, turn the draw lists into
geometry, record that geometry into frames. Only the last knows what a GPU is — which is why
`--frames` means something on a machine with no Vulkan at all.

**Only the last step multiplies.** A panel torn out onto the desktop is a second `UiSurface` of the
*same* document, so it is laid out by the same pass, styled by the same cascade and reached by the
same reparent that moved it there. What it needs of its own is an `EditorPane`. Two things about that
which are not obvious:

- **A `UiRenderer` each, not one shared.** The renderer rings its vertex and box buffers across the
  device's frames in flight and advances a region per `Upload`, so two uploads in one device frame
  consume two regions — and after as many frames as there are regions the second window writes over
  geometry the GPU is still reading. Sharing it is a validation-clean way to draw yesterday's frame.
- **A pane exists without a device.** On a headless run the surface is still laid out, still drawn
  and still turned into vertices; only the presenting is missing. A pane that came into existence
  with a swapchain would make `--frames` prove nothing about the window it never opened.

`--run view.float-panel` is the end-to-end proof: it opens a second operating-system window, gives it
a swapchain and presents to it, validation-clean.

`Shaders/` is the fifth thing and is its own README: nine SPIR-V modules from three Raven sources, and
the reflection beside them that tells `EditorHost` where each vertex attribute and each descriptor
went.

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
2. Load the plugins — **after** the editor's own commands, so a plugin naming one that already
   exists is refused rather than shadowing it, and **before** the two steps below, because a
   plugin's commands own keymap defaults and a plugin's panels are named by saved layouts.
3. Load the keymap — **after** the commands that own its defaults, or every override in the file
   lands on a command with no default and the file rewrites itself with the whole map in it.
4. Load the theme tokens.
5. Apply the saved layout — **after** the panels are registered, or a saved arrangement names panels
   the workspace cannot build.

A first run has none of the three files and opens on the Default preset in dark.

## The panels, and which of them are real now

`Vixen.Editor.Inspector` and `Vixen.Editor.SceneView` have landed, so three of the five panels are
looking at a real model:

| Panel | What it is |
|---|---|
| Hierarchy | a `TreeView` over the scene's entities; selecting drives the shared selection, and renaming, creating and deleting are all undo entries |
| Inspector | an `InspectorView` over whichever selection was last clicked in — the scene's entities or the project's assets — recording every edit on the scene document's stack |
| Scene | a `SceneViewport`: orbit, pan, zoom, the axis cross, gizmo modes and snapping, drawn into the panel — as lines, for the reason below |
| Project | `ProjectBrowser`: the asset database as a tree, with a search box, over the real `Assets/` directory. Double-clicking a row opens the asset |
| An asset | one per open document, built by whichever of the nine asset editors claims the file |
| Console | still a line of text |

`Vixen.Editor.App.Tests` drives that arrangement the way `EditorHost` does — a real application, a
real project in a temporary directory, real pointer events into the panels, no GPU — because what
breaks here is the line *between* two pieces that each have tests of their own. Selecting an asset
did nothing for exactly that reason: every part of the path worked and one of the joins was missing.

**Double-clicking an asset opens it in a panel of its own.** `ProjectBrowser` raises `Activated`,
`AssetEditorRegistry` says which of the nine editors in
[`Vixen.Editor.AssetEditors`](../Vixen.Editor.AssetEditors/README.md) claims the file, and the
document lands in a dock panel registered on demand.

- ⚠ **The panel is named after the asset's GUID.** A path would be shorter and would leave a panel
  nobody can reopen the moment the file moved; the GUID is the identity precisely so that it does not.
- ⚠ **Registered once, reopened afterwards**, because the workspace refuses a second registration
  under one id and the registry already hands back the document that is open — so a second
  double-click brings the tab forward rather than building a second view over one undo stack.
- ⚠ **A file nothing claims raises a notification.** There is no fallback editor, and a double-click
  that did nothing at all reads as a broken application.
- **One world for every scene and a fresh one per prefab.** Sharing the editor's world across scenes
  is what makes an entity handle mean one thing here; a prefab must not share it, because "isolated"
  is the claim that its entities are not in the level.

The addressable analysis is wired to a `ProjectWorkspace` of its own rather than to the editor's
database, for the reason `ContentTasks` already gives: `Scan` clears and repopulates its dictionaries.

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
pivot and snap toggles under a Gizmo submenu, the six axis views and the projection under a Camera
one, the navigation preferences under a Navigation one, and focus, frame-all and the grid alongside.

- **All six axis views, not three.** Front, right and top had numpad keys and back, left and bottom
  did not exist, so the opposite of a view could only be reached by orbiting a hundred and eighty
  degrees by hand.
- **The navigation preferences are ticked commands rather than a dialog.** Orbit-around-selection is
  the one people notice within a minute of opening a scene and could not otherwise change;
  zoom-to-cursor and invert-orbit-Y are what the same people ask for next. A palette entry and a menu
  tick is the whole of what a preference needs before there is a preferences window to put it in — and
  it is what makes them searchable and rebindable.
- ⚠ **Its own submenu, not three more lines on Camera.** These change what every *future* drag does
  rather than doing anything now, and a menu where half the entries move the camera and half silently
  change how it moves is one nobody reads twice.

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

**Single-clicking a row shows the asset in the inspector.** `ProjectAsset` is to a GUID what
`SceneEntity` is to an entity: the object an inspector can show members of, living here for the same
reason. Until it existed, `EditorProject.Selection` was a dead end — the browser wrote to it and
nothing read it — so a click in the Project panel moved a highlight and did nothing else.

- ⚠ **The envelope, not the importer's settings.** `AssetEntry` is deliberately what the database
  knows without parsing a sidecar; the settings are a document with an undo stack and an
  apply/revert of its own, which is what double-clicking opens. An editable second copy of them in
  the inspector would be a writer to that file with no idea the first one exists.
- ⚠ **Every row is read-only.** Renaming an asset moves a file and rewrites every reference to it —
  `EditorContext.Touch` exists for exactly that — and there is no command for it yet. A writable
  Name box would rename the object in memory and leave the file where it was.

⚠ **Only indexed nodes reach the selection.** A folder scanned read-only has no sidecar and so no
GUID, and putting `AssetId.Empty` in `EditorProject.Selection` would make every such folder select the
same nothing and look like one asset.

## Plugins

`Plugins/` under the project, then `Plugins/` under the user's data directory. The first id wins, so
a plugin checked into a repository overrides the copy the user installed globally — which is what
makes "everybody on this team gets the same tools" true. Neither folder normally exists, and that is
not an error.

Everything about *how* one is loaded is `Vixen.Editor.Plugin`'s and is written down there. What is
this project's is the two decisions above it: **where** to look, and **which extension points to
publish**.

`PluginServices` gets `EditorProject`, `SceneDocument` and `DrawerRegistry` — the shell's own
registries a plugin reaches through `PluginContext` without being handed anything.

⚠ **Importers and build steps are not published, and it is not an oversight.**
`ContentPipeline` builds its `ImporterRegistry` inside the background task, from
`ProjectWorkspace.Importers()`, precisely so the editor, `vixen content build` and the compiler
worker processes cannot end up with different sets — a worker with a different set produces
different artefacts for the same file, which shows up as a cache that never hits. So there is no
long-lived registry here to add to, and manufacturing one would be this application building a set
the workers have not got.

`Reload Plugins` is in the palette: it unloads every active plugin, re-reads the manifests and loads
them again, which is the plugin-development loop. It also checks that each old load context actually
left memory and says so when one did not — the runtime reports nothing about a collectible context
that cannot be collected, and the symptom otherwise is a plugin whose statics are not what it
expects on its second load.

⚠ **Unloaded on the way down, before the shell is disposed.** Unloading is what takes a plugin's
panels back out, and closing a panel through a disposed docking workspace would throw during
`Dispose` — which is the one place an exception costs the user their layout file.

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

⚠ **Four draws into one target, in an order that is not arbitrary.** The spawned shapes go first and
are the only thing that writes depth. The grid, the three-axis marker on each entity and the line to
each parent follow, depth-tested, so a marker inside a cube is inside it. Then the gizmo's shafts,
rings and plane quads with no depth test at all — a handle you cannot reach through the thing it
moves is a handle you cannot use. Last, the gizmo's *solid* parts: the cone on the end of a translate
arm, the cube on the end of a scale one, and the cube in the middle either way — triangles rather than
segments, so they want a second `MeshRenderer` rather than a second range in the first.

⚠ **The handles people aim at are solid, and they used to be wire.** An outlined arrowhead is four
ribs and a square: from the one angle it was built for it reads as an arrow, and from every other it
is four unrelated lines crossing near the end of a shaft. The middle handle was worse — a flat square
held square to the camera, which is a sticker on the front of a solid object rather than part of one.
Both are the parts of a gizmo people aim at, the shafts only saying which way, so both were exactly
the wrong parts to draw as a hint. Being solid is also why they have to be the last draw and why
`MeshRenderer` grew the overlay pipeline `LineRenderer` already had: a wire handle behind a cube still
shows a few pixels through it, and a solid one is simply gone.

⚠ **What it does not draw is materials.** There is no material system wired to an editor viewport and
no model importer feeding one, so the mesh pass is `MeshRenderer` — a tool renderer whose cost is
linear in vertices and which has no culling and no materials. It is what makes a spawned cube visible
today; a viewport driven by `RenderSystem` through a `GraphicsCompositor` is what replaces it.

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

`ProjectAsset` is the same join on the other side: a GUID and the project it is read through, so
that the Project panel's selection has somewhere to land.

⚠ **Both selections are polled once a frame rather than subscribed to.** `Selection<T>` is
signal-backed and an `Effect` would be the better wiring, but nothing in this loop flushes the
reactive scheduler and adding one changes the loop's contract for notifications and background tasks
as well. Comparing a handful of handles once a frame is not a cost.

⚠ **Several selections, one inspector, and the panel that was clicked in wins.** There is one per
open scene — the editor's own, plus one for every scene or prefab opened as an asset, each with its
own hierarchy and its own undo stack — and one for the project browser. They cannot be shown
together: `InspectorRegistry.CommonType` draws nothing for a selection with no single type in it,
which an entity and a texture do not have. So the poll asks which of them changed, shows that one,
and **clears the ones whose views this application owns — both the document selection and the tree
rows drawing it**. Two panels highlighted while the inspector can only show one is a picture that
lies about what the next Delete, the next gizmo drag or the next rename will act on.

- ⚠ **The rows shown are edited against their own document.** `InspectorView.EditedDocument` is set
  to the scene the entities came from, so an edit to an opened scene is recorded on that scene's
  stack rather than on the editor's own — otherwise `Ctrl+Z` in one window undoes a change made in
  another.
- ⚠ **An asset editor's own hierarchy is not cleared.** `SceneHierarchyView` takes selection outwards
  only, by its own documented decision, so clearing its document's selection from here would leave a
  row highlighted with nothing behind it.

⚠ **A panel's factory runs again when it is reopened**, so nothing durable may live in one. The
camera is kept as a `ViewBookmark` on `EditorApplication` and restored when the scene panel is
rebuilt; without it, closing and reopening the viewport puts the user back at the origin.

## Known gaps

- **A document's panel is not in any layout preset.** It is opened on demand and closed by hand, so
  the five presets show the five standing panels and an asset editor lands wherever the workspace
  puts a new one. Remembering which documents were open across a restart is the arrangement's job and
  `current.vxlayout` does not hold it.
- **No plugin-management panel.** Plugins load, but the only way to see what is installed is the
  notification on the way up. The panel is a list over `PluginHost.Plugins` with enable, disable and
  reload on it, and nothing in the loader is missing for it.
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
- ⚠ **Double-clicking the scene that is already open loads it a second time.** The editor opens its
  own scene by *path*, as a `SceneDocument` carrying `AssetId.Empty`, so `AssetEditorRegistry` has no
  way to know that the GUID being opened is the document already on screen — and both share one
  world, so the entity count doubles. Opening the scene as an asset from the start is the fix, and it
  is a change to how the editor decides what it is editing rather than a check to add here.
- **`Samples/02-HelloUi` still carries the interface shaders as GLSL**, and so does
  `Platform/Vixen.Graphics.Golden.Tests`. This project's are Raven now — `Shaders/*.rvn`, and
  [`Shaders/README.md`](Shaders/README.md) says how they are built — so the three copies are no
  longer byte for byte, and the way that already bit was `ui-box.frag`: the golden fixture's copy
  grew shadow blur and the editor's did not, so the editor could not draw a box shadow that every
  other consumer of `UiShape` could. The Raven port is the current behaviour and the duplication is
  still the gap. What closes it is one set of `.rvn` the three of them share, which is a move rather
  than a rewrite now that there is somewhere to move from.
- ~~**`PlatformInput.cs` is the second copy of the same file.**~~ Closed: both copies are gone and
  `Vixen.Platform.Ui` holds the one. Multi-window is what forced it — routing an event to the wrong
  surface is a bug two copies would have to be fixed for twice.
