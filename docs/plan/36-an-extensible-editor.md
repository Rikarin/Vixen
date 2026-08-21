<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 36 — An extensible editor

The editor has a plugin system, an inspector registry, a drawer registry, a markup language and a
hot-reload host. It also has a 3,601-line `EditorApplication.cs` that hard-references twelve feature
assemblies and a hardcoded array of the asset kinds the Create menu offers. Both statements are
true, and the second is why the first does not add up to an extensible editor.

> **Status.** P2, P3b, P4 and P5 are done. **P1 and P3 are not**, and both were marked done at some
> point in this document's history — P1 because its exit criteria tested the mechanism rather than
> its reach, P3 because moving a feature's panels out was read as moving its reference out.
> [Part 6](#part-6--what-is-owed) is the consolidated list, measured against the tree rather than
> against the plan. Every number in Part 1 is as first measured and is not maintained; where a
> current figure matters it is given at the phase.

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

✅ **F3 and F5 are closed, and F4 and F6 are answered.** P2's registry is what a Create ▸ entry, a
custom inspector and a scene-view tool now go through, and `PluginContext.With` is how a drawer
reaches the registry the host published rather than a process static. The findings below are left as
they were measured, because the audit is the argument and rewriting it would lose it.

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

🟡 **F7 is answered and not closed.** [P4](#p4--vxml-becomes-the-authoring-path-) makes the path
work end to end and adds the third file; three files against ~120,000 lines of hand-written editor C#
is a path that has been walked rather than one that is adopted. The denominator has grown since this
was measured and the numerator has moved by one, so the finding is *more* true than when written.

**F7 — The declarative path exists and is not used.** Two `.vxml` files in the whole repository:
`Editor/Vixen.Editor.App/UndoHistory.vxml` and `Editor/Vixen.Editor.Ui/Tasks/TaskCenter.vxml`.
Against ~109,000 lines of hand-written C# UI. `.vxml` is a language we built, shipped generators and
a hot-reload host for, and then did not adopt. Any plan that says "author panels in markup" has to
reckon with the fact that we already can and don't.

✅ **F8 is closed, and the finding was half right.** A registry existed all along —
`ImporterRegistry`, with `Add`, and the conflict rule that refuses two claimants for one extension.
What was missing is that it is built **fresh per run** by `BuiltInImporters.Create`, inside a
background task, deliberately, so that the editor and the CLI cannot disagree about the set: a plugin
had nothing to add to because every registry it could reach was about to be thrown away.
`EditorApplication`'s own remark said exactly that and named `Vixen.Editor.Assets` as where the
change belonged.

`ImporterContributions` is that change — a set that outlives a run, folded in by `Create`, published
through `PluginServices`, and removable, so a plugin's scope withdraws its importer on unload. Doc
11's "a plugin can add an importer" is true now.

⚠ **A contributed importer does not reach an out-of-process compiler worker, and this is the one
place the claim has a hole an author would hit.** `Tools/Vixen.AssetCompiler` starts workers for
crash isolation and [`WorkerHost.cs:34`](../../Tools/Vixen.AssetCompiler/WorkerHost.cs) builds each
one's registry from the parameterless `Create` — which folds in `ImporterContributions.Default`, and
in a worker process that set is empty because the worker never loaded the plugin. So an asset only a
plugin can import works in the editor and fails in the pool. Closing it means the worker loading the
same plugin set the coordinator has, which is a change to the worker's start-up and is named rather
than done.

⚠ **An earlier revision said "build steps are the same shape and are still not published".** There is
no `BuildStep` or `IBuildStep` type anywhere in the repository, so that sentence named an omission in
something that does not exist. What is actually true is narrower: the player build in `EditorBuilds`
has no contribution point at all, so a plugin cannot add a step to it — which is a *missing
mechanism*, not an unpublished registry, and belongs with D4's last two rows rather than with F8.

**F8 — Importers are constructed and handed in.** `ImportPipeline(database, importers, artifacts, …)`
— whoever builds the pipeline decides what importers exist. There is no registry for a plugin to
add to, which is why doc 11's "a plugin can add an importer" is not true today.

✅ **F9 is closed.** [P5](#p5--project-editor-scripts-) is `Editor/Vixen.Editor.Scripts`: every
`Editor/` folder in a project, compiled in process and loaded through the plugin host.

**F9 — There is no project script compilation.** `PluginDiscovery.Scan` walks directories for a
manifest and a **pre-built assembly**. Unity's headline workflow — drop a `.cs` file in
`Assets/Editor/`, it compiles, it works — has no counterpart. `GameAssemblies` in `Vixen.Cli` is a
build-time concept and does not run in the editor.

**F10 — One idea, two registries, and the editor reconciles them.** `SceneComponentRegistry` and
`SceneBehaviorRegistry` are separate types with near-identical surfaces, and the inspector carries
the seam twice: `IComponentBridge` has a component implementation and a `BehaviorBridge`, merged by
a `Registered` list that walks both.

⚠ **The runtime architecture is not the problem.** [Doc 04](04-ecs-and-scripting.md) § Layer 3
defines it precisely — a `Behavior` is a managed component holding a handle into the world's
`BehaviorStore`, dispatched through a generated per-assembly table. That is a clear design and it
works.

The struct doc 04 names is `BehaviorRef`, and until this document it was called `BehaviorLink` in
code — renamed to match the record rather than the other way round.

What was undefined is the *authoring* story, and it is now decided. See
[the authoring rule](#the-authoring-rule) below; the editor's job is to stop contradicting it.

**F11 — The editor force-loads the assemblies whose types it wants registered.**
`ComponentsView.Prime()` calls `RuntimeHelpers.RunModuleConstructor` on `Camera`, `Light`,
`AudioSource` and their neighbours, because a module initialiser does not run until the assembly is
touched. It is F2's disease in a second organ: a hardcoded list, in the application, of which
subsystems exist.

**F12 — The Project panel's two views disagree about what an asset looks like.**

The grid already has per-type coloured icons. `AssetThumbnails` is
`readonly record struct Thumbnail(PathBuilder Glyph, Color4 Tint)` and a `For(importer)` switch, and
`AssetGrid` draws it — a folder is amber, an unknown kind is grey, and each importer gets its own
glyph.

The tree does not. [`ProjectBrowser.cs:596`](../../Editor/Vixen.Editor.App/ProjectBrowser.cs) is the
whole of its icon logic:

```csharp no-compile="the whole of the tree view's icon logic"
node.Icon = asset.IsFolder ? EditorIcons.Folder : EditorIcons.File;
```

So the same asset is a coloured mesh glyph in one pane and a generic "file" in the other. ⚠ **The
useful reading is not "icons are missing" but "the mechanism exists, is hardcoded, and only one of
two consumers uses it".** `For` is a `switch` over importer names — a plugin's asset type cannot
appear in it — and the Hierarchy and the inspector's component headers have no icon path at all.

**The element draws them and the renderer is not the constraint.**
`Vixen.Ui.Controls.Icon` is a vector control — `PathBuilder Geometry`, `ViewBox`, `FillRule`, with
move/line/quadratic/cubic verbs, which is SVG's path model. It has no colour of its own because
`OnDraw` makes exactly one call:

```csharp no-compile="the whole of Icon's painting"
context.Fill(scaled, context.Foreground, FillRule);
```

⚠ **`DrawContext.Fill` and `DrawContext.Stroke` each already take a `Color4`.** Per-path fill and
stroke colours are therefore free at the drawing layer — what is single-colour is this one call site
and the single-`Tint` `Thumbnail` record, not the renderer underneath them.

✅ **F11 and F12 are closed, and F10 is answered rather than done as written.** `Prime()`'s three
hardcoded module-constructor calls are an `AuthoringAssembly` contribution; both of the Project
panel's views resolve one picture through one method, and the outliner and the inspector header read
the same registry. F10's two *engine* registries stay two, for the reason
[P3b](#p3b--one-authoring-unit-and-icons-) gives — what merged is the editor's vocabulary, which is
where F10 measured the seam.

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

🟡 **One of the five rows goes through the pipeline, and the honest statement is that the
invariant split in two.** See [what the invariant bought and what it did
not](#what-the-invariant-bought-and-what-it-did-not) for the measurement; the summary is that the
*undo stack* is a single source of truth across all five and `EditTarget`/`EditProperty` is a single
source of truth for the inspector alone.

⚠ **An earlier revision of this line claimed P1 answered the first two rows. It answered one.**
Row 2 was improved rather than migrated: `IGizmoTarget.Record` replaced `SceneViewport.Records` and
the type test beside it, so the gizmo has *one* recording path instead of two — but that path builds
a `TransformTargetsCommand` and pushes it onto `Document.Stack` directly, and never touches
`EditProperty`. Cleaning up a second path is not the same as joining the first, and reading it as
"two of five" is what made the remaining work look half the size it is.

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

#### What the invariant bought and what it did not

Measured against the code, not against the plan. **The mechanism delivers all six. It is reached by
one editing surface of five.**

| The six | Built | Reached by |
|---|---|---|
| undo / redo | ✅ `EditProperty.Apply` → `Document.Stack.Execute` | **all five** |
| scene dirty-tracking | ✅ `EditorDocument.IsDirty` over the same stack | **all five** |
| multi-object editing | ✅ `EditProperty.Read` returns `EditValue(null, IsMixed: true)`; `WriteEach` for per-object values | the inspector |
| prefab override + styling | ✅ `InspectorField.IsOverridden` → `IPrefabSource`/`PrefabSource`; the row gains `.overridden` and its label stops being muted | the inspector |
| change detection | ✅ `EditProperty.Changed`, and `Write` returns whether anything moved | the inspector |
| the binding markup needs | ✅ `MarkupBinding` / `PropertyField` against an `EditTarget` | the inspector |

⚠ **The invariant is two claims and only the first is true here.** *Everything lands on one undo
stack* — an `IEditorCommand` on `EditorDocument.Stack` — and that holds for all five surfaces, so a
gizmo drag, a terrain stroke and a field edit interleave correctly on one Ctrl+Z. *Everything is
expressed as a property on a shared object* holds for the inspector and nothing else: every file that
names `EditProperty` or `EditTarget` is in `Vixen.Editor.Inspector`.

⚠ **The override mark is a colour rather than Unity's bold**, and the theme says why: not being muted
is a mark that survives a retheme, where a chosen colour would not.

⚠ **What the other four surfaces lose is not undo — it is the other four rows.** No multi-object
edit, no mixed state, no `Changed` to subscribe to, no override mark, and — since
[P4](#p4--vxml-becomes-the-authoring-path-) — **no markup binding**, so a terrain, foliage,
node-graph or blockout panel cannot be authored in `.vxml` at all. That last one grew teeth after P4
made markup a real path; when P1 wrote the caveat it was theoretical.

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
through, providing exactly what Unity's does.

🟡 **The type is built and the migration is one surface of five.** Read the table below as the design
and [what the invariant bought](#what-the-invariant-bought-and-what-it-did-not) as the state; the
mechanism does all of this, the inspector uses it, and the other four surfaces reach only the first
row. `SetValuesCommand`'s own remarks are the shortest correct summary.

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

✅ **The type is built.** `EditTarget`, `EditProperty`, `EditValue`, `IEditMember`, `IEditProvider`
and `SetValuesCommand` are in `Vixen.Editor.Core`; `InspectorField` derives from `EditProperty` rather
than reimplementing it, and `SetMembersCommand` survives as what an `InspectorMember` hands back from
`IEditMember.CreateSetCommand` — the typed accessors are the reason it exists and the pipeline does
not need them boxed away. `Records` is gone.

**Two providers now.** `NodePortEditProvider` describes a graph node's inline port values as
`InspectorMember`s, so `InspectorView` draws them and `NodeInspector` is a host rather than a panel
of its own. It is the seam's first user outside the inspector, and the case that could not have been
solved any other way: a node's members are decided by the node *type* a saved graph names by string,
not by the CLR type every node shares, so no registry keyed by `Type` can hold them.

⚠ **The count of what is owed was wrong, and the audit is worth recording.** Terrain and foliage were
already on the pipeline — every one of their settings objects is a plain `[Inspector]` class that
`InspectorEditProvider` describes and `InspectorView` draws, `TerrainBrushInspector.vxml` included.
Blockout's settings are records in `Core/Vixen.Geometry.*` with no annotation, and nothing in the
editor draws them at all; a provider would not have changed that. What is actually left is
**transforms** — see [what is owed](#part-6--what-is-owed).

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

✅ **Built, with one departure stated at [P2](#p2--the-registry-).**
`EditorRegistry` is a typed multimap rather than a method per kind: a contribution kind is a record in
the assembly that owns it, and the registry, the plugin contract and the shell learn nothing when one
is added. The application's own Create ▸ list is now producer 1 — still a literal, because the
application's own kinds belong to the application, but it goes *into* the registry and the menu is
built *from* it.

### D3 — Discovery is declared, not listed

Attributes next to the code, exactly as Unity does, resolved differently by tier:

```csharp no-compile="the shape; the generator emits the registration for in-tree types"
[CustomInspector(typeof(TerrainComponent))]
[CustomDrawer(typeof(Curve))]
[EditorMenu("Assets/Create/Terrain Layer", Priority = 200)]
[EditorTool("Sculpt", typeof(TerrainComponent))]
[AssetImporter(".fbx", ".gltf")]
```

✅ **All of Unity's eight exist now, and every one works identically in a plugin and in a project's
`Editor/` folder.** One correction to the sketch: `[AssetImporter]` is spelled `[Importer]` and has
been since before this document — `AssetImporter<T>.Extensions` reads it and every built-in carries
one.

| Unity's | Ours | State |
|---|---|---|
| `[MenuItem("path")]` | `[EditorMenu("…", Priority = 200)]` | ✅ both tiers. ⚠ The sketch's path above is a Create ▸ entry, which is a `NewAssetKind`; an `[EditorMenu]` puts a *verb* on a menu |
| `[CustomEditor(typeof(T))]` | `[CustomInspector(typeof(T))]` | ✅ both tiers, on a `static void (UiElement, EditTarget)` |
| `[CustomPropertyDrawer(typeof(T))]` | `[CustomDrawer(typeof(T))]` | ✅ both tiers, on an `IPropertyDrawer`; `ForAttribute` picks the other resolution |
| `[EditorTool]` | `[EditorTool("Sculpt", typeof(T))]` | ✅ both tiers, on an `IViewportInput` |
| `[ScriptedImporter]` | `[Importer(".fbx", ".gltf")]` | ✅ both tiers |
| `[CreateAssetMenu]` | `[CreateAssetMenu("Title", ".ext")]` | ✅ both tiers, on a `static string ()` returning the starter contents |
| `[Overlay]` | `[Overlay("Title")]` | ✅ both tiers, on a `static void (UiElement, SceneViewport)` |
| `[DrawGizmo]` | `[DrawGizmo(typeof(T))]` | ✅ both tiers, on a `static void (GizmoDraw, object, GizmoPlacement, bool)` |

⚠ **Three of the eight are named as classes in Unity and are static methods here**, and it is the same
argument each time. Unity's attribute goes on a subclass because the base carries state the override
needs — an `Editor`'s serialized object, an `Overlay`'s docking. Ours have no base: a custom inspector
*is* an `Action<UiElement, EditTarget>` and an overlay *is* an `Action<UiElement, SceneViewport>`, so a
class whose only job is to hold one method is ceremony. `[CustomDrawer]` and `[EditorTool]` stay on
classes because a drawer and a tool genuinely have state between calls.

⚠ **`[CreateAssetMenu]` is a method returning the contents rather than an attribute on a type, and
that is the one place the shape had to differ.** Unity can put it on a `ScriptableObject` because a new
asset there is a default instance serialised by a serializer that already knows the type. Ours is a
file with an extension an importer claims, and `NewAssetKind.Contents`'s own remark names the trap: an
empty file is right for a kind whose editor opens a blank document and wrong for a kind an importer
reads, which deserialises it and puts a warning beside it instead of an asset. Making the author write
the return value is what stops that being a default nobody saw. It runs **per file**, so a starter
document carrying an identifier is a different file each time — `NewAssetKind.Build` exists for that
and the test asserts two creations produce `id: 1` and `id: 2`.

⚠ **`[DrawGizmo]` was the one whose "Replaces the hardcoding at" column in D4 said *nothing*, and that
was wrong.** `SceneLines.LightShapes` is a walk over the scene testing for one component type and
switching on its kind — this mechanism, written once, in the application's assembly, for the one
component the application happens to know about. A plugin's component had no way to be drawn at all.
Component gizmos are behind their own `SceneShow.Components` rather than `SceneShow.Gizmos`, which is
the transform handles: turning the arrows off is not a request to hide every trigger volume.

⚠ **They are read by a bounded scan, not by a generator, and that is a departure from this
section.** D3 says a plugin ships the generator (F5's unset `IsPackable`) and a script cannot, which
would have left the two tiers permanently asymmetric. But `PluginHost` **already** enumerates a
plugin's types to find its entry point, and `ScriptCompiler` has just compiled the script assembly
from a folder it is watching — so the walk exists in both places already and costs nothing to extend.
ADR-002's two objections are about the *shipped product*: a scan reads metadata a trimmed publish has
deleted, and start-up cost grows with what is installed. Neither is true of one discrete assembly the
editor loaded seconds ago. In-tree code registers the records directly and nothing scans it.

`IContributionScanner` is the seam that makes it possible: the attributes name `CustomInspector`,
`DrawerRegistry` and `SceneTool`, which the plugin contract must not reference — so `PluginHost` holds
scanners and `Vixen.Editor.App` supplies the one that knows those types. That is P2's rule kept
rather than broken.

⚠ **The declarations are read *after* `Activate`**, so a hand-written registration beats an attribute
for the same type. "The code I wrote wins over the attribute I forgot about" is the rule, and both
tiers follow it.

✅ **`[Importer]` works in a script too, and needed one thing rather than a generator host.** An
importer is *named* by its settings type's `[DataContract]` alias, which `TypeRegistry` answers — and
everything downstream goes through the same registry: `YamlSerializer` reads and writes a `.meta`
through it, and `ArtifactKey` hashes the settings as the YAML it emitted. So `ReflectedTypes` builds
the descriptor by reflection and registers it, and the pipeline never knows the difference.

⚠ **Only permissible because the editor is managed, and it lives where that is true.** The engine is
published NativeAOT and ADR-002 is why the generator exists at all; a reflection describer in
`Vixen.Core.Reflection` would be one a runtime could reach. It is in `Vixen.Editor.Scripts`.

⚠ **It must agree with the generated one, member for member**, or a settings type moved from a script
into a plugin would change what its `.meta` says — the same file read as different values, with no
error. `ReflectedTypeTests` builds both for three shipped settings types and compares the alias, the
member names, the orders and the types.

⚠ **`init`-only setters were the risk worth testing.** Every settings record in the codebase is
`{ get; init; }`, the generator reaches those through `[UnsafeAccessor]`, and a reflected setter that
silently did nothing would be a `.meta` that reads back as every default. Reflection can call an
`init` setter — it is an ordinary setter with a modreq — and a test round-trips one through YAML.

⚠ **`[Component]` and `[Behavior]` stay out of `Editor/` deliberately**, and not for want of a
mechanism. Runtime code belongs in `Assets/`, where the project's own `.csproj` compiles it with the
generators; a component that existed only because the editor compiled a script would be a scene a
game build cannot load. That is Unity's split and it is the right one: **`Editor/` converts the data,
`Assets/` is the game.**

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
| ~~`AddInspector(Type, descriptor)`~~ | ✅ `CustomInspector`, and `[CustomInspector]` — F5 |
| ~~`AddDrawer(type/attribute, drawer)`~~ | ✅ `PluginContext.With<DrawerRegistry>`, and `[CustomDrawer]` |
| ~~`AddAssetKind(kind)`~~ | ✅ `NewAssetKind`, and `[CreateAssetMenu]` — F3 |
| ~~`AddImporter(importer)`~~ | ✅ `ImporterContributions`, published through `PluginServices` — F8 |
| ~~`AddTool(tool)`~~ | ✅ `SceneTool`, and `[EditorTool]` — F6 |
| ~~`AddOverlay(overlay)`~~ | ✅ `SceneOverlay`, and `[Overlay]` — `ViewportChrome` was the only thing that could put a panel over a pane |
| ~~`AddGizmo(type, draw)`~~ | ✅ `ComponentGizmo`, and `[DrawGizmo]` — ⚠ **"nothing" was wrong**: `SceneLines.LightShapes` is this, hardcoded for one component |
| `AddSettingsPage(page)` | `EditorSettingsPanels` |
| `AddPreview(type, thumbnail)` | nothing |

⚠ **Seven of nine, and none of them is a method on `PluginContext`.** P2's departure held: a
contribution kind is a record in the assembly that owns it, and `Owns`/`With` are the whole surface.
Adding `SceneOverlay` and `ComponentGizmo` changed nothing in the plugin contract, which is the
property the table's shape would have destroyed.

⚠ **The last two rows are real and unbuilt.** A settings page needs `EditorSettingsPanels` to become a
registry the shell reads rather than a list it holds, and a preview needs the thumbnail cache to ask
a registry before it falls back — both are the same move made twice more, and neither is done.

### The authoring rule

Stated here because it is what D5 implements, and owed to
[doc 04](04-ecs-and-scripting.md) as an authoring section it does not currently have.

> **A `Behavior` is a script.** It is what a game author reaches for when the logic has one instance
> in the scene, or a handful, or does not decompose into data a system can sweep. One class, its
> properties, its `Update` — the shape a Unity author already knows, and no component and system
> pair to write for each idea.
>
> **A component-and-system pair is for the case that pays for itself.** Many instances, the same
> operation over all of them, a benefit from being contiguous in memory. That is what the ECS is
> *for*, and it is not what a door that opens once is.

⚠ **This is a rule about scale and shape, not about capability.** A behaviour is not the beginner's
option or a slower path with a nicer face; it is the right answer for logic whose instance count
never justifies an archetype. Framing it as "the easy one" is what makes people write systems for
door hinges, and framing it as "the fast one" is what makes them write behaviours for particles.

**Three consequences the editor has to honour.**

1. **The Add menu leads with what the author is likelier to want.** A person adding logic to a
   specific entity is usually writing a script. Components and behaviours belong in one sorted list
   (D5), and the list should not make the script the thing you find second.
2. **A behaviour's properties are its inspector, and they are the whole point.** "One behaviour with
   properties and a script" only works if those properties are as editable, as undoable, as
   multi-selectable and as drawer-extensible as a component's fields. That is D1 and D2 doing their
   job for both kinds — and it is the concrete meaning of "not second-class".
3. **The cost of the choice is reversible.** An author who guesses wrong and finds a behaviour on
   ten thousand entities should be able to migrate without re-authoring the scene. ⚠ This document
   does not build that, and does not pretend the migration is free — it names it so doc 04 can decide
   whether a supported conversion is owed or whether "you will rewrite it" is the honest answer.

⚠ **What this rule does *not* license is two of everything.** Doc 04's runtime split is a good
design and stays. The editor having two registries, two bridges and a reconciliation layer is not
that design — it is an accident of building the component path first, and D5 removes it.

### D5 — One authoring unit, and `Behavior` is not a lesser one

The editor stops knowing that components and behaviours are different things. `IComponentBridge`
already proves it can be one vocabulary; what is missing is that the vocabulary is *primary* rather
than a reconciliation layer over two registries.

| Decision | |
|---|---|
| **One registry** | `SceneComponentRegistry` and `SceneBehaviorRegistry` become one, with a kind on the entry. Doc 04's runtime split is untouched — `BehaviorRef` and `BehaviorStore` stay exactly as they are |
| **One add path** | Add ▸ lists both, sorted together, with the kind as a subtitle rather than a separate menu |
| **Equal entitlements** | A behaviour gets what a component gets: a generated inspector descriptor, an icon, a Create-menu entry where it makes sense, drawers, and undo through D1 |
| **`Prime()` dies** | F11's hardcoded `RunModuleConstructor` list goes when D2's registry is populated by producers rather than by whichever assembly happened to be touched |

⚠ **"Equal entitlements" is the row that carries the authoring rule.** A script whose properties are
harder to edit than a component's fields is not a script anybody will use for the case the rule
assigns to it — they will write the component-and-system pair the rule says they should not have to.
The rule and the parity are the same work seen from two ends.

### D6 — A type declares its icon, and the registry serves it

One more thing on the attribute set in D3:

```csharp no-compile="the shape; geometry comes from a .svg beside the type or an EditorIcons entry"
[EditorIcon("Icons/terrain-layer.svg", Tint = "#7FB800")]
public sealed class TerrainLayer { … }
```

Resolved through D2's registry, replacing `AssetThumbnails.For`'s switch and reaching the three
surfaces that do not have it:

| Surface | Today | With this |
|---|---|---|
| Project **grid** | per-importer glyph + tint, from a hardcoded switch | the same, from the registry |
| Project **tree** | folder or file (F12) | the same icon the grid shows |
| Hierarchy | no icon | the entity's most characteristic component |
| Inspector | close button only | the component's icon in its header |

**Multicolour from the start.** An icon is a list of `(path, fill, stroke)` rather than one
`PathBuilder` and one tint, and `Icon.OnDraw` loops instead of making a single `Fill`. ⚠ **This is
cheap because `DrawContext.Fill` and `Stroke` already take a colour each** — the tessellator, the
draw list and the batching are untouched. An earlier draft of this document recommended
monochrome-plus-tint first on the assumption that per-path paint meant a new rendering path; it does
not, and deferring it would have meant migrating every authored icon later for no saving.

⚠ **The real decision is not how many colours but whether a colour follows the theme.** `Icon` today
paints with `context.Foreground`, so every icon recolours for a light or dark theme automatically. A
literal colour cannot. So a path's paint has three cases and all three are needed: *theme foreground*
(the default, and what the existing 34 icons want), *a named theme token* (so an icon can say "the
warning colour" and still track a retheme), and *a literal* (for the brand colours a file-type glyph
actually needs). An icon set that only offers literals looks correct in the theme it was drawn for
and wrong in the other one.

⚠ **Icons are a plugin's to declare, which is why this is in this document rather than a UI task.**
A plugin that adds an asset type whose icon cannot be set is a plugin whose assets are visibly
second-class in the panel that shows them — the same shape of problem as F3's Create menu.

---

## Part 4 — Phases

Each phase is shippable and leaves the editor working. The order is chosen so the riskiest
structural change (P3) happens after the thing that makes it verifiable (P2).

### P1 — The editing pipeline 🟡

`EditTarget`, `EditProperty`, `EditValue`, `IEditMember`, `IEditProvider` and `SetValuesCommand` in
`Vixen.Editor.Core`. `InspectorField` is an `EditProperty` with four inspector-only additions;
`InspectorMember` satisfies `IEditMember`; `InspectorEditProvider` is the first provider.
`IGizmoTarget.Record` replaces `SceneViewport.Records` and the mesh type test beside it, so
`EndManipulate` is one path.

**Exit, all met:** the inspector edits a multi-selection of mixed values and shows the mixed state; a
gizmo drag and a field edit land on one undo stack in the order they happened —
`OneEditPathTests`, driven through the shell; `Records` is gone and the proxy-shape case that
motivated it still works, now tested through the viewport's real end-of-drag path rather than by
calling the hook.

⚠ **Marked 🟡 rather than ✅, and the correction is worth stating plainly.** The exit criteria above
were met and the phase was recorded as done — but they test the *mechanism*, not its reach, and the
document then read as though the invariant had landed. It had not: for a long while the pipeline was
used by the inspector and by nothing else. `NodePortEditProvider` is the second provider and the
first outside it, asserted by `NodePortProviderTests` — which drives `EditTarget`, `InspectorView`
and `MarkupBinding` over a node's ports and builds no control of its own.

⚠ **The gizmo does not go through it either, which an earlier revision of this document got wrong.**
`EntityGizmoTarget.Record` builds a `TransformTargetsCommand` and pushes it onto `Document.Stack`.
That is one recording path where there were two — a real improvement, and the reason `Records` could
die — but it is not the gizmo writing through `EditProperty`. Counting it as migrated made the
remaining work look half its actual size.

⚠ **What the other surfaces have and have not.** Terrain strokes, foliage strokes, node-graph edits
and blockout keep their own commands, and D1 only ever asked them to *declare* to one stack, which
they do — they are `IEditorCommand`s on the document's `CommandStack`, so undo ordering and
dirty-tracking are right. A stroke stays that way and should: a brush dab is not a member somebody
binds by name. What their *panels* lacked was a provider, and only the node graph's turned out to
lack one — the terrain and foliage panels are `InspectorView`s over `[Inspector]` classes and have
been all along. The node graph's is fixed; the cost it was paying was the whole list: no multi-node
edit, no mixed state, no `Changed`, no drawer, no reset and no markup binding.

⚠ **`EditProperty` and `EditorProperty{T}` are one letter apart and are different things**, which is
a hazard this phase created and documented rather than renamed away from: the doc named `EditProperty`
and the document model already had the other. One is a binding over N objects with no storage; the
other is a signal on one object. Both remarks say so at the type.

### P2 — The registry ✅

`EditorRegistry` and `IEditorRegistry` in `Vixen.Editor.Core`: a typed multimap, `Add` handing back
the removal, `Changed` carrying the kind. Three contribution records — `NewAssetKind`,
`CustomInspector`, `SceneTool` — each in the assembly that owns it. `PluginContext` gains `Owns` and
`With`, and the registry is published through `PluginServices`.

**Exit, met.** `OutOfTreePluginTests` compiles a plugin from source at run time, drops it in a
folder, and starts an ordinary editor over it. The plugin adds a Create ▸ entry, a custom inspector,
a property drawer and a scene-view tool, and each is asserted by its *effect* — a `.widget` file on
disk, the plugin's own element in the inspector body, the drawer the registry resolves, the camera
the tool moved.

⚠ **Compiled at run time rather than as a fixture project, and that is the stronger test.** The
plugin can see exactly what the editor publishes and nothing else, so a contribution point that only
worked because the two were built together fails here rather than passing quietly.

⚠ **Two additions to `PluginContext`, not D4's eight.** `Owns(scope)` takes ownership of what
`IEditorRegistry.Add` returned; `With<TService>(register, unregister)` registers with one of the
host's own registries and records the undo. A method per contribution kind would have put the whole
kind list in the plugin contract assembly, which means it referencing every feature assembly that
owns one — F2's problem, one layer down. Adding a kind now changes nothing in the contract.

⚠ **Drawers, commands, panels, layouts and modes keep their own registries.** D2 says the registry
is "the only thing the shell reads", and taken literally that would mean copying `DrawerRegistry`
into it — two places a drawer can be declared, which is F10 exactly. What goes in `EditorRegistry`
is what had no owner; what already had one gains a removal path and is reached through
`PluginServices`, which is what makes F4's "mutating a static" a host decision instead.

⚠ **D3's attribute set was deliberately not built in this phase, and is built now.** At the time,
shipping `[CustomInspector]` with nothing reading it would have been an attribute that looks like a
mechanism — the mistake doc 02 made with `GraphicsBackendSelector` and which this document had to
correct — and P3 did not need it, because a feature registering in its own module initializer is
already AOT-clean and scan-free. The set landed later, read by a bounded scan rather than a
generator: see [D3](#d3--discovery-is-declared-not-listed) for all eight and why the scan is
permissible.

⚠ **The rule that judgement produced is worth keeping, because it caught the same mistake again.**
The first version of the `[Overlay]` test asserted the `SceneOverlay` was in the registry — which
passes with `ViewportChrome` never reading it. A declaration is built when something *does* something
with it, and the test has to assert the doing.

### P3 — The built-ins move to the front door 🟡

Terrain, Blockout, Profiler, Debugger and the graph editors register through P2's API.
`Vixen.Editor.App` stops referencing them. `EditorApplication` becomes a host: open a project, load
modules, run a frame.

**Exit:** `Vixen.Editor.App.csproj` references `Core`, `Ui`, `Plugin`, `Inspector`, `SceneView` and
nothing else. `EditorApplication.cs` is under 800 lines. Every feature that worked still works, and
`CheckArchitecture` gains a rule that fails the build if a feature assembly is referenced again.

🟡 **Blockout and Terrain are out; four editor assemblies remain, and none of the three exit
criteria is met.** The modules are named in `Vixen.Editor.Host.EditorModules` and nowhere else.
`Vixen.Editor.App.csproj` still names, beside the five the exit allows:

| Still referenced | Why | What would move it |
|---|---|---|
| `Assets` | the import pipeline — an editor that cannot import without a plugin is not an editor | nothing; ⚠ **the exit list is wrong to omit it**, and the criterion below is corrected |
| `AssetEditors` | `AssetEditorRegistry`, and the arbitration each file already says is the application's: which scene a sequence drives, what analyses an addressable group, opening a shader graph from a material | the registry moving somewhere both ends see |
| `Profiler` | the **diagnostics report**, which aggregates the project, the scene, the log ring *and* the last profile capture | publishing the log ring and the data directory, and moving the report into the module |
| `Debugger` | the report, **and** device deploy in `EditorBuilds` — two reasons, not one | the report moving, *and* a deploy contribution |
| `Diagnostics` | the joining assembly the moves created; the app activates the module | nothing — it is a module, and its reference is the `Activate` call |

⚠ **This is a net increase of one, and an earlier revision of this row hid it.** The table used to
read "`Profiler` + `Debugger` + `Diagnostics`" in a single row marked done, while the phase summary
above said "Profiler + Debugger ✅ done". Both were true about the *panels* and neither was true about
the *references*: the module was created and the app kept referencing the two originals as well.
Seven panels moved; three references stand where two did.

⚠ **`EditorApplication.cs` is 3,787 lines** — measured, and the exit wants under 800. The header of
this document says 3,601 and two other places said 3,641 and 3,675; those were all true when written
and none of them is now. The file grows by tens of lines with each phase that gives it something to
own — the reload host, the icon resolution, the plugin host, the gizmo pass — which is the shape of
the problem rather than a lapse.

⚠ **This phase was never going to fix that, and the plan conflated two jobs.** The five moves took
3,299 lines out of the *assembly* — 20,175 to 16,876 — but almost all of it came from the other
partials and from the host. Splitting the god object is a different job from moving the features out
of it: a file of project opening, panels, selection, commands and play mode is long for reasons no
feature move addresses.

⚠ **`CheckArchitecture` has no rule for this and never gained one.** `build/Build.ArchitectureRules.cs`
enforces the layer order (`Core` < `Platform` < `Editor`/`Tools`) and an editor-only package list;
nothing fails the build when `Vixen.Editor.App` references a feature assembly again. Until it does,
every row removed from the table above can come back without anybody noticing — which for F2, the
finding this document says matters most, is the difference between a fix and a tidy-up.

✅ **The seam is built.** `PluginHost.Activate(id, name, module)` runs a compiled-in `IEditorPlugin`
through the same `PluginContext`, the same registration scope, the same rollback-on-throw and the
same `Unload` an assembly off a disk gets — no `AssemblyLoadContext`, because a compiled-in module is
in the default one and pretending otherwise would report a leak for every built-in.
`PluginContext.FindMenu` and `AddSubmenu` are what a module needs to put its verbs in the menu the
thing they act on already has, rather than a top-level heading per feature.

✅ **Blockout, Terrain, the diagnostics pair and the asset editors' binder are through it.** `BlockoutModule` lives in
`Vixen.Editor.Blockout`, registers the mode and doc 24's five Scene submenus through
`PluginContext`, and asks the host for the four things it needs — the editing state, the work plane,
a mesh baker and a mesh source — through `PluginServices.Require`. `Vixen.Editor.App` holds one line
about it: the `Activate` call. **The assembly it lives in cannot see the editor's application at
all**, which is the part a compiler enforces rather than a convention.

`TerrainModule` is the same shape and four times the size: two modes, five panels and the session
that binds them to the scene — `EditorTerrainPanels` and `EditorTerrainSession`, 1,340 lines, both
partials of `EditorApplication` until now.

⚠ **Terrain needed two extension points that did not exist, and every feature after it will want
them.** `PluginContext.OnUpdate`, because a brush follows the *entity* selection and nothing raises
an event about that; and `EditorDocument.Saved`, because a scene names a heightfield and a foliage
file beside itself and saving one without the others leaves a project whose ground exists only in a
process that has exited. Each was a line the application had to know to write. D4's table has neither
— they are what a feature needs rather than what a contribution *is*, which is a distinction the
table does not draw.

⚠ **`AssetEditorRegistry.Opened` is the fourth seam these moves have needed**, and it is on the
registry rather than on `EditorProject` for a reason worth writing down: `Register` runs from
`EditorDocument`'s base constructor, so an event there would hand a subscriber a half-built document
— the one thing that class's own remarks promise does not happen.

⚠ **And `ITerrainScene` moved to `Core/Vixen.Rendering.Terrain`.** Its implementation is the
terrain module's and its consumer is the editor's scene presenter; a contract owned by either end
would have been a reference back to it. The module contributes its implementation through
`EditorRegistry` rather than the presenter fetching it off the application.

⚠ **The order of the migration is the reverse of the plan's, and that is the useful correction.**
Dereferencing an assembly is the *last* step, not the first: it is one csproj line and one
`new BlockoutModule()`, and it is blocked on splitting the executable off — `Vixen.Editor.App` is
the exe, and a feature cannot be dereferenced by the thing that has to instantiate it. All the
*work*, and all the risk, is the decoupling, and that can be done in place, one feature at a time,
with the tests green throughout. It is also where every missing service is discovered: Blockout's
move is what found that a service published under its implementation type is invisible to a module
asking for the interface.

**What is left, measured rather than estimated:**

| Step | Where it is now | Size |
|---|---|---|
| ~~Blockout~~ | ✅ `BlockoutModule` | done — 120 lines out of the app, its mode already took `IMeshBaker`/`IMeshSource` so its assembly needed no new reference |
| ~~Terrain~~ | ✅ `TerrainModule` | done — 1,340 lines out of the app, plus the two extension points above |
| Profiler + Debugger | 🟡 `Vixen.Editor.Diagnostics` | the panels are out — seven of them and their commands — but ⚠ **the references are not, and calling this done was wrong**. The **report** stayed in the application because it aggregates the project's name, the scene's counts, the log ring and the memory arenas, and only the last is the module's; `Debugger` is additionally kept by device deploy in `EditorBuilds`. The app now names `Profiler`, `Debugger` *and* `Diagnostics` where it named two |
| Asset editors | 🟡 `AssetEditorsModule` | the binder is out — `AnimationBinder`, 341 lines, plus `EditorApplication.Bound`. ⚠ **The rest stays and should**: which scene a sequence drives, what analyses an addressable group, and opening a shader graph from a material are the *application's* arbitration, and each file already says so. Dereferencing `Vixen.Editor.AssetEditors` therefore needs `AssetEditorRegistry` to move somewhere both can see, which is a separate decision |
| ~~Split the executable~~ | ✅ `Editor/Vixen.Editor.Host` | done — `EditorHost`, `Program`, `EditorPane`, `WindowPlacement`, the SPIR-V and the font. `Vixen.Editor.App` is a library that takes its modules as a constructor argument and knows only that some `IEditorPlugin`s exist and what they are called |
| `Vixen.Editor.Assets` | 8 files | ⚠ **Not a feature, and the exit list is wrong to omit it.** It is the import pipeline, and an editor that cannot import without a plugin is not an editor. F2's own inventory names it beside `Core`. Proposed: it stays, and the criterion is corrected to `Core`, `Ui`, `Plugin`, `Inspector`, `SceneView`, `Assets` |
| `EditorApplication.cs` under 800 lines | 3,787 today, and rising | the remainder — project opening, panels, selection, play mode — is a split of its own and not this phase's. ⚠ Each phase that gives the application something new to own adds tens of lines, so this criterion moves away from itself unless the split is scheduled |
| `CheckArchitecture` rule | not built | ⚠ **The exit criterion nothing has touched.** Without it the rows above can be undone silently, which for F2 is the difference between a fix and a tidy-up |

### P3b — One authoring unit, and icons ✅

D5 and D6, together because both are registry consumers and neither is worth a phase alone.

**Exit, met, with two corrections stated below.** `IconArt` is a list of `(path, fill, stroke, width)`
and `Icon.OnDraw` loops; a paint is theme foreground, a named custom property, or a literal, and the
first two follow a retheme. `TypeIcon` and `AssetIcon` are registry contributions read by the Project
grid, the Project tree, the outliner row and the inspector's component header — the last of which had
no picture at all. Add ▸ is one list sorted by name with `Script` as a subtitle on the lines that are
one. `ComponentsView.Prime` is gone.

⚠ **F12 was never a size decision, which is what made it worth fixing rather than papering over.**
The tree's line carried a remark saying a tile was large enough for the answer to be worth reading
and a row was not — but the same asset being a purple mesh in one pane and a generic page in the
other is the kind of disagreement nobody reports and everybody notices. Both panes call one method
now, and `The_tree_and_the_grid_draw_one_asset_the_same_way` asserts they hand back the same
instance.

⚠ **The terrain module contributes the pictures for the five file kinds it introduced**, from an
assembly that cannot see `Vixen.Editor.App` — a `.vxlayer` draws three coloured bands and the Project
panel never learns that terrain exists. That is this document's claim in its smallest form.

**Correction 1 — `[EditorIcon("…svg")]` is not built, and the spelling was wrong rather than the
idea.** There is no SVG path parser in this repository and its absence is a decision `Icon` already
records: an icon set is compiled content, so turning `"M12 2L2 22h20z"` into segments belongs to an
asset pipeline rather than to every application at start-up. An attribute naming a file nothing can
read is an attribute that looks like a mechanism — the mistake P2 declined to repeat. **A type
declares its icon by registering it**, which a module initializer, a plugin's `Activate` and a
project's own script can all do, and which is what D6's title actually asks for.

**Correction 2 — the two engine registries did not merge, and should not.** D5's first row says
`SceneComponentRegistry` and `SceneBehaviorRegistry` "become one, with a kind on the entry".
`SceneBehaviorRegistry`'s own remarks argue the opposite and are right: a component binder's
`TypeId` and `IsTag` mean nothing for a behaviour, and its `Read` writes into a chunk column that
does not exist. Those two are *runtime* registries that the scene loader and the serializer read.

What F10 actually reports is that **the editor** carries the seam — and the editor's vocabulary is
`IComponentBridge`, which was already one. What was missing is what D5's own prose says: that the
vocabulary is primary rather than a reconciliation layer. So the kind moved onto the bridge as
`AuthoringKind`, the Add menu stopped ordering by which registry a thing came from, and nothing above
the bridge branches on it.

⚠ **`Prime()` is gone; the list is not, and could not be.** F11 called it "a hardcoded list, in the
application, of which subsystems exist" — it is now `AuthoringAssembly`, a contribution, so a module
declares its own and a plugin's runtime assembly is declarable by whoever ships it. Eliminating the
list entirely is not available: a `[ModuleInitializer]` does not run until something touches the
module, and the only thing that finds an assembly nobody named is a scan, which ADR-002 and
`SceneComponentRegistry` both refuse for reasons that have not changed. The application still names
its three subsystems, in the same place F2's feature list went.

⚠ **Two bugs surfaced on the way and are fixed.** The outliner subscribed to the structure and the
rename and not to `ComponentsChanged`, so adding a light to an entity left its row drawing the plain
dot — and `GlyphFor`'s own remark asserted this already worked. And `SceneDocument.Recomposed` was
internal, so a module putting its own component on an entity had no way to tell the panels.

✅ **And doc 04 gained [the authoring rule](#the-authoring-rule) verbatim**, in Layer 3 where a game
author choosing between a script and a system will look, with the three editor consequences and the
unbuilt migration named.

### P4 — `.vxml` becomes the authoring path ✅

**Exit, met.** `Editor/Vixen.Editor.Terrain/TerrainBrushInspector.vxml` is the brush panel's
inspector: nine `<PropertyField>`s in three `<Expander>`s, no `@code` block, no C# in the file at
all. It is registered as a `CustomInspector` by the terrain **module** — so the shipped example is
also the plugin path — and mounted through the editor's reload host, so an edit to the file rebuilds
the panel without a restart.

Three pieces:

| | |
|---|---|
| `binding-path` | a universal attribute beside `class`, so any tag can carry it. `<Slider binding-path="Speed" />` lands as a style-tree attribute and `MarkupBinding.Bind` joins it afterwards — Unity's rule, and the only one that works, because a markup `Build` body cannot name a C# type |
| `PropertyField` | `<PropertyField Path="Radius" />` draws whatever the default would have, through the same `InspectorRows.Add` the generated inspector calls. The reset button, the tooltip, the mixed state and the undo arrive without being asked for |
| `MarkupInspector.Of<T>(host)` | mounts the component through `HotReloadHost` and re-binds after a reload |

⚠ **`EditTarget` gained a virtual `Create`, and `InspectorView` now builds an `InspectorTarget`.**
A custom inspector's `Find` was handing back a plain `EditProperty`, so a hand-written inspector's
rows were quietly poorer than the generated ones it replaced — no reset, no prefab override — which
is the opposite of what an author writes one for. Building an `InspectorField` beside the cached
property was not an option: `TryFind` caches per name precisely so `Changed` is subscribable, and a
second instance is one nothing is ever raised on.

⚠ **The editor had never created a `HotReloadHost`, and that is worth stating plainly.** The whole
reload mechanism was built, tested and unreferenced — this document said "hot reload already exists
and is joined up", and only the first half was true. F7 with an extra step: a declarative path that
reloads in principle and not in this application. `EditorApplication` now owns one, registers it with
`MetadataUpdate` and publishes it through `PluginServices`.

⚠ **Two ways to name a member, and both are needed.** `<PropertyField Path="…" />` says "draw this
the way you would have"; `<Slider binding-path="…" />` says "I have chosen the control, join it up".
An inspector with only the first could not lay anything out; one with only the second would make
every author reimplement the default row, badly, once per member. The `binding-path` table names the
controls an inspector is actually made of rather than reflecting over property names, which ADR-002
forbids and which would turn a typo into a control that silently does nothing.

⚠ **What the markup buys here is order and grouping, not fewer lines.** The generated inspector
draws members in declaration order because that is the only order it has; a brush is a shape, a
stroke and a pattern, and `Spacing` belongs with `Rotation` rather than with `Falloff`. `[Header]`
on the type could say some of that and not this.

⚠ **The two existing `.vxml` files are now three, which is a start and not a claim.** F7 measured
two files against the whole of the hand-written editor UI, and one inspector does not change that
ratio — the editor has grown faster than the markup has. What it changes is that the path is walked: the emitter, the binder, the reload and the
editing pipeline are joined end to end and a test drives the real file.

### P5 — Project `Editor/` scripts ✅

`Editor/Vixen.Editor.Scripts`: Roslyn over every `Editor/` folder in a project, into one editor-only
assembly, loaded through `PluginHost` and rebuilt when a file is saved.

**Exit, met.** `EditorScriptWorkflowTests` opens a real editor over a real project, writes a `.cs`
file into `Assets/Editor/`, and finds the command it declared — and writes a broken one, and finds
the editor still running with the panel open. `Vixen.Editor.Scripts.Tests` drives the nine cases
underneath: the compile, the load, the reload, the unload, the failed rebuild, the two authoring
shapes, and the project with no scripts at all.

**Two shapes, because the small one is the headline and the large one is the door.**

```csharp no-compile="the whole of a project's first editor tool"
[EditorMenu("Tools/Rebuild Navigation")]
public static void Rebuild() { … }
```

An `IEditorPlugin` in the same folder is handed the same `PluginContext` a packaged plugin gets, so a
script that wants a panel, a mode, a custom inspector or an asset kind writes what a plugin writes.

⚠ **A script is a plugin, and that decides everything else.** The compiled assembly goes into a
`PluginLoadContext` and through `PluginHost.Activate`, so it gets the registration scope, the
rollback-on-throw, the diagnostics, the plugin manager's row and the unload. A script host that
reimplemented any of those would be a second answer to a question that has one — and the one it would
get wrong is the unload, which is where every leak in this part of the editor lives.

⚠ **Roslyn in process, unlike the game code beside it.** `ProjectAssemblies` shells out to
`dotnet build` because a game's `.csproj` has a restore and package references only MSBuild resolves.
An `Editor/` folder is a pile of `.cs` files with no project file, referencing what the running
editor has loaded — nothing for MSBuild to work out, and a second process per keystroke would make
the loop useless.

⚠ **This is the one place in the editor that enumerates an assembly's types, and the bound is the
point.** ADR-002 forbids scanning as a way of building the editor for two reasons that both hold
elsewhere: a scan reads metadata a trimmed publish has deleted, and start-up cost grows with what is
installed. Neither applies to an assembly the editor compiled from source seconds ago in a folder it
is watching. What a script author cannot do is run a source generator over a loose `.cs` file, and
that is the whole of why tier three differs from tiers one and two.

⚠ **A failed build leaves the previous one loaded.** Somebody halfway through typing a method name
must not lose the menu they were about to use. What they get is the errors and the editor they had.

⚠ **`Vixen.Sdk` now excludes `**/Editor/**/*.cs` from a game's compilation, and without that the
convention was decoration.** Unity's second mechanism is a convention *with a compilation
consequence*; leaving the files in is not a warning but a broken build, because an editor script
references `Vixen.Editor.Plugin` for `[EditorMenu]` and a game does not have it. The failure would
have been a wall of CS0246 in files nobody asked to compile, in a project that was fine until they
wrote their first tool. `VixenExcludeEditorScripts` is the way out for a project whose folder is
called `Editor` by coincidence.

⚠ **`PluginHost` publishes itself, and `Activate` takes a load context.** Both are P5's, and both are
small: a module that loads more modules needs somewhere to put them, and a script assembly is the
first thing activated through that door that has an assembly to drop afterwards. `Activate` also
stopped refusing an id whose previous holder is unloaded — which is what a rebuild is.

**What is not built, and is named rather than implied:**

* **No incremental compilation.** A save rebuilds the folder — tens of milliseconds for a dozen
  files, and nothing here measures a project with hundreds.
* ~~**No `[CustomEditor]`-shaped attribute set**~~ — ✅ built after this phase and listed in
  [D3](#d3--discovery-is-declared-not-listed). The bullet was true when P5 shipped `[EditorMenu]`
  alone; the set landed in the commit that made all eight symmetric, and D3's table is the current
  statement.
* **No cross-assembly editor-only check.** The SDK keeps `Editor/` code out of the game's build; it
  does not fail a build that references an `Editor/` type from runtime code, because nothing compiles
  the two together to notice.

---

## Part 5 — The seams

Deliberately open, and named so the first project that needs one does not fork.

* **`IEditorRegistry`** — the shell reads it; three producers write it. A fourth (a remote plugin
  store, a scripted DSL) is a new producer and not a new shell.
* **`IEditProvider`** — how an `EditTarget` reaches its data. Entities, assets, graph nodes and a
  project settings file are four implementations, not four pipelines. ⚠ **Two exist** —
  `InspectorEditProvider` over the generator's descriptors, and `NodePortEditProvider` over a graph
  node's ports, which is the one that proves the seam takes weight: its members belong to no CLR
  type and the ordinary inspector panel draws them anyway. A settings file is still owed.
* **`IToolContext`** — what a scene-view tool is handed. Terrain's brushes and blockout's handles
  should be two implementations of the same thing; today they are two subsystems. ⚠ **This type does
  not exist**, in any form — it is a proposal, not a seam something is already using.
* ~~**Plugin API versioning**~~ — ✅ closed, and the bullet was stale. `PluginHost` calls
  `EditorApi.Explain(manifest.Api)` and refuses an incompatible plugin with the explanation. Widening
  the surface is what made it matter, which is what this bullet predicted.
* **A settings page and an asset preview** — D4's last two rows, and the only two of its nine that are
  still a list in the application rather than a registry the shell reads.

---

## Part 6 — What is owed

Measured against the tree, not against the phase list. Ordered by what a person would notice.

### The editing pipeline reaches three surfaces of four

**Transforms.** `EntityGizmoTarget.Record` still builds a `TransformTargetsCommand` and pushes it
onto `Document.Stack` rather than writing through an `EditProperty`. That is the last of the four
this section used to list — ⚠ **and it is blocked on a decision this document has not taken, not on
somebody doing the migration.** See [what routing the gizmo through the pipeline
costs](#what-routing-the-gizmo-through-the-pipeline-costs), below. The audit that closed the other
three is worth keeping:

* **Terrain and foliage** never needed one. Their settings are `[Inspector]` classes,
  `InspectorEditProvider` describes them, `InspectorView` draws them, and
  `TerrainBrushInspector.vxml` is already markup over one. This section counted them as owed on the
  strength of their *strokes* keeping their own commands — which they should: a brush dab is not a
  member anybody binds by name.
* **Node graphs** did need one, and it is `NodePortEditProvider`. A port's value lives on the graph
  keyed by name, so it is a member of nothing; describing it as an `InspectorMember` is what put the
  ordinary inspector panel behind the shader and VFX graphs' side panels.
* **Blockout** needed a panel rather than a provider, and now has one — ✅ `BlockoutMode.Panel` names
  a **Blockout** panel of three `InspectorView`s over `BlockoutRetopologySettings`,
  `BlockoutChartSettings` and `BlockoutPackSettings`, drawn by `InspectorEditProvider` like every
  other settings object in the editor. `BlockoutSettingsTests` asserts it by asking
  `ReflectedDescriptor` about *whatever the mode holds*, so a regression to the records fails it.

  ⚠ **"Annotating them, in one line each" was wrong three times over, and the correction is the
  point.** `UvSettings`, `PackSettings` and `RemeshSettings` are `init`-only records in
  `Core/Vixen.Geometry.*`, and each of these alone rules the one-liner out: the inspector's generator
  treats `init` as writable and emits `owner.Property = value`, which is a compiler error in
  generated code nobody sees; a `Core/` assembly cannot reference an editor one, which
  `ReflectedDescriptor`'s own remarks state as *"no runtime type carries `[Inspector]`, and none
  should"*; and a panel binds to an object that survives being edited, which a record replaced
  wholesale by every `with` expression is not. What the annotation goes on is the editable class
  beside each record — the arrangement `ModelImportEdits` already used, and the one a parity test
  keeps honest.

⚠ **This was recorded as done once, and then over-counted when the correction was written.** Both
mistakes have the same shape — the section was measured against the phase list rather than the tree.

⚠ **Still owed inside the node graph itself:** the port fields drawn *on* the node in
`NodeGraphView` are built by hand and are now the only place in that assembly that constructs an
editor for a port. They are a different surface with a different constraint — a row has to fit inside
a node that clips its own contents — so they are their own task rather than an oversight.

#### What routing the gizmo through the pipeline costs

Measured against the tree with a throwaway `IEditProvider` over `IGizmoTarget` and the real
`EditTarget`/`EditProperty`, driven against a real `SceneDocument`. **Two of the three things this
row assumed turned out not to be true, and the third is a design decision.**

⚠ **There is no coalescing to preserve, and the fear that there was is the wrong worry.** A drag does
not produce a stream of edits: `SceneViewport.EndManipulate` builds the `GizmoDrag` and asks the first
target for one entry, once, on mouse-up, then `Execute`s it and `Seal`s — `SceneViewport.cs:1135-1156`.
`TransformTargetsCommand.TryMergeWith` returns false on purpose. The gizmo owns the live manipulation
and the command owns the history, which is the division `CommandTransaction` makes for every drag.

⚠ **Multi-select is already the shape the pipeline would have to be bent into.** One command, N
targets, each with its own before *and* after triple, named `"Move (3)"` —
`TransformTargetsCommand.cs:59-72`. Through the pipeline a drag gives each target a *different*
value, so `EditProperty.Write` (one command, N objects, one value) does not apply and `WriteEach`
does; `WriteEach` executes one command **per target** (`EditProperty.cs:171`), and position, rotation
and scale are three members, so one entry becomes a `CompositeCommand` of 3N.

⚠ **And the blocker: the pipeline has no way to record a change that has already been applied.**
`IEditMember.CreateSetCommand(targets, value, document)` takes no before-state — every implementation
reads it at construction (`ReflectedDescriptor.cs:96`), and after a drag what it reads *is* the after
state, so the entry undoes to where the drag ended. Restoring the captured pose first does not defeat
that either, because `EditProperty.Write` and `WriteEach` both skip a target whose current value
already equals the one being written (`EditProperty.cs:115`, `:167`) — and
`IGizmoTarget.Position`/`Rotation` read `WorldTransform` while their setters write `LocalTransform`
(`Core/Vixen.Engine/Transforms/Transform.cs:68-69`, `:86-94`), so a read taken immediately after a
restoring write still returns the value the drag left. Measured: the first shape records **nothing**
(stack depth 0), and the second leaves the entity back where the drag started **with nothing on the
stack** — a silently discarded drag, and every existing test still green.

**So the phase needs one of two decisions, and neither is a migration.**

| | What it is | What it costs |
|---|---|---|
| A recording entry point | An `IEditMember.CreateSetCommand` overload — or an `EditProperty.Record(before, after)` — that is *handed* the before state rather than reading it | Public API on `Vixen.Editor.Core` that every provider inherits and every implementation has to mean something by. It is also the honest fix: "the surface applied it and is telling you" is a real case the seam does not have |
| Write per frame | Drop `TransformTargetsCommand`, have the gizmo's per-frame writes go through `EditProperty`, and let `SetValuesCommand.TryMergeWith` collapse them (`SetValuesCommand.cs:103-115`) | The design `TransformTargetsCommand`'s own remarks reject on cost: three hundred commands allocated and executed to move one crate, each re-applying a transform the gizmo already applied |

⚠ **What this row must not become is "it was migrated".** The gizmo is on one *stack* and always has
been — `OneEditPathTests` proves a drag and a field edit interleave correctly — and that is the
invariant § D1 actually asked for. What it is not on is `EditProperty`, and the distance between
those two is the table above rather than an afternoon.

### P3's three exit criteria, none met

| Owed | State |
|---|---|
| The `CheckArchitecture` rule | **Not started.** One rule in `build/Build.ArchitectureRules.cs`, and the cheapest item here. Without it every later row can be undone in silence |
| `AssetEditors` dereferenced | Needs `AssetEditorRegistry` somewhere both ends see. A decision, then a move |
| `Profiler` + `Debugger` dereferenced | Needs the diagnostics report to move into the module — which needs the log ring and the data directory published — *and* a deploy contribution for `EditorBuilds` |
| `EditorApplication.cs` under 800 lines | 3,787 today. A split of its own, and it moves away from the target unless scheduled |

⚠ **The exit list is corrected to allow `Assets`.** It is the import pipeline; an editor that cannot
import without a plugin is not an editor. The criterion is `Core`, `Ui`, `Plugin`, `Inspector`,
`SceneView`, `Assets`.

### The extension surface, last two rows

* **`AddSettingsPage`** — `EditorSettingsPanels` is still a list in the application.
* **`AddPreview`** — the thumbnail cache has no registry to ask before it falls back.
* **A build-step contribution.** `EditorBuilds` has no contribution point, so a plugin cannot add a
  step to a player build. Named here rather than at F8, which was about importers.

### Correctness gaps with a user-visible failure

* **A contributed importer does not reach an out-of-process compiler worker.**
  [`WorkerHost.cs:34`](../../Tools/Vixen.AssetCompiler/WorkerHost.cs) builds its registry from the
  parameterless `Create`, and the worker never loaded the plugin. Imports in the editor, fails in the
  pool — the worst shape on this list, because it is a difference between two paths that should agree.

### Smaller, and each a deliberate question rather than a lapse

* **`IsPackable` on `Vixen.Editor.Inspector.Generator`.** Now buys only the `[Inspector]`-specific
  annotations and the reset button — see [what this document does not
  do](#what-this-document-does-not-do).
* **`IToolContext`** does not exist; terrain's brushes and blockout's handles are still two subsystems.
* **No incremental compilation for project scripts**, and **no cross-assembly editor-only check**.
* **F7's number.** Three `.vxml` files against ~120,000 lines of hand-written editor C#. The path is
  walked, not adopted; nothing here proposes a sweep, and pretending three is a trend would be the
  same error this section exists to remove.

### What is *not* owed, so it stops being re-raised

* **The two engine registries stay two.** `SceneComponentRegistry` and `SceneBehaviorRegistry` are
  runtime registries with genuinely different contracts; F10's seam was the editor's vocabulary, and
  `AuthoringKind` on the bridge closed it. D5's first row is wrong as written and
  [P3b](#p3b--one-authoring-unit-and-icons-) says so.
* **`[EditorIcon("…svg")]`.** There is no SVG path parser and its absence is a decision. A type
  declares its icon by registering one.
* **`[Component]` and `[Behavior]` in `Editor/`.** `Editor/` converts the data, `Assets/` is the game.

---

## What this document does not do

* **It does not add a scripting language.** P5 compiles C#. A visual or interpreted editor-scripting
  layer is a different argument.
* **It does not redesign the UI framework.** `Vixen.Ui` and `.vxml` stay; P4 adopts what exists.
* **It does not promise Unity's API.** The mechanisms are the same shape; the names and the object
  model are ours, because our components are ECS data and `SerializedProperty` leans on Unity's
  object model in ways we should not copy.
* **It does not touch the runtime.** Every change is under `Editor/`, plus `Core/Vixen.Ui.Controls`
  for D6's icon art and `Core/Vixen.Ui.Markup` for P4's `binding-path`. ⚠ **An earlier revision said
  "plus one `IsPackable` in a generator", and that line now describes nothing.** F5's fix was to ship
  `Vixen.Editor.Inspector.Generator` so an out-of-tree plugin could obtain generated descriptors; D3's
  attributes made the *declaration* half of that unnecessary, and `ReflectedDescriptor` — which builds
  inspector rows from the serialization descriptor every `[DataContract]` type already has — makes the
  *default inspector* work for a plugin's own components without it. What packaging the generator
  would still buy is narrower: the `[Inspector]`-only annotations a serializer has no reason to know
  (conditions, multiline hints, panel-chosen header grouping) and the reset button on types whose
  constructor the fallback cannot reach. It is still unset, and it is now a small deliberate question
  rather than a fix in flight.

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
