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

**Editing a panel without restarting the editor** is
[`Editor/Vixen.Editor.Host/README.md`](../Vixen.Editor.Host/README.md): the exact `dotnet watch`
command, which of the three hot-reload channels work today, and — the part worth reading before
reaching for it — *which* `.vxml` panels are rebuilt and which are not. A panel is reloadable if it
was mounted through `HotReloadHost.Mount` and not otherwise, which is a one-line difference at the
call site.

## The files, and what each is for

| | |
|---|---|
| `Program.cs` | the platform, the main window, and the loop that reopens the editor over another project |
| `EditorHost.cs` | the device, the windows and the four steps of a frame |
| `EditorPane.cs` | one window's half of a frame: a swapchain, a renderer and the geometry between them |
| `EditorApplication.cs` | the project, the scene, which panels exist, which layouts, and what persists |
| `EditorParity.cs` | every menu of doc 20's Part C, and the verbs behind them |
| `EditorSettingsPanels.cs` | the Preferences and Project Settings pages, and the plugin and history panels |
| `EditorProjects.cs` | which project, asked at start-up and answered without a restart |
| `EditorSettings.cs` | the settings assets the editor ships: three of the project's and one of the user's |
| `EditorBuilds.cs` | the Build Settings panel, Build and Run, and what Deploy means for a device |
| `BuildSettingsView.cs` | doc 20's B7 window: target, configuration, scenes-in-build, output path |
| `UndoHistory.vxml` | the undo history panel — the first of this assembly's panels written in markup |
| `SearchSources.cs` | what `Ctrl+Shift+F` looks in that is not a command |
| `SceneEntity.cs` | the join: one entity as a row of editors and as something a gizmo can drag |
| `EditorAnimation.cs` | the one thing doc 34's four documents will not do for themselves: reach another asset |

`EditorApplication` and its four partials are the part a game team would fork. The loop is four steps worth naming: pump the
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
| `current.vxlayout` | the arrangement the editor was left in, open documents included |
| `<name>.vxlayout` | arrangements the user saved by name |
| `keybindings.yaml` | the chosen keymap preset, and only the bindings that differ from it |
| `preferences.yaml` | the editor's own preferences: external editor, undo depth, and two limits |
| `plugins.yaml` | which plugins *this user* has switched off, which is not the same as the author's `enabled:` |
| `projects.yaml` | which projects have been opened, and when |
| `window.yaml` | the main window's size and place |
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
2. Load the preferences — **after** the commands, because the undo depth is pushed into stacks that
   exist by then, and because the Preferences panel the previous step registered can write them back.
3. Load the plugins — **after** the editor's own commands, so a plugin naming one that already
   exists is refused rather than shadowing it, and **before** the two steps below, because a
   plugin's commands own keymap defaults and a plugin's panels are named by saved layouts. The
   user's list of switched-off plugins is read **before** anything is activated, because a plugin
   somebody disabled because it broke the editor is the one whose `Activate` must not run.
4. Load the keymap — **after** the commands that own its defaults, or every override in the file
   lands on a command with no default and the file rewrites itself with the whole map in it.
5. Load the theme tokens.
6. Apply the saved layout — **after** the panels are registered, or a saved arrangement names panels
   the workspace cannot build. ⚠ An asset editor's panel is registered on *demand* and cannot be, so
   `DockingWorkspace.Resolve` asks this class to open the document rather than the arrangement
   silently losing the tab.

A first run has none of the files and opens on the Default preset in dark.

## Which project, and how another one is opened

`--project` first, then the most recent one that is still on disk, then a scratch project under the
user's data directory. A genuine first run — scratch, and nothing in the history — puts the project
browser up once; after that the editor reopens what you were in.

⚠ **Opening another project does not swap one underneath the editor.** `RequestProject` asks about
unsaved work through the same prompt the window's close button uses, then closes this editor and
leaves the root in `PendingProject`; `Program`'s loop disposes the host and builds another over the
same window. The new editor is therefore assembled by exactly the code that assembles it at launch —
half a dozen fields reassigned in place would be half a dozen chances to leave a panel pointing at a
dead world.

## The panels, and which of them are real now

Every one of them is looking at a real model rather than at a placeholder:

