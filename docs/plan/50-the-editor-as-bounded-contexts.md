<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 50 — The editor as bounded contexts

The editor has a plugin contract, a typed registry, eight Unity-shaped declaration attributes, an
architecture rule that forbids the application from referencing a feature, and a 14,823-line
`sealed partial class EditorApplication` spread over thirteen files, with 102 instance fields, 336
methods, a 489-line constructor and every verb the editor has as a private method closed over
those fields. Both
statements are true, and the second is why the first is not yet an architecture.

> **Status.** Written 2026-09-17. § P0's instruments landed 2026-10-19 (see § P0 for what
> re-measuring them changed); the moves are in progress. Every number in Part 1 was measured on
> master that day with the command beside it; a revision that restates a figure without re-running
> the command is writing down a memory (doc 36 § P3 learned this six times).

**The claim this document has to earn.** Every use case the editor has — open a project, group a
selection, import a folder, enter play, build a player, save a layout — is a public method on an
application service that a plugin can `Require`, a test can call without a window, and a menu item is
only a *name* for. The application is a composition root: it constructs the services, publishes
them, activates the modules, and runs a frame. If that claim fails, the honest description is *a good
plugin API that the editor's own verbs do not go through*, which is exactly doc 36 § F2's finding
one layer in.

⚠️ **Extends [36](36-an-extensible-editor.md), and is the split it declined.** Doc 36 built the
front door — `IEditorRegistry`, `PluginContext`, `[EditorMenu]` and its seven siblings, the
in-process module seam, `ApplicationReferenceRule` — and its § P3 says in so many words that
*"splitting the god object is a different job from moving the features out of it … a split of its
own and not this phase's"*. This is that job. It also picks up the two decisions doc 36 left on the
table (the gizmo's recording entry point, § Part 6; the home of `AssetEditorRegistry`, § P3) because
the split changes the answer to both.

⚠️ **Adjacent, not absorbed.** [45](45-commands-and-focus-scope.md), [46](46-what-an-application-needs.md)
and [49](49-responder-chain-and-appkit-parity.md) are about where the *command system* lives and
how a command finds its handler. This document takes them as given: a command id stays a string,
a handler is whatever those documents make it, and nothing here moves `CommandRegistry`. What this
document decides is *who registers* a command and *what it calls*.

**Read [Part 1](#part-1--what-is-actually-there) before the design.** The audit is the argument.

---

## Part 1 — What is actually there

### The bones that are good

| Piece | Where | State |
|---|---|---|
| Plugin discovery, isolation, lifecycle, API versioning | `Editor/Vixen.Editor.Plugin` | **Real**, and the only editor project with a `PublicAPI` baseline |
| In-process modules through the same door | `PluginHost.Activate(id, name, module)` — `PluginHost.cs:525` | **Real.** Fourteen built-ins go through it: `EditorModules.Standard()`, `Editor/Vixen.Editor.Host/EditorModules.cs:71-95` |
| Typed contribution registry | `IEditorRegistry` / `EditorRegistry` in `Vixen.Editor.Core` | **Real.** Twelve contribution records, each in the assembly that owns it |
| The eight declaration attributes | `EditorMenuAttribute` (Plugin), `CreateAssetMenuAttribute` (Core), `[CustomInspector]`/`[CustomDrawer]` (Inspector), `[EditorTool]`/`[Overlay]`/`[DrawGizmo]` (SceneView), `[Importer]` (Assets) | **Declared and read** — `DeclaredContributions : IContributionScanner`, `Editor/Vixen.Editor.App/DeclaredContributions.cs:47` |
| The architecture rule | `build/ApplicationReferenceRule.cs` | **Real**, with `Allowed` (6) and a shrink-only `NotYetMoved` (5) |
| The editing pipeline | `EditTarget` / `EditProperty` / `IEditProvider` / `SetValuesCommand` in `Vixen.Editor.Core` | **Real**, reached by the inspector and node ports; not by the gizmo (doc 36 § Part 6) |
| Domain kernel with (almost) no UI | `Vixen.Editor.Core.csproj` | No importer reference and no feature name in it. ⚠ **Its README (`Editor/Vixen.Editor.Core/README.md:67-70`, `:428-429`) says it does not reference `Vixen.Ui`; the csproj does (`Vixen.Editor.Core.csproj:98`), for `IUndoManager` alone (#647), and `CommandStack.cs:4` is the only consumer.** The README is stale, not the boundary |

### The findings

**F1 — The application is one class in thirteen files.**

```bash
grep -ln "partial class EditorApplication" Editor/Vixen.Editor.App/*.cs | xargs wc -l
```

| File | Lines | What it holds |
|---|---|---|
| `EditorApplication.cs` | 5,596 | the constructor (553–1271), `Update` (1271–1434), panels (2324–2662), documents (2684–2855), plugins (2855–3049), the inspector (3049–3315), collections, layouts, `Commands()`, `SceneCommands()`, the scene menu, context menus, selection following, drag-and-drop, the hierarchy, bounds, create verbs |
| `EditorParity.cs` | 3,194 | every menu of doc 20 Part C and the verbs behind them: file, recents, editing, assets, entities, play, build, help, modes, toolbar |
| `EditorWorlds.cs` | 1,263 | worlds, the Create ▸ kinds, document-kind dispatch |
| `EditorSettingsPanels.cs` | 985 | preferences, project settings, plugin manager, history |
| `ViewportCommands.cs` | 715 | |
| `EditorSelectionVerbs.cs` | 544 | |
| `EditorBuilds.cs` | 499 | build settings, Build and Run, deploy |
| `EditorFrames.cs` | 476 | |
| `EditorDiagnostics.cs` | 460 | the diagnostics report |
| `EditorSourceControl.cs` | 380 | |
| `EditorProjects.cs` | 364 | which project, without a restart |
| `SceneMenus.cs` | 266 | |
| `ShaderGraphPreviews.cs` | 81 | |
| **Total** | **14,823** | |

⚠ **Two figures this document's first draft got wrong, corrected by the census.** A grep for
field-shaped lines said 434; the class has **214 state-bearing members** (102 instance fields, 46
consts, 9 static tables, ~57 properties) and **336 methods**, and 5,730 of the 14,823 lines — 39 % —
are comment. The constructor is `EditorApplication.cs:566-1054@c8b280993`, **489 lines**, not 720; `Update`
is `:1284-1428`. The corrected shape is smaller and no less the problem: 102 instance fields is a
class with a hundred reasons to change.

The census by concern (method-body lines; "touches" = methods reading another concern's fields):

| Concern | Fields | Methods | Lines | Touches |
|---|---|---|---|---|
| Scene document & selection | 36 | 107 | ~2,620 | Project 14, Viewport 11, Inspector 6 |
| Project lifecycle (incl. the constructor) | 18 | 28 | ~1,377 | the constructor hits 11 concerns; `Update` hits 10 |
| Asset browsing / import / content | 27 | 52 | ~1,326 | **Project in 35 of 52** — `project.Selection`, `project.Assets`, `project.Paths` |
| Command & menu registration | 3 | 33 | ~1,083 | Scene 12, Viewport 9, Plugins 4 |
| Preferences / settings | 16 | 28 | ~640 | Project 7, Inspector 5, Plugins 4 |
| Player build & deploy | 6 | 13 | ~333 | Project 6, Assets 5 |
| Diagnostics / log / console | 16 | 9 | ~286 | Project 5, Viewport 4 |
| Viewport / camera / frame | 25 | 8 | ~259 | Preferences 2 — view bookmarks live in `preferences.Viewports` |
| Workspace / layout / hot-reload | 10 | 12 | ~268 | `Layouts()` names panel ids from four other concerns |
| Play mode | 8 | 9 | ~263 | Viewport, Diagnostics, Scene |
| Source control | 7 | 12 | ~234 | Project 8; `Sweep` is gated on `browser is null` |
| Inspector / curves | 8 | 6 | ~227 | Preferences 6 |
| Plugin hosting & reload | 6 | 8 | ~181 | `PluginPoints` exports six concerns through one seam |
| Asset-editor documents | 8 | 11 | ~135 | — |

Three things the census found that the design has to answer:

* **State parked on the application because a panel's factory runs again on reopen** — 25 fields,
  each with a `Closed = () => x = null` or a remark saying so (`picker`, `probe`, `cameras`,
  `chrome`, `hierarchy*`, `browser`, `console`, `inspectors`, `bakeView`, `buildView`,
  `revisionsView`, `historyView` …). Doc 36 never named this: **the panel model has no place for a
  panel's own durable state, so the application became that place.**
* **Extractions started and not finished.** `ContentTasks`, `PlayModeController`, `SourceControl`,
  `EditorLog`, `EditorUserStore`, `AssetCollections`, `ThumbnailCache` each exist as a type, and
  the application keeps 60–135 lines of that concern's logic beside each — `AssetCollections` has
  `Add`/`Remove`/`Forget` and the application re-declares all three to add the save; saved
  *filters*, the sibling feature, live in `preferences` instead. Same feature, two stores.
* **The cheap moves exist and are large**: 171 methods (~3,400 lines) touch one concern's fields;
  the selection verbs (all of `EditorSelectionVerbs.cs`, 20 methods), the entity verbs
  (`EditorParity.cs:2446-2901@0d7dda4a9`, 12), the outliner (`EditorApplication.cs:4703-5086@c40c2ea93`, 7) and the
  project chooser (all of `EditorProjects.cs`) are self-contained clusters.

⚠ **And the App README's load order is wrong in two places** (`Editor/Vixen.Editor.App/README.md:104-120@3e9f7e5d9`). *"Preferences
after commands, because the undo depth is pushed into stacks that exist by then"* — the ordering
holds, the reason does not: `ApplyPreferences` writes `project.GlobalStack.Capacity` and
`scene.Stack.Capacity` (`EditorSettingsPanels.cs:547-550`), which exist from line 633, before any
command. *"Disabled list before activation"* — true for disk plugins, **false for the built-in
modules**: they are activated inside `Commands()` → `RegisterModes()` (`EditorParity.cs:1230-1248@0d7dda4a9`,
called from `EditorApplication.cs:3880@0d7dda4a9`), before `LoadDisabledPlugins` at `:1013`, and
`PluginHost.Activate` (`PluginHost.cs:522@0d7dda4a9`) never consults `suppressed`. **A built-in module cannot
be disabled**, which the plugin manager's Disable button does not say. *(Fixed by
[#1288](https://github.com/Rikarin/Vixen/issues/1288): `Activate` consults the set and keeps a
suppressed built-in as a disabled row, and the list is read before `Commands()`. ⚠ It also found a
second half: a built-in disabled while running could not be re-enabled without a restart, because
`Deactivate` dropped the only instance `Revive` could start.)*

⚠ **The verbs are private and closed over the fields.** `Group()`, `SnapToFloor()`, `EnterPlay()`,
`ExportPackage()`, `BringIn()`, `DropIntoScene()` — `EditorParity.cs:684-3032@0d7dda4a9` — are instance
methods that read `scene`, `project`, `browser`, `content`, `play` off `this`. A plugin that wants
*"group the selection"* cannot call it; a test that wants it has to open a whole `EditorSession`;
a second surface (a context menu, a palette entry, a script) gets it only by the application
registering another command that closes over the same method. **This is the finding that
matters**: the plugin API cannot reach the editor's own use cases because the editor's own use
cases are not objects.

**F2 — Every built-in registers by hand; the attributes have no production users.**

```bash
for a in EditorMenu CustomInspector CustomDrawer EditorTool CreateAssetMenu Overlay DrawGizmo; do
  grep -rlE "^\s*\[$a\(" Editor Core Samples --include="*.cs" | grep -v Tests | wc -l; done
```

Seven zeros. `[Importer]` has 27, because `AssetImporter<T>.Extensions` has always read it. Doc 36
§ D3 said *"in-tree code registers the records directly and nothing scans it"* and promised the
in-tree half of the attribute set would be a **source generator** — *"the generator sees the
attribute and emits a registration — no reflection, ADR-002 intact"*. That generator was never
built. `PluginHost.Declared(context, assembly)` (`PluginHost.cs:819`) is the reflective scan for a
plugin's or a script's assembly; the in-process `Activate` overload does not call it, and no module
does either. So the mechanism a third party is told to use is one no first-party feature has ever
been through, and the count of hand registrations says what that costs:

```bash
grep -rE "Commands\.Add\(|\.AddCommand\(|Menus\.Add\(|AddMenu|AddSubmenu\(|AddMenuItem\(|Registry\.Add\(|Modes\.Add\(|Panels\.Add\(|AddPanel\(" Editor --include="*.cs" --include="*.vxml" | grep -v Tests/ | awk -F/ '{print $2}' | sort | uniq -c | sort -rn
```

| Assembly | Registration call sites |
|---|---|
| `Vixen.Editor.TextureGraph` | 98 |
| `Vixen.Editor.ShaderGraph` | 70 |
| `Vixen.Editor.VfxGraph` | 64 |
| `Vixen.Editor.App` | 61 (and ~180 distinct command-id literals) |
| `Vixen.Editor.AssetEditors` | 37 |
| `Vixen.Editor.Texturing` | 26 |
| `Vixen.Editor.Terrain` | 22 |
| the rest | ≤ 13 each |

**F3 — Five feature assemblies are still referenced, and the import pipeline references two more.**

`ApplicationReferenceRule.NotYetMoved` (`build/ApplicationReferenceRule.cs:116@475df7b53`): `AssetEditors`,
`NodeGraph`, `Profiler`, `Debugger`, `Diagnostics`. Doc 36 § P3 measured what each costs and
recommended *"decided not to"* for `AssetEditors` because the registry's home needs `UiElement` and
`EditorProject` together. Part 3 § D4 revisits that with a different split.

⚠ Not counted anywhere until now: **`Vixen.Editor.Assets` → `ShaderGraph`, `VfxGraph`**
(`Editor/Vixen.Editor.Assets/Vixen.Editor.Assets.csproj`). The import pipeline — the thing doc 36
put in `Allowed` because *"an editor that cannot import without a plugin is not an editor"* — knows
two graph kinds by name. `Blockout`, `Terrain`, `Water` reference `Plugin` + `SceneView` + `Ui` and
nothing feature-shaped; those three are what a clean reference set looks like.

**F4 — A first-party plugin that nothing loads.** `Vixen.Editor.Texturing` is written as a disk
plugin — its README's first sentence — and is activated by nothing: not in `EditorModules.Standard()`,
no `plugin.yaml`, no copy step, `Host` does not reference it. Filed as
[#1276](https://github.com/Rikarin/Vixen/issues/1276). It is here because it is the shape this
document is about: the *plugin* path has never carried a first-party feature, so it is unproved in
the same way the attributes are. ✅ **Decided and landed (#1276):** `TexturingModule` is in
`EditorModules.Standard()` and `Host` references it; `TexturingModuleReachTests` holds its four panels,
ten Tools lines and two Create ▸ kinds to exactly once in a session opened as `EditorHost` opens one,
gone on unload and collectable after it (red with the module out of the list), and `ModuleReach.txt`
gained its 22 lines and lost none.

**F5 — The feature assemblies have their own god files.** `LayerStackView.cs` 3,783,
`TexturingModule.cs` 2,381, `LayerStackGraph.cs` 2,019, `TextureGraphCompiler.cs` 1,809,
`SceneViewport.cs` 1,668, `ProjectBrowser.cs` 1,491, `BlockoutMode.cs` 1,462,
`EditorWorldRenderer.cs` 1,435, `NodeGraphView.cs` 1,392, `EditorHost.cs` 1,410, `EditorShell.cs`
1,180, `ComponentsView.vxml` 1,117. Moving a feature behind a module boundary did not make the
feature modular inside; a `TexturingModule` of 2,381 lines is the application's shape reproduced
one level down.

**F6 — The plugin's view of the editor is a bag of fourteen services and the shell.**
`EditorApplication.PluginPoints()` (`EditorApplication.cs:3017-3118@69c09e6ed`) publishes: the project, the
scene, `DrawerRegistry.Default`, `ImporterContributions.Default`, the extension registry, the
editing state, the work plane, `IMeshBaker`, `IMeshMapBaker`, `IMeshSource`, `IActiveScene`,
`IActiveView`, `IDeviceDeploy`, `IEditorGraphics`, the asset editors, the reload host. Two of them
are process-wide statics handed over as if they were the host's. Nothing in the list is a *use
case* — a plugin gets the scene document and may do to it what it likes; it does not get *"delete
the selection, undoably, the way the Delete key does"*.

**F7 — The built-ins that did go through the door still leak round it.** Measured module by
module (the front-door audit, 2026-09-17):

| Module | Through `context.*` / `Require` / `Owns` | Round the side |
|---|---|---|
| `AssetEditors`, `Scripts`, `Texturing` | everything | — |
| `Blockout` | mode, submenus, four services | ~~`Shell.RegisterPanel` ×2 with no removal (`BlockoutModulePanels.cs:64,115@0d7dda4a9`), `BlockoutTheme.Install` never uninstalled~~ — `context.AddPanel` since #1285, and the theme through `context.AddStyleSheet` since #1484, so the loaded-sheet count returns to baseline; no `Deactivate` |
| `Terrain` | registry adds via `Owns`, `Saved` unhooked on unload | ~~`Shell.RegisterPanel` ×5 (`TerrainModulePanels.cs:112-276@0d7dda4a9`, #740), `Shell.Modes.Add` ×2 (`TerrainModulePanels.cs:475-476@0d7dda4a9`)~~ — `context.AddPanel`/`AddMode` since #1287, and the three mode events unhooked on unload |
| `Water` | commands paired with `OnUnload` | ~~`Shell.RegisterPanel` ×2 (`WaterModulePanels.cs:33,63@0d7dda4a9`), `Shell.Modes.Add` (`WaterModule.cs:135@0d7dda4a9`), a process-wide `WaterDebug` static~~ — `context.*` since #1286; the switches are a per-module `WaterDebugFlags` contribution, and `WaterDebug.Process` stays the game console's (the static could not go: samples and `BuoyancyDebugSystem` read it) |
| `TerrainPhysics` | `IEditorRegistry` | ~~writes `ITerrainColliders` **into the shared services bag** (`TerrainPhysicsModule.cs:75@0d7dda4a9`) and never removes it — `PluginServices.Add` throws on a duplicate, so a reload of this module fails~~ — contributed through `Owns` since #1287; `TerrainModule` follows the registry each frame |
| `Diagnostics` | eight `AddPanel`s | `ProfilerTheme.Install` + `DebuggerTheme.Install` never released (`DiagnosticsModule.cs:349-350`) |

`PluginContext.AddPanel` and `AddMode` exist precisely to record the removal
(`PluginContext.cs:99-106, 178-185`); three of the seven modules do not use them. And the application holds
**typed fields to two modules** — `FindModule<DiagnosticsModule>` and `FindModule<AssetEditorsModule>`
(`EditorDiagnostics.cs:67-94@28c44db26`, `EditorApplication.cs:568@28c44db26`, `:569@28c44db26`) — forwarding the device, the GPU
frame, the capture source, the profiler model and the deploy status into one and `Follow(view)` into
the other. A module the host has a field for is not a module.

**F8 — The extension surface is one-sided.** Of the twelve contribution kinds, `SettingsPage`,
`AssetPreview` and `BuildStep` have a reader and **no production writer**; `SceneTool`,
`SceneOverlay` and `ComponentGizmo` are written only by the attribute scanner, which no production
assembly is scanned by; `IMeshMapBaker` is published and asked for by nothing. Five of the ten
service seams have one consumer (`EditorDocument.Saved`, `IActiveView`, `IEditorGraphics`,
`IMeshBaker`, `IDeviceDeploy`). Six services are published under their **implementation** type
(`EditorProject`, `SceneDocument`, `MeshEdit`, `WorkPlane`, `AssetEditorRegistry`, `HotReloadHost`),
and two — `DrawerRegistry.Default`, `ImporterContributions.Default` — are process statics handed
over as if the host owned them. `Vixen.Editor.Plugin`'s `PublicAPI.Shipped.txt` is one line
(`#nullable enable`); all 195 symbols are in `Unshipped`. **Nothing about the contract has ever been
declared stable.** ✅ *Since #1311 the core is*: `IEditorPlugin`, `PluginContext`, `PluginServices`
and `PluginHost` — 59 lines, every member — are `Shipped`; the doc 50 services stay `Unshipped`
until P2 finishes.

**F9 — The shell carries 110 feature names, measured, and they are not where doc 36 looked.**
The feature-name audit (`.cs` and `.vxml`, code only, comments excluded):

| Shell assembly | Rows | The largest |
|---|---|---|
| `Vixen.Editor.App` | 48 | `BuiltInAssetKinds`, 24 rows with 21 literal extensions (`LibraryModule.cs:207-243@874d511f5` since #1292, `EditorWorlds.cs:764-799@28c44db26` before); `BuiltInSubsystems` naming `TerrainComponent`, `WaterZoneComponent`, `BuoyancyBody` (`EditorApplication.cs:4214-4232`); the *Profiling* layout preset naming five diagnostics panel ids (`EditorApplication.cs:3659@fdc943b19`, `DiagnosticsModule`'s since #1296); `StandardIcons` keyed by importer name and extension (`StandardIcons.cs:89-115`); `case Vixen.Editor.AssetEditors.Vfx.VfxGraphView` (`EditorWorlds.cs:823,827@5f03c9b80`); `is StandardFrameDocument`, `is ShaderGraphDocument`, `is MaterialView`, `is TextureImportView` (`EditorApplication.cs:772-778, 2912-2920@5f03c9b80`, removed by #1297); `ITerrainScene`/`IVegetationScene`/`IWaterScene` — one property per feature (`ScenePresenter.cs:255-298`) |
| `Vixen.Editor.Ui` | 38 | **35 string ids in `EditorStrings.cs` that belong to a feature which already has its own `*Strings` class** (Terrain 9, Blockout 10, Water 4, Texturing 4, Diagnostics 8); `ModeArt` has a static icon per mode; `EditorIcons["profiler"]` |
| `Vixen.Editor.Assets` | 14 | `BuiltInImporters.cs:57-99@3e9f7e5d9` is a hand list of **34** importers naming Vfx, ShaderGraph, Terrain ×2, Water, Ai ×4, Animation ×7, Gameplay, Net; `VfxImporter` and `ShaderGraphSources` construct the two graph compilers, which is the `ShaderGraph`/`VfxGraph` reference |
| `Vixen.Editor.Host` | 8 | builds `ShaderGraphPreviewRenderer` itself (`EditorHost.cs:581`), loads the terrain stages' SPIR-V (`:1033-1047`), a stale `using Vixen.Editor.Profiler` (`:8`) |
| `Vixen.Editor.SceneView` | 2 | `SceneLines.LightShapes` still walks entities for one component (`SceneLines.cs:901`); five seams named for one feature each (`IVegetationScene`, `IWaterScene`, `SplineOverlay`, `SplineEdit`, `MeshExport`'s `"Blockout"` default) |
| `Vixen.Editor.Core`, `Vixen.Editor.Inspector` | **0** | the two assemblies whose READMEs promised it |

⚠ **`Core` and `Inspector` being clean is the finding that makes the rest tractable**: the domain
kernel already has no feature in it. Every one of the 110 is in the application, the chrome, the host
or the import pipeline — the tiers this document's § D1 puts *above* the domain.

**F10 — The views are cleaner than the application, and the vocabulary is doubled where the
application joins them.** The domain audit read the six largest views for I/O, process launches,
database mutation and importer calls: `LayerStackView.cs` 0, `NodeGraphView.cs` 0,
`SceneViewport.cs` 0, `EditorShell.cs` 0, `ComponentsView.vxml` 0 (its three writes are all
`Stack.Execute`), `ProjectBrowser.cs` 2 — a `Scan()`+`Save()` pair (`:1167-1175@3e9f7e5d9`) that duplicates
`EditorProject.Open` minus its "save only if changed" guard. **The application logic is not in the
views; it is in the one class** — `EditorParity.cs` alone has 21 file-system calls, including
`Directory.Delete(project.Paths.Library, recursive: true)` at `EditorParity.cs:1657`.

What *is* doubled is the language, and each pair is two contexts sharing a word:

| One idea | Vocabulary A | Vocabulary B | Measured |
|---|---|---|---|
| **the active scene** | `EditorProject.ActiveDocument` (`EditorProject.cs:56`) | `EditorApplication.scene` (`EditorApplication.cs:103@c40c2ea93`), whose remarks said *"half the editor holds the active scene"* (`EditorApplication.cs:97-98@c40c2ea93`), plus `Shown => inspected ?? scene` wrapped as `IActiveScene` | `ActiveDocument` has **3** readers; `scene.` is read **124** times across the partials; `EditorWorlds.cs:497-500@c40c2ea93` set both in sequence — `SetActiveScene` then `project.Activate` |
| **dirty** | `CommandStack.IsDirty` → `EditorDocument.IsDirty` → `EditorProject.HasUnsavedChanges` | `SettingsView.dirty` + `ProjectSettingsStore.HasUnsavedChanges` | `HasUnsavedChanges` gates Save All and close (`EditorParity.cs:127,1952`) and **does not include** the settings store's — project settings edits are non-undoable by construction (`EditorSettingsPanels.cs:749`, `EditedDocument = null`) and can be lost on close |
| **selection** | `Selection<T>` — assets on the project, entities on the scene | `HashSet<NodeId>` (`NodeGraphView.cs:109@0d7dda4a9`), `List<FoliageAddress>`, `HashSet<SplineHandle>`, `LayerPath?` | the graph's is read cross-assembly by two `.vxml` views; none of B's is reactive or on `EditorContext` |
| **a document** | `EditorDocument` (25 subclasses) | `Vixen.Ui.IEditableDocument` — `EditorDocument` implements it explicitly since #656, and the editor's scenes have a `Location` since #1493 | the model itemises its own divergence at `EditorDocument.cs:29-61` |
| **a property** | `EditProperty` (18 uses, the live edit path) | `EditorProperty<T>` / `EditorObject` / `SetPropertyCommand<T>` — **5 uses, all in Core, no subclass of `EditorObject` anywhere under `Editor/`** | `EditProperty.cs:18-19@3e9f7e5d9` said it *"is what a `SceneDocument`'s own fields are"*; they are plain dictionaries (`SceneDocument.cs:120-194`). A dead type with a remark that describes the live one |

And **hidden and locked are edited behind the stack's back**: `SceneDocument.SetHidden`/`SetLocked`
have no command (grep: none), and are written directly from the outliner's eye and lock toggles
(`EditorApplication.cs:5125,5127@c40c2ea93`, `EditorParity.cs:601,610`, `EditorWorlds.cs:597,610@c40c2ea93`), from `Isolate`
(`SceneEditing.cs:622-677`), which #1290 moved out of `EditorSelectionVerbs.cs`, and — the one that is a bug — from **inside a Blockout
transaction** (`BlockoutBoolean.cs:66-71@3e9f7e5d9`), so undoing a boolean restores the reparent and the mesh
and leaves the operand hidden. Filed as [#1277](https://github.com/Rikarin/Vixen/issues/1277).

---

## Part 2 — What Evans actually says, and which parts to take

Four things from *Domain-Driven Design*, stated as they apply to an editor rather than a bank.

**1. A bounded context is a boundary of language, and the assembly is only its enforcement.** Inside
"Scene Authoring", *selection* means entities and *dirty* means the scene's command stack; inside
"Content Library", *selection* means asset ids and *dirty* means an unimported file. The editor has
both selections and both dirties and one class that reads all four. Naming the contexts is what lets
each word mean one thing.

**2. The application layer is thin and the use cases are its objects.** An application service
exposes what the user can *do*; it coordinates aggregates and commands and holds no domain rule of
its own. Its methods are the reason menus, shortcuts, palettes, scripts and plugins can all reach one
behaviour — a menu item is a name for a use case, not the place it lives.

**3. Aggregates own their invariants; nothing edits their insides from outside.** `SceneDocument`
already is one: the hierarchy, the selection and the command stack are its, and doc 36 § D1 is the
rule that every write goes through the stack. What is missing is the same discipline on the *other*
roots — the project, the library, the workspace — whose state is spread across the application's
fields.

**4. A context map names the relationships, and the anti-corruption layer is where two languages
meet.** A *Workspace* that shows a *Document* needs a `UiElement` from it; the document must not
know what a `UiElement` is. Doc 36 § P3 hit exactly this when it could not find a home for
`IAssetEditorFactory` and its `Open` + `CreateView` pair — that pair is two contexts' words on one
interface, and the answer is the ACL, not a fourth assembly.

**What not to take.** Repositories and factories as ceremony (the asset database is already the
repository); event sourcing (the command stack is the history and it is per document by design);
a mediator or message bus (doc 45/49 are building the one dispatch this editor needs, and a second
one would be F10's two-registries mistake); DI containers (`PluginServices` is a bag by name, on
purpose — `PluginServices.Add` throws on a second publish — and that is enough).

---

## Part 3 — The design

### D1 — The context map

Twelve contexts, in three tiers. **The tier decides the reference direction**, which is what the
architecture rules enforce.

| Tier | Context | Aggregate root(s) | Owns today (measured) | Lives in |
|---|---|---|---|---|
| **Domain** | **Project** | `EditorProject` | paths, recents, scaffold, disk watch, project assemblies, external-edit announcement — `EditorProjects.cs`, `Watch`/`FollowDisk`/`BuildCode` (`ProjectSession.cs:294-512`), `ProjectAssemblies.cs`, `ProjectHistory.cs` | `Vixen.Editor.Core` (already) |
| | **Scene Authoring** | `SceneDocument`, `Selection<Entity>` | hierarchy, entity verbs (create, group, parent, duplicate, align, snap, LOD), marks, bounds — `EditorParity.cs:684-855, 2446-2913@0d7dda4a9`, `EditorSelectionVerbs.cs`, `EditorApplication.cs:4542-5737@28c44db26` | `Vixen.Editor.SceneView` + a service |
| | **Content Library** | `AssetDatabase` | browser model, collections, saved filters, thumbnails, import and content build, drag-and-drop, package export — `EditorParity.cs:411-684, 1545-2038@c8b280993`, `EditorApplication.cs:3520-3645, 4868-5052@c8b280993`, `ContentTasks.cs`, `AssetCollections.cs`, `ThumbnailCache.cs` | `Vixen.Editor.Assets` + a service |
| | **Documents** | `EditorDocument`, `CommandStack` | open, reopen, join to a panel, `AssetEditorRegistry`, `Saved`, `Opened` — `EditorApplication.cs:2696-3060@c8b280993` | `Vixen.Editor.Core` (already) |
| | **Editing** (shared kernel) | `EditTarget`, `EditProperty`, `IEditProvider` | the one edit path, doc 36 § D1 | `Vixen.Editor.Core` (already) |
| | **Session** | `PlayModeController` | enter/stop play, snapshot/restore, `PlayPhysics`, play diagnostics — `EditorParity.cs:855-953, 2917-3035@0d7dda4a9`, `PlayPhysics.cs` | `Vixen.Editor.Core` |
| | **Delivery** | `BuildPlan` | build settings, Build and Run, deploy, `BuildStep` contributions — `EditorBuilds.cs`, `EditorApplication.cs:5251@c8b280993` (`AnalyseContent`, since removed) | `Vixen.Editor.Assets.Content` |
| **Application** | **Workspace** | `DockingWorkspace`, `EditorUserStore` | panels, layouts, view bookmarks, what persists — `EditorApplication.cs:1463-1531, 2217-2670, 3600-3677, 5742@28c44db26` | `Vixen.Editor.Ui` (already) |
| | **Extensibility** | `PluginHost`, `EditorRegistry` | modules, disk plugins, scripts, hot reload, `PluginPoints` — `EditorApplication.cs:3060-3254@c8b280993`, `Vixen.Editor.Scripts` | `Vixen.Editor.Plugin` (already) |
| | **Composition root** | `EditorApplication` | construct, publish, activate, tick | `Vixen.Editor.App` — **≤ 800 lines** |
| **Supporting** | Diagnostics, Source control, Preferences | `EditorLog`, `SourceControl`, `EditorSettings` | `EditorDiagnostics.cs`, `EditorSourceControl.cs`, `EditorSettingsPanels.cs` | modules |

**Relationships, stated:**

* Scene Authoring, Content Library, Documents, Session and Delivery are **customers of Editing**
  (shared kernel) and of Project (**conformist** — they take the project as it is).
* Workspace is a **customer of Documents** through an **anti-corruption layer**: a document exposes
  a *view factory contribution* (Ui side) separately from its *document factory* (Core side). This
  is the split that dissolves doc 36's `AssetEditorRegistry` impasse — § D4.
* Extensibility is **open host**: every domain service is published to it and it knows none of them
  by name. A module reaches a use case with `Require<ISceneEditing>()` exactly as it reaches
  `IMeshBaker` today.
* The composition root is the only thing that may reference all of them, and it may only *construct
  and publish*. A rule (§ D6) fails the build when it grows.

### D2 — A use case is a method on a service, and a command is a name for it

The move that pays for everything else. Each domain context gets one application service —
`ISceneEditing`, `IContentLibrary`, `IProjectSession`, `IDocumentHost`, `IPlaySession`,
`IPlayerDelivery`, `IWorkspace` — whose public methods are the verbs that today are private on
`EditorApplication`:

```csharp no-compile="the shape; names are the existing methods' names"
public interface ISceneEditing {
    SceneDocument Active { get; }
    void Group();                       // EditorParity.cs:2507
    void GroupAsLod();                  // EditorParity.cs:2562
    void SnapToFloor();                 // EditorParity.cs:2832
    void AlignWithView();               // EditorParity.cs:2738
    Entity CreateShape(PrimitiveKind kind);   // EditorApplication.cs:5693
    …
}
```

Three properties, each the reason for the shape:

1. **A test calls it without a window.** `EditorSession` today is the only way to reach `Group()`,
   and it opens a shell, a docking workspace and every module. A service takes a `SceneDocument`
   and a `Selection` and is testable with neither. ⚠ The existing `*Tests` that drive verbs through
   the shell stay — they are the proof the *wiring* holds — and the new ones are the proof the
   *verb* holds.
2. **A plugin calls it.** `context.Services.Require<ISceneEditing>().Group()` is what "reusable
   through plugins" means concretely, and it is one line in `PluginPoints()` per service.
3. **A menu item is a registration that names it**, and so is a context-menu line, a palette entry
   and a script — the same method, four surfaces, no fifth copy of the closure.

⚠ **The state moves with the verb, not after.** A service that calls back into the application for
`browser` or `console` has moved nothing. The census (Part 1's sub-audit) says which fields belong
to which verb; the phase that moves a verb moves its fields, or it is not that phase.

⚠ **A panel's durable state belongs to its context's model, not to the application.** The 25
parked fields (Part 1 § F1) are each *"the thing the panel is looking at, which must outlive the
panel"* — the browser's folder and grid mode, the console's model, the viewport's cameras. In this
design that is the service's state: `IContentLibrary.Shown`, `IWorkspace.Views[i]`,
`IDiagnostics.Console`. The panel factory reads it on build and writes through it; nothing is
nulled in `Closed` because nothing was parked. This is the same rule
[the markup work](../../Editor/Vixen.Editor.Ui/README.md#the-panel-ledger--what-is-markup-what-is-next-and-what-never-will-be)
arrived at from the other side — a `.vxml` panel binds to a model that exists before the panel
does — and it is what lets a panel be markup at all.

⚠ **What stays on the composition root, and must.** Which project (the reopen loop), the frame tick,
`PluginPoints()`, the module list — and the arbitration doc 36 § P3 named as *"the application's
job"*: which scene a sequence drives, what analyses an addressable group. Those are the ~800 lines.

### D3 — The application's own verbs go through the front door as modules

Doc 36 § D2: *"the built-ins must move to producer 2 or 1 and stop being producer 0 … an API whose
own authors bypass it is a guess."* Terrain, Blockout, Water and Diagnostics did. **The application
itself did not**: `Commands()`, `SceneCommands()`, `ParityCommands()`, `FileCommands()`,
`EditingCommands()`, `AssetCommands()`, `EntityCommands()`, `PlayCommands()`,
`BuildAndToolCommands()`, `HelpCommands()`, `ParityMenus()`, `ParityToolbar()` are ~1,300 lines of
producer 0.

Each becomes an `IEditorPlugin` — `SceneModule`, `LibraryModule`, `PlayModule`, `DeliveryModule`,
`WorkspaceModule`, `HelpModule` — that `Require`s its service and registers its verbs through
`PluginContext`. They are activated by `EditorModules.Standard()` like the other seven, **before**
the feature modules (the Scene menu must exist before Terrain adds to it). ⚠ They start life inside
`Vixen.Editor.App` and move to their own assembly only when their reference set is clean; doc 36
learned that dereferencing is the last step, not the first.

And they use the attributes. Which needs § D5.

### D4 — The Documents ↔ Workspace anti-corruption layer

Doc 36 § P3 measured what dereferencing `AssetEditors` costs (ten names, seven files, two invisible
to the compiler) and could find no home for `AssetEditorRegistry` because `IAssetEditorFactory`
carries `Open` (needs `EditorProject`, `EditorDocument`, `AssetId` — Core) and `CreateView` (needs
`UiElement` — Ui) *together, deliberately*: *"a document with no view is a model nobody can reach
and a view with no document is a control with nothing behind it."*

The deliberate pairing is right as a **product** invariant and wrong as a **type** — it is two
contexts' words on one interface. The split:

| | Where | What it knows |
|---|---|---|
| `DocumentKind` — a contribution: extension → `Func<EditorProject, AssetId, EditorDocument>` | `Vixen.Editor.Core` | the project and the document; no `UiElement` |
| `DocumentView` — a contribution: `Type` of document → `Func<EditorDocument, UiElement>` | `Vixen.Editor.Ui` | the element; the document only as `EditorDocument` |
| The pairing invariant | a **test** in the Documents context: every registered `DocumentKind`'s document type has a `DocumentView`, and the reverse | where doc 36 says a rule with no gate is a wish |

`IDocumentHost.Open(AssetId)` (Core) resolves the kind; `IWorkspace.Show(EditorDocument)` (Ui)
resolves the view. `AssetEditorRegistry.Opened` becomes `IDocumentHost.Opened`. The ten names doc 36
counted are then reached through the two registries and `AssetEditors` leaves `NotYetMoved`.

⚠ **This reopens a "decided not to", and says why.** Doc 36's refusal was correct *for the interface
it had*: one type needing both halves has no clean home. The refusal does not survive the split,
and this document rather than a batch is where that gets decided. If Jiu wants the pairing to stay a
type, § P4 below is struck and `AssetEditors` stays in `Allowed`.

### D5 — The in-tree generator, so the attributes have authors

Doc 36 § D3's original design: in-tree, *"the generator sees the attribute and emits a
registration"*; out-of-tree, the bounded reflective scan. The second half was built and the first
was not, so today an in-tree `[EditorMenu]` does nothing — which is why there are none.

`Vixen.Editor.Plugin.Generator` reads the eight attributes in any assembly that references it and
emits, per assembly, a `static partial class Declared { public static void Register(PluginContext) }`
that a module's `Activate` calls in one line. Same shape as `InspectorDescriptorGenerator` →
`InspectorRegistry.Register`, which is the ADR-002-clean precedent this repository already runs.

⚠ **Both tiers must agree, member for member.** The generator and `DeclaredContributions.Scan`
read the same attributes and must produce the same registrations; a test compiles one fixture
through both and compares the registry — the shape `ReflectedTypeTests` uses for the importer
descriptor.

⚠ **Localisation is why `[EditorMenu("Scene/Create/Cube")]` cannot be the whole story.** A
built-in's title is a `StringId` in a `*Strings` class, and `EditorMenuAttribute` carries a `Path`
and an `Id` and no `StringId`. The attribute gains `Title` as the *declaration-class member name*
(`Title = nameof(SceneStrings.CreateCube)`), the generator resolves it, and the scan resolves it by
reflection; a plugin without a catalogue keeps the literal path. `CheckStrings` already fails on an
id used nowhere, so a title the generator stops referencing is caught.

**Sabotage that proves it:** remove one `[EditorMenu]` from `SceneModule`; the reach test (§ D6)
goes red on the missing menu line. Comment out the `Declared.Register` call; every line of that
module's goes red at once.

✅ **Built for Blockout (#1283–#1285), with three corrections to this section.** (1) A built-in's
menus are mostly lines for commands its *mode* registers — with enablement, a context and a chord —
so `[EditorMenu]` also goes on a `const string` command id (a line, every path part a menu) and on a
`static IEnumerable<string> ()` (a run of lines), and `[EditorSubmenu]` gives a submenu its translated
title and a place after a named sibling; the verb shape alone could not have expressed one Blockout
line. (2) "Both tiers must agree" is half enforced by construction: placement moved into
`DeclaredMenus`, which both readers call, and the generated code constructs each attribute as written,
so only *what was found* can differ — which `DeclaredParityTests` compares. (3) The `Title` rule is
"the class `Strings` names, or the one `*Strings` class of the declaring assembly that declares it";
`nameof` alone loses the class. Both sabotages above were run against `BlockoutModule`: one
`[EditorMenu]` removed → 62 of 63 lines; `Register` commented out → 0.

### D6 — The instruments, before the moves

Every rule below is shrink-only, in the `CheckWhitespace` / `NotYetMoved` shape: a line in the
exemption list that has become clean fails too.

| Instrument | What it fails on | Shape |
|---|---|---|
| **`ApplicationSizeRule`** | any `partial class EditorApplication` file over its recorded ceiling; the ceilings are a table in `build/` and may only go down | `wc -l` per file, committed |
| **`FeatureNameRule`** | a shell assembly (`App`, `Ui`, `Host`, `Core`, `SceneView`, `Inspector`, `Assets`) naming a type, a namespace, an extension literal or a command-id prefix that a feature assembly owns — the sub-audit's list is the initial exemption file | grep over `.cs` **and** `.vxml`; the compiler under-reports these |
| **`ReachTest`** | an `EditorSession` opened the way `EditorHost` opens one, asserting every standard module's Create ▸ entries, menu lines and panels are present — by *effect*, doc 36 § P2's rule | closes [#1276](https://github.com/Rikarin/Vixen/issues/1276)'s test half |
| **`DeclaredParityTest`** | the generator and the scan disagreeing on one fixture | § D5 |
| **`DocumentPairingTest`** | a `DocumentKind` with no `DocumentView` or the reverse | § D4 |
| **`ServiceReachTest`** | a public method on any `I*Editing`/`I*Library`/… service that no command, menu, context menu or module calls — the *"finished thing nothing calls"* rule applied to the new surface | grep for callers, not for the type |
| **`ModuleRegistrationRule`** | an `IEditorPlugin` assembly calling `Shell.RegisterPanel`, `Shell.Modes.Add`, `Shell.Commands.Add`, `*Theme.Install` or `Services.Add` directly rather than through `PluginContext` (Part 1 § F7's six leaks are the initial exemption list) | grep; the removal-recording wrappers already exist — ⚠ **they did not for `*Theme.Install`** (measured for #1299): `UiDocument` had `Load`, `LoadOnce` and `ReloadStyles` and no way to take a sheet back out. Since #1484 it has `Unload`, over a slot that stays empty so every other index keeps naming its sheet, and `PluginContext.AddStyleSheet` is the wrapper; Blockout and the node graph use it |
| **`ContributionWriterTest`** | a contribution kind with a reader and no production writer (`SettingsPage`, `AssetPreview`, `BuildStep` today) — a registry nobody writes is a list with a longer name | one test over `IEditorRegistry`'s kinds |

⚠ **`Docs` is not `CheckDocs`, and each new public service type owes a guide page or a
`DocsExempt` line** (CLAUDE.md § Gates). Seven services and two contribution records is nine pages. **Landed (#1311) for the services that exist:** `editor/content-library` (`IContentLibrary`, `ShownContent`), `editor/project-session` (`IProjectSession`, `RecentProject`) and `editor/scene-editing` (`ISceneEditing`); `IPlaySession` was already on `editor/play-mode-systems` and `IBlockout` on `editor/writing-a-plugin`. `IDocumentHost`, `IWorkspace`, `DocumentKind` and `DocumentView` landed with #1294 and are on `editor/documents-and-views`. ⚠ The plugin README's list of what `PluginPoints()` publishes had drifted by five services; `PublishedServicesReadmeTests` now compares it with the editor.

---

## Part 4 — Phases

Each shippable, the editor working throughout, tests green at every merge. Order: instruments,
then the change that gives the attributes authors, then the extraction that the instruments can
watch, then the feature-side god files. **The risk is in P2**, and P0 is what makes P2 visible.

### P0 — Instruments (≈ 0.5 EM)

`ApplicationSizeRule`, `FeatureNameRule` with its measured exemption list, `ReachTest`,
`ModuleRegistrationRule`, `ContributionWriterTest`. Land the `FeatureNameRule` first, because its
exemption file *is* the audit and every later phase deletes lines from it. Three corrections ride
along because they are one-line truths the rules would otherwise encode wrongly: the Core README's
`Vixen.Ui` line, the App README's two load-order reasons (Part 1 § F1), and the deletion of
`EditorObject` / `EditorProperty<T>` / `SetPropertyCommand<T>` — five uses, all in Core, no
subclass anywhere (§ F10) — so that `EditProperty` stops having a twin one letter away. Sabotage each: add a `Vixen.Editor.Terrain.TerrainMode` name to `EditorShell.cs`, add
50 lines to `SceneMenus.cs`, remove `WaterModule` from `Standard()` — three red runs, then revert.

**Exit:** three rules in `CheckArchitecture`, each with a false-positive-free run over the tree and a
test that shows it can fire (`ApplicationReferenceRuleTests` is the shape). #1276 decided and closed.

✅ **Landed 2026-10-19, re-measured rather than restated.** The three rules are in `CheckArchitecture`
with fixtures in `Vixen.ApiCheck.Tests`; the two by-effect tests are in `Vixen.Editor.App.Tests`.
Every list regenerates with one command — `./build.sh CheckArchitecture --update-exemptions` for
the three files, `VIXEN_REGENERATE=1` on `ModuleReachTests` for the manifest — and every list fails on
a row the tree no longer needs. What the measurement changed:

* **`ApplicationSizeRule`** — fourteen partials, not thirteen (`PlayOverlaySwitches.cs`), 15,241
  lines; `EditorApplication.cs` is 5,826. A `(total)` row and a new-partial-file failure stop a verb
  moving into a new partial from counting as a move.
* **`FeatureNameRule`** — **109 rows** (159 occurrences): App 63, Assets 41, Host 4, Ui 1, and Core,
  Inspector and SceneView none. ⚠ Not § F9's 110 by definition, not by miss: F9 also counted runtime
  components a shell names (`TerrainComponent` is `Core/`'s) and member names that spell a feature
  (`ModeArt.Blockout`), and its 35 `EditorStrings` ids had already moved (#1301). The vocabulary is
  derived from the feature assemblies; `Host/EditorModules.cs` is the one named false positive,
  exempt in code because a list row for it could never become clean.
* **`ModuleRegistrationRule`** — 18 rows over ten module-bearing projects. ⚠ § F7's six leaks are
  there and so are four it did not name: `BlockoutMode`, `TerrainMode`, `FoliageMode` and `WaterMode`
  put 25 commands straight into the shell, and three Texturing views install the module's sheet
  (✅ #1486: `TexturingModule.Activate` loads it through `context.AddStyleSheet`; the rows are gone).
* **`ContributionWriterTests`** — **sixteen** kinds read through `IEditorRegistry`, not twelve (the
  four interface seams `ITerrainScene`, `IVegetationScene`, `IWaterScene`, `IPlaySystems` were not
  counted); § F8's six unwritten kinds confirmed exactly.
* **`ModuleReachTests`** — ⚠ **found that Blockout's five Scene submenus reach nobody**: the modules
  activate in `Commands()` → `RegisterModes()` before `SceneMenu()` creates the Scene menu, so
  `FindMenu` answers null ([#1483](https://github.com/Rikarin/Vixen/issues/1483)). And Terrain's
  unload takes back nothing a user sees, so its reach is recorded under `editor`. The disabled-module
  negative half waits on #1288.
* The three one-line truths (#1282): the Core README now says `Vixen.Ui` is referenced, for
  `IUndoManager` alone; the App README's two load-order reasons are corrected; and `EditorObject`,
  `EditorProperty<T>` and `SetPropertyCommand<T>` are deleted. ⚠ "Five uses, all in Core" was short by
  the forty in `Vixen.Editor.Core.Tests` — the command stack's merge, seal and transaction tests drove
  it through an `EditorObject` — so those now go through `EditProperty` and `SetValuesCommand`.

### P1 — The generator and the first attribute authors (≈ 1 EM)

`Vixen.Editor.Plugin.Generator`; `EditorMenuAttribute.Title`; `DeclaredParityTest`. Migrate
**Blockout** first — 11 registrations, the smallest clean module — then **Water** (11) and
**Terrain** (22). Each migration deletes the module's hand registrations and is proved by the
`ReachTest` staying green with the attributes and going red without them.

The same three migrations close § F7's leaks as they go — `context.AddPanel`/`AddMode` instead of
`Shell.*` (#740), `TerrainPhysics` publishing `ITerrainColliders` through `Owns` rather than
`Services.Add`, the three theme installs released on unload — and `PluginHost.Activate` learns to
consult `suppressed`, so the plugin manager's Disable means the same thing for a built-in as for a
disk plugin (Part 1 § F1's README correction).

**Exit:** three modules with zero hand registrations and zero `ModuleRegistrationRule` exemptions;
the seven attributes have ≥ 3 production users each or a stated reason one cannot (`[DrawGizmo]`
may have one); `DeclaredParityTest` green; a built-in module disabled in Preferences is absent from
the next session's menu, asserted by the `ReachTest`.

### P2 — The services, one context at a time, in place (≈ 3 EM)

The extraction. Order by Part 1 § F1's *cheap moves* first — the selection verbs, the entity verbs,
the outliner and the project chooser are self-contained clusters totalling ~1,100 lines and move
without touching another concern — and then by what a plugin most plausibly wants:

| Step | Service | Out of | Moves with it |
|---|---|---|---|
| 2a | `IPlaySession` | `EditorParity.cs:855-953, 2917-3035@0d7dda4a9`, `PlayPhysics.cs`, `DrainPlayDiagnostics` | the smallest; proves the shape. *Landed (#1289): `IPlaySession` extends `IActivePlaySession`; `EditorPlaySession` owns the verbs, the debug draw, the overlays and the gap report; `PlayModule` registers the transport. Not moved: `PlayPhysics` (a contribution, not the session), the Play ▸ Options lines and the toolbar strip; no ceilings lowered, because `ApplicationSizeRule` (#1279) was not on this branch* |
| 2b | `ISceneEditing` | `EditorParity.cs:684-855, 2446-2913@0d7dda4a9`, `EditorSelectionVerbs.cs`, `EditorApplication.cs:4542-5737@28c44db26` | `SceneEntity.cs`; the gizmo recording decision (§ P5); **`Active` is `EditorProject.ActiveDocument` and the 124 reads of the `scene` field become reads of it** (Part 1 § F10's first pair); a visibility command for hidden/locked (#1277). *First slice landed (#1290): the entity verbs block and `SceneModule`. ⚠ Refuted: `ActiveDocument` is not the active scene — opening an asset editor activates its document (`EditorApplication.Open`'s `project.Activate(document)`), so `Active` stays the active scene; and the `scene` reads re-measure at ~350, not 124. ⚠ Hidden/locked are non-undoable by a recorded decision (`EntityCommands`' remark), so a command for them is Jiu's call, not this step's. Second slice: Part D's selection and transform verbs (isolate, select by name/type, selection sets, reset/copy/paste transform, align, distribute) are `ISceneEditing` methods taking what their dialog asked; the four that ask nothing are `SceneModule`'s and the six that ask stay with the application's chooser, so `EditorSelectionVerbs.cs` is its dialogs (544 → 243 lines), not deleted. Third slice: `ChooseAsync` is `EditorShell`'s, so the six are `SceneModule`'s (`SceneQuestions`) and `EditorSelectionVerbs.cs` is deleted; the create verbs are service methods and their fifteen commands the module's; `ModuleReach.txt` moved 40 lines to `vixen.scene` and lost none. Fourth slice: which scenes are open and which is active are the service's (`Scenes`, `Activate`, `ActiveChanged`); the application's `scene` field is written only by `Followed`, its handler, so a plugin activating a scene moves the editor; opening from a file and closing (a file and a question) stay the application's. ⚠ Closing the active additive scene left the editor on the closed document (#1498, fixed here). The outliner is `SceneOutliner`, built and registered by `SceneModule` (its panel and Window line moved to `vixen.scene` in `ModuleReach.txt`, none lost); its filter and order are the workspace's `OutlinerState` (`IWorkspace.ViewState`), and the application's ~20 `hierarchyStale = true` are `SceneEditing.Restructured()`. ⚠ A reopened outliner kept filtering under an empty filter box; the box is filled from the state now. Not yet: `SceneCommands`/the Scene and outliner menus' own registration, and the Scenes panel* |
| 2c | `IContentLibrary` | `EditorParity.cs:411-684, 1545-2304@c8b280993`, `EditorApplication.cs:3520-3645, 4868-5052@c8b280993` | `AssetCollections`, `ThumbnailCache`, `ContentTasks`; the `NewAssetKinds` literal becomes `LibraryModule`'s. **First slice landed (#1292):** `IContentLibrary` in `Vixen.Editor.Assets` with import (dialog and OS drop), closure, package export, collections and saved filters; `ContentLibrary` in the App; published through `PluginPoints`. ⚠ The two *files* stay — a collection is per project and a filter per user, both types' documented decision — and what was doubled was the API. **Second slice (#1292):** `LibraryModule` (in `Vixen.Editor.App`, activated by `EditorModules.Standard()` before the feature modules) registers the built-in kinds through `PluginContext` — 19 since #1287 took the terrain layer, foliage and grass types to the terrain module, 22 when this slice landed — and the three reference walks (`assets.find-references`, `edit.find-references`, `assets.select-dependencies`) over `IContentLibrary.Referrers`/`Dependencies`; `CreateAsset` is `IContentLibrary.Create`. `ModuleReach.txt` moved 47 lines from `editor` to `vixen.library` and lost none. ⚠ The `FeatureNameRule` rows *moved* with the literal rather than leaving the shell — the module is still in App, as § D3 says modules start. **Third slice (#1292):** `IContentLibrary.Rename`/`Move`/`Delete`/`ReimportAll` are the application's `RenameAsset`, two `Move` loops (the drop and Move To) and `DeleteSelectedAssets`' loop, moved, with `Rearranged` raised after each — refused or not — for the browser to redraw. `LibraryModule` registers Delete (asking through `Shell.Dialogs`, with the referrers in the question), Show in Explorer (over `IExternalLinks`) and Reimport All; `ModuleReach.txt` moved those 3 lines from `editor` to `vixen.library` and lost none. Rename and Move To stay registered by the application — one starts the browser's inline editor, the other asks through the drawn chooser — and end in the service. None is undoable, as none was (`ContentLibraryTests` holds Undo unchanged across a rename); a rename keeps the GUID because the `.meta` moves with the file, asserted across a reopen. ⚠ Move To's success line now names the folder rather than its path — one body for both doors, and the drop's wording won. **Fourth slice (#1292):** Open is `LibraryModule`'s, over the published `IWorkspace.Open`, which asks `IDocumentHost.TryOpen`; `ModuleReach.txt` moved that one line. ⚠ Refuted: History never needed the document host — it opens the revisions panel over the source-control provider, both the application's. Not yet: History and Revert (no source-control service), `ThumbnailCache`/`ContentTasks` |
| 2d | `IProjectSession` | `EditorProjects.cs`, `EditorApplication.cs:1695-2116@c8b280993` | `ProjectAssemblies`, `ProjectHistory`, the watcher. **First slice landed (#1293):** `IProjectSession` in `Vixen.Editor.Core` with `Rescan`, `RequestProject`/`PendingProject`; `ProjectBrowser` asks it rather than scanning and saving the database itself, and the disk watcher's closed-browser copy of the same three calls is gone. ⚠ The browser never had `EditorProject.Open`'s save-only-if-changed guard and still does not — an in-editor rename changes the database in memory and then rescans, so a guard measuring the scan alone would leave a stale index; that is a decision, not a move. **Second slice (#1293):** `Recent` (a `RecentProject` list — the record moved into Core), `CreateProject` (New Project's scaffold) and the newer-engine warning are the session's, and the constructor records the open project through `ProjectSession.Opened`; `EditorApplication.Recent` is gone. **Third slice (#1293):** the open (`ProjectSession.Open` builds the `EditorProject` the constructor used to), the watcher (`Watch`, `FollowDisk`, `IsWatching`, `ChangedOnDisk`) and the code build (`BuildCode`, `CodeBuilt`, with the behaviour save/restore and `ProjectAssemblies`) are the session's; the application keeps what a change means to its panels (`FollowedDisk`: shader reload, browser redraw, sweep, thumbnails, open documents) and calls `FollowDisk` once a frame. `ProjectSessionTests` counts `ChangedOnDisk` on `Require<IProjectSession>()` — one burst per write, one rescan per burst, indexed before it is raised — and asserts the application holds no watcher and no `ProjectAssemblies`. ⚠ Found: the scan's own `.meta` write echoes back as a second burst (#1488), and the moved build strips the scene's behaviours when the project has no code or the build fails (#1490) — both pre-existing, filed, and now fixed: `ScanReport.Written` names every sidecar a scan writes or moves and `Rescan` suppresses each on the watcher (one outside add is one burst, asserted), and a build takes off only the loaded project assembly's behaviours, puts them back on every path, reloads the last good build when a rebuild fails, and otherwise holds them and refuses to save the scene. **Fourth slice (#1293):** `IProjectSession.Code` is an `IProjectCode` (the `.csproj` and the loaded assembly, read-only) over the App's `ProjectAssemblies`; `ExternalEdits` moved with the watcher (`Watch(assets)` builds it after the watcher — one of the constructor's order-dependent steps as a line — and `FollowDisk` routes to open documents after `ChangedOnDisk`), and `EditorProject.SaveFailed`'s report is the session's. ⚠ The constructor is 492 lines (513 before): what remains is panels, documents, plugins, play and content, not the project. Not yet: the startup chooser's dialog (`ShowProjectBrowser`, `ChooseAsync`, `Picked` — this editor's drawn dialogs, which keep `EditorProjects.cs` alive) |
| 2e | `IDocumentHost` + `IWorkspace` | `EditorApplication.cs:1447-1515, 2236-3060, 3657-3734@c8b280993` | § D4's two registries; `Inspecting`. *First slice landed (#1294, decision (a)): `DocumentKind` + `IDocumentHost`/`DocumentHost` in `Vixen.Editor.Core`, `DocumentView`/`DocumentView<T>` + `IWorkspace`/`EditorWorkspace` in `Vixen.Editor.Ui` (taking `IEditableDocument`, #656's port), the 28 factories split, `AssetEditorRegistry` and `IAssetEditorFactory` deleted, both published to plugins; `DocumentPairingTests` (App, by effect, modules included) and `StandardDocumentPairingTests`. ⚠ Refuted: "keyed by document `Type`" alone — the scene and prefab kinds both open the sealed `SceneDocument`, so a view also says which documents it `Owns` (by writer); and one kind can open two types (markup: `MarkupDocument`, `StyleSheetDocument`), so `DocumentKind.DocumentTypes` is a list the host enforces. Second slice (#1294): `Place`, the asset panel id's format and parse (`EditorWorkspace.PanelOf`/`TryReadPanel`), `SaveLayout`, `ReloadStyles` and the theme tokens the Appearance page caches are `IWorkspace`'s, over the shell and the user store; `WorkspaceModule` registers Save Layout and Reload Styles over it (#1296), and `IWorkspace.Recent` is the editor's File ▸ Recent Documents (#656). ⚠ Refuted: "§ F1's parked fields move onto `IWorkspace`" — `Vixen.Editor.Ui` refuses `Vixen.Editor.Core`, and the browser, outliner, inspectors, picker, probe, chrome and cameras are `Vixen.Editor.App`/`.Inspector`/`.SceneView` types an interface there cannot name; they are their own contexts' models (this document's own `IContentLibrary.Shown`, `IDiagnostics.Console`), and the theme text, the one that is the Workspace's, moved. Third slice (#1294): the asset tabs are `AssetTabs`, a panel model in `Vixen.Editor.App` — `Open`, `ReopenDocument`, the two parked tab fields, and the file questions over them (`TryGetOpenScene`, `EditedElsewhere`, `RefuseSecondDocument`, `CloseTabsTheSceneLeft`); `EditorApplication` keeps one-line forwarders, so the scene verbs' call sites did not move. Each tab installs `DocumentClosePrompt` (#1501). ⚠ Refuted: `Joined` is owed — it no longer exists. Not yet, and staying for now: `Inspecting` (the inspector's per-visit pair, its preferences and selection following — an inspector model's, not the workspace's) and `Configure`/`RememberView`/`RestoreView` (the viewport panes' memory — `SceneViewport` is a `Vixen.Editor.SceneView` type `IWorkspace` cannot name); `Panels()` registers the console, the two inspectors and the UI diagnostics panel, each its own context's*
| 2f | `IPlayerDelivery` | `EditorBuilds.cs`, `AnalyseContent` | the deploy contribution (frees `Debugger`). *Landed (#1295): `IPlayerDelivery` in `Vixen.Editor.Core` (beside `PlayerBuildSettings`, not `Assets.Content` — nothing in it is a pipeline type and the deploying module does not reference the pipeline); `PlayerDelivery` is `EditorBuilds.cs` moved, which is deleted; `DeliveryModule` registers the Build menu and panel. ⚠ Refuted: the device records did not need rehoming. Deploy's per-kind switch is the device manager's vocabulary, so it moved into `DiagnosticsModule` and `IDeviceDeploy` was deleted rather than moved — the application names no device type. ⚠ Found: Deploy had been refused on every buildable project (`deployer?.Refuse(d) ?? "cannot build"`). Not moved: `AnalyseContent`, which is the content library's* |

Each step: the service, its tests without a window, its line in `PluginPoints()`, its
`ApplicationSizeRule` ceilings lowered in the same commit, and the module (§ D3) that registers its
verbs through attributes — so `EditorParity.cs` empties as `SceneModule`, `LibraryModule`,
`PlayModule`, `DeliveryModule`, `WorkspaceModule`, `HelpModule` fill.

⚠ **One step per branch, merged as it lands; never two services in one worktree.** They all edit the
constructor, and the merge conflict is the whole cost.

**Exit:** `EditorParity.cs` deleted; `EditorApplication.cs` ≤ 800 lines by the rule and not by a
claim; six services published; every verb reachable by `Require<T>()` and asserted by
`ServiceReachTest`.

### P3 — Finish doc 36 § P3 (≈ 0.75 EM)

With § D4 and § 2f done: `AssetEditors` (through `DocumentKind`/`DocumentView`), `Debugger`
(deploy is a contribution; `IDeviceDeploy` and its three records move to `Vixen.Editor.Core` or
the delivery context), `Profiler` + `Diagnostics` (the report moves into `DiagnosticsModule` once
the log ring and data directory are published — doc 36 already names both), `NodeGraph`
(`NodeGraphTheme.Install` becomes a `UserAgentSheet` contribution, #917). `Assets` drops
`ShaderGraph`/`VfxGraph` by the graph importers moving beside their documents, registered through
`ImporterContributions` like a plugin's — and `BuiltInImporters.cs:57-99@3e9f7e5d9`'s hand list of 34 shrinks
to the kinds the pipeline itself owns, with each feature's importers arriving from its module.
`Host` stops building `ShaderGraphPreviewRenderer` and loading the terrain stages itself
(`EditorHost.cs:581, 1033-1047@2e0aa771a`); both become what the modules publish. ⚠ **Partly done**: the pane
reads the terrain, vegetation and water scenes from the registry every frame rather than the host
copying them on (#1299's third point — it was also what kept an unloaded module reachable), and
`NodeGraphTheme` is `AssetEditorsModule`'s through `context.AddStyleSheet` (#1484), so `NodeGraph` has
left `NotYetMoved` — ⚠ a context wrapper rather than a `UserAgentSheet` registry kind, because a kind
needs a reader diffing sheets on `Changed` and the wrapper reaches the same exit with no new reader.
⚠ **The preview renderer is `AssetEditorsModule`'s now** (#1299, decision (a)): `IEditorGraphics.Show`
names a GPU view over the thumbnail surface's renderer, the module builds the renderer in `OnUpdate`
once there is a device and gives it back in `OnDeviceLost`, and — a member the decision did not name and
could not work without — `PluginContext.OnFrame` runs it inside the host's frame, because a command list
recorded from the editor's update races the next `BeginFrame`'s pool reset. `EditorHost` has no `using
Vixen.Editor.ShaderGraph`. The terrain stages stay in the host, decided-not-now. ⚠ **And the import
pipeline references neither graph assembly** (#1300, option A): `VfxImporter`, `ShaderGraphImporter` and
`ShaderGraphSources` live beside their compilers and each host (`EditorModules`, `VixenCommand`,
`WorkerHost`) links them into `ImporterContributions.Linked`, which `CompilerPool` never forwards;
`EditorEffects` asks an `IEffectSourceProvider` the module publishes rather than calling the compiler;
`HostImporterMap` pins all three hosts' extension → importer maps to one literal list, and a sidecar
naming an importer the host lacks is refused rather than imported as bytes. ⚠ **The terrain (2), water
and AI (4) importers followed** (#1300's remainder): `TerrainImporters`/`WaterImporters`/`AiImporters`.Link
in each host, `HostImporterMap.LinkedOnly` grown to match, every moved importer's bytes pinned in this
process and in a worker (`FeatureImporterBytesTests`) — and a first import with no sidecar, in a host
missing a link, is now a warning naming the format (the fallback speaks about any unclaimed `.vx…`,
plus `.r16`/`.hmpng`), which the sidecar refusal could not see. ⚠ **The rest are pipeline-owned, and
#1300 is complete on that list** (#1499, decided): animation ×7, gameplay definitions, network rules,
the compositor frame and the audio mixer each import a format a *runtime* assembly owns —
`Core/Vixen.Animation`'s content types (`.vxanim` is spelled there), `Vixen.Gameplay`'s `Definition`,
`Vixen.Net.Engine`'s `NetworkRulesAsset`, `Vixen.Rendering`'s `GraphicsCompositorAsset`, `Vixen.Audio`'s
`MixerAsset` — and what this phase moves out is an editor feature's importer. They stay in
`BuiltInImporters` with that reason at the registration and on the two `FeatureNameExempt` rows they
leave (`.vxcompositor`, `.vxmixer`); nothing moved. `HostImporterMap` stays the instrument, and
`FeatureImporterBytesTests` still pins the animation seven's bytes, so a later move — doc 28's
`Vixen.Editor.Gameplay`, if it is ever built — is held to them.
`.vxtexgraph`/`.vxlayers` are claimed by the pipeline's `EditorOnlyImporter` (#1491): recipes nothing at
run time reads, so nothing ships. The 35 feature string ids
in `EditorStrings.cs` move to the `*Strings` class that already exists for each — `CheckStrings`
fails on an id declared and used nowhere, so the move cannot leave a stale one behind.

**Exit:** `NotYetMoved` is empty and the rule says so (it must be deleted rather than left empty —
an empty shrink-only list asserts nothing). ✅ **Met (#1297, #1302).** `AssetEditors` left last,
through § D4's two registries: the module registers the twenty-six kinds and views, the stylesheet,
the Addressables panel, the analyser and the sequence's scene; the frame reaches the viewport as
`IViewportFrame`, the VFX graph and the sequence tick as `ITickingView`, the texture previews are the
module's through `IEditorGraphics` (which gained `SurfaceChanged` so a module hears the surface
arrive), the shader graph previews are handed by the host to the module, and `Prefab`/`PrefabSource`
moved into the application — they named only shell assemblies. `Vixen.Editor.App.csproj` no longer
references the assembly, and `NotYetMoved` is deleted: `ApplicationReferenceRule` is `Allowed` and
nothing else. ⚠ Refuted on #1297: the issue's "`PrefabSource` via `IPrefabSource`" — the application
also calls `Link`/`Unlink`/`Clear` and constructs one, which the interface does not carry, and the
type needs nothing a feature owns, so it moved rather than hid behind the interface. ⚠ Found: the
weak-reference probe written for the module (`AssetEditorsModuleReachTests`) was red on the first
try — a material view's handler, written as a lambda in the same method as a call to an instance
method, shared a closure holding the module, and the tab outlives the module.

### P4 — The feature-side god files (≈ 2 EM, parallelisable per feature)

The same three moves inside each feature: model out of the view, verbs onto a service, registration
through attributes. Targets in order of what a user hits: `SceneViewport.cs` (1,668 — the thing every
tool is handed; doc 36 § Part 5 says narrow it *when a consumer can say which members matter*, and
after P2 the consumers can), `ProjectBrowser.cs` (1,491), `NodeGraphView.cs` (1,392),
`LayerStackView.cs` (3,783) and `TexturingModule.cs` (2,381), `BlockoutMode.cs` (1,462),
`ComponentsView.vxml` (1,117). ⚠ `NodeGraphView`'s selection is `NodeGraphModel.Selection`, a
`Selection<NodeId>`, since #1305, and its on-node port editors write through
`NodePortEditProvider`'s `InspectorField` as the side panel does; the row is the canvas's
`PortEditor` unless `DrawerRegistry` resolves the port to a drawer the built-in set would not, which
is then hosted on the node (`PortEditorKind.Hosted`, decision (b)) — a graph with no custom drawers
builds nothing during a pan (`PortDrawerTests`). ⚠ `ComponentsView`'s
"one template per authoring kind" does not exist: there is one keyed `@for` over `IComponentBridge`,
deliberately the same for components and behaviours, and the size is a 945-line `@code` block — so
"split by component kind" has nothing to split along. #1308 decided (c) then (d): `IPrefabSource` gained `Link`/`Unlink` as default no-ops and `Pair` takes the interface, so `ComponentsView` names no `Vixen.Editor.AssetEditors` type (four `FeatureNameExempt` rows gone, the dumps unchanged); the size is closed as decided-not-to, because the only split is by concern. Each is its own issue with its own ceiling in `ApplicationSizeRule`'s
table, which is renamed `FileSizeRule` the day it gains a second assembly.

**`ProjectBrowser` (#1304), first slice landed:** the folder, collection, search and kind are
`IContentLibrary.Shown` (`ShownContent`), so they survive the panel being closed and reopened, and
Save Filter reads them from the library rather than out of the view; the rescan is
`IProjectSession`'s (#1293). ⚠ *"Its second asset tree"* is refuted: `AssetTree.Build` has exactly
one production caller, the browser, and the database holds a flat list — the tree is a projection,
not a second copy of state, and moving it into the service would need a database change signal
`AssetDatabase` does not have (and would create the second copy the finding warned about). **Second slice:** the grid toggle and the tile size are `Shown.Grid`/`Shown.TileSize` too — read from and written to the preferences through delegates `ShownContent` is built over, asked on every read because Revert replaces the preferences object — so `ProjectBrowser.ViewChanged`/`TileSizeChanged` and the factory's restore are gone. ⚠ A first version copied the two in one setter at a time and wrote a half-restored view back (the toggle's change fired while the size still held its default); the restart half of `BrowserShownTests` caught it, and the copy is gone rather than ordered. **Third slice:** the panel is built over `IContentLibrary` rather than its `Shown` alone, and calls it — rename, move by drop, OS-drop import, add to / remove from / forget a collection, forget a filter — so `Renamed`, `Moved`, `FilesDropped`, `CollectionAdded`/`Removed`/`Forgotten`, `FilterForgotten` and the `Collections`/`SavedFilters` lambdas are gone (`ProjectBrowser.cs` 1,490 → 1,438). What still leaves as an event needs something the panel is not: a name typed first (Save Filter, a new collection), a document (Open) or another panel (a drag out). `ContentLibraryTests` counts a rename typed into the row on `Require<IContentLibrary>()` — red, with the OS-drop reach test and two `BrowserFileDropTests`, when the panel is handed a private second library. **Fourth:** the kind restore has a test of its own (`BrowserShownTests.The_kind_survives_closing_the_panel`, red when `Refilter` reads its own dropdown rather than `Shown.Kind`), and `ProjectBrowser.cs` has a ceiling — `ApplicationSizeRule.Named` holds a file outside the class to a row of its own (1,438) and keeps it out of the class's total, which is this section's "own ceiling in `ApplicationSizeRule`'s table" for the first P4 file.

**`LayerStackView` + `TexturingModule` (#1306), the three moves:** the ten Tools verbs are
`ITexturing`, which the module publishes as itself with `PluginContext.Publish` — itself rather than a
class beside it, because the verbs read the open documents, the baker, the brush and the armed force-bake,
which are the module's state; the bodies moved unchanged into `TexturingVerbs.cs` (`TexturingModule.cs`
2,381 → 1,744 with the declared lines; `LayerStackView.cs` grew 3,783 → 3,861 with the follow). The Tools lines are `[EditorMenu]` on the command ids, registered by the generated
`Declared.Register` only when the descriptor has no assembly path, so the module holds whether #1276
makes it a `Standard()` module or a scanned disk plugin. The chosen set (`PaintSet`, now a signal) and
the selected layer (`LayerSelection`, a `Selection<LayerPath>`) are the document's; the view reads them,
follows a write it did not make, and the module evaluates `stack.PaintSet` rather than asking the view.
⚠ *"Four `LayerStackDocument?` copies"* is three-quarters refuted: `framed` was `built` under another
name and is gone, but `bound` survives an empty show that `built` does not and `watched` is the
subscription's. ⚠ *"The 26 registration sites"* were already through `PluginContext` — what was left
round the side is the three `TexturingTheme.Install` calls, which no wrapper can record (§ D6). ✅ Since
#1484 one can, and #1486 used it: the module loads the sheet and an unload takes it out.

### P5 — The gizmo decision (≈ 0.25 EM, a decision before a change)

Doc 36 § Part 6 measured the two options — a recording entry point (`EditProperty.Record(before,
after)`) or per-frame writes coalesced by `SetValuesCommand.TryMergeWith`. **This document
recommends the first**: *"the surface applied it and is telling you"* is a real case, and `ISceneEditing`
is its first non-gizmo customer (a script that moved an entity wants the same entry). It lands in 2b.
*Landed (#1291): `EditProperty.Record(before, after, name)` and `CreateRecordCommand`, with
`IEditMember.CreateRecordCommand` defaulting to `RecordedValuesCommand`; the entity gizmo records
through it with its own N-target `TransformTargetsCommand`.*

---

## Part 5 — What this document does not do

* **It does not move the command system.** Docs 45/46/49 own that; this document registers into
  whatever they leave and calls whatever they resolve.
* **It does not add a DI container, a mediator or a message bus.** `PluginServices` by name and
  `IEditorRegistry` by kind are the two lookups, and they are enough.
* **It does not turn every feature assembly into a disk plugin.** In-process modules through
  `Activate` are the front door; #1276 decided Texturing is one of them, so there is no disk-shaped
  exception.
* **It does not redesign `.vxml`, the inspector, or the editing pipeline.** It gives them more
  callers.
* **It does not touch the runtime**, and it makes no change under `Core/` except a possible new
  home for `IDeviceDeploy`'s records if the delivery context wants one.

---

## Part 6 — The task list

Filed on `Rikarin/Vixen` 2026-09-17 under the `bounded-contexts` label — thirty-four issues, #1276–#1311.
Waves follow the phases; a wave is five worktrees, merged as each lands, gates once on master.

| # | Task | Phase | Wave |
|---|---|---|---|
| [#1278](https://github.com/Rikarin/Vixen/issues/1278) | `FeatureNameRule` + measured exemption list (110 rows; re-measured 109 — § P0) | P0 | 1 · landed |
| [#1279](https://github.com/Rikarin/Vixen/issues/1279) | `ApplicationSizeRule` with per-file ceilings, shrink-only | P0 | 1 · landed |
| [#1280](https://github.com/Rikarin/Vixen/issues/1280) | `ReachTest`: every standard module's Create ▸ / menu / panel present, by effect | P0 | 1 · ✅ both halves: the measured manifest, and a module switched off in the plugin manager reaching nothing in the next session (red with `Activate` ignoring the suppressed set) |
| [#1281](https://github.com/Rikarin/Vixen/issues/1281) | `ModuleRegistrationRule` + `ContributionWriterTest` | P0 | 1 · landed |
| [#1282](https://github.com/Rikarin/Vixen/issues/1282) | Three README/dead-type corrections (Core `Vixen.Ui` line; App load-order reasons; delete `EditorObject` family) | P0 | 1 · landed |
| — | [#1276](https://github.com/Rikarin/Vixen/issues/1276) `TexturingModule` reaches nothing — decide and close | P0 | 1 · ✅ in `Standard()`; `TexturingModuleReachTests`, 22 manifest lines gained |
| — | [#1277](https://github.com/Rikarin/Vixen/issues/1277) Blockout boolean undo leaves operands hidden | P2b | 1 · filed · ✅ the hide is a recorded command in the transaction; the delete-undo half of the same shape is [#1482](https://github.com/Rikarin/Vixen/issues/1482), ✅ the delete's snapshot now carries the marks — and the mesh, shape, materials and boolean, which it was losing too |
| [#1283](https://github.com/Rikarin/Vixen/issues/1283) | `Vixen.Editor.Plugin.Generator`: the eight attributes → `Declared.Register` | P1 | 2 · ✅ nine attributes (with `[EditorSubmenu]` and `[EditorIcon]`; `[Importer]` stays the pipeline's); placement shared through `DeclaredMenus`; not packed — a disk plugin gets the scan |
| [#1284](https://github.com/Rikarin/Vixen/issues/1284) | `EditorMenuAttribute.Title` as a `*Strings` member name; `DeclaredParityTest` | P1 | 2 · ✅ `[EditorMenu]`/`[EditorSubmenu]` and `DeclaredParityTests`; then decided (a): `NewAssetKind.Title` and `SceneOverlay.Title` are `StringId`s, `[CreateAssetMenu]`/`[Overlay]` take `Title`/`Strings` by the same rule (`VXP0003`, a refused plugin), and a live language switch relabels a Create ▸ line, the New Asset… chooser and an overlay header (`ContributionTitleLanguageTests`). ⚠ The menu-bar line was already live through a run-time id no template held; the chooser and the header were frozen |
| [#1285](https://github.com/Rikarin/Vixen/issues/1285) | Blockout → attributes, and through `context.AddPanel`/`AddMode` | P1 | 2 · 🟡 menus declared, panels and theme through the context — the theme removed on unload since #1484, the sheet count back to baseline; ⚠ its submenus had been absent from the shipped editor (modules activated before the Scene menu existed). ✅ The mode's own commands stay `IEditorMode.Register`'s, decided (a): that contract *is* a front door — `context.AddMode` invokes `Register` and the recorded removal's `EditorModes.Remove` calls `Unregister` — so the four mode rows in `ModuleRegistrationExempt.txt` carry that reason rather than a debt |
| [#1286](https://github.com/Rikarin/Vixen/issues/1286) | Water → attributes, `WaterDebug` static becomes module state | P1 | 2 · 🟡 through the context; ⚠ no menu to move — the module never built one, its debug switches are palette-only (a remark claiming "the mode's menu" was never true), and the sea state's Create line reads the panel's current state, which `[CreateAssetMenu]` cannot. ✅ Placement decided: Scene ▸ Water ▸ Debug, six checkable lines declared by `[EditorMenu]` on the command ids, ticked from the session's `WaterDebugFlags`; `WaterModuleReachTests` holds them once each and gone on unload (red with `Declared.Register` removed) |
| [#1287](https://github.com/Rikarin/Vixen/issues/1287) | Terrain → attributes; #740 closed; `TerrainPhysics` publishes via `Owns` | P1 | 2 · 🟡 through the context; ⚠ no menu to move either — what was attribute-shaped was three Create ▸ kinds held by the *application*, now `[CreateAssetMenu]` on `TerrainAssetKinds` (the attribute's first in-tree user). The modes' commands stay `IEditorMode.Register`'s, as #1285's |
| [#1288](https://github.com/Rikarin/Vixen/issues/1288) | `PluginHost.Activate` honours `suppressed`; Disable works for built-ins | P1 | 2 |
| [#1289](https://github.com/Rikarin/Vixen/issues/1289) | `IPlaySession` out of `EditorParity` + `PlayPhysics`; `PlayModule` | P2a | 3 |
| [#1290](https://github.com/Rikarin/Vixen/issues/1290) | `ISceneEditing`: selection + entity verbs + outliner; `Active` = `ActiveDocument`; `SceneModule` | P2b | 3 · three slices landed (the entity verbs; Part D's selection and transform verbs; the dialog verbs through a shell `ChooseAsync`, and the create verbs). The active scene and the open-scene list are the service's (fourth slice, #1498 fixed); the outliner is `SceneModule`'s (`SceneOutliner`); the Scene menu (`SceneMenuBar`), the Scenes panel and Open Scene Additively / Close Scene (`ScenesPanel`, over `ISceneEditing.OpenAdditively`/`Close`) are `SceneModule`'s (fifth slice); `Active` = `ActiveDocument` was refuted; hide/lock stay non-undoable (decided) |
| [#1291](https://github.com/Rikarin/Vixen/issues/1291) | Gizmo recording entry point (`EditProperty.Record(before, after)`) | P5 → 2b | 3 |
| [#1292](https://github.com/Rikarin/Vixen/issues/1292) | `IContentLibrary`: browser model, collections + filters in one store, import, drop; `LibraryModule` owns `BuiltInAssetKinds` | P2c | 4 · 🟡 four slices; Open through `IWorkspace`/`IDocumentHost`; History and Revert wait on a source-control service |
| [#1293](https://github.com/Rikarin/Vixen/issues/1293) | `IProjectSession`: open/recents/scaffold/watch/build; `ProjectBrowser` stops calling `Scan`/`Save` | P2d | 4 |
| [#1294](https://github.com/Rikarin/Vixen/issues/1294) | `DocumentKind` / `DocumentView` split + `DocumentPairingTest`; `IDocumentHost`, `IWorkspace`; panel state moves to models | P2e | 5 · first slice landed: the split, the two registries, the pairing test; second slice: the arrangement's verbs (`Place`, the panel id, Save Layout, Reload Styles, the theme tokens) on `IWorkspace`. ⚠ The other parked fields cannot sit in `Vixen.Editor.Ui` (it refuses Core) and are their contexts'; third slice: the asset tabs are `AssetTabs` (App), and a dirty tab asks before it closes (#1501). `Inspecting`, the viewport panes' memory and `Panels()` are owed |
| [#1295](https://github.com/Rikarin/Vixen/issues/1295) | `IPlayerDelivery`; deploy as a contribution; `IDeviceDeploy` records rehomed | P2f | 5 · ✅ `IPlayerDelivery` + `DeliveryModule`; ⚠ the records stayed — the per-kind switch moved to the module and `IDeviceDeploy` was deleted |
| [#1296](https://github.com/Rikarin/Vixen/issues/1296) | `HelpModule`, `WorkspaceModule`; `EditorParity.cs` deleted | P2 | 5 · 🟡 `HelpModule` (the six Help lines over a published `IExternalLinks`) and `WorkspaceModule` (six layout presets through `AddLayout`; Profiling moved to `DiagnosticsModule`, whose panels it names) landed. ⚠ `EditorParity.cs` is not empty and is not deleted — file, edit, asset, entity, play-option and tool verbs, the menus and the toolbar are the other P2 steps' — and Save Layout / Reload Styles are `WorkspaceModule`'s over `IWorkspace` since #1294's second slice, with File ▸ Recent Documents (#656); the issue carries the list |
| [#1297](https://github.com/Rikarin/Vixen/issues/1297) | `AssetEditors` off `NotYetMoved` through 17 | P3 | 6 · landed: the App no longer references it |
| [#1298](https://github.com/Rikarin/Vixen/issues/1298) | `Profiler` + `Debugger` + `Diagnostics` off: report into the module, log ring and data dir published; typed module fields gone | P3 | 6 · ✅ all three off `NotYetMoved`; the device and GPU frame through `IEditorGraphics.Frame`, the ring as `RingBufferSink`, the directory as `EditorDataDirectory`, the pickers as `INativeDialogs`. ⚠ "Asserted by the existing report tests" — there were none; `Report` had never been read by a test. `FindModule` stays for the asset editors (#1297) |
| [#1299](https://github.com/Rikarin/Vixen/issues/1299) | `NodeGraph` off: theme as a contribution (#917); `Host` stops building the shader-graph preview and terrain stages | P3 | 6 · ✅ theme (`AssetEditorsModule`, through `context.AddStyleSheet`; `NodeGraph` off `NotYetMoved`), pane reads the scene seams, preview renderer `AssetEditorsModule`'s (`IEditorGraphics.Show`, `PluginContext.OnFrame`; device-tested on an RTX 4060 Ti) · terrain stages stay in the host (decided) |
| [#1300](https://github.com/Rikarin/Vixen/issues/1300) | `Assets` drops `ShaderGraph`/`VfxGraph`; `BuiltInImporters` shrinks to the pipeline's own | P3 | 6 · ✅ both references gone (option A: host-linked `ImporterContributions.Linked`, `IEffectSourceProvider`, `HostImporterMap`); terrain, water and AI importers linked too, bytes pinned, first-import gap loud; animation, gameplay, net, compositor and mixer decided pipeline-owned — runtime formats, reason recorded at the registration and on their exemption rows (#1499) |
| [#1301](https://github.com/Rikarin/Vixen/issues/1301) | 35 feature string ids out of `EditorStrings` | P3 | 6 |
| [#1302](https://github.com/Rikarin/Vixen/issues/1302) | `NotYetMoved` deleted; `ApplicationReferenceRule` is `Allowed` only | P3 | 6 |
| [#1303](https://github.com/Rikarin/Vixen/issues/1303)–[#1309](https://github.com/Rikarin/Vixen/issues/1309) | One per feature god file: `SceneViewport`, `ProjectBrowser`, `NodeGraphView`, `LayerStackView` + `TexturingModule`, `BlockoutMode`, `ComponentsView.vxml`, `EditorHost` | P4 | 7–8 · #1307 (`BlockoutMode`) ✅ the verbs are `IBlockout`, published with `PluginContext.Publish` · #1303 (`SceneViewport`) partial: a tool and an `[Overlay]` are handed `IViewportPane`, the eight members its consumers read; ⚠ `Extensions` is already the published registry in the editor (`pane.Extensions = Extensions`), `Default` only a pane's fallback outside one; the split, each pane's camera and its nine bookmarks are the workspace's `SceneViewState` (`IWorkspace.ViewState`), so the bookmarks now survive the Scene panel being closed and reopened, which they did not (the pane owned them) · #1306 (`LayerStackView` + `TexturingModule`) 🟡 all three moves landed — `ITexturing`, declared Tools lines, the chosen set and selected layer on the document — and the three view theme installs stay (#917) |
| [#1310](https://github.com/Rikarin/Vixen/issues/1310) | Settings dirty joins `HasUnsavedChanges`; settings edits through `EditProperty` with a document (#1162's provider) | P2e | 5 · both halves landed: the join, and a `SettingsDocument` with its own stack, hosted by the window and never the active document (decision (a)) |
| [#1311](https://github.com/Rikarin/Vixen/issues/1311) | Guide pages for the seven services and two records (`CheckDocs`, not `Docs`) | each | with its phase · 🟡 the pages for the services that exist ✅; `Vixen.Editor.Plugin`'s first `Shipped` baseline ✅ (the core four, decided (b)); five pages wait on #1294 and the delivery lane |

## Part 7 — Estimate and risk

≈ 7.5 EM sequential; P4 parallelises across features and P2's steps parallelise poorly (all touch the
constructor). The risk is concentrated in P2b and P2c — the two largest verb sets, both with
drag-and-drop and both read by the outliner, the browser and the viewport at once — and the mitigation
is P0: a ceiling that must go down each merge, a name list that must shrink, and a reach test that
says whether the menu still has its lines. A phase that lands without moving those numbers has not
landed, whatever its branch says.
