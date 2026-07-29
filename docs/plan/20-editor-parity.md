# 20 — Editor Parity

> ⚠️ **Extends [11](11-editor.md).** Doc 11 says what the editor *is* — the assemblies, the document
> model, the extension points, and why each is shaped the way it is. That architecture is right and
> nothing here changes it. What doc 11 does not do is enumerate the **surface**: the panels a user
> expects to find, the menu they expect to find them under, the windows that are not panels, and the
> verbs that have to exist before somebody who has used Unreal or Unity for a decade stops noticing
> that this is not one of them. This document is that enumeration, ordered into milestones.

## What "on par" means here, stated before it is claimed

Unreal's editor is roughly two decades of a large team; Unity's is comparable. Matching them
feature-for-feature is not a plan, it is a category error. What *is* achievable, and what this
document commits to, is the bar a professional user applies in their first week:

1. **Nothing they reach for is missing.** Every verb they know has a home, a keybinding, and a menu
   line. A verb that is not implemented is *visibly* not implemented rather than absent.
2. **Nothing they find is a toy.** A console that cannot filter, an outliner that cannot multi-select,
   a content browser that cannot rename — each of those is worse than not shipping the panel, because
   it is a promise the editor breaks the second time it is used.
3. **The window is theirs.** Docking, layouts, keybindings, theme, and per-panel state survive a
   restart and can be reset when they get it wrong.
4. **It tells the truth about itself.** Long operations report progress and can be cancelled; errors
   persist; the profiler measures the editor as well as the game.

Everything below is measured against those four. Where a row is deliberately post-1.0 it says so and
says why — a list of everything two mature engines contain, with no ordering, would be a wish rather
than a plan.

---

## Where the editor actually is

Reconciled against the code in `Editor/`, not against doc 11's aspirations.

