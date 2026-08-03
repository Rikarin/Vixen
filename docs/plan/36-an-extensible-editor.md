<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 36 — An extensible editor

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

✅ **P1 answered the first two rows and the hook.** `EditTarget` and `EditProperty` are the one path
an inspector field, a scene-view tool or a plugin's panel writes through; `IGizmoTarget.Record`
replaced the hook and the type test beside it. ⚠ **The last three rows are still their own
commands, and P1 did not ask them not to be.** They already record onto the document's stack, so the
ordering is right; what they lack is a provider, which is what stops a panel binding their members by
name and is therefore P2's and P4's to fix rather than P1's.

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

✅ **Built.** `EditTarget`, `EditProperty`, `EditValue`, `IEditMember`, `IEditProvider` and
`SetValuesCommand` are in `Vixen.Editor.Core`; `InspectorField` derives from `EditProperty` rather
than reimplementing it, and `SetMembersCommand` survives as what an `InspectorMember` hands back from
`IEditMember.CreateSetCommand` — the typed accessors are the reason it exists and the pipeline does
not need them boxed away. `Records` is gone; see [P1](#p1--the-editing-pipeline-) for what was and
was not migrated.

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

✅ **Built, with one departure stated at [P2](#p2--the-registry----and-the-attributes-which-are-not-built).**
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

### P1 — The editing pipeline ✅

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

⚠ **What was not done, and is not owed by this phase.** Terrain strokes, foliage strokes, node-graph
edits and blockout keep their own commands. D1 only ever asked them to *declare* to one stack, which
they already do — they are `IEditorCommand`s on the document's `CommandStack`. What they do not yet
have is an `IEditProvider`, so a panel cannot bind their members by name; that is what P2's registry
and P4's markup need, and it is where the remaining value is.

⚠ **`EditProperty` and `EditorProperty{T}` are one letter apart and are different things**, which is
a hazard this phase created and documented rather than renamed away from: the doc named `EditProperty`
and the document model already had the other. One is a binding over N objects with no storage; the
other is a signal on one object. Both remarks say so at the type.

### P2 — The registry ✅ — and the attributes, which are not built

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

⚠ **D3's attribute set and its generator are not built, deliberately.** The exit criterion is met
without them and P3 does not need them — a feature registering in its own module initializer is
already AOT-clean and scan-free. Shipping `[CustomInspector]` with nothing reading it would be an
attribute that looks like a mechanism, which is the mistake doc 02 made with `GraphicsBackendSelector`
and which this document had to correct. **The ergonomics are owed to P3**, which is what will first
have a hundred registrations to write out by hand and will therefore know what the attribute has to
say.

### P3 — The built-ins move to the front door 🟡

Terrain, Blockout, Profiler, Debugger and the graph editors register through P2's API.
`Vixen.Editor.App` stops referencing them. `EditorApplication` becomes a host: open a project, load
modules, run a frame.

**Exit:** `Vixen.Editor.App.csproj` references `Core`, `Ui`, `Plugin`, `Inspector`, `SceneView` and
nothing else. `EditorApplication.cs` is under 800 lines. Every feature that worked still works, and
`CheckArchitecture` gains a rule that fails the build if a feature assembly is referenced again.

✅ **The seam is built.** `PluginHost.Activate(id, name, module)` runs a compiled-in `IEditorPlugin`
through the same `PluginContext`, the same registration scope, the same rollback-on-throw and the
same `Unload` an assembly off a disk gets — no `AssemblyLoadContext`, because a compiled-in module is
in the default one and pretending otherwise would report a leak for every built-in.
`PluginContext.FindMenu` and `AddSubmenu` are what a module needs to put its verbs in the menu the
thing they act on already has, rather than a top-level heading per feature.

✅ **Blockout is through it, and it is the feature F2 named twice.** `BlockoutModule` lives in
`Vixen.Editor.Blockout`, registers the mode and doc 24's five Scene submenus through
`PluginContext`, and asks the host for the four things it needs — the editing state, the work plane,
a mesh baker and a mesh source — through `PluginServices.Require`. `Vixen.Editor.App` holds one line
about it: the `Activate` call. **The assembly it lives in cannot see the editor's application at
all**, which is the part a compiler enforces rather than a convention.

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
| Terrain | `EditorTerrainPanels.cs` 739 + `EditorTerrainSession.cs` 601 | ~1,340 lines, both partials of `EditorApplication` |
| Profiler + Debugger | `EditorDiagnostics.cs` 500 | ⚠ needs a **third** assembly: the file's own remark is that neither feature knows what a project or a device is, "which is what lets both be tested against a bare `UiDocument`". Moving the joining code *into* them would destroy that |
| Asset editors | `EditorAnimation.cs` 341, plus registration in `EditorApplication.cs` | ~400 lines |
| Split the executable | `EditorHost.cs` 875 lines, `Program.cs`, `EditorPane.cs` in `Vixen.Editor.App` | one new project, six files touched outside it. **Last**, and it is what turns four `Activate` calls and four csproj lines into the exit criterion |
| `Vixen.Editor.Assets` | 8 files | ⚠ **Not a feature, and the exit list is wrong to omit it.** It is the import pipeline, and an editor that cannot import without a plugin is not an editor. F2's own inventory names it beside `Core`. Proposed: it stays, and the criterion is corrected to `Core`, `Ui`, `Plugin`, `Inspector`, `SceneView`, `Assets` |
| `EditorApplication.cs` under 800 lines | 3,675 today; the moves above account for ~2,400 across the partials | the remainder — project opening, panels, selection, play mode — is a split of its own and not this phase's |

### P3b — One authoring unit, and icons

D5 and D6, together because both are registry consumers and neither is worth a phase alone. The two
registries merge; `Prime()` goes; `Icon` gains a fill; `[EditorIcon]` resolves through P2's registry;
the Project panel, the Hierarchy and the inspector header read it.

**Exit:** Add ▸ lists components and behaviours in one sorted list and a behaviour's inspector is
indistinguishable from a component's. A `.vxterrainlayer` shows the same multicoloured icon in the
Project tree and the Project grid — F12's disagreement is the cheapest thing here to regression-test
— and so does an asset type contributed by the out-of-tree test plugin from P2. An icon whose paths
say "theme foreground" still inverts with the theme. `Prime()` no longer exists.

⚠ **And doc 04 gains [the authoring rule](#the-authoring-rule) verbatim.** It is stated here so the
editor can be built against it, but doc 04 is where a game author deciding between a script and a
system will look — and a rule that lives only in the editor's plan is a rule they will never read.

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