| Panel | What it is |
|---|---|
| Hierarchy | a `TreeView` over the scene's entities, with a name filter above it. Selection goes both ways — clicking a row selects, and selecting anywhere else highlights the row — and renaming, creating, deleting and dragging-to-reparent are all undo entries |
| Inspector | an `InspectorView` over whichever selection was last clicked in — the scene's entities or the project's assets — recording every edit on the scene document's stack, with `ComponentsView` under the rows in the same scroll region |
| Scene | a `ViewportLayout` of one, two or four `SceneViewport`s, each with its own camera, view mode and show flags, and a floating toolbar, stats readout and rubber-band drawn over the focused one |
| Project | `ProjectBrowser`: the asset database as a tree, with a search box, over the real `Assets/` directory. Double-clicking a row opens the asset |
| An asset | one per open document, built by whichever of the nine asset editors claims the file |
| Console | a virtualised list over the editor's log ring: level toggles with counts, a category filter, search, collapse-duplicates, clear-on-play, and a detail pane with the stack |
| Preferences | a `SettingsView` over the user's store: General, Appearance, Scene View, and two pages that open the panels they are about |
| Project Settings | the same control over `ProjectSettingsStore`, drawing two `[DataContract]` types with `InspectorView` |
| Plugins | a grid over `PluginHost.Plugins` with enable, disable and reload, and the failure under it as a sentence |
| Undo History | the active document's stack, where choosing a step undoes back to it |
| Build Settings | target, configuration, output path and the scenes that ship, over `PlayerBuild` — the same three calls `vixen build` makes |
| Devices | E4's list, plus a Deploy that builds and launches on this machine and says which tool is missing for every other kind |

The shell registers two more of its own — **Keyboard Shortcuts** and **Message Log** — because both
are views over things `EditorShell` owns rather than over anything here.

⚠ **A settings page is drawn from a settings object or from *commands*, never from both.** The scene
navigation preferences and the theme are ticked commands: palette-searchable, rebindable, on a menu.
The window draws those same commands as toggles rather than a copy of their state, because two
writers to one setting is how a preferences window and a menu tick come to disagree.

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

## Panels written in markup

`UndoHistory.vxml` is the first, and the reason to start with it is that it is the one panel whose
model was already reactive: `CommandStack.Depth` is a `Computed`, so every binding re-runs when an
edit is pushed, undone or redone. What it was before is a `Control` that compared the stack, its
entry count and its depth **once a frame** and rewrote every row when any of them moved — and its
own remarks said why: *"a stack is signal-backed and nothing in the editor's loop flushes the
reactive scheduler, so polling is the same trade this application already makes"*. The shell flushes
now (`EditorShell.Tick` drains `Document.Effects`), so that trade is off. What is left of `Tick` is
one reference comparison, and it is there for the one thing that is genuinely not a signal: which
document's stack the panel is pointed at.

⚠ **Two things the old panel claimed and did not draw**, both found by rewriting it rather than by
reading it:

- Surplus rows were "hidden" with `AddClass("hidden")`, and nothing in the editor declares a
  `.hidden` rule — the class is a *utility*, and the editor does not load the utility sheet. A
  shrinking history left rows on screen. `@for` removes them from the tree instead, so the bug
  cannot come back.
- The row marking where the document is now set `ElementState.Checked`, and there is no
  `button:checked` rule outside the toolbar. The marker the panel's remarks call "the only thing
  here that answers what have I not saved" has never been visible. It is a class now, with a rule.

Both were invisible to the test, which counted rows by asking the view rather than the tree.
`UndoHistory.Count` counts the elements in the scroll view.

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

**Watched.** `FollowDisk` drains an `IFileWatcher` over `Assets/` on the frame thread — on the frame
because a rescan clears and repopulates the database's dictionaries, and doing that from a
`FileSystemWatcher` callback would race every panel reading it. The three things that used to be the
argument against having a watcher are all `Vixen.Core.IO`'s: `FileChangeCoalescer` debounces, folds an
atomic save's temporary-plus-rename into one change, and ignores a path this program is about to
write. `Ctrl+R` still forces a rescan.

⚠ **And the changes now reach the open documents, not only the tree.** Everything on this path used
to read the drained list for its *length* — `ReloadShaders` is the one exception and filters to
`.rvn` — so a `.vxcompositor` saved beside the running editor changed the database, the browser and
the build panel, and did not change the panel that had it open. `ExternalEdits` is the last few
metres: it routes a path to the document editing that asset, reloads it when it is clean, and marks
it stale and says so when it is not. Its other half is the one that makes the first half safe —
`EditorProject.DocumentSaving` fires *before* a write, so the editor's own saves are suppressed
rather than round-tripping. See [the guide](../../docs/guide/editor/external-edits.md).

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

## Building a player

`Build Settings…` opens the panel; `Build and Run` (`Ctrl+B`) runs `ContentTasks.BuildPlayer`, which
is import → content build → `dotnet publish` → optionally launch. That is `vixen build`'s own
sequence, over `PlayerBuild` in `Vixen.Editor.Assets`, because doc 20's B7 asks for a window "over
`Tools/Vixen.Cli`'s existing calls" and a second orchestration would drift from the first exactly the
way the content pipeline's would have.

⚠ **On the same one-at-a-time guard as an import**, and not politeness: a player build imports and
packs, so two of them write the same `Library/` and the same catalog.