| | Built | Missing |
|---|---|---|
| **Panels** | Hierarchy, Inspector, Scene viewport, Project browser, one per open asset document, Console | ~24 more, listed in [Part B](#part-b--the-panel-inventory) |
| **Menus** | File, Edit, Scene, View, Help — five groups, of which Scene is the only complete one | Assets, Entity, Play, Window, Build, Tools; and most of File and Edit |
| **Commands** | 38 registered ids | ~180 more |
| **Windows** | One OS window, plus a floated panel through `view.float-panel` | Preferences, Project Settings, Keybindings, Plugin manager, Project browser (startup), About, all modal dialogs |
| **Layouts** | Five presets, saved/named arrangements, `current.vxlayout` | Open documents are not part of an arrangement; floating groups are not promoted in a preset |
| **Shell services** | Commands, keymap, palette, menus, toolbar, status bar, notifications, background tasks, theming, localisation, docking, plugins | Dialog service, context menus, modes, icons, MRU, search-everywhere |

Three specific findings worth surfacing, because each changes what the first milestone should be:

- ⚠ **Five menu lines already name commands nobody registers.** `EditorShell.DefaultMenus` names
  `file.new-project`, `file.open-project`, `file.save-all`, `edit.preferences` and
  `help.documentation`; `MenuPresenter` skips an entry whose command is not registered, so the File
  menu currently has Save and Exit in it and the Edit menu has Undo and Redo. The bar is *already
  shaped* for the editor this document describes. Registering those five commands is the smallest
  possible first commit and it makes the shape visible.
- ⚠ **The file-dialog blocker is gone and the READMEs have not caught up.**
  [`Vixen.Editor.App/README.md`](../../Editor/Vixen.Editor.App/README.md) says "No file dialog, so no
  *open project…*", and that was true when it was written. `INativeDialogs` now has implementations in
  `Vixen.Platform.Windows`, `.Linux`, `.MacOS`, `.iOS` and `.Web`, reached through
  `IPlatformSupplement`, and `Vixen.Editor.App` does not use any of it. Open/Save/Import are wiring,
  not research.
- ⚠ **The Console is a line of text.** It is the panel that is looked at most often when something has
  gone wrong and it is the least built thing in the shell. `Vixen.Core.Diagnostics` already keeps a
  `RingBufferSink` with per-category levels, so the panel is a virtualised view over a buffer that
  exists.

---

## Part A — Shell infrastructure

These are the things every panel in Part B needs and none of them should build twice. Nothing in Part
B should start before the piece of Part A it stands on.

### A1 — The application frame

The frame is menu bar → **mode bar** → toolbar → workspace → status bar. Three of those five exist.

- **A mode bar** is the one structural addition. Unreal's Select / Landscape / Foliage / Mesh Paint
  strip is not a toolbar of commands, it is a statement about *what the viewport's input means right
  now*, and retrofitting one is how editors end up with six mutually-exclusive booleans on the
  viewport. `IEditorMode` — an id, a label, an icon, an activation pair, an optional toolbar, an
  optional panel, and first refusal on viewport input — is a small interface that has to exist before
  the second mode does. Ship it with one mode (Select) so the seam is proven and nothing depends on
  the mode set being final. It joins the eight extension points in `Vixen.Editor.Plugin`.
- **The toolbar grows sections rather than entries.** Today it is one flat strip of eleven ids. The
  bar an AAA editor carries is: mode buttons | save & build | transform mode, space, pivot, snap
  (with a dropdown per snap value) | play controls | layout & settings. `ToolbarPresenter.Show`
  already takes `null` as a separator; what it needs is a *dropdown* entry (a command that opens a
  small popover of commands) and a **toggle group**, so that Translate/Rotate/Scale draw as one
  segmented control rather than three buttons that happen to be adjacent.
- **The status bar reports four things**, left to right: the transient message it already has, the
  current selection count, the frame time of the editor's own loop, and the task centre it already
  has. Editor frame time on the status bar is what makes doc 00's editor-shell performance bar
  something a person notices rather than something a benchmark asserts once.
- **The title bar states the document.** `<scene name><*> — <project name> — Vixen`. It is the only
  affordance that answers "which project is this window" when three are open.

### A2 — Windows, dialogs, and a dialog service

The editor has one OS window and one way to make a second (`view.float-panel`). Everything modal is
currently unbuildable.

- **`DialogService`** in `Vixen.Editor.Ui`: `ConfirmAsync`, `PromptAsync`, `ChooseAsync`, and
  `ShowAsync<TView>` for a modelled dialog. Backed by `Vixen.Ui.Controls`' `Dialogs.cs` and the
  overlay layer, *not* by a native window — a modal that is an OS window cannot be screenshotted by
  the golden-image tests and cannot be driven by the automation harness.
- ⚠ **Save-on-close is the first consumer and it is not optional.** Closing the editor, closing a
  document tab, or opening a second project with dirty documents must ask. An editor that loses an
  afternoon once is one nobody opens again, and every document already knows whether it is dirty
  (`EditorDocument.IsDirty`) — what is missing is the thing that asks.
- **Native dialogs go through `INativeDialogs`**, reached through `IPlatformSupplement`. Open Project,
  New Project, Open Scene, Save Scene As, Import Assets and Export Package are all one call each. The
  capability is a runtime question (`PlatformCapabilities.NativeDialogs`), so the commands grey
  themselves out on Web and Android rather than being absent — the same rule `view.float-panel`
  already follows.
- **Promoting a floating dock group to a real OS window** is doc 11's remaining docking gap and it is
  half done: `EditorPane` already proves a second surface, swapchain and input queue, and
  `--run view.float-panel` is validation-clean. What is left is that `DockLayout` does not record
  *which* groups were promoted, so an arrangement restores them docked. That is a field on the
  serialised group plus a rule about what happens when a saved window is off every current display.
- **A startup Project Browser window.** Unreal's project browser and Unity Hub exist because the
  first question an editor is asked is "which project", and `--project` is not an answer for a user.
  Recent projects with their last-opened time, a New Project pane over `Tools/Vixen.Templates`, and a
  Browse button. It is the last thing in this section to build and the first thing a new user sees.

### A3 — Command system, completed

The registry is right. Five things are missing from it and each shows up as a whole class of feature
that cannot be built.

| Missing | Why it blocks something |
|---|---|
| **Context menus** | Right-clicking an outliner row, a browser row, a viewport, a node or an inspector row is how half of an editor's verbs are reached. `MenuPresenter` is already a view over the registry; a `ContextMenuPresenter` over a `MenuGroup` built per-site is the same code with a different anchor. |
| **Command context / scope** | `scene.delete-entity` and an asset delete are both Delete. Today enablement predicates guess from focus. A command declares the context id it belongs to, the shell tracks the focused context, and the dispatcher picks — which is also what stops a keybinding in the Project panel from deleting an entity. |
| **Dynamic menus** | `MenuDynamic` exists in the model and nothing produces one. Open Recent, Panels ▸, Layouts ▸, Build Target ▸ and Add Component ▸ are all it. |
| **Icons** | `ControlIcons` covers the control set. A toolbar and a menu need an editor icon set — roughly 120 glyphs — as one font or one atlas, with an id per icon so a plugin can name one. |
| **Radio groups** | `Checked` gives a tick. Translate/Rotate/Scale is a *choice*, and drawing three ticks where one radio belongs is how a menu stops being readable. |

Also here: **command history and repeat** (`Ctrl+Shift+R` repeats the last command), and **"recently
used" boosting in the palette**, which is the single cheapest thing that makes a palette feel fast.

### A4 — Preferences and Project Settings

Two windows, one mechanism, and the mechanism already exists twice.

- **Project Settings** edits `[DataContract]` types under `ProjectSettings/` through
  `ProjectSettingsStore` and draws them with `InspectorView`. Doc 11's claim that "adding a project
  setting is declaring a type, not also writing a dialog" is already true in the model and there is no
  window over it. Left rail of categories, right pane of drawers, a search box over every setting in
  every category, and a Reset per category.
- **Preferences** is the same window over the *user's* store rather than the project's, and its first
  categories are General, Appearance (theme, font, density), Scene View (the three navigation
  preferences currently living as ticked commands), Keybindings (A5), Colours, External Tools, and
  Plugins. ⚠ **The three navigation preferences stay as commands.** They are palette-searchable and
  rebindable there, and the preferences window shows the *same* commands rather than a second copy of
  the state — two writers to one setting is how a preferences window and a menu tick disagree.
- ⚠ **A setting is not saved on every keystroke.** The layout file's rule (`written on the way down`)
  applies here for the same reason, with an explicit Apply for anything that costs something to
  change.

### A5 — The keybinding editor

`KeyMap` has conflict detection, per-command overrides, defaults-vs-overrides separation, and reset.
There is no UI, which doc 11 already flags. The panel is a `DataGrid` of command / category /
binding / source, a filter box, a "press a key" capture through `Vixen.Input`'s rebinding path,
conflict reporting inline, per-row and global reset, and import/export of a keymap file.

⚠ **Presets matter more than they look.** A Unity user and an Unreal user disagree about what `W`
does and both are certain. Shipping `Vixen`, `Unity` and `Unreal` keymap presets — a YAML file each,
which is what `KeyMap`'s override layer already reads — converts a week of friction into a dropdown.

### A6 — Layouts, completed

- **Open documents belong to an arrangement.** `current.vxlayout` records the panels; an asset editor
  opened by double-click is registered on demand and named by GUID, so it is *nameable* — the layout
  just does not carry the list. Recording it, and reopening those documents on restore, is what makes
  "the editor comes back how I left it" true.
- **A layout per mode is a menu, not five presets.** Window ▸ Layouts ▸ with the presets, the user's
  saved ones (currently a palette source only), Save Layout As…, and Reset. ⚠ The palette source
  stays — an unbounded list belongs there — but a menu with the five presets on it is what a new user
  finds.
- **Two more presets to ship**: `Profiling` and `Sequencing`, once Parts B4 and B5 exist.

### A7 — Notifications, messages, and the Console

- **A Message Log panel** over `NotificationCenter`'s history, which is kept and bounded and has no
  view. Errors already do not expire; what is missing is the place they accumulate.
- **The Console is a real panel** (this is the largest single item in Part A): a virtualised list over
  `Vixen.Core.Diagnostics`' ring buffer, with level toggles (error/warn/info/debug counts as badges),
  a category filter, a search box, collapse-duplicates, clear, clear-on-play, a detail pane showing
  the full record and stack, and double-click-to-open-source through the external-tool setting.
  ⚠ **It must not allocate per line.** A game logging per frame into a panel that keeps strings is a
  leak with a UI; the ring buffer is fixed-size and the panel virtualises over it.

### A8 — Search everywhere

`Ctrl+P` is a command palette over `IPaletteSource`. `Ctrl+Shift+F` should be the same machinery over
*content*: assets by name and type, entities by name and component, settings, menu actions, and
in-file matches from `ReferenceIndex`. The sources do their own matching (already the contract), so
the asset source can index rather than scan. Results are grouped by source with a preview pane.

⚠ **Find References is the same query and belongs in three places at once** — the browser's context
menu, the inspector's asset field, and here. `ReferenceIndex` answers it already.

---

## Part B — The panel inventory

Every window an AAA editor has, what it corresponds to, what it costs, and where it goes. **Owner**
is the assembly that should hold it. Status: ✅ built, 🟡 partial, ⛔ absent.

### B1 — Core editing

| Panel | UE / Unity | Owner | Status | What is owed |
|---|---|---|---|---|
| **Hierarchy** | Outliner / Hierarchy | `.App` → `.SceneView` | ✅ | — |
| **Inspector** | Details / Inspector | `.Inspector` | 🟡 | Component add/remove UI (see E1's table for what it needs), multiple inspector windows, pinned/favourite members, debug (raw) mode |
| **Scene viewport** | Level Viewport / Scene | `.SceneView` | 🟡 | See [B2](#b2--the-viewport) |
| **Project browser** | Content Browser / Project | `.App` | 🟡 | Grid view with thumbnails, saved filters, collections/favourites, drag-and-drop out (see E1's table), source-control column, folder tree beside the list rather than one tree |
| **Console** | Output Log / Console | `.Ui` | ✅ | — |
| **Message log** | Message Log | `.Ui` | ⛔ | A view over the notification history |
| **Command palette** | — (both have search) | `.Ui` | ✅ | Recency boosting, more sources ([A8](#a8--search-everywhere)) |

### B2 — The viewport

The viewport is one panel and about nine features, so it gets its own table.

| Feature | Status | What is owed |
|---|---|---|
| Camera navigation | ✅ | — |
| Transform gizmos, snapping, spaces, pivots | ✅ | Filled plane quads and a torus ring — the hit test already treats a plane handle as filled and the outline understates it |
| Picking | 🟡 | A ray test works; the **picking stage** is written, tested, and driven by nothing, because it needs a render target the host does not own |
| Marquee / rubber-band select | ⛔ | A region resolve rather than a one-pixel readback |
| Selection outline | ⛔ | A stencil pass and a post effect — the one thing here that is not geometry a tool can build |
| **Viewport overlay toolbar** | ⛔ | Camera speed, view mode, show flags, gizmo toggles, projection, maximise — floating over the top-left of the viewport, as both reference editors do. Chrome, not rendering |
| **Multiple viewports** | ⛔ | `ViewportLayout` exists in `.SceneView`; 1/2/4-pane with independent cameras and view modes is the host wiring N `ScenePresenter`s |
| **Show flags** | ⛔ | A checklist of what to draw: grid, gizmos, wireframe, colliders, lights, audio sources, navigation, bounds. A menu over a bitset the presenter reads |
| **Debug view modes** | 🟡 | `ViewModes` exists in the model; the UI to pick one and the compositor swap that honours it do not |
| **Stats overlay** | ⛔ | Draw calls, triangles, frame time, in-viewport |
| **View bookmarks** | 🟡 | `ViewBookmark` exists and holds the camera across a panel rebuild; `Ctrl+1..9` to set and `1..9` to recall is commands over it |
| **Meshes and materials** | ⛔ | The viewport draws lines. This is doc 14 Phase 7's neighbourhood, not a shell gap, and it is the single most visible difference from a reference editor |

### B3 — Content and assets

| Panel / window | UE / Unity | Owner | Status | Notes |
|---|---|---|---|---|
| Import settings (texture, model) | Import settings | `.AssetEditors` | ✅ | Includes the per-target override matrix |
| Material editor | Material Editor | `.AssetEditors` | ✅ | Preview is a request; the host renders |
| Prefab editor | Blueprint / Prefab mode | `.AssetEditors` | 🟡 | Instance links are not written to a `.vxscene` yet — an instance placed today is an ordinary subtree tomorrow |
| Shader editor (`.rvn`) | — / Shader | `.AssetEditors` | ✅ | One file, no cross-file resolution |
| UI editor (`.vxml`/`.vcss`) | UMG / UI Builder | `.AssetEditors` | 🟡 | VCSS preview is genuinely live; VXML is structure only. A *visual* UI designer is post-1.0 and is called out in [Part G](#part-g--out-of-scope) |
| Addressable groups | — / Addressables | `.AssetEditors` | ✅ | Runs the real planner |
| Graphics compositor | — | `.AssetEditors` | ✅ | No importer for `.vxcomp` yet |
| **Asset picker browser** | Asset picker | `.App` | ⛔ | `AssetDrawer` raises `PickRequested` and nothing opens. Small, and every asset field is dead without it |
| **Thumbnail service** | ✅ both | `.App` | ⛔ | Offscreen render per asset type, cached on disk under `Library/`, invalidated by source hash. Unlocks the grid view, the picker, and node previews |
| **Import dialog** | ✅ both | `.App` | ⛔ | Drag a file in from the OS, choose a destination, preview the settings |

### B4 — Diagnostics

`Vixen.Editor.Profiler` and `Vixen.Editor.Debugger` are named in doc 11's tree and **neither project
exists**. Doc 13 specifies the runtime half; `Vixen.Core.Diagnostics` has the sample rings and the
Chrome-trace export.

| Panel | UE / Unity | Status | Depends on |
|---|---|---|---|
| **CPU profiler** — flame chart over job-system samples, per-frame timeline, capture/compare | Insights / Profiler | ⛔ | `Profiler` sample rings ✅ |
| **GPU profiler** — timeline from timestamp queries, pass breakdown | GPU Visualizer / Profiler | ⛔ | Timestamp queries in the RHI |
| **Frame debugger** — step draw calls, inspect bound state and render targets | RenderDoc-adjacent / Frame Debugger | ⛔ | Command-stream capture; `Vixen.Graphics.Null`'s recording harness is the shape |
| **Memory** — managed heap, native allocators, GPU heaps, asset residency | Memory Insights / Memory Profiler | ⛔ | `LeakTracker`, allocator instrumentation |
| **Remote inspector** — attach to a running build, browse and mutate live entities | Device output / Profiler remote | ⛔ | Doc 13's protocol; `Vixen.Net` transports ✅ |
| **Statistics** — counts, budgets, warnings per scene | Statistics / Stats | ⛔ | Scene traversal only |

⚠ **The profiler must be able to profile the editor.** An editor that can only profile the game
cannot answer why the editor is slow, and doc 00's editor-shell performance bar is a claim about the
editor. The same panel over the same rings, with a source selector.

### B5 — Authoring

| Panel | UE / Unity | Owner | Status | Notes |
|---|---|---|---|---|
| Shader graph | Material Editor / Shader Graph | `.ShaderGraph` | ✅ | Owes procedural nodes, custom-code node, post/UI masters, node previews |
| VFX graph | Niagara / VFX Graph | `.VfxGraph` | 🟡 | Model and compiler exist; **no document, no factory, no editor registration** — it is not reachable from the editor. Then a live preview |
| **Animation graph** | Animation Blueprint / Animator | ⛔ | ⛔ | The third graph doc 11 names. States, transitions, blend trees, layers, masks, IK, parameters, events |
| **Animation clip editor** | Sequencer curves / Animation window | `.AssetEditors` | ⛔ | `CurveEditor` and `Timeline` controls exist; the format does not. Dope sheet, curve mode, event track |
| **Sequencer / cinematics** | Sequencer / Timeline | new | ⛔ | Tracks over entities, cameras, audio, events; `Timeline` control exists. This is the largest single missing authoring surface and it is what "cinematics" means to both reference editors |
| **Audio mixer** | Audio Mixer (both) | new | ⛔ | Buses, sends, effects, snapshots. `Vixen.Audio` has the runtime |
| **Input actions** | Input / Input System | `.AssetEditors` | ⛔ | Doc 11's own gap: `Vixen.Input` has the whole action model and no editor. Maps, actions, composite bindings, control schemes, interactive rebinding |
| **Font editor** | — / Font asset | `.AssetEditors` | ⛔ | Glyph coverage, atlas preview, fallback chain |
| **Curve / gradient presets** | ✅ both | `.Inspector` | 🟡 | Controls exist; a library of saved presets does not |

### B6 — World building

| Panel | UE / Unity | Status | Notes |
|---|---|---|---|
| **World / scene settings** | World Settings / Lighting+Physics settings | ⛔ | The per-scene half of Project Settings: environment, ambient, fog, GI, physics, navigation. Inspector over a `[DataContract]` on the scene |
| **Layers and tags** | Layers / Tags & Layers | ⛔ | Needs an ECS-side concept first |
| **Lighting / GI** | Lighting / Lighting window | ⛔ | Doc 19 retires baked lightmaps, so this is a *dynamic* GI panel: distance-field coverage, irradiance-field placement, surface-cache budgets, and the debug views for each |
| **Navigation** | Navigation / Navigation window | ⛔ | `Vixen.Navigation` exists; bake settings, agent profiles, a debug draw |
| **Physics debug** | — | ⛔ | Collider draw, contact visualisation, layer matrix |
| **Terrain / foliage** | Landscape + Foliage / Terrain | ⛔ | Post-1.0, [Part G](#part-g--out-of-scope) |
| **Multi-scene** | Levels / multi-scene editing | ⛔ | The editor opens one scene by path. Additive loading, per-scene visibility and lock, and a scene as a unit of ownership is what a team of more than three needs |

### B7 — Build, deploy, and extend

| Window | UE / Unity | Status | Notes |
|---|---|---|---|
| **Build settings** | Project Launcher / Build Settings | ⛔ | Target, configuration, scenes-in-build, variant, output path, over `Tools/Vixen.Cli`'s existing calls |
| **Device manager / deploy** | Device Manager / Build & Run | ⛔ | List devices, deploy, launch, attach the remote inspector |
| **Plugin manager** | Plugins / Package Manager | ⛔ | A list over `PluginHost.Plugins` with enable, disable, reload. Doc 11 calls it "a view and nothing more" and it is |
| **Source control** | Revision Control / Version Control | ⛔ | P2. Status column in the browser, and check-out/revert/diff/history over a provider interface with a git implementation |
| **Crash reporter** | Crash Reporter (both) | ⛔ | Out-of-process, minidump plus the last N log lines plus the undo history, with consent |
| **Session recovery** | Auto-save recovery (both) | ⛔ | A journal, and a kill-and-restore loop that tests it |

---

## Part C — The menu bar, entry by entry

Ten menus. `⋯` marks a line that opens a window or dialog; **bold** is new; the rest exists.
Shortcuts are the defaults and every one is rebindable, with the `Unity` and `Unreal` presets from
[A5](#a5--the-keybinding-editor) remapping them wholesale.

### File

| Entry | Id | Shortcut |
|---|---|---|
| **New Project⋯** | `file.new-project` | |
| **Open Project⋯** | `file.open-project` | `Ctrl+Shift+O` |
| **Open Recent ▸** | `file.recent` (dynamic) | |
| — | | |
| **New Scene** | `file.new-scene` | `Ctrl+N` |
| **Open Scene⋯** | `file.open-scene` | `Ctrl+O` |
| Save Scene | `file.save` | `Ctrl+S` |
| **Save Scene As⋯** | `file.save-as` | `Ctrl+Shift+S` |
| **Save All** | `file.save-all` | `Ctrl+Alt+S` |
| — | | |
| **Import Assets⋯** | `assets.import-files` | |
| **Export Package⋯** | `file.export-package` | |
| — | | |
| **Project Settings⋯** | `file.project-settings` | |
| — | | |
| Exit | `file.exit` | `Alt+F4` / `Cmd+Q` |

### Edit

Undo, Redo, **Undo History⋯**, | **Cut** `Ctrl+X`, **Copy** `Ctrl+C`, **Paste** `Ctrl+V`, **Paste As
Child**, **Duplicate** `Ctrl+D`, **Delete** `Del`, Rename `F2`, | **Select All** `Ctrl+A`,
**Deselect All** `Ctrl+Shift+A`, **Invert Selection**, **Select Children**, **Select Parent**,
| **Search Everywhere⋯** `Ctrl+Shift+F`, **Find References**, | **Preferences⋯** `Ctrl+,`,
**Keyboard Shortcuts⋯**.

⚠ **Cut/Copy/Paste are context-scoped, not one command each.** They mean different things in the
outliner, the browser, the inspector and a text field, which is what [A3](#a3--command-system-completed)'s
command context is for.

### Assets

**Create ▸** (Folder, Scene, Prefab, Material, Shader, Shader Graph, VFX Graph, UI Document,
Stylesheet, Animation Clip, Animation Graph, Input Actions, Addressable Group, Graphics Compositor,
C# Behavior), | **Show in Explorer/Finder**, **Open**, Rename, **Delete**, **Move To⋯**, |
**Reimport**, **Reimport All**, | **Find References**, **Select Dependencies**, | Refresh `Ctrl+R`,
Import Assets, Build Content `Ctrl+Shift+B`.

⚠ **Rename and Delete here are not the inspector's.** Renaming an asset moves a file and rewrites
every referrer — `EditorContext.Touch` and `ReferenceIndex` are both already built for exactly this —
and Delete must report what breaks before it does it. Doing them naively is the fastest way to
corrupt a project, which is why the browser's rows are read-only today.

### Entity

Unreal calls it Actor, Unity calls it GameObject; the noun here is Entity.

**Create Empty** `Ctrl+Shift+N`, **Create Empty Child** `Alt+Shift+N`, | **3D Object ▸** (Cube,
Sphere, Capsule, Cylinder, Plane, Quad), **Light ▸** (Directional, Point, Spot, Area), **Camera**,
**Audio ▸**, **UI ▸**, **VFX ▸**, | **Make Prefab⋯**, **Unpack Prefab**, **Apply Overrides**, |
**Group** `Ctrl+G`, **Ungroup**, **Set Parent**, **Clear Parent**, | **Align With View**
`Ctrl+Shift+F`, **Move To View**, **Snap To Floor** `End`, | Focus `F`, | **Toggle Active**
`Alt+Shift+A`, **Toggle Lock**.

### Scene

Built, and the most complete menu in the editor. Additions: **Viewport Layout ▸** (1 / 2 / 4 panes),
**View Mode ▸** (Lit, Unlit, Wireframe, Albedo, Normal, Roughness, Overdraw, Light Complexity),
**Show ▸** (the show-flag checklist), **Bookmarks ▸** (set `Ctrl+1..9`, go `1..9`), **Camera Speed ▸**.

### Play

**Play** `F5`, **Pause** `Ctrl+Shift+P`, **Step Frame** `F10`, **Stop** `Shift+F5`, | **Mode ▸**
(In Editor, Standalone Process, Server + N Clients — both topologies exist in `.SceneView`'s
`PlayMode` and `PlayerSessions` and neither has a menu), | **Options ▸** (Maximise on Play, Mute
Audio, Clear Console on Play, Enter Play Mode Options).

⚠ **Play is a menu *and* the toolbar's centre group.** It is the most-clicked control in either
reference editor and the one place where being one click away is measurably worse.

### Window

**Layouts ▸** (Default, Scene, Shading, Animation, Debug, Profiling, Sequencing, | Save Layout As⋯,
Reset Layout), | **Panels ▸** (dynamic, every registered panel, ticked when open), | Float Panel,
**Close Panel** `Ctrl+W`, **Next/Previous Tab** `Ctrl+Tab`, | **Toggle Theme**, **Full Screen** `F11`.

### Build

**Build Settings⋯**, | Build Content `Ctrl+Shift+B`, **Build and Run** `Ctrl+B`, | **Target ▸**
(Windows, Linux, macOS, Android, iOS, Web), **Configuration ▸** (Debug, Release), | **Deploy ▸**
(devices), | **Clean Library**, **Rebuild Shaders**.

### Tools

**Profiler**, **Frame Debugger**, **Memory**, **Statistics**, **Remote Inspector**, | **Plugins⋯**,
Reload Plugins, | **Reload Shaders**, **Reload Styles**, | **Generate Diagnostics Report⋯**.

### Help

**Documentation** (`help.documentation` — the id the shell already names), **API Reference**,
**Release Notes**, **Report a Bug⋯**, **Show Log Folder**, | About.

---

## Part D — Functions, by domain

The verbs. A menu line without the verb behind it is the thing this section exists to prevent.

### Selection

Click, add (`Ctrl`), range (`Shift`), marquee, select-all/none/invert, select children/parent,
select by type, select by name, **selection sets** (save and recall a selection — a small feature
professionals use constantly and neither reference editor made discoverable), lock, isolate
(hide everything else). Selection is already `Selection<T>` and signal-backed; what is missing is
multi-select in the outliner and the marquee in the viewport.

### Transform

Translate/rotate/scale ✅, world/local/parent/screen space ✅, pivot/centre ✅, grid and angle snap
✅, **vertex snap** (in the model, not honoured — needs the readback picking already does, for a
position rather than an id), **surface snap**, numeric entry ✅ through the inspector, **relative
transform entry**, **copy/paste transform**, **reset transform**, **align to view**, **distribute and
align** across a multi-selection.

### Hierarchy

Create, delete, rename, duplicate ✅ (three of five undoable), **reparent by drag** ⛔ (the primitive
exists), **reorder among siblings** (`Hierarchy.SetParentAfter` exists), group/ungroup, **multi-select
operations**, **filter by component type**, **visibility and lock per entity**.

### Content

Create asset from template, rename with reference fixup, move with reference fixup, delete with
"what breaks" reporting, duplicate, reimport, show in OS, find references, select dependencies,
**drag into the viewport** ✅ (placement with surface snapping is built), **drag into an inspector
field** ⛔, **drag from OS into the browser** ⛔, favourites, collections, saved filters.

### Play and simulate

Play/pause/step/stop, both topologies ✅ in the model, **maximise on play**, **stats during play**,
**edit-during-play with a clear rule about what survives** — ⚠ this is the one place where being
different from Unity is *better*: a snapshot restore that translates selection handles
(`WorldSnapshot.Restore` returns the map) is already built, and a documented "changes made in play
mode are discarded, and the editor says so before entering" is honest where Unity's silent loss is
the single most complained-about behaviour in that editor.

### Prefabs

Create from selection, open in isolation ✅, apply/revert per-override ✅ in the inspector,
**instance links written to the scene** ⛔ (doc 08's R7), **nested prefabs**, **variants**.

---

## Part E — Milestones

Effort in engineer-months, on doc 14's scale and benchmarked the same way. This is **~11 EM on top of
Phase 6's remaining 4.5**, which is what the difference between "the editor works" and "the editor is
one a professional will use" actually costs.

Each milestone ends with something demonstrable, and each is ordered so the thing it needs already
exists.

### E0 — The frame (1.5 EM)

Register the five commands the menu bar already names. `DialogService`, native dialogs wired,
save-on-close. Context menus, command contexts, the icon set, radio groups, `MenuDynamic` producers.
Toolbar sections, dropdowns and toggle groups. Status-bar selection count and frame time. Title bar.
Window, Build, Tools, Assets, Entity and Play menus, with every line either implemented or explicitly
disabled with a tooltip saying why.

**Exit:** every menu in Part C is present; no menu line is silently missing; `--run` can drive any of
them; the golden-screenshot fixture covers the full bar in both themes.

### E1 — The three panels people live in (2.0 EM)

Console (the whole of A7). Outliner: multi-select, drag-reparent undoably, filters, visibility and
lock, virtualisation, context menu, selection inwards. Content browser: grid view, thumbnail service,
filters, create menu, rename/move/delete with reference fixup, drag-and-drop both ways, asset-picker
browser. Inspector: nested drawer, list drawer, component add/remove, lock, context menu.

**Exit:** the scenario test in doc 11's testing table runs end to end — create project, import asset,
drag into scene, edit property, undo, save, reopen, assert — and a second one that renames the asset
and asserts the scene still resolves it.

**Where E1 stands.** Both exit scenarios run, in `Vixen.Editor.App.Tests/ScenarioTests`, through the
`Vixen.Editor.Testing` harness — which E6 owns and this milestone's ordering note said to build here.
The console, the outliner and the browser's verbs are done, as are the inspector's nested and list
drawers, its lock and its row menu. Three things are not, and each is a gap in the *runtime* rather
than in the panel:

| Not built | What it actually needs |
|---|---|
| **Drag from the browser into the scene** | No runtime component carries an `AssetId`, so there is nothing for an entity to hold a mesh or a texture *in*. A drop that made an entity named after the file would be the editor pretending. The scenario puts a cube in through the Entity menu instead and says so where it does it |
| **Component add/remove in the inspector** | Two halves. `ISceneComponentBinder` already does boxed `ValueOn`/`AddTo`; it needs `Has`, `RemoveFrom` and an enumeration of what is registered. And the inspector draws an `[Inspector]` descriptor, which no runtime component carries — so it needs an adapter from `Vixen.Core.Reflection`'s `TypeDescriptor`, whose boxed accessors every `[DataContract]` component already has. The write-back is the nested drawer's by-value path, which `InspectorField.WriteEach` exists for |
| **Grid view and thumbnails** | A thumbnail service and a second view over the same tree. The one item here that is cosmetic rather than structural |

⚠ **"Rename with reference fixup" turned out not to be a rewrite**, and the note above about it being
the fastest way to corrupt a project is right for a reason worth recording. Doc 08 chose a GUID in a
prefixed scalar over a path, so a referrer needs nothing done to it when a file moves. The corruption
is leaving the **sidecar** behind: the next scan finds an asset with no identity, mints a new one, and
every reference in the project dangles with nothing having reported an error — invisible until
somebody opens a scene. `AssetOperations` is that one invariant and the bookkeeping around it.

### E2 — The viewport (2.0 EM)

Overlay toolbar, show flags, view-mode UI and the compositor swap behind it, multi-viewport, stats
overlay, bookmarks, marquee selection, the picking stage driven by a real target, selection outline,
vertex and surface snap, filled gizmo geometry.

**Exit:** four panes with independent cameras and view modes; a marquee selects; a selected object is
outlined; a golden screenshot per view mode.

### E3 — Settings, keys, layouts, plugins (1.0 EM)

Project Settings window, Preferences window, keybinding editor with the three presets, layouts
carrying open documents, OS-window promotion recorded in an arrangement, plugin manager, Message Log,
search everywhere, startup Project Browser.

**Exit:** a fresh install can be driven to a Unity-shaped or Unreal-shaped keymap in one dropdown;
the editor restores its full arrangement including open documents; a plugin can be enabled, disabled
and reloaded from a panel.

### E4 — Diagnostics (2.0 EM)

`Vixen.Editor.Profiler` and `Vixen.Editor.Debugger` as projects. CPU flame chart, GPU timeline,
frame debugger, memory view, statistics, remote inspector client, device manager. Profiling layout
preset.

**Exit:** a frame of the editor and a frame of a running game are both profilable in the same panel;
a draw call can be stepped and its render target inspected; a build on a device can be attached to
and an entity mutated live.

### E5 — Authoring surfaces (2.5 EM)

VFX graph reachable (document, factory, registration) and previewed. Animation clip format and
editor. Animation graph. Sequencer. Input actions editor. Audio mixer. Font editor. World settings.
Lighting/GI panel. Navigation panel. Multi-scene.

**Exit:** doc 11's thirteen-row asset-editor table has thirteen rows built; a cinematic can be
authored, scrubbed and played.

### E6 — Production hardening (1.5 EM)

Crash reporter, session recovery with the kill-and-restore loop, source-control provider and git
implementation, build settings and deploy, `PublishEditor` with signing and notarisation, the editor
UI automation harness and the golden-screenshot suite over every layout preset, the editor-shell
performance benchmark from doc 00 actually run.

**Exit:** Phase 6's stated exit criteria, plus: the editor survives being killed mid-edit with no
lost work; a signed installer exists for three desktops; the performance bar is a number in CI.

### Ordering note

E0 → E1 → E2 is strictly sequential: E1's context menus are E0's, and E2's overlay toolbar is E0's
toolbar work. **E3 and E4 are independent of each other and of E2**, so with more than one engineer
they run in parallel from the end of E1. E5 depends on E1 (thumbnails, browser) and partly on E2
(previews). E6 depends on everything and should start its automation harness during E1, not after E5
— a harness written last is a harness written against a frozen target.

---

## Part F — Testing

Doc 11's testing table is right and this adds three rows it does not have, all three of which are
about the *shell* rather than the model.

| Level | Mechanism |
|---|---|
| **Menu and command coverage** | A test that walks `MenuModel` and asserts every `MenuCommand` names a registered command. The five dangling ids found while writing this document are exactly what it catches, and it is about fifteen lines |
| **Keymap presets** | Each preset file is asserted to bind only registered commands and to raise no conflict. A preset that silently drops a binding is worse than no preset |
| **Panel lifecycle** | Every registered panel is built, docked, floated, closed and rebuilt in one test. `A panel's factory runs again when it is reopened` is already a documented hazard and nothing currently proves a given panel survives it |
| Editor UI automation | As doc 11: a headless host driving synthetic input against the real element tree. Extended with one scenario per milestone exit above |
| Golden screenshots | As doc 11, plus: every layout preset, both themes, the full menu bar open, and one per viewport view mode |

⚠ **The screenshots are the ones that find shell bugs.** `Vixen.Editor.Ui`'s own README records that
the inspector bugs which started that work were invisible to every behavioural test and obvious in
the first screenshot. That is not an anecdote, it is the reason the golden suite is a gate rather
than a nicety.

---

## Part G — Out of scope

Named so that "missing" and "not doing" are different words.

| Not doing | Why |
|---|---|
| **Visual scripting** (Blueprints / Bolt) | The node-graph framework makes it buildable and it is a runtime project, not an editor one: it needs an execution model, a debugger, and a compilation target. Post-1.0, and the framework is deliberately ready for it |
| **Terrain and foliage tools** | A whole subsystem — heightfields, layers, sculpting, procedural scatter, LOD, and a renderer for each — behind a mode. Post-1.0, and the `IEditorMode` seam in [A1](#a1--the-application-frame) is what it will attach to |
| **A visual UI designer** | The VXML pane previews structure and the VCSS pane is genuinely live, which is the return on hot reload. A drag-and-drop designer is a second authoring model over the same document and should wait until the markup layer has stopped moving |
| **Mesh editing / modelling tools** | Unreal ships them and they are not what an engine is for. Import from a DCC |
| **Collaborative multi-user editing** | The document model's mutation vocabulary was chosen partly with this in mind (doc 11 names multi-user awareness), so it stays *possible*. It is not 1.0 |
| **A native Metal or D3D12 editor backend** | Doc 14's decision, unchanged |

---

## Risks

| Risk | Mitigation |
|---|---|
| **The viewport draws lines.** No amount of shell work makes an editor that cannot show a model feel finished, and this is the first thing anybody notices | It is Phase 7's material-system wiring, not this document's, and it should be scheduled *before* E2 rather than after — E2's view modes and outline are much easier to judge against real geometry |
| **Scope of Part B is larger than Parts A and C together** | The milestones are ordered so each ends demonstrable, and E3/E4 parallelise. The panel inventory is a checklist, not a commitment to build all of it before 1.0 — B4 and B7 are where a schedule squeeze should land |
| **Icon set is a design dependency, not an engineering one** | ~120 glyphs is a real piece of work by someone who draws. Start it at E0 and treat a missing icon as a labelled button rather than a blocker |
| **Keymap presets promise compatibility they cannot fully keep** | A preset maps the commands that exist. It must be documented as "the bindings you know for the features we have", not as an emulation mode |
| **The editor gets slower one panel at a time** | The status-bar frame time in [A1](#a1--the-application-frame) makes it visible daily, and E6's benchmark makes it a gate. `It redraws every frame` is a known gap; redraw-on-change should land in E2, while the number of panels is still small enough to audit |

Licensed under Apache-2.0.
