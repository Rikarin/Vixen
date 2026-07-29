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

Reconciled against the code in `Editor/`, not against doc 11's aspirations, and **as of the end of
[E2](#e2--the-viewport-20-em)**. The counts came out of the registry rather than
from counting `Add` calls, because half of the commands are registered in loops.

| | Built | Missing |
|---|---|---|
| **Panels** | Hierarchy, Inspector, Scene viewport (1/2/4 panes), Project browser, Console, one per open asset document | ~24 more, listed in [Part B](#part-b--the-panel-inventory) |
| **Menus** | All ten of [Part C](#part-c--the-menu-bar-entry-by-entry): File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help | Nothing structural. Individual lines are disabled-with-a-reason rather than absent |
| **Commands** | 197 registered ids, of which 51 are declared-and-disabled with the milestone that builds them | The 51, plus whatever E3–E6 adds |
| **Windows** | One OS window, a floating dock group promoted to a real one, and drawn modal dialogs | Preferences, Project Settings, Keybindings, Plugin manager, Project browser (startup), About |
| **Layouts** | Five presets, saved/named arrangements, `current.vxlayout`, floating groups with their geometry | Open documents are not part of an arrangement |
| **Shell services** | Commands with contexts and scopes, keymap, palette, menus, context menus, toolbar with sections and groups, status bar, notifications, background tasks, theming, localisation, docking, plugins, dialogs, icons, MRU, **an automation harness** | Modes, search-everywhere, keymap presets |

The three findings this document opened with are all closed, and they are kept because the reasoning
is the record of why E0 was shaped the way it was:

- ✅ **Five menu lines already named commands nobody registered.** `file.new-project`,
  `file.open-project`, `file.save-all`, `edit.preferences` and `help.documentation` — the bar was
  *already shaped* for the editor this document describes, and registering them was the smallest
  possible first commit. All five exist; two of them are still declared-and-disabled, because
  swapping a project underneath a live editor is [E3](#e3--settings-keys-layouts-plugins-10-em).
- ✅ **The file-dialog blocker was gone and the READMEs had not caught up.** `INativeDialogs` is now
  reached through `IPlatformSupplement` and `EditorServices`, and Open Scene, Save As and Import grey
  themselves out on a platform without pickers rather than being absent.
- ✅ **The Console was a line of text.** It is now the whole of [A7](#a7--notifications-messages-and-the-console): a virtualised view over
  `RingBufferSink` with level badges, a category filter, search, collapse, clear-on-play and a detail
  pane.

---

## Part A — Shell infrastructure

These are the things every panel in Part B needs and none of them should build twice. Nothing in Part
B should start before the piece of Part A it stands on.

> ⚠️ **A1–A3 and A7 are built, and the prose below is kept in the present tense on purpose.** It is
> the record of *why* each piece is shaped the way it is, which is the part a reader still needs and
> the part a checklist loses. Where a section's opening sentence describes the editor as it was
> before [E0](#e0--the-frame-15-em) — "three of those five exist", "everything modal is currently
> unbuildable" — read it as the problem statement it was written as. What is genuinely still owed is
> called out in each section and summarised in the table above: **the mode bar, keymap presets,
> search-everywhere, the Message Log panel, command repeat, and palette recency.**

### A1 — The application frame

The frame is menu bar → **mode bar** → toolbar → workspace → status bar. Four of those five exist.

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
- **Promoting a floating dock group to a real OS window** is doc 11's remaining docking gap, and it is
  now nearly closed. `EditorPane` proves a second surface, swapchain and input queue;
  `--run view.float-panel` is validation-clean; and ⚠ **the claim that `DockLayout` does not record
  which groups were promoted is stale** — `DockFloat(Group, X, Y, Width, Height)` is serialised with
  the arrangement, and whether one becomes an OS window or a rectangle inside the host is
  `IUiWindowHost`'s answer at restore time rather than something the file has to state. What is
  genuinely left is the second half of the original sentence: **a rule about what happens when a
  saved window is off every current display**, which today restores a panel somewhere nobody can
  reach it.
- **A startup Project Browser window.** Unreal's project browser and Unity Hub exist because the
  first question an editor is asked is "which project", and `--project` is not an answer for a user.
  Recent projects with their last-opened time, a New Project pane over `Tools/Vixen.Templates`, and a
  Browse button. It is the last thing in this section to build and the first thing a new user sees.

### A3 — Command system, completed

The registry is right. Five things were missing from it and each showed up as a whole class of feature
that could not be built. **All five are built** — this is E0's substance — and the reasoning is kept
because it is what each of them is *for*, which is the part a reader still needs.

| | Why it unblocked a class of feature |
|---|---|
| ✅ **Context menus** | Right-clicking an outliner row, a browser row, a viewport, a node or an inspector row is how half of an editor's verbs are reached. `MenuPresenter.Context` is the bar's own code with a different anchor, so a context menu and a menu cannot disagree about what a verb does. |
| ✅ **Command context / scope** | An entity delete and an asset delete are both Delete. Each declares its context id, the shell tracks which panel was last acted in, and `KeyMap` files the two under different contexts so neither has to give up the key. |
| ✅ **Dynamic menus** | `MenuDynamic` produces Open Recent, Panels ▸ and Layouts ▸. Add Component ▸ is the one consumer still owed, and it is waiting on the component bridge rather than on this. |
| ✅ **Icons** | `EditorIcons` — 23 glyphs on the 24×24 grid with an id per icon, rather than the ~120 estimated here. The estimate was for a full set; what a toolbar and ten menus actually name is an order of magnitude less, and a plugin can add its own. |
| ✅ **Radio groups** | `RadioGroup` on a command, drawn as a segmented control in the toolbar and as one mark in a menu. Translate/Rotate/Scale is one *choice* and reads as one. |

Still owed here: **command history and repeat** (`Ctrl+Shift+R` repeats the last command), and
**"recently used" boosting in the palette**, which is the single cheapest thing that makes a palette
feel fast.

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
which is the format `KeyMap`'s override layer already reads — converts a week of friction into a
dropdown.

⚠ **`KeyMap` has no notion of a preset**, and "the override layer already reads it" is about the
*file format* rather than about the mechanism. A preset is a third layer between the shipped defaults
and the user's own overrides, because choosing Unreal and then rebinding one key has to leave the
other two hundred following the preset rather than being copied into the user's file — otherwise the
next preset update reaches nobody who has ever rebound anything. That layer is the work; the dropdown
is not.

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
| **Inspector** | Details / Inspector | `.Inspector` | 🟡 | Multiple inspector windows, pinned/favourite members, debug (raw) mode |
| **Scene viewport** | Level Viewport / Scene | `.SceneView` | 🟡 | See [B2](#b2--the-viewport) |
| **Project browser** | Content Browser / Project | `.App` | 🟡 | Saved filters, collections/favourites, source-control column, a folder tree beside the grid |
| **Console** | Output Log / Console | `.Ui` | ✅ | — |
| **Message log** | Message Log | `.Ui` | ⛔ | A view over the notification history |
| **Command palette** | — (both have search) | `.Ui` | ✅ | Recency boosting, more sources ([A8](#a8--search-everywhere)) |

### B2 — The viewport

The viewport is one panel and about nine features, so it gets its own table.

| Feature | Status | What is owed |
|---|---|---|
| Camera navigation | ✅ | — |
| Transform gizmos, snapping, spaces, pivots | ✅ | — |
| Picking | 🟡 | A ray test and a screen-space region query both work and are exact against what the viewport draws. `PickingRenderer` is still driven by nothing, and ⚠ **the reason has moved rather than gone away**: it is a `SceneRenderer` over a `RenderStage`, so it needs the viewport driven by `RenderSystem` through a `GraphicsCompositor` — which is the same Phase 7 wiring the bottom row names. It is what will be right the day a shader moves a vertex |
| Marquee / rubber-band select | ✅ | — |
| Selection outline | ✅ | An inverted hull built on the processor, not a stencil pass — see `.SceneView`'s README for why that is exact here rather than an approximation, and what it gets wrong if any of three steps is skipped |
| **Viewport overlay toolbar** | ✅ | — |
| **Multiple viewports** | ✅ | — |
| **Show flags** | ✅ | Colliders, audio sources and navigation are deliberately absent: there is no component or mesh behind any of the three, and a tick that does nothing fails this document's second bar. They arrive with the subsystems |
| **Debug view modes** | 🟡 | Six of the nine are drawn by the tool renderer and the UI picks them; roughness, overdraw and light complexity are registered and greyed with the reason, because a mode with no compositor falls back to shaded and would draw the line above it. All nine become compositor swaps with the row below |
| **Stats overlay** | ✅ | — |
| **View bookmarks** | ✅ | — |
| **Meshes and materials** | ⛔ | The viewport draws lines and untextured primitives. This is doc 14 Phase 7's neighbourhood, not a shell gap, and it is the single most visible difference from a reference editor — and it is what the two 🟡 rows above are waiting on |

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

Built, and the most complete menu in the editor. The five additions this document asked for are now
on it: **Viewport Layout ▸** (1 / 2 / 4 panes, on `Alt+1..4`), **View Mode ▸** (nine entries, three of
them greyed with the reason), **Show ▸** (the show-flag checklist), **Bookmarks ▸** (go `1..9` above,
set `Ctrl+1..9` below — recall is used more often than save), **Camera Speed ▸** (five multiples),
and **Maximise Viewport** on `Shift+Space`.

⚠ **Every one of them acts on the *focused* pane**, which is what makes a split layout mean anything:
pressing the wireframe key changes the pane being worked in and not its three neighbours. The
viewport's own floating toolbar is a second view over the same command ids rather than a second set
of controls, and only the focused pane draws one — four strips of which three would be showing their
neighbour's state is worse than one that is always right.

### Play

**Play** `F5`, **Pause** `Ctrl+Shift+P`, **Step Frame** `F10`, **Stop** `Shift+F5`, | **Mode ▸**
(In Editor, Standalone Process, Server + N Clients — both topologies exist in `.SceneView`'s
`PlayMode` and `PlayerSessions` and neither has a menu), | **Options ▸** (Maximise on Play ✅, Mute
Audio, Clear Console on Play ✅, Enter Play Mode Options).

⚠ **Maximise on Play is a preference and not an action**, which is what its tick says: it changes what
the *next* Play does. It goes through the same pair `scene.maximise` uses, so stopping restores
whatever the arrangement was rather than an arrangement it remembered separately — and it is skipped
when the panel is already one pane, or stopping would leave the toggle claiming a viewport was
maximised when nothing had changed.

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
(hide everything else). Selection is already `Selection<T>` and signal-backed; multi-select in the
outliner is E1's and the marquee is E2's, and both are built. ⚠ **A band *touches* rather than
contains**, which is what both reference editors do by default: a rule that only took what it fully
enclosed cannot select anything larger than the pane, so the gesture would stop working precisely
where a scene gets big. Unreal's strict-box preference is worth having and is not what makes the
gesture work.

### Transform

Translate/rotate/scale ✅, world/local/parent/screen space ✅, pivot/centre ✅, grid and angle snap
✅, **vertex snap** ✅, **surface snap** ✅, numeric entry ✅ through the inspector, **relative
transform entry**, **copy/paste transform**, **reset transform**, **align to view** ✅, **snap to
floor** ✅, **move to view** ✅, **distribute and align** across a multi-selection.

⚠ **Vertex and surface snap did not need the readback after all**, and the note that said they did
was reasoning from the wrong end. What a snap asks is "what does this ray hit" and "which vertex is
nearest the pointer", and `SceneProbe` answers both exactly against the geometry the viewport draws —
one matrix inversion per entity for the first, one screen-space box rejection then a projection per
vertex for the second. The readback is what will be right when a shader moves a vertex, which is the
same day the picking stage lands. ⚠ **Both must exclude what is being dragged**, or the answer is the
dragged object's own surface for the whole of every drag — a snap that never moves anything.

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
The console, the outliner and the browser are done, as is the inspector: nested and list drawers, the
lock, the row menu, and component add/remove.

⚠ **Component add/remove needed two things neither side could supply, and both are now there.**
`ISceneComponentBinder` gained `Has` and `RemoveFrom` and the registry an enumeration, because an
archetype knows dense ids handed out in first-touch order and a panel knows names — asking each
registered component in turn is the only direction that works. And `ReflectedDescriptor` draws rows
from the `[DataContract]` description the serializer already generates, so a component never has to
carry `[Inspector]` — which it must not, since that would be a runtime assembly referencing an editor
one. A game's components appear with nothing asked of the game.

The three gaps this section used to name are closed, and each turned out to be a piece of the
*runtime* or of the control set rather than of a panel — which is why they were last:

| Was owed | What it needed, and what it is now |
|---|---|
| **Drag from the browser into the scene** | Nothing in the runtime carried an `AssetId`, so an entity had nowhere to hold "this is the crate". `AssetInstance` is that component, editor-side beside `Light` and `MeshShape` for the same reason they are. ⚠ **It is a reference, not a renderer** — nothing draws an asset yet — but the reference is authored, saved, editable through the inspector's existing asset field, and written in `vx:` form so `ReferenceIndex` counts it and deleting the asset warns about the scene |
| **Picture thumbnails** | A decode and a GPU upload. `IThumbnailSurface` is the seam: the application decides what is worth a picture and reduces it on the thread pool, the host uploads it on the frame thread, and null — a headless run, every test — falls back to the type glyph. Bounded and evicting, because a cache with no ceiling is a leak with a picture on it |
| **A virtualising grid** | `VirtualizingGrid`, beside `VirtualizingPanel` rather than inside it. ⚠ The difference is one number: a list's row is at `n × height` and a grid's item is at a position that depends on the *measured* width, so the same resize that changes the viewport changes which item is where and the content height with it |

What is still owed there is smaller and is in the table above: saved filters, collections, a
source-control column, and a folder tree beside the grid rather than a breadcrumb.

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

**Where E2 stands.** The first three exit clauses run, in `Vixen.Editor.App.Tests/ViewportTests` and
`Vixen.Editor.SceneView.Tests`. Nine of the eleven items are built: the overlay toolbar, the show
flags, the view-mode UI, 1/2/4-pane layouts with a presenter and an image id each, the stats readout,
nine bookmark slots on `Ctrl+1..9` and `1..9`, rubber-band selection, the selection outline, vertex
and surface snap, and filled plane quads with tubular rotation rings. Two are not, and both are the
same dependency wearing two hats:

| Not built | What it actually needs |
|---|---|
| **The picking stage driven by a real target** | ⚠ **The reason stated in [B2](#b2--the-viewport) was wrong in a way worth recording.** It is true that `EditorHost` owns a `RenderGraph` and that `ScenePresenter.Declare` hands back a `GraphTexture` — and neither is what `PickingRenderer` consumes. It is a `SceneRenderer` over a `RenderStage`, so it needs a `GraphicsCompositor` and a render system feeding it, which the editor's viewport does not have: what draws the scene is `SceneMeshes` through `MeshRenderer`, a tool renderer with no materials. Clicking and banding both work, exactly, through the processor — and that is the right answer for primitives and the wrong one the day a shader moves a vertex |
| **The compositor swap behind the view modes** | The same thing. Six modes are expressible as vertex colouring and edge emission on the tool path and are built that way; roughness needs a material, light complexity needs the clustered light list, and overdraw needs an additive pipeline — none of which a tool renderer has. All three are registered and greyed with the reason rather than silently falling back to shaded |

⚠ **This is the Risks table's first row arriving exactly as predicted.** It says the material-system
wiring "should be scheduled *before* E2 rather than after — E2's view modes and outline are much
easier to judge against real geometry". It was not, and the cost is precisely the two rows above:
everything that could be built against the tool renderer was, and the two items that are *about* the
renderer could not be. Neither is shell work and neither should be scheduled as though it were.

⚠ **The golden screenshot per view mode is E6's suite and is not here.** The view modes are covered
behaviourally — what goes into the vertex buffers, and what does not — which is the half a
screenshot cannot check; the half it can is the half `Vixen.Editor.Ui`'s README says finds shell bugs,
and it wants the fixture E6 owns.

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

⚠ **One item here is not editor work at all and E4 cannot be scheduled as though it were
self-contained.** [B4](#b4--diagnostics) already names timestamp queries as the GPU
profiler's dependency; what is worth stating at the milestone is that **there is no query API in the
RHI to build against** — not an interface, not a stub, and nothing in the Vulkan backend. A GPU
timeline needs a query pool, a `WriteTimestamp` on the command list, and a resolve path, added to
`Vixen.Graphics` and implemented per backend. That is a graphics change on the critical path of an
editor milestone, and it is the one thing in E4 that cannot start with the panel.

The other three are ready to build against what exists: `Profiler.Collect` hands back per-thread
sample rings with depth and a frame index, `Vixen.Graphics.Null`'s `CommandRecorder` is the shape a
frame capture takes, and `RemoteSink` is the remote inspector's transport.

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

⚠ **E2's remainder is not an E3 or an E4 item and must not be swept into one.** The picking stage and
the compositor swap are both Phase 7's material wiring, which is doc 14's schedule rather than this
one's; what is left of E2 does not become smaller by waiting and does not belong to a shell
milestone. Whoever picks up Phase 7 closes both, and the panels are already written against them.

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
| **Modelling tools** — sculpting, retopology, UV unwrapping, map baking, remesh | Authoring for its own sake, and not what an engine is for. Import from a DCC. ⚠ **This row used to read "mesh editing / modelling tools" and it was drawing the line in the wrong place** — *blockout* is level design with geometry as the notation, its loop is edit → play → adjust measured in seconds, and a DCC round-trip breaks that loop rather than slowing it. Both reference engines reached the same conclusion from opposite directions (Unreal replaced BSP with Modeling Mode; Unity bought ProBuilder and made it first-party). [blockout-tools.md](../blockout-tools.md) is the plan, and the first table in it is where the line now sits |
| **Collaborative multi-user editing** | The document model's mutation vocabulary was chosen partly with this in mind (doc 11 names multi-user awareness), so it stays *possible*. It is not 1.0. [21](21-realtime-collaboration.md) is what "possible" costs — ~5.75 EM in five milestones, of which only the first (presence, 1.0 EM, touching no document code) is worth building before the rest is funded |
| **A native Metal or D3D12 editor backend** | Doc 14's decision, unchanged |

---

## Risks

| Risk | Mitigation |
|---|---|
| **The viewport draws lines.** No amount of shell work makes an editor that cannot show a model feel finished, and this is the first thing anybody notices | It is Phase 7's material-system wiring, not this document's, and it should be scheduled *before* E2 rather than after — E2's view modes and outline are much easier to judge against real geometry. ⚠ **This came true and cost exactly what it said it would**: E2 shipped nine of eleven items, and the two it did not — the picking stage and the compositor swap behind the view modes — are the two that are *about* the renderer rather than about the shell. See [E2](#e2--the-viewport-20-em) |
| **Scope of Part B is larger than Parts A and C together** | The milestones are ordered so each ends demonstrable, and E3/E4 parallelise. The panel inventory is a checklist, not a commitment to build all of it before 1.0 — B4 and B7 are where a schedule squeeze should land |
| **Icon set is a design dependency, not an engineering one** | ~120 glyphs is a real piece of work by someone who draws. Start it at E0 and treat a missing icon as a labelled button rather than a blocker |
| **Keymap presets promise compatibility they cannot fully keep** | A preset maps the commands that exist. It must be documented as "the bindings you know for the features we have", not as an emulation mode |
| **The editor gets slower one panel at a time** | The status-bar frame time in [A1](#a1--the-application-frame) makes it visible daily, and E6's benchmark makes it a gate. ⚠ **Redraw-on-change was supposed to land in E2 and did not**, and the number of panels is no longer as small: a four-pane layout is four render targets, four collect passes and four sets of chrome, all redrawn every frame whether anything moved or not. What E2 *did* add is the per-pane readout that makes the cost visible — frame time, draw calls and triangles in the corner of each pane — so the gap is now measurable daily rather than only asserted here. Landing it is E3's or E4's, and it is more work than it was |

Licensed under Apache-2.0.
