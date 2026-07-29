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
[E2](#e2--the-viewport-20-em), [E3](#e3--settings-keys-layouts-plugins-10-em) and
[E4](#e4--diagnostics-20-em)** — which ran in parallel, as the [ordering note](#ordering-note) says
they could. The counts came out of the registry rather than
from counting `Add` calls, because half of the commands are registered in loops.

| | Built | Missing |
|---|---|---|
| **Panels** | Hierarchy, Inspector, Scene viewport (1/2/4 panes), Project browser, Console, the seven of [B4](#b4--diagnostics), Message Log, Keyboard Shortcuts, Preferences, Project Settings, Build Settings, Plugins, Undo History, **World Settings, Lighting, Navigation, Scenes**, one per open asset document | ~6 more, listed in [Part B](#part-b--the-panel-inventory) |
| **Menus** | All ten of [Part C](#part-c--the-menu-bar-entry-by-entry): File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help | Nothing structural. Individual lines are disabled-with-a-reason rather than absent |
| **Commands** | Every id [Part C](#part-c--the-menu-bar-entry-by-entry) names, plus Open Recent's one per project, the Build menu's one per target and per variant, and **seven Assets ▸ Create lines, one per asset kind E5 adds**. The declared-and-disabled ones that are left name the rest of E6, Raven's compiler, or a runtime concept that does not exist | Whatever the rest of E6 adds |
| **Windows** | One OS window, a floating dock group promoted to a real one with an off-display rule, drawn modal dialogs, and the startup Project Browser | About is still a notification rather than a window |
| **Layouts** | Seven presets — the six, plus `Sequencing` now that B5 exists — saved/named arrangements, `current.vxlayout`, floating groups with their geometry, **and the open documents** | Nothing |
| **Shell services** | Commands with contexts and scopes, a three-layer keymap with presets, palette, **search-everywhere**, menus, context menus, toolbar with sections and groups, status bar, notifications, background tasks, theming, localisation, docking, plugins, dialogs, icons, MRU, **a settings mechanism**, **an automation harness** | Modes |

The three findings this document opened with are all closed, and they are kept because the reasoning
is the record of why E0 was shaped the way it was:

- ✅ **Five menu lines already named commands nobody registered.** `file.new-project`,
  `file.open-project`, `file.save-all`, `edit.preferences` and `help.documentation` — the bar was
  *already shaped* for the editor this document describes, and registering them was the smallest
  possible first commit. All five exist and all five now do something: the last two were
  declared-and-disabled until [E3](#e3--settings-keys-layouts-plugins-10-em) found that swapping a
  project underneath a live editor is a problem you solve by not swapping one.
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

> ⚠️ **All eight are built bar three items, and the prose below is kept in the present tense on
> purpose.** It is the record of *why* each piece is shaped the way it is, which is the part a reader
> still needs and the part a checklist loses. Where a section's opening sentence describes the editor
> as it was before [E0](#e0--the-frame-15-em) — "three of those five exist", "everything modal is
> currently unbuildable", "there is no UI" — read it as the problem statement it was written as. What
> is genuinely still owed is called out in each section and summarised in the table above: **the mode
> bar, command repeat, and palette recency.**

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
- ✅ **Promoting a floating dock group to a real OS window** is doc 11's remaining docking gap, and it
  is closed. `EditorPane` proves a second surface, swapchain and input queue;
  `--run view.float-panel` is validation-clean; and ⚠ **the claim that `DockLayout` does not record
  which groups were promoted is stale** — `DockFloat(Group, X, Y, Width, Height)` is serialised with
  the arrangement, and whether one becomes an OS window or a rectangle inside the host is
  `IUiWindowHost`'s answer at restore time rather than something the file has to state. The second
  half of the original sentence — **a rule about what happens when a saved window is off every
  current display** — is `PlatformWindowHost.IsReachable`: a hundred and twenty points of title bar
  has to land on something, or the position is dropped and the platform places the window. It is the
  same rule the main window's own geometry goes through, shared rather than written twice.
- ✅ **A startup Project Browser window.** Unreal's project browser and Unity Hub exist because the
  first question an editor is asked is "which project", and `--project` is not an answer for a user.
  Recent projects with their last-opened time, a Browse button and a New Project one.
  ⚠ **It is a drawn dialog rather than an OS window, which is this section's own rule applied to its
  own example** — the first thing a new user sees is precisely the screen a regression must not be
  able to hide in, so it has to be photographable by the golden suite and drivable by the harness.
  ✅ **New Project instantiates the `game` template**, which it did not until E6 needed it to. The
  sentence that used to be here — that `Tools/Vixen.Templates` "is reached with `dotnet new` and
  produces a solution" — was wrong twice over: `TemplateCatalog` reads the same tree of files out of
  an assembly with no `dotnet new` anywhere near it, and the `game` template is a project rather than
  a solution. What was true is that the reader lived in `Tools/Vixen.Cli`, which no editor
  references. So New Project made two directories, every project born in the editor had no `.csproj`,
  and [E6](#e6--production-hardening-15-em)'s Build and Run was greyed for all of them with a message
  naming a terminal command. `ProjectScaffold` in `Vixen.Editor.Core` is `ProjectWorkspace`'s move
  made a second time, for the third consumer of the same argument.

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

Two windows, one mechanism, and the mechanism already exists twice. **All three bullets are built**,
and `SettingsView` is the mechanism: a rail, a pane, a search over every setting on every page, a
Reset per page and an Apply. The only difference between the two windows is whose store the pages are
over.

- ✅ **Project Settings** edits `[DataContract]` types under `ProjectSettings/` through
  `ProjectSettingsStore` and draws them with `InspectorView`. Doc 11's claim that "adding a project
  setting is declaring a type, not also writing a dialog" is now true end to end: `ProjectInfo` and
  `ContentBuild` are two contract types with `[Inspector]` on their members and no dialog code at all.
  ⚠ **Both have a reader, which is the bar a shipped setting has to clear** — the product name is what
  the title bar says and the content target is what the import and the build run for. A settings page
  of fields nothing reads teaches people that the settings do not work.
- ✅ **Preferences** is the same window over the *user's* store rather than the project's: General,
  Appearance, Scene View, Keybindings and Plugins. ⚠ **The three navigation preferences stay as
  commands, and so does the theme.** They are palette-searchable and rebindable there, and the
  window draws the *same* commands as toggles rather than a second copy of the state — two writers to
  one setting is how a preferences window and a menu tick disagree. The last two pages are a sentence
  and a button that opens the panel, for the same reason: a keybinding table of two hundred rows is
  not a page in a dialog.
- ✅ **A setting is not saved on every keystroke.** The layout file's rule (`written on the way down`)
  applies here for the same reason, with an explicit Apply — which the two settings that cost
  something to change are exactly why: lowering the undo depth drops history immediately, and
  changing the content target invalidates an import.

⚠ **Colours turned out to be a text area over `theme.yaml` rather than a colour-ramp editor.**
`ThemeService.LoadTokens` already reads that file and `tools.reload-styles` already re-reads it — the
only thing missing was a way to reach it without knowing where it is. A second representation of the
ramp would be a second thing to keep in step with the sheet.

### A5 — The keybinding editor

`KeyMap` has conflict detection, per-command overrides, defaults-vs-overrides separation, and reset.
There is no UI, which doc 11 already flags. **The panel is built**: a `DataGrid` of command /
category / binding / source, a filter box, a "press a key" capture, conflict reporting inline,
per-row and global reset, and import/export of a keymap file.

⚠ **Presets matter more than they look.** A Unity user and an Unreal user disagree about what `W`
does and both are certain — they happen to agree about `W`, and disagree about Play, Duplicate and
Save All. `Vixen`, `Unity` and `Unreal` ship, and choosing one is a dropdown.

⚠ **`KeyMap` has no notion of a preset**, and "the override layer already reads it" is about the
*file format* rather than about the mechanism. A preset is a third layer between the shipped defaults
and the user's own overrides, because choosing Unreal and then rebinding one key has to leave the
other two hundred following the preset rather than being copied into the user's file — otherwise the
next preset update reaches nobody who has ever rebound anything. That layer is the work; the dropdown
is not. ✅ **It is built as a layer**, and two things fell out of building it that way:

- **The composition takes a chord off whatever held it, so a preset can be twenty lines.** Unity puts
  Play on `Ctrl+P`, which is this editor's palette; the preset says where the palette goes and says
  nothing at all about the command it displaced, because the composition works that out. The rule is
  most-specific-layer-first, and within a layer in command-id order so the answer is the same on
  every machine.
- **`Vixen`'s preset is empty, and that is not a stub.** The shipped defaults *are* the Vixen keymap
  — they are declared beside the commands, where a default belongs — so a preset restating them would
  be a second copy of the same table. Choosing `Vixen` means "no layer".

⚠ **Capture is a mode rather than a modal, and it is what makes the panel testable.** A dialog that
swallows keystrokes to record them cannot be driven by the automation harness, which is
[A2](#a2--windows-dialogs-and-a-dialog-service)'s argument turned round. The consequence worth stating
is that Escape is the one chord this panel will not bind, because Escape is how capture ends.

### A6 — Layouts, completed

- ✅ **Open documents belong to an arrangement.** `current.vxlayout` records the panels; an asset
  editor opened by double-click is registered on demand and named by GUID, so it is *nameable* — the
  layout just does not carry the list. ⚠ **The fix turned out not to be a list in the file.** The
  arrangement already held `asset.<guid>`; what was missing was anything able to build one on the way
  back, so the id was written and the tab came back absent. `DockingWorkspace.Resolve` is a hook the
  workspace asks before giving up, and the application answers it by opening the document — the same
  path a double-click takes. A workspace that knew what a GUID meant would be a workspace that knows
  what an asset is.
- ✅ **A layout per mode is a menu, not five presets.** Window ▸ Layouts ▸ with the presets, the
  user's saved ones (also a palette source), Save Layout As…, and Reset. ⚠ The palette source
  stays — an unbounded list belongs there — but a menu with the five presets on it is what a new user
  finds.
- **Two more presets to ship**: `Profiling` ✅ — B4 exists, so it does — and `Sequencing`, once B5
  does. ⚠ The Profiling one is deliberately not the Default's shape: profiling is a *reading* rather
  than an edit, so the viewport is the narrow column and the numbers get the width. A flame chart
  squeezed into a right-hand inspector slot is one where every bar is a pixel.

### A7 — Notifications, messages, and the Console

- ✅ **A Message Log panel** over `NotificationCenter`'s history, which is kept and bounded and has no
  view. Errors already do not expire; what is missing is the place they accumulate. ⚠ **It is not the
  Console, and the difference is who wrote the line.** The console is the whole of the diagnostics
  ring — every category, every level, the game's lines and the engine's. This is what the *editor*
  decided was worth interrupting somebody about, which is two orders of magnitude shorter and the one
  you scan after something went wrong. The mirror means every entry here is in the console too; the
  reverse is emphatically not true.
- **The Console is a real panel** (this is the largest single item in Part A): a virtualised list over
  `Vixen.Core.Diagnostics`' ring buffer, with level toggles (error/warn/info/debug counts as badges),
  a category filter, a search box, collapse-duplicates, clear, clear-on-play, a detail pane showing
  the full record and stack, and double-click-to-open-source through the external-tool setting — which
  is `EditorPreferences.ExternalEditor` now that there is a window to hold it. ⚠ **What is still
  honest about that is the limit**: a stack frame carries a file and a line only in a build with
  symbols beside it, so what the editor can hand the tool is the project root.
  ⚠ **It must not allocate per line.** A game logging per frame into a panel that keeps strings is a
  leak with a UI; the ring buffer is fixed-size and the panel virtualises over it.

### A8 — Search everywhere

`Ctrl+P` is a command palette over `IPaletteSource`. `Ctrl+Shift+F` is **the same machinery over
*content***: assets by name and path, entities by name, and the commands themselves. The sources do
their own matching (already the contract), so the asset source can index rather than scan — today it
scans the database's own dictionary, which is a few thousand cheap comparisons per keystroke and
measurably nothing beside the layout pass that follows. Results are grouped by source with a preview
pane.

⚠ **A second palette rather than a mode on the first, and the two want opposite answers to three
questions.** Grouping: a palette is entirely commands, so the useful distinction is File / Edit /
Scene; a search across four kinds of thing wants the *kind* first. The empty query: a palette opened
and not yet typed into should offer what the editor can do, and a search-everywhere offering twenty
commands would push out the first asset that matches. And Return: one runs a verb, the other reveals
a thing, and a mode would be an overlay whose Return means two things.

⚠ **Find References is the same query and belongs in three places at once** — the browser's context
menu, the inspector's asset field, and here. `ReferenceIndex` answers it already. **Two of the three
are built and they are one command rather than two**, which is what stops them disagreeing; the
answer is a *selection* in the browser rather than a read-only list, because what somebody does with
it is open one, delete the lot, or look at what they have in common, and the browser already does all
three to a selection. ⚠ **The inspector's asset field is not built**: it is a change to `AssetDrawer`
rather than to this, and claiming it would be claiming a menu nobody can reach.

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
| **Message log** | Message Log | `.Ui` | ✅ | — |
| **Command palette** | — (both have search) | `.Ui` | ✅ | Recency boosting. Search-everywhere is a second palette over content ([A8](#a8--search-everywhere)) |
| **Preferences / Project Settings** | Preferences / Project Settings | `.Ui` + `.App` | ✅ | More settings types, as the runtime grows things worth setting |
| **Keyboard shortcuts** | Keyboard Shortcuts / Shortcuts | `.Ui` | ✅ | — |
| **Undo history** | Undo History / — | `.App` | ✅ | — |

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

Both projects exist, and this table is the record of what each was waiting on. Doc 13 specifies the
runtime half; `Vixen.Core.Diagnostics` has the sample rings and the Chrome-trace export.

| Panel | UE / Unity | Status | What it took |
|---|---|---|---|
| **CPU profiler** — flame chart over job-system samples, per-frame timeline, capture/compare | Insights / Profiler | ✅ | Rebuilding the nesting the rings do not keep. Samples arrive in *completion* order, so the obvious reading builds every tree upside down |
| **GPU profiler** — timeline from timestamp queries, pass breakdown | GPU Visualizer / Profiler | ✅ | The RHI change below, then one pool per frame in flight and a resolve that never waits |
| **Frame debugger** — step draw calls, inspect bound state and render targets | RenderDoc-adjacent / Frame Debugger | 🟡 | State ✅, replayed by walking the stream's prefix. ⚠ **Not the intermediate render target**: `Vixen.Graphics.Null` is the only recording path and it has the state, not the pixels. A Vulkan capture hook is a second adapter, not a change to the panel |
| **Memory** — managed heap, native allocators, GPU heaps, asset residency | Memory Insights / Memory Profiler | 🟡 | Managed ✅, native ✅ through `LeakTracker` — which compiles out of release, and the panel says so rather than reading zero. GPU heaps need `VK_EXT_memory_budget`, which the backend does not query; assets are counts, because the database holds identities and not sizes |
| **Remote inspector** — attach to a running build, browse and mutate live entities | Device output / Profiler remote | 🟡 | The editor's half ✅ over any `ITransport`. ⚠ **The runtime half is doc 13's and is not written**, and neither is discovery — a `FakeBuild` in the tests is the shape a player implements |
| **Statistics** — counts, budgets, warnings per scene | Statistics / Stats | ✅ | Traversal only, as this row said. No draw calls: the viewport draws lines, and a count there would be a guess presented as a measurement |
| **Device manager** | Device Manager / Build & Run | 🟡 | The list, the statuses, the hand-off to the inspector and — since [E6](#e6--production-hardening-15-em)'s build settings — Deploy ✅. Finding an Android device is `adb` and a console is a vendor SDK — one `IDeviceProvider` each, and the same tool is what would install to it, which is why deploying to anything but this machine is greyed with the tool's name |

⚠ **The profiler must be able to profile the editor.** An editor that can only profile the game
cannot answer why the editor is slow, and doc 00's editor-shell performance bar is a claim about the
editor. The same panel over the same rings, with a source selector — which is what `IProfileSource`
is, and `EditorHost` instruments its loop with the four phases its own remarks name so that the
"Editor" source has something to show.

### B5 — Authoring

| Panel | UE / Unity | Owner | Status | Notes |
|---|---|---|---|---|
| Shader graph | Material Editor / Shader Graph | `.ShaderGraph` + `.AssetEditors` | ✅ | `.vxshadergraph`: document, panel, factory, registration and a Create ▸ line — the half that had been missing while the row said ✅, exactly as the VFX row's did. ⚠ **Compiling runs both compilers**: the graph's, whose diagnostics name a node and a port, and Raven's front end over the emitted text, whose name a line — a well-formed graph can emit a shader that does not type-check, and a panel listing only the first would call that success. Doc 07's "show generated code" is a read-only `CodeEditor` beside the canvas. Owes procedural nodes, custom-code node, post/UI masters, node previews, and a material that draws with one |
| VFX graph | Niagara / VFX Graph | `.VfxGraph` + `.AssetEditors` | ✅ | Document, factory, registration and a preview that is the *real* simulation — `VfxSystem` over the compiled graph — projected by the panel, because particles need a material. ⚠ The node library and the compiler stay in `.VfxGraph`, which knows nothing about a project or a panel; the document and the view are where every other row of doc 11's table already is |
| **Animation graph** | Animation Blueprint / Animator | `.AnimationGraph` + `.AssetEditors` | 🟡 | Layers, states, motions and blend trees, transitions with conditions, parameters, masks. ⚠ **Not on the node-graph framework** — see [E5](#e5--authoring-surfaces-25-em). IK is the runtime's and has no authored surface yet |
| **Animation clip editor** | Sequencer curves / Animation window | `.AssetEditors` | ✅ | `.vxanim` — ten scalar curves per target rather than three vector tracks, because a curve editor edits one number and a vector track cannot say "X has a key here and Y does not". Dope sheet, curve mode, event track |
| **Sequencer / cinematics** | Sequencer / Timeline | `.AssetEditors` | 🟡 | `.vxseq` over entities, cameras, audio and events, scrubbed against the open scene and restored on the way out. ⚠ A camera track *cuts* and reports; making the viewport look through it is Phase 7's compositor wiring |
| **Audio mixer** | Audio Mixer (both) | `.AssetEditors` | ✅ | A strip per bus with its sends, inserts and snapshots, validated by running the real `MixerBuilder`. ⚠ The format was already `Vixen.Audio`'s |
| **Input actions** | Input / Input System | `.AssetEditors` | ✅ | Maps, actions, composite bindings, control schemes, and rebinding as a *mode* rather than a modal — `KeyBindingsView`'s argument, restated |
| **Font editor** | — / Font asset | `.AssetEditors` | ✅ | `.vxfont`: coverage per Unicode block against *assigned* code points, a glyph page drawn from the face's own outlines, and a fallback chain whose colour says which face drew each cell |
| **Curve / gradient presets** | ✅ both | `.Inspector` | 🟡 | Controls exist; a library of saved presets does not |

### B6 — World building

| Panel | UE / Unity | Status | Notes |
|---|---|---|---|
| **World / scene settings** | World Settings / Lighting+Physics settings | ✅ | Environment, ambient, fog, physics and navigation as `[DataContract]` types with `[Inspector]` members and no dialog code. ⚠ **A sidecar beside the `.vxscene`, not a block inside it** — a scene file is the one two people touch every day |
| **Layers and tags** | Layers / Tags & Layers | ⛔ | Needs an ECS-side concept first. On the Scene menu, disabled with that reason |
| **Lighting / GI** | Lighting / Lighting window | 🟡 | The dynamic budgets doc 19 names — distance-field range and voxel size, probe spacing and per-frame budget, surface-cache cards, bounces — with a derived cost readout that says **(derived)**. ⚠ The four debug views are named as absent: they need the GI path, which is Phase 7's |
| **Navigation** | Navigation / Navigation window | 🟡 | Agent profile, cell sizes, and a bake through the real `NavMeshBaker`. ⚠ Over the *boxes* the scene's primitives occupy, because the viewport draws primitives; it becomes true geometry with the renderer |
| **Physics debug** | — | ⛔ | Collider draw, contact visualisation, layer matrix |
| **Terrain / foliage** | Landscape + Foliage / Terrain | ⛔ | Post-1.0, [Part G](#part-g--out-of-scope) |
| **Multi-scene** | Levels / multi-scene editing | ✅ | Additive loading into **one world** — which is what `SceneManager` already does and what keeps an entity handle meaning one thing — a Scenes panel with per-scene visibility and lock, an active scene new entities go into, and Save All Scenes |

### B7 — Build, deploy, and extend

| Window | UE / Unity | Status | Notes |
|---|---|---|---|
| **Build settings** | Project Launcher / Build Settings | ✅ | A panel over `PlayerBuildSettings`, running `ContentTasks.BuildPlayer` — import, pack, `dotnet publish`, launch. ⚠ **`PublishRunner` moved out of the CLI to make "over `Tools/Vixen.Cli`'s existing calls" literally true**: it is `PlayerBuild` in `Vixen.Editor.Assets`, beside `ContentPipeline`, for the reason `ProjectWorkspace` is there. What is *not* shared is the shader bundle — `ShaderBuildRunner` links Raven's compiler, which the editor deliberately does not carry, and the build log says so |
| **Device manager / deploy** | Device Manager / Build & Run | 🟡 | List ✅, deploy and launch ✅ for this machine, attach ⛔. ⚠ **The fourth verb is not a gap in this row**: attaching needs something on the other side to answer, which is doc 13's runtime half — the same absence [B4](#b4--diagnostics)'s remote-inspector row names. Every other kind of device is greyed with the tool that is missing, because the tool that would *find* an Android phone is the tool that would install to it |
| **Plugin manager** | Plugins / Package Manager | ✅ | A list over `PluginHost.Plugins` with enable, disable, reload. ⚠ The two switches are kept apart: `plugin.yaml`'s `enabled:` is the author's and is shared by a team, and the user's is recorded beside their layout |
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

⚠ **Two lines came out with a different arity than this table says, and both for the same reason: the
menu is a view of a setting rather than a list of verbs.** Target has a seventh entry above the six —
*This Machine* — because an unset target means "whatever machine this is", which is not one of the six
and a submenu with no tick at all reads as a setting that failed to load. Configuration has four
rather than two, because doc 17's variants are what a player build actually chooses between and the
compiler configuration is derived from one: Debug, Development, Release, Server. Web is on the Target
list and greyed with its reason, which is the rule this document opened with rather than an exception
to it.

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
| **Drag from the browser into the scene** | Nothing in the runtime carried an `AssetId`, so an entity had nowhere to hold "this is the crate". `AssetInstance` is that component. ⚠ **It is a reference, not a renderer** — nothing draws an asset yet, and `Light`/`PrimitiveShape`/`MeshRenderable` have since become `Vixen.Rendering`'s own components while this stayed editor-side, since "this entity stands for this asset" is still the honest reading of a drop of a texture or a clip — but the reference is authored, saved, editable through the inspector's existing asset field, and written in `vx:` form so `ReferenceIndex` counts it and deleting the asset warns about the scene |
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
| **The picking stage driven by a real target** | ⚠ **The reason stated in [B2](#b2--the-viewport) was wrong in a way worth recording.** It is true that `EditorHost` owns a `RenderGraph` and that `ScenePresenter.Declare` hands back a `GraphTexture` — and neither is what `PickingRenderer` consumes. It is a `SceneRenderer` over a `RenderStage`, so it needs a `GraphicsCompositor` and a render system feeding it, which the editor's viewport does not have: what draws the scene is `SceneMeshes` through `MeshInstanceRenderer`, which has device-resident geometry and a per-entity transform — [blockout-tools § B1](../blockout-tools.md#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-) — and no compositor and no materials. Clicking and banding both work, exactly, through the processor — and that is the right answer for primitives and the wrong one the day a shader moves a vertex |
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

**Where E3 stands.** All three exit sentences run as tests in `Vixen.Editor.App.Tests`, and every
line of Part C that named E3 as its milestone now names nothing. Five things came out differently
from how this document described them, and each is worth the sentence:

| What the plan said | What it turned out to be |
|---|---|
| **New and Open Project need "a project swapped underneath a live editor"** — a world, a scene, an asset database and every open document | Nothing is swapped. The request closes this editor and hands `Program` the next root, which builds another host over the same window — so the new editor is assembled by exactly the code that assembles it at launch, and by the code every restart in the test harness already proves. Half a dozen fields reassigned in place would have been half a dozen chances to leave a panel pointing at a dead world |
| **The layout does not carry its open documents** | It always did. The arrangement held `asset.<guid>`; what was missing was anything able to *build* one on the way back, because an asset editor's panel is registered on demand. One hook on the workspace, answered by the application |
| **Two windows** for Preferences and Project Settings | Two *panels*, which dock, tab, and become real OS windows through `view.float-panel`. [A2](#a2--windows-dialogs-and-a-dialog-service)'s own rule — everything modal is drawn, so the golden suite can photograph it — points the same way, and a settings window is the one thing people leave open beside what they are changing |
| **Colours** as a settings category | A text area over `theme.yaml`, which `ThemeService` already reads. A colour-ramp editor would be a second representation of the palette to keep in step with the sheet |
| **A preset is a YAML file, "which is the format the override layer already reads"** | True of the format and beside the point. The work was the third layer, and the part that made presets small was letting the *composition* take a chord off whatever held it — so Unity's preset says where the palette went and nothing about the twenty commands it did not displace |

⚠ **Two defects fell out of writing the tests rather than out of writing the features**, and both are
the kind Part F exists to catch. Closing the Scene tab left the application holding a viewport whose
control had been removed, and the next frame asked a removed element for its width — the
panel-lifecycle row, found the first time every registered panel was closed and reopened in one test.
And `RangeBase` threw from a property setter for any `[Range]` whose minimum exceeded one, because the
bounds are two properties and are necessarily set one at a time: the first `[Range]` int in the
codebase was the undo depth, and it took the preferences window down with it.

⚠ **The first of those two was fixed at the wrong depth and then at the right one, which is worth
recording.** Nulling the viewport when its control turns out to be removed fixes the panel that
crashed and leaves the mechanism missing: `PanelDescriptor` had a `Build` and no teardown, so *every*
panel whose owner keeps a reference to what the factory made has the same hole — the undo history had
it too, and was polled for the rest of the session after being closed once. `PanelDescriptor.Closed`
is the missing half, and the workspace fires it by comparing what the host is showing against what it
was showing, because the commonest way a panel closes — the tab's own button — is not a call into the
workspace at all.

Three things this milestone is *not*, said plainly: **About is still a notification** rather than the
window Part C's Help menu implies; **Find References does not reach the inspector's asset field**,
which is a change to `AssetDrawer` rather than to any of this; and **New Project made two
directories** rather than instantiating one of `Tools/Vixen.Templates` — ✅ **closed by E6**, which is
the milestone where the cost of it turned up: a project with no `.csproj` is one Build and Run cannot
publish, and until E6 nothing in the editor had ever asked for one.

### E4 — Diagnostics (2.0 EM)

`Vixen.Editor.Profiler` and `Vixen.Editor.Debugger` as projects. CPU flame chart, GPU timeline,
frame debugger, memory view, statistics, remote inspector client, device manager. Profiling layout
preset.

**Exit:** a frame of the editor and a frame of a running game are both profilable in the same panel;
a draw call can be stepped and its render target inspected; a build on a device can be attached to
and an entity mutated live.

**Where E4 stands.** Both projects exist, the seven panels are registered, the five Tools verbs and
Deploy are verbs rather than declared-and-disabled lines, and the Profiling preset [A6](#a6--layouts-completed)
owed is registered. Two of the three exit clauses hold: the editor's own frame profiles in the same
panel a game would, and an entity is mutated live over the protocol — `RemoteInspectorTests` is that
sentence as a test.

Five things are not built, and each is a gap in something *below* the panel rather than in the panel
— which is the same shape [E1](#e1--the-three-panels-people-live-in-20-em)'s table has, and is worth
recording for the same reason: every one of them is reachable by adding a piece to a layer that
already exists, not by rewriting a view.

| Not built | What it actually needs |
|---|---|
| **Render-target inspection**, which is half of the third exit clause | A capture that was *executed*. `Vixen.Graphics.Null` is the engine's only recording path and it holds the state, not the pixels — doc 13 wants stepping to draw N to present what the frame had drawn by then, and that is a Vulkan command-stream hook plus a replay. `FrameCapture` takes `CapturedCommand` rather than the Null backend's enum precisely so that hook lands as a second adapter beside `NullFrameCapture`. Until then the panel says so rather than showing a black image somebody would read as an empty target |
| **The remote inspector's runtime half** | Doc 13 owns it and it is not written: the editor's end greets, browses, writes and takes counters, and nothing on the other side answers except `Vixen.Editor.Debugger.Tests`' `FakeBuild` — which is deliberately written against `InspectorProtocol`'s readers and writers only, so it is the shape a player implements rather than a mock |
| **Device discovery** | An Android device is `adb`, a console is a vendor SDK, and a machine on the LAN is whatever discovery `Vixen.Net` grows. One `IDeviceProvider` each. The window, the statuses, the selection and the hand-off to the inspector are built; what it lists today is the local machine, and it says so |
| **GPU heaps in the memory view** | `VK_EXT_memory_budget`, which the Vulkan backend does not query. The arena is *absent* rather than shown as zero — the difference between "not measured" and "nothing allocated" is the whole value of the panel. Native allocations have the same shape from the other side: `LeakTracker` compiles out of release, and the rows say that instead of reading zero |
| **A GPU timeline on OpenGL and WebGPU** | `GL_TIMESTAMP` is desktop-only and this backend targets GLES and WebGL2; WebGPU's `timestamp-query` is an optional device feature browsers gate, so asking for it unconditionally would fail device creation on most of the targets. Both report `HasTimestampQueries` false with the reason, and the panel shows the reason |

Two more that E4 touches and does not close, both named where they belong: **`tools.diagnostics-report`
is written and is missing its second half** — it carries the log ring, the memory arenas, the scene's
counts and the last capture, and says *in the file* that the minidump and the undo history are
[E6](#e6--production-hardening-15-em)'s — and **Deploy opened the device list rather than deploying**,
because deploying needs a build. ✅ **That one is closed**: E6's `build.settings` exists, the panel
raises `DeployRequested`, and what E4 contributed to it — the list, the statuses, the selection — is
exactly what the deploy is chosen from. `DeviceStatus.Deploying` was an enum member no code could
produce until there was something to produce it.

⚠ **The RHI blocker this milestone opened with is closed, and it was the shape it said it was.**
There was no query API in `Vixen.Graphics` — not an interface, not a stub, nothing in the Vulkan
backend. There is now: `QueryPoolHandle`, `IGraphicsDevice.CreateQueryPool`/`TryResolveQueries`,
`ICommandList.ResetQueries`/`WriteTimestamp`, and `HasTimestampQueries` with a `TimestampPeriod`
beside it. Vulkan implements it against the *graphics family's* validity bits rather than the
device's, because a transfer queue that cannot be timed is an ordinary configuration; the Null
backend records the writes and resolves synthetic readings; OpenGL and WebGPU report the capability
absent with the reason, which the GPU panel shows instead of an empty chart.

Three things worth recording, because each was a decision rather than a translation:

- **`TryResolveQueries` never waits.** Asking the driver to would stall the frame thread on the GPU
  once per frame — a profiler that halves the frame rate it is reporting. So the recorder holds one
  pool per frame in flight *plus one*, writes into the newest and asks about the oldest, and takes
  `false` for an answer until the submission has retired.
- **A GPU timestamp's zero point means nothing.** It is comparable with another reading from the
  same device and with nothing on the CPU; lining the two up needs `VK_EXT_calibrated_timestamps`,
  which many drivers lack. So doc 13's frame breakdown is a GPU timeline *beside* a CPU one, and the
  bars are drawn relative to the frame's own first reading.
- **The capture's vocabulary is not the Null backend's.** `FrameCapture` takes `CapturedCommand`,
  and `NullFrameCapture` is the one file that knows a backend exists — so the Vulkan hook doc 13
  eventually wants arrives as a second adapter rather than as a rewrite of the panel.

### E5 — Authoring surfaces (2.5 EM)

VFX graph reachable (document, factory, registration) and previewed. Animation clip format and
editor. Animation graph. Sequencer. Input actions editor. Audio mixer. Font editor. World settings.
Lighting/GI panel. Navigation panel. Multi-scene.

**Exit:** doc 11's thirteen-row asset-editor table has thirteen rows built; a cinematic can be
authored, scrubbed and played.

**Where E5 stands.** Both exit clauses run as tests in `Vixen.Editor.App.Tests/MilestoneE5Tests` —
the first named row by row rather than counted, because a count passes when somebody deletes the font
editor and adds two of something else. Eleven surfaces, seven new asset kinds, four panels, and one
thing that had to be built before any of it was reachable: a way to *make* one of these files. Six
things came out differently from how this document described them, and each is worth the sentence.

| What the plan said | What it turned out to be |
|---|---|
| **The animation graph is the third graph on `Vixen.Editor.NodeGraph`** — doc 11's tree puts it under the framework beside `.ShaderGraph` and `.VfxGraph` | It is its own model, and the reason is not a detail. A shader graph's edge carries a *value*, a VFX graph's carries *order*, and a state machine's carries *"may become"*: there is nothing on it, several leave one state and several arrive at another, and a graph without a cycle is a character that can never return to idle. Every rule that framework exists for — one edge per input, ports typed by what flows, a topological order — would have to be switched off to hold one. What is shared is the *shape of the editor*, which is where sharing belongs |
| **"The VFX graph wants a document and a factory"** | And a preview, which is the half that had a decision in it. The simulation is real — `VfxSystem` over the `VfxCompiledGraph` the document just compiled, the same class a game runs — and the *picture* is a projection this control draws, because particles are drawn by a material and the editor's viewport is a tool renderer. Borrowing the tool renderer would have been a second thing to rewrite when Phase 7 lands and would still not show a textured sprite |
| **Three formats were owed: animation clip, input actions, font** | Two. `MixerAsset` already existed — `Vixen.Audio`'s own authoring layer, whose remarks say why: "a sound designer who has to open a C# file to move a fader does not move the fader" — so the mixer editor is a panel over a format, not a format. ⚠ What it *did* need was one line of MSBuild: the assembly ran the binary-serializer generator and not the reflection one, so every one of those records was describable in principle and unreadable as YAML in practice |
| **World settings are "an inspector over a `[DataContract]` on the scene"** | Over a `[DataContract]` in a **sidecar beside** the scene. A `.vxscene` is where every entity anybody adds lands, so it is the file two people on a team touch every day; the fog colour changes once a month. Keeping them apart means changing the sky does not conflict with somebody else having moved a crate — doc 08's argument for `.meta` files, restated |
| **Multi-scene needs "a scene as a unit of ownership"** | It needs a second `SceneDocument` over the *same* world, which is what `SceneManager` already does additively. A second world would have meant the outliner, the gizmo and the picker each learning which world an entity came from; one world is what keeps an entity handle meaning one thing across the editor. What did change is three fields: `scene`, `picker` and `probe` stopped being `readonly` |
| **Nothing about creating one of these files** | The gap that would have made all six editors unreachable. A format with no way to make a file of it is a format nobody meets, whatever the double-click does — so Assets ▸ Create grew seven lines, each an ordinary command with a palette entry and a bindable key, and each writes a zero-byte file. That is not a shortcut: every one of these documents already opens an empty file as a sensible new one, so a templates folder would be a second place the defaults live |

Two defects fell out of writing the tests rather than out of writing the features, which is the shape
[E3](#e3--settings-keys-layouts-plugins-10-em) reported and the reason Part F's rows are worth having:

- ⚠ **A record struct's `default` runs no constructor and no property initializer.** `InputRow` is the
  tag on an action-tree row, and `TreeNode.Tag` is null when nothing is selected — so its `string`
  members were genuinely null however the declaration was written, and the panel threw on the first
  frame after somebody clicked the empty space under the last row. Coalescing in an accessor *looks*
  like it fixes this and does not; the readers use `is { Length: > 0 }`.
- ⚠ **`ViewportLayout.Discard` removed elements the host had already removed.** `UiElement.Remove`
  throws on a second removal by contract, and a panel closes by the host tearing its contents out —
  so the teardown hook fires *after* the children are gone. The panel-lifecycle test found it the
  moment four more panels were registered, which is the second time that row has earned its place.

What is not built, each named against the layer it is missing from rather than as a checkbox:

| Not built | What it actually needs |
|---|---|
| **A VFX emitter component** | `entity.create-vfx` is still declared-and-disabled, and the reason has moved rather than gone: the graph is authorable now, and the runtime has no component for an entity to carry it with. An entity called VFX would reference nothing |
| **The lighting panel's four debug views** | Distance-field coverage, probe placement, surface-cache residency and indirect-only are named as absent with the reason, because doc 19's GI path is doc 14's Phase 7. The budgets beside them are arithmetic over the settings and say **(derived)** in the row: a number presented as measured when it was computed is the failure this panel could most easily have had |
| **A navigation bake over real geometry** | It bakes, through the real `NavMeshBaker`, over the *boxes* the scene's primitives occupy — which is a real navigation mesh over a real blockout, and is what a level designer bakes at this stage anyway. It becomes true geometry the day the renderer has meshes, with nothing in the panel changing |
| **Render-target inspection for a sequence's camera track** | The track cuts and the player reports which camera; making the viewport look through it is the same `GraphicsCompositor` wiring [E2](#e2--the-viewport-20-em)'s two remaining rows are waiting on |
| **Layers and tags** | Named on the Scene menu and disabled with the reason. Doc 20's own row says they need an ECS-side concept first, and a panel maintaining a list of names nothing reads would fail this document's second bar |
| **Curve and gradient preset libraries** | B5's last 🟡 row. The controls exist and a library of saved presets is a user-store file rather than an editor surface — it belongs beside the layouts and the keymap, not in an asset editor |

### E6 — Production hardening (1.5 EM)

Crash reporter, session recovery with the kill-and-restore loop, source-control provider and git
implementation, build settings and deploy, `PublishEditor` with signing and notarisation, the editor
UI automation harness and the golden-screenshot suite over every layout preset, the editor-shell
performance benchmark from doc 00 actually run.

**Exit:** Phase 6's stated exit criteria, plus: the editor survives being killed mid-edit with no
lost work; a signed installer exists for three desktops; the performance bar is a number in CI.

**Where E6 stands.** One row of it is built — **build settings and deploy** — and it is taken first
because it is the one the other milestones were waiting on rather than the other way round: E4's
Deploy line and Part C's `build.settings`, `build.run`, `Target ▸` and `Configuration ▸` were all
declared-and-disabled naming this milestone. Three things came out differently from how this document
described them, and each is worth the sentence:

| What the plan said | What it turned out to be |
|---|---|
| **"Over `Tools/Vixen.Cli`'s existing calls"** | Not possible as written, and the fix is the one `ProjectWorkspace` already made. `PublishRunner` was *in* the CLI, which is a tool no editor references — so a window "over" it would have been a second copy of the same `dotnet publish`. It is now `PlayerBuild`, beside `ContentPipeline` in `Vixen.Editor.Assets`, and `vixen build` and Build and Run are literally the same three calls in the same order |
| **Target and configuration are two settings** | Two *fields*, and neither is the compiler's configuration. Doc 17's variants are the axis a player build has — Development is optimised and keeps its profiler — so the variant travels as `VixenVariant` and `-c Release` is derived from it. Part C's `Configuration ▸ (Debug, Release)` is therefore four lines rather than two: a menu of the two everybody knows, over a setting of four, would leave the other two unreachable and unmarkable |
| **Scenes-in-build is a list of scenes** | And the honest half is what reads it. A build checks every entry still resolves and says which do not — somebody else's rename arriving in a checkout is the failure this list actually has. What does *not* read it is anything at boot: doc 17's `AppConfig.StartupScene` is what will make the first entry mean something, and the panel says so rather than implying the order does |
| **New Project is [E3](#e3--settings-keys-layouts-plugins-10-em)'s and is finished** | It made two directories, which was fine until something asked the project to build. Every project the editor had ever created had no `.csproj`, so this milestone's own Build and Run was greyed for all of them — an editor that cannot finish the project it just made, failing this document's second bar on the first screen a new user sees. `TemplateCatalog` moved to `Vixen.Editor.Core` beside a `ProjectScaffold` that both heads write through, and New Project instantiates `game` |

⚠ **The player's target and the content target are deliberately two settings, and this was not
obvious.** `ContentBuildSettings.Target` is what the editor's own panels are imported for and
`PlayerBuildSettings.Target` is what ships. One field would be simpler and would make "build the
Android player" also mean "reimport this whole project as ASTC" — which is exactly what a team
building for a phone from a workstation must not have happen.

⚠ **The console is the build log, which is not a detail.** An editor has no terminal, so a publish
whose output went to `Console.Out` would be a build that failed with its reason written to a handle
nobody has. `dotnet publish` is captured line by line into the diagnostics ring, and MSBuild's own
severity is read back out of the line — a compiler error logged as information is one the console's
default filter hides, which would be the panel concealing the thing it was opened for.

⚠ **A player build cannot compile the shader bundle, and this is a real difference from
`vixen build`.** `ShaderBuildRunner` links Raven's compiler, a build-time library the editor
deliberately does not carry — see `Tools/Vixen.ShaderCompiler`'s README for why. A project with a
`Shaders.effects.json` is told so in the build log, and `build.rebuild-shaders` is the one Build-menu
line still declared-and-disabled, now naming that reason rather than this milestone. What closes it is
a compiler *service* the editor talks to rather than links, which `Tools/Vixen.ShaderCompilerService`
is already the shape of.

Still owed here: the crash reporter, session recovery, source control, `PublishEditor` with signing
and notarisation, the golden-screenshot suite, and doc 00's editor-shell benchmark actually run.

### Ordering note

E0 → E1 → E2 is strictly sequential: E1's context menus are E0's, and E2's overlay toolbar is E0's
toolbar work. **E3 and E4 are independent of each other and of E2**, so with more than one engineer
they run in parallel from the end of E1 — and all three were in fact taken in parallel, which is the
evidence for the claim rather than a restatement of it. The three touched one file in common,
`EditorApplication`, and every collision in it was additive. E5 depends on E1 (thumbnails, browser)
and partly on E2 (previews). E6 depends on everything and should start its automation harness during
E1, not after E5 — a harness written last is a harness written against a frozen target.

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
| **Menu and command coverage** ✅ | A test that walks `MenuModel` and asserts every `MenuCommand` names a registered command. The five dangling ids found while writing this document are exactly what it catches, and it is about fifteen lines |
| **Keymap presets** ✅ | Each preset file is asserted to bind only registered commands and to raise no conflict. A preset that silently drops a binding is worse than no preset. ⚠ The conflict half is asserted against the *whole real registry* rather than against the preset alone: a chord free among a preset's own twenty entries can still be held by one of the editor's two hundred |
| **Panel lifecycle** 🟡 | Every registered panel is built, docked, floated, closed and rebuilt in one test. `A panel's factory runs again when it is reopened` is already a documented hazard and nothing currently proves a given panel survives it. Closed-and-reopened is covered and found a real defect on the first run; **floated is not**, because tearing a panel out needs a window host and the harness has none |
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