⚠ **The console is the build log.** An editor has no terminal, so `dotnet publish` is run with its
output captured and every line goes into the ring the Console panel reads — where it can be filtered,
searched, and read after the toast has gone. MSBuild's own severity is read back out of the line
(`: error CS…`), because a compiler error logged as information is one the console's default filter
hides, which would be the panel concealing the thing it was opened for.

⚠ **A launch is awaited, so the task centre entry is what says a player is running** — and its Cancel
is what kills one that came up with no window. The notification at the end carries the game's own
exit code rather than the build's.

⚠ **No shader bundle, and it is said rather than left to be found.** The ahead-of-time compile is
`ShaderBuildRunner`'s, which links Raven's compiler — a build-time library this application
deliberately does not carry. A project with a `ProjectSettings/Shaders.effects.json` is told so in the
build log, and `build.rebuild-shaders` is declared-and-disabled with the same sentence.

⚠ **The Target submenu, the Configuration submenu and the window write one setting.** Doc 20's A4
rule about two writers applies, and is kept by there being one field: `PlayerBuildSettings.Target` is
what both tick from. There is no Apply on this window, which is the one settings surface where that is
right — Build *reads* these fields, so an uncommitted edit would mean the button building something
other than what is on screen.

⚠ **The player's target and the content target are two settings on purpose.**
`ContentBuildSettings.Target` is what the editor's own panels are imported for and
`PlayerBuildSettings.Target` is what ships. A single field would make "build the Android player" also
mean "reimport the whole project as ASTC", which is precisely what a team building for a phone from a
workstation must not have happen.

⚠ **The scenes-in-build list reaches the player, and `PlayerBuildSettings` lives in
`Vixen.Editor.Core` so that it reaches it whichever head built one.** The content build resolves every
entry to the address its sidecar declares and writes them, in order, as the `SceneManifest` a host
opens its first entry from — and that build is `ContentPipeline`, which `vixen content build` runs
too. A settings type only this application could see would have meant the editor's Build and Run
producing a player that opens its level and CI's producing one that opens nothing, which is the drift
`PlayerBuild` and `ProjectWorkspace` both moved out of here to prevent.

⚠ **An entry that cannot ship is refused before the import rather than warned about.** It used to be a
warning, on the argument that a stale entry may not matter to what is being tested; it is a refusal
now because the build itself refuses it — a scene that names nothing or has no address makes a player
that starts to an empty world. The panel's own State column says which of the two it is, where
somebody is looking, rather than only in the console a minute later.

**Deploy** on the Devices panel is the same build with a device attached to it: this machine is a
publish and a launch, and every other kind of device says which tool is missing — `adb`, a vendor SDK
— because the tool that would *find* one is the tool that would install to it. Attaching the remote
inspector afterwards is the fourth verb doc 20's B7 asks for and is the one still owed: it needs
something on the other side to answer, and doc 13's runtime half is not written.

`--run ID` executes one editor command on the first frame, which is how CI proves an import or a
build through the *editor's* path — enablement, background task and notification — rather than
through the pipeline the CLI already covers. It exits 2 for a command that is not there or not
enabled.

**The scene panel is live.** `ScenePresenter` renders into an offscreen colour target, registers it
with `UiRenderer.RegisterImage`, and the viewport control draws it — so the scene arrives in the
interface as an ordinary element that panels can be drawn over.

⚠ **One presenter per pane, each with a target and an image id of its own.** Four panes sharing a
presenter would share a render target, so all four would show whichever camera wrote it last; sharing
the *id* alone does the same thing one layer up, in the interface's image registry — four identical
views of the perspective camera, which reads as the other three cameras not working. The pool is
grown and shrunk in the frame loop rather than when the arrangement changes, because that is the only
place that can be sure no frame is in flight over the target it is about to destroy.

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

**This assembly has a stylesheet of its own, and it is a file**: `WorldTheme.vcss` at the project
root, embedded by the `**/*.vcss` glob in `Vixen.Ui.targets` and read back by
`EditorApplication.WorldTheme.Css`. Sixteen lines styling the world and scene panels' elements, a
sixth user-agent sheet after the five the constructor already loads. It was a `const string` in the
middle of `EditorWorlds.cs` until it was moved out byte for byte — the class stays `internal` and
nested, which the resource name does not care about.

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

⚠ **A panel's factory runs again when it is reopened**, so nothing durable may live in one. Each
pane's camera is kept as a `ViewBookmark` on `EditorApplication` and restored when the scene panel is
rebuilt; without it, closing and reopening the viewport puts the user back at the origin. ⚠ **The
saved cameras are forgotten when the arrangement changes**, because a single pane's camera restored
into a freshly-split layout would overwrite `ViewportLayout`'s top/front/side presets with three
copies of wherever that pane happened to be looking — a four-pane layout that comes up as four
identical perspective views, which is the exact thing the presets exist to prevent.

