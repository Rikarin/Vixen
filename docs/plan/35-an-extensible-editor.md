<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 35 — An extensible editor

The editor has a plugin system, an inspector registry, a drawer registry, a markup language and a
hot-reload host. It also has a 3,601-line `EditorApplication.cs` that hard-references twelve feature
assemblies and a hardcoded array of the asset kinds the Create menu offers. Both statements are
true, and the second is why the first does not add up to an extensible editor.

**The claim this document has to earn.** A person who has never opened this repository can add a
panel, a custom inspector for their own component, a property drawer, a Create-menu entry, a
scene-view tool and an asset importer — by dropping a folder into their project, without editing
`Vixen.Editor.App`, without a fork, and without us shipping a new build. If that claim fails, the
honest description of what we have is *a good editor with a plugin API that reaches five of the
fifteen things a plugin needs to reach*.

⚠️ **Extends [11](11-editor.md) and [20](20-editor-parity.md).** Doc 11 designed the plugin model and
doc 20 drove feature parity; this document is about the gap that opened between them — parity was
delivered by wiring features into the application, and each one that landed made the application
less able to accept the next one from outside.

**Read [Part 1](#part-1--what-is-actually-there) before the phases.** The audit is the argument. The
gap is not "there is no extensibility" — it is that extensibility was built for the shell and not for
the content, and every feature since has been added through the back door.

---

## Part 1 — What is actually there

Measured, not recalled. Every claim below is a file you can open.

### The bones that are good

| Piece | Where | State |
|---|---|---|
| Plugin discovery, isolation, lifecycle | `Editor/Vixen.Editor.Plugin` | **Real.** Manifest + `AssemblyLoadContext`, catalog, enable/disable, failure capture, dependency list, a management panel over it |
| Inspector descriptors | `Vixen.Editor.Inspector/InspectorRegistry.cs` | **Real**, and generated — `InspectorDescriptorGenerator` emits `InspectorRegistry.Register`, so there is no reflection scan (ADR-002 holds) |
| Property drawers | `Vixen.Editor.Inspector/DrawerRegistry.cs` | **Real** — by type, by attribute, with a fallback chain and a resolution order |
| Command / menu / keymap vocabulary | `Vixen.Editor.Ui` | **Real** — `EditorCommand`, `KeyChord`, `MenuGroup`, `PanelDescriptor`, `IEditorMode` |
| Declarative markup | `Core/Vixen.Ui.Markup` (+ generators, hot reload) | **Real**, and almost unused — see below |

`Vixen.Editor.Plugin` is the only project under `Editor/` with a `PublicAPI` baseline. Somebody
already decided it was a contract. That instinct was right and this document is mostly about
honouring it.

### The nine findings

**F1 — The application is a god object.** `EditorApplication.cs` is 3,601 lines and
`EditorParity.cs` is 2,163. Together they are 5% of the editor's ~109,000 lines and they are where
every feature is joined to every other.

**F2 — Every feature is compiled in, none is discovered.** `Vixen.Editor.App.csproj` references
twelve editor assemblies by hand: `AssetEditors`, `Assets`, `Blockout`, `Core`, `Debugger`,
`Inspector`, `Inspector.Generator`, `Plugin`, `Profiler`, `SceneView`, `Terrain`, `Ui`. Terrain mode
is not a plugin; it is a project reference. Blockout is not a plugin; it is a project reference.

⚠ **This is the finding that matters most**, because it means the plugin API has never had to be
sufficient. Every built-in feature took the shortcut, so nothing ever proved the front door works.

**F3 — The Create menu is a hardcoded tuple array.**
[`EditorWorlds.cs:744`](../../Editor/Vixen.Editor.App/EditorWorlds.cs) —

```csharp no-compile="the shape, not the whole array"
NewAssetKinds = [
    ("assets.create-shader-graph", "Shader Graph", ".vxshadergraph", "New Shader Graph", "", true),
    ("assets.create-vfx",          "VFX Graph",    ".vxvfx",         "New Effect",       "", true),
    …
];
```

A plugin that introduces an asset type cannot put it in **Create ▸**. It is a literal in the
application.

**F4 — A plugin can reach three services.** `EditorApplication.PluginPoints()` builds a
`PluginServices` containing the project, the scene, and `DrawerRegistry.Default`. That is the whole
surface area behind `PluginContext`.

What a plugin **can** do today: add a command, a menu, a menu item, a panel, a mode, a layout, a
default key binding.

What it **cannot** do: register an inspector for a type; register a drawer through anything but
mutating a static; add a Create-menu entry; add an asset importer; add a scene-view tool, gizmo or
overlay; add a settings page; add a validator; add an asset-preview generator; contribute to the
toolbar; take part in undo.

**F5 — Custom inspectors are compile-time and in-tree only.** The descriptor generator is a
`ProjectReference` analyzer and `Vixen.Editor.Inspector.Generator.csproj` sets no `IsPackable`. An
out-of-tree plugin cannot obtain a generated descriptor, and there is no attribute-scanned runtime
path either. **There is no `[CustomEditor]` equivalent available to anyone outside this repository.**

**F6 — Modes are added imperatively from application code.**
`EditorParity.cs:1154` — `Shell.Modes.Add(new SelectMode())`.
`EditorTerrainPanels.cs:451` — `Shell.Modes.Add(terrain)`.
The mode list is code in the app, not a registry the app reads.

**F7 — The declarative path exists and is not used.** Two `.vxml` files in the whole repository:
`Editor/Vixen.Editor.App/UndoHistory.vxml` and `Editor/Vixen.Editor.Ui/Tasks/TaskCenter.vxml`.
Against ~109,000 lines of hand-written C# UI. `.vxml` is a language we built, shipped generators and
a hot-reload host for, and then did not adopt. Any plan that says "author panels in markup" has to
reckon with the fact that we already can and don't.

**F8 — Importers are constructed and handed in.** `ImportPipeline(database, importers, artifacts, …)`
— whoever builds the pipeline decides what importers exist. There is no registry for a plugin to
add to, which is why doc 11's "a plugin can add an importer" is not true today.

**F9 — There is no project script compilation.** `PluginDiscovery.Scan` walks directories for a
manifest and a **pre-built assembly**. Unity's headline workflow — drop a `.cs` file in
`Assets/Editor/`, it compiles, it works — has no counterpart. `GameAssemblies` in `Vixen.Cli` is a
build-time concept and does not run in the editor.

### The single-source-of-truth finding

The user's phrasing was exact, and it is the deepest problem. There is **no single edit path**.

| Surface | Records through |
|---|---|
| Inspector fields | `SetMembersCommand` |
| Gizmo drags | `TransformTargetsCommand`, or `SceneViewport.Records` for hosts whose targets are not entities |
| Terrain / foliage | their own stroke commands |
| Node graphs | `NodeGraphCommands` |
| Blockout | its own |

Each is defensible alone. Together they mean a new editing surface must invent a sixth, and a plugin
cannot participate in undo at all. The `Records` hook that landed on master this week exists
*because* the default path assumed the target was an entity — which is the shape of a system that
grows a hook per exception rather than having one rule.

---

## Part 2 — What Unity actually does, and which parts to take

From the tutorial and the reference docs (sources at the end). Unity's editor extensibility is four
mechanisms and one invariant.

### The four mechanisms

**1. Attribute-declared, assembly-scanned registration.** `[CustomEditor(typeof(T))]`,
`[CustomPropertyDrawer(typeof(T))]`, `[MenuItem("path")]`, `[CreateAssetMenu]`, `[EditorTool]`,
`[Overlay]`, `[DrawGizmo]`, `[ScriptedImporter]`. One pattern: *declare next to the code, discovered
by the editor, never registered in a central list.* Nothing in Unity has a `NewAssetKinds` array.

**2. `Editor/` folders and editor-only assemblies.** Code under an `Editor/` folder is compiled into
an editor-only assembly and excluded from builds. It is a **convention with a compilation
consequence**, which is what makes "just write a script" work.

**3. UXML + USS + `binding-path`.** A custom inspector overrides `CreateInspectorGUI()`, clones a
`VisualTreeAsset`, and returns it. Binding happens *after* the method returns: elements carrying
`binding-path="m_Make"` are wired to the matching `SerializedProperty` automatically. `PropertyField`
draws whatever the default would have drawn. **The markup does not know about the C#; the binding
layer joins them by name.**

**4. Menu paths as the composition mechanism.** `"Tools/My Thing/Do It"` — no menu object, no
parent lookup, no ordering call. A string, a priority, and an optional validate method. Menus
compose because nobody owns the tree.

### The invariant — and it is the whole game

**Every edit goes through `SerializedObject` / `SerializedProperty`.** Not as a style rule: it is
what buys, in one mechanism,

* undo and redo,
* scene dirty-tracking,
* **multi-object editing** (assign to all targets, read from the first, `hasMultipleDifferentValues`
  for the mixed case),
* **prefab override tracking and the bold-label styling that shows it**,
* change detection (`BeginChangeCheck`/`EndChangeCheck`),
* and the binding that makes UXML work at all.

A custom inspector that assigned fields directly would lose all six. That is why Unity's tutorial
spends its length on `serializedObject.FindProperty` rather than on GUI calls.

⚠ **This is the piece Vixen is missing, and every other gap is downstream of it.** A plugin cannot
take part in undo because there is no shared object to take part *in*. Markup cannot bind because
there is nothing to bind *to*. Multi-select editing is bespoke per surface because there is no
`hasMultipleDifferentValues`.

### What not to take

* **IMGUI.** Unity is migrating away from it; we have a retained UI framework already. `OnInspectorGUI`
  has no place here — `CreateInspectorGUI` returning a tree is the model to copy.
* **Reflection-driven discovery at runtime.** ADR-002 forbids it and the existing generator already
  proves we do not need it *in-tree*. Out-of-tree plugins are a different case — see D3.
* **`MonoBehaviour`-shaped assumptions.** Our components are ECS data, and `SerializedProperty`'s
  design leans on Unity's object model. The abstraction we need is the *invariant*, not the API.

---

## Part 3 — The design

Four decisions. Everything in the phases follows from them.

### D1 — One editing pipeline: `EditTarget` and `EditProperty`

The single source of truth the user asked for. One abstraction that every editing surface writes
through, providing exactly what Unity's does:

| Concern | Answered by |
|---|---|
| Undo / redo | the pipeline records one command per applied change set |
| Multi-object edit | an `EditTarget` wraps *N* objects; reads report `Mixed` when they disagree |
| Change detection | `Apply` returns what changed, so panels refresh on fact rather than per frame |
| Binding | `.vxml` binds by property path against this, not against a C# object |
| Plugin participation | a plugin writes through the same pipeline and gets undo for free |

⚠ **The migration is the work, not the type.** The five existing command paths (F-list) each become
a producer of the same change set. `SetMembersCommand` and `TransformTargetsCommand` collapse into
it; terrain strokes and graph edits keep their own commands but *declare* them to the pipeline so
one undo stack orders them. `SceneViewport.Records` is retired — it becomes the ordinary case.

### D2 — One registry, populated three ways

A single `EditorRegistry` is the only thing the shell reads. Three producers write to it, and the
shell cannot tell them apart:

| Producer | When | Constraint |
|---|---|---|
| Source generator | compile time | in-tree and first-party packages; AOT-clean, no scan |
| Plugin `Activate` | load time | the `PluginContext.Add*` surface, widened per D4 |
| Project scripts and `.vxml` | project open | discovered under the project's `Editor/` folder |

⚠ **The built-ins must move to producer 2 or 1 and stop being producer 0.** Terrain, Blockout,
Profiler, Debugger and the graph editors go through the same registration API a third party uses.
This is the only way to know the API is sufficient — an API whose own authors bypass it is a guess.
It is also the single largest change here, and F2 is what it is repaying.

### D3 — Discovery is declared, not listed

Attributes next to the code, exactly as Unity does, resolved differently by tier:

```csharp no-compile="the shape; the generator emits the registration for in-tree types"
[CustomInspector(typeof(TerrainComponent))]
[CustomDrawer(typeof(Curve))]
[EditorMenu("Assets/Create/Terrain Layer", Priority = 200)]
[EditorTool("Sculpt", typeof(TerrainComponent))]
[AssetImporter(".fbx", ".gltf")]
```

**In-tree and first-party packages:** the generator sees the attribute and emits a registration —
no reflection, ADR-002 intact, trimmable.

**Out-of-tree plugins:** the plugin ships the same generator (packaged — F5's fix is one
`IsPackable`) so its own build emits its registrations, and `Activate` runs them. A plugin that
does not use the generator can still call `context.Add*` by hand. ⚠ **No assembly-wide reflection
scan at editor start**, which is the trap: it would cost startup time, break trimming, and make a
plugin's failure a mystery rather than a message.

### D4 — The extension surface, completed

`PluginContext` gains what F4 says is missing. The list is the deliverable:

| Add | Replaces the hardcoding at |
|---|---|
| `AddInspector(Type, descriptor)` | F5 — no path at all |
| `AddDrawer(type/attribute, drawer)` | mutating `DrawerRegistry.Default` |
| `AddAssetKind(kind)` | `NewAssetKinds` (F3) |
| `AddImporter(importer)` | `ImportPipeline`'s constructor argument (F8) |
| `AddTool(tool)` / `AddOverlay(overlay)` | `Shell.Modes.Add` from app code (F6) |
| `AddGizmo(type, draw)` | nothing |
| `AddSettingsPage(page)` | `EditorSettingsPanels` |
| `AddPreview(type, thumbnail)` | nothing |

---

## Part 4 — Phases

Each phase is shippable and leaves the editor working. The order is chosen so the riskiest
structural change (P3) happens after the thing that makes it verifiable (P2).

### P1 — The editing pipeline

`EditTarget`, `EditProperty`, `Mixed`, one undo stack. `SetMembersCommand` and
`TransformTargetsCommand` reimplemented on it. `SceneViewport.Records` retired.

**Exit:** the inspector edits a multi-selection of mixed values and shows the mixed state; a gizmo
drag and a field edit land on one undo stack in the order they happened; `Records` is gone and the
shape-editor case that motivated it still works.

### P2 — The registry and the attributes

`EditorRegistry`, the `[Custom*]` attribute set, generator support for each, and `PluginContext`
widened to D4's table. Built-ins still register the old way.

**Exit:** a test plugin — built outside the solution, loaded from a folder — adds an inspector, a
drawer, a Create-menu entry and a scene-view tool, and all four work. ⚠ This test is the document's
real acceptance criterion; everything else is scaffolding for it.

### P3 — The built-ins move to the front door

Terrain, Blockout, Profiler, Debugger and the graph editors register through P2's API.
`Vixen.Editor.App` stops referencing them. `EditorApplication` becomes a host: open a project, load
modules, run a frame.

**Exit:** `Vixen.Editor.App.csproj` references `Core`, `Ui`, `Plugin`, `Inspector`, `SceneView` and
nothing else. `EditorApplication.cs` is under 800 lines. Every feature that worked still works, and
`CheckArchitecture` gains a rule that fails the build if a feature assembly is referenced again.

### P4 — `.vxml` becomes the authoring path

`binding-path` against D1's pipeline. `PropertyField` equivalent. An inspector authorable as markup
with no C#. Hot reload already exists and is joined up.

**Exit:** one shipped inspector is markup with no hand-written C#, and editing it while the editor
runs updates the panel. ⚠ **And the two existing `.vxml` files stop being the only two** — F7 is the
warning that a declarative path nobody adopts is a declarative path that does not work.

### P5 — Project `Editor/` scripts

Roslyn compilation of `<project>/Editor/**/*.cs` into an editor-only assembly, loaded and reloaded
like a plugin. This is Unity's headline workflow and the largest piece.

**Exit:** a `.cs` file dropped into a project's `Editor/` folder adds a menu item without restarting
the editor; a compile error is a panel, not a crash.

⚠ **P5 is separable and last on purpose.** P1–P4 deliver an editor extensible by *packaged plugins*,
which is most of the value. P5 adds extensibility by *loose scripts*, which is more convenient and
considerably more machinery — a compiler host, an assembly-unload story, and a failure surface.
If the schedule slips, this is what drops.

---

## Part 5 — The seams

Deliberately open, and named so the first project that needs one does not fork.

* **`IEditorRegistry`** — the shell reads it; three producers write it. A fourth (a remote plugin
  store, a scripted DSL) is a new producer and not a new shell.
* **`IEditProvider`** — how an `EditTarget` reaches its data. Entities, assets, graph nodes and a
  project settings file are four implementations, not four pipelines.
* **`IToolContext`** — what a scene-view tool is handed. Terrain's brushes and blockout's handles
  should be two implementations of the same thing; today they are two subsystems.
* **Plugin API versioning** — `PluginManifest.Api` exists and nothing enforces it. P2 makes it mean
  something, because widening the surface makes breaking it possible.

---

## What this document does not do

* **It does not add a scripting language.** P5 compiles C#. A visual or interpreted editor-scripting
  layer is a different argument.
* **It does not redesign the UI framework.** `Vixen.Ui` and `.vxml` stay; P4 adopts what exists.
* **It does not promise Unity's API.** The mechanisms are the same shape; the names and the object
  model are ours, because our components are ECS data and `SerializedProperty` leans on Unity's
  object model in ways we should not copy.
* **It does not touch the runtime.** Every change is under `Editor/`, plus one `IsPackable` in a
  generator.

---

## Sources

* [Unity Learn — Editor Scripting](https://learn.unity.com/tutorial/editor-scripting)
* [Unity Manual — Create a custom Inspector (UI Toolkit)](https://docs.unity3d.com/6000.0/Documentation/Manual/UIE-HowTo-CreateCustomInspector.html)
* [Unity Scripting API — PropertyDrawer](https://docs.unity3d.com/ScriptReference/PropertyDrawer.html)
* [Unity Scripting API — SerializedObject](https://docs.unity3d.com/ScriptReference/SerializedObject.html)
* [Unity Scripting API — Editor](https://docs.unity3d.com/ScriptReference/Editor.html)
* [Unity Manual — Custom Unity Editor tools](https://docs.unity3d.com/6000.0/Documentation/Manual/UsingCustomEditorTools.html)
* [Unity Manual — Create a custom overlay](https://docs.unity3d.com/Manual/overlays-custom.html)
* [Unity Manual — Property Drawers with IMGUI](https://docs.unity3d.com/Manual/editor-PropertyDrawers.html)