## The editor's own log

The editor is not built by `VixenApp`, so nothing along its path ever made a `RingBufferSink` — the
console would have been a perfectly good panel over an empty ring. `EditorLog` is that sink plus the
one thing that fills it: every `NotificationCenter` message becomes a log line, because a
notification is the editor deciding something is worth saying and a toast says it for four seconds.

⚠ **The mirror is one-way and must stay that way.** The console reads the ring; the ring is fed from
notifications. A console that raised notifications for log lines would close the loop, and a single
warning would toast, log, toast, log.

## Known gaps

- ~~**A document's panel is not in any layout preset.**~~ It still is not — an asset editor is opened
  on demand and lands wherever the workspace puts a new one — but the half that mattered is closed:
  `current.vxlayout` always *named* the panel, and what was missing was anything able to build one on
  the way back. `DockingWorkspace.Resolve` asks, `ReopenDocument` answers by opening the asset, and
  `EditorPreferences.RestoreOpenDocuments` is how somebody asks for a clean start instead.
- ~~**No plugin-management panel.**~~ `PluginManagerView` is a grid over `PluginHost.Plugins` with
  enable, disable and reload. ⚠ What is still owed is a plugin *browser*: this lists what is
  installed, and installing one is still copying a folder.
- ~~**No "open project…".**~~ It opens the project browser, and choosing one closes this editor and
  reopens it over the new root — see above. ~~**New Project makes four directories rather than
  instantiating a template.**~~ It writes the `game` template, through the same `ProjectScaffold`
  `vixen new game` writes it through. ⚠ **The old note said the templates were "reached with
  `dotnet new`", and that was never true** — `TemplateCatalog` reads the same tree out of an
  assembly. What was true is that it read it out of `Tools/Vixen.Cli`'s, which this application does
  not reference; it is `Vixen.Editor.Core`'s now. The cost of the gap only showed up two milestones
  later: a project with no `.csproj` is one Build and Run cannot publish, so every project the
  editor had ever made had that button greyed.
- ~~**Reparenting is not undoable.**~~ `ReparentCommand` records the sibling that was in front —
  `Hierarchy.PreviousSiblingOf`, restored through `SetParentAfter` — so an undo puts the third of
  five children back third rather than first. Dragging in the outliner goes through it, and the
  whole selection moves when the dragged row is part of it. ⚠ **A root is the exception**: roots are
  not a sibling list, so an entity undone back to the root set returns in creation order. Making
  that exact needs the scene format to carry a root order.
- ~~**Clicking in the viewport does not select.**~~ It does, and dragging a box round several does
  too, through `ScenePicker` and `IScenePicker.Within` — a ray test and a screen-space region query,
  both exact against the geometry the viewport actually draws. ⚠ **The id readback is still not
  driven, and the reason this note used to give is no longer the reason.** It said
  `PickingRenderer` "needs the viewport driven by `RenderSystem` through a `GraphicsCompositor`.
  This application has neither" — it has both, since #145–#151: `FramePresenter` draws every pane
  through a real `GraphicsCompositor` into the window's graph and `EditorWorldRenderer` owns a
  `RenderView` per pane. What actually blocks it is one level down and is a *shader*:
  `PickingRenderer.Stage` is a `RenderStage` whose `ShaderName` must name something that writes an
  object id into an `R32UInt` target, and **no such `.rvn` exists** — nothing in `Raven/Library`
  writes an id, and the only `ShaderName` overrides in the tree are post-effects, water and
  `DepthOnly`. Behind that shader sit two more missing pieces: nothing maps the id back to an
  entity, which is the `Func<uint, Entity>` `SceneViewport.Resolve` takes and nobody supplies; and
  the pane's compositor would have to carry `PickingRenderer.IdResource` and `DepthResource` per
  pane. ⚠ **`PickingRenderer` also has no test and no caller** — `ScenePicker`'s own remarks call
  the stage "written and tested", and only the first half is true. It is still what will be right
  the day a shader moves a vertex; it is a shader-and-mapping job, not a compositor one.
- **It redraws every frame.** Redrawing only on change is the right end state and is not free — every
  animation, toast expiry and task progress has to say so, and one that forgets leaves a progress bar
  frozen at forty per cent.
- ~~**One scene at a time.**~~ Closed by doc 20's E5. Scenes open **additively into one world** —
  which is what `SceneManager` already does, and what keeps an entity handle meaning one thing across
  the editor — and the Scenes panel lists them with per-scene visibility and lock. Making one active
  is an assignment to the `scene` field every panel already reads, which is why the change was three
  fields losing `readonly` rather than an index every panel had to learn about. ⚠ **Per-scene
  visibility writes the documents' own hidden sets**, so it is editor state and is not saved — the
  rule `entity.toggle-hidden` already follows.
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
