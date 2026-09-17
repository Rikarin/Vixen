<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 50 — The editor as bounded contexts

The editor has a plugin contract, a typed registry, eight Unity-shaped declaration attributes, an
architecture rule that forbids the application from referencing a feature, and a 14,823-line
`sealed partial class EditorApplication` spread over thirteen files, with 434 fields, a 720-line
constructor and every verb the editor has as a private method closed over those fields. Both
statements are true, and the second is why the first is not yet an architecture.

> **Status.** Written 2026-09-17. Nothing in it is built. Every number in Part 1 was measured on
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
| In-process modules through the same door | `PluginHost.Activate(id, name, module)` — `PluginHost.cs:522` | **Real.** Seven built-ins go through it: `EditorModules.Standard()`, `Editor/Vixen.Editor.Host/EditorModules.cs:42-66` |
| Typed contribution registry | `IEditorRegistry` / `EditorRegistry` in `Vixen.Editor.Core` | **Real.** Twelve contribution records, each in the assembly that owns it |
| The eight declaration attributes | `EditorMenuAttribute` (Plugin), `CreateAssetMenuAttribute` (Core), `[CustomInspector]`/`[CustomDrawer]` (Inspector), `[EditorTool]`/`[Overlay]`/`[DrawGizmo]` (SceneView), `[Importer]` (Assets) | **Declared and read** — `DeclaredContributions : IContributionScanner`, `Editor/Vixen.Editor.App/DeclaredContributions.cs:47` |
| The architecture rule | `build/ApplicationReferenceRule.cs` | **Real**, with `Allowed` (6) and a shrink-only `NotYetMoved` (5) |
| The editing pipeline | `EditTarget` / `EditProperty` / `IEditProvider` / `SetValuesCommand` in `Vixen.Editor.Core` | **Real**, reached by the inspector and node ports; not by the gizmo (doc 36 § Part 6) |
| Domain kernel with no UI | `Vixen.Editor.Core.csproj` | References neither `Vixen.Ui` nor the importers, and its README says so deliberately |

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

434 field declarations in the main file alone; 12 public methods; 40 `using`s, four of them feature
namespaces (`AssetEditors`, `AssetEditors.Content`, `Debugger`, `NodeGraph`). The 720-line
constructor is where every concern is joined to every other, and the App README's *"Load order,
which is not arbitrary"* (`Editor/Vixen.Editor.App/README.md:104`) is the documentation of that
joining.

⚠ **The verbs are private and closed over the fields.** `Group()`, `SnapToFloor()`, `EnterPlay()`,
`ExportPackage()`, `BringIn()`, `DropIntoScene()` — `EditorParity.cs:684-2999` — are instance
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
built. `PluginHost.Declared(context, assembly)` (`PluginHost.cs:757`) is the reflective scan for a
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

`ApplicationReferenceRule.NotYetMoved` (`build/ApplicationReferenceRule.cs:116`): `AssetEditors`,
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
the same way the attributes are.

**F5 — The feature assemblies have their own god files.** `LayerStackView.cs` 3,783,
`TexturingModule.cs` 2,381, `LayerStackGraph.cs` 2,019, `TextureGraphCompiler.cs` 1,809,
`SceneViewport.cs` 1,668, `ProjectBrowser.cs` 1,491, `BlockoutMode.cs` 1,462,
`EditorWorldRenderer.cs` 1,435, `NodeGraphView.cs` 1,392, `EditorHost.cs` 1,410, `EditorShell.cs`
1,180, `ComponentsView.vxml` 1,117. Moving a feature behind a module boundary did not make the
feature modular inside; a `TexturingModule` of 2,381 lines is the application's shape reproduced
one level down.

**F6 — The plugin's view of the editor is a bag of fourteen services and the shell.**
`EditorApplication.PluginPoints()` (`EditorApplication.cs:2855-2949`) publishes: the project, the
scene, `DrawerRegistry.Default`, `ImporterContributions.Default`, the extension registry, the
editing state, the work plane, `IMeshBaker`, `IMeshMapBaker`, `IMeshSource`, `IActiveScene`,
`IActiveView`, `IDeviceDeploy`, `IEditorGraphics`, the asset editors, the reload host. Two of them
are process-wide statics handed over as if they were the host's. Nothing in the list is a *use
case* — a plugin gets the scene document and may do to it what it likes; it does not get *"delete
the selection, undoably, the way the Delete key does"*.

⚠ **The four sub-audits.** A state census of the class by concern, a `file:line` list of every
feature name the shell assemblies carry, the front-door sufficiency of the eight modules, and the
domain-model / view-leak survey were run in parallel with this draft. Their tables amend this Part
when they land; the design below does not depend on their figures, only on their existence.

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
| **Domain** | **Project** | `EditorProject` | paths, recents, scaffold, disk watch, project assemblies, external-edit announcement — `EditorProjects.cs`, `Watch`/`FollowDisk`/`BuildProjectCode` (`EditorApplication.cs:1682-2103`), `ProjectAssemblies.cs`, `ProjectHistory.cs` | `Vixen.Editor.Core` (already) |
| | **Scene Authoring** | `SceneDocument`, `Selection<Entity>` | hierarchy, entity verbs (create, group, parent, duplicate, align, snap, LOD), marks, bounds — `EditorParity.cs:684-855, 2413-2880`, `EditorSelectionVerbs.cs`, `EditorApplication.cs:4337-5532` | `Vixen.Editor.SceneView` + a service |
| | **Content Library** | `AssetDatabase` | browser model, collections, saved filters, thumbnails, import and content build, drag-and-drop, package export — `EditorParity.cs:411-684, 1512-2005`, `EditorApplication.cs:3315-3440, 4663-4847`, `ContentTasks.cs`, `AssetCollections.cs`, `ThumbnailCache.cs` | `Vixen.Editor.Assets` + a service |
| | **Documents** | `EditorDocument`, `CommandStack` | open, reopen, join to a panel, `AssetEditorRegistry`, `Saved`, `Opened` — `EditorApplication.cs:2662-2855` | `Vixen.Editor.Core` (already) |
| | **Editing** (shared kernel) | `EditTarget`, `EditProperty`, `IEditProvider` | the one edit path, doc 36 § D1 | `Vixen.Editor.Core` (already) |
| | **Session** | `PlayModeController` | enter/stop play, snapshot/restore, `PlayPhysics`, play diagnostics — `EditorParity.cs:855-954, 2880-2999`, `PlayPhysics.cs` | `Vixen.Editor.Core` |
| | **Delivery** | `BuildPlan` | build settings, Build and Run, deploy, `BuildStep` contributions — `EditorBuilds.cs`, `EditorApplication.cs:5046` | `Vixen.Editor.Assets.Content` |
| **Application** | **Workspace** | `DockingWorkspace`, `EditorUserStore` | panels, layouts, view bookmarks, what persists — `EditorApplication.cs:1434-1502, 2223-2662, 3452-3529, 5580` | `Vixen.Editor.Ui` (already) |
| | **Extensibility** | `PluginHost`, `EditorRegistry` | modules, disk plugins, scripts, hot reload, `PluginPoints` — `EditorApplication.cs:2855-3049`, `Vixen.Editor.Scripts` | `Vixen.Editor.Plugin` (already) |
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
    void Group();                       // EditorParity.cs:2474
    void GroupAsLod();                  // EditorParity.cs:2529
    void SnapToFloor();                 // EditorParity.cs:2799
    void AlignWithView();               // EditorParity.cs:2705
    Entity CreateShape(PrimitiveKind kind);   // EditorApplication.cs:5488
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

⚠ **`Docs` is not `CheckDocs`, and each new public service type owes a guide page or a
`DocsExempt` line** (CLAUDE.md § Gates). Seven services and two contribution records is nine pages.

---

## Part 4 — Phases

Each shippable, the editor working throughout, tests green at every merge. Order: instruments,
then the change that gives the attributes authors, then the extraction that the instruments can
watch, then the feature-side god files. **The risk is in P2**, and P0 is what makes P2 visible.

### P0 — Instruments (≈ 0.5 EM)

`ApplicationSizeRule`, `FeatureNameRule` with its measured exemption list, `ReachTest`. Land the
`FeatureNameRule` first, because its exemption file *is* the audit and every later phase deletes
lines from it. Sabotage each: add a `Vixen.Editor.Terrain.TerrainMode` name to `EditorShell.cs`, add
50 lines to `SceneMenus.cs`, remove `WaterModule` from `Standard()` — three red runs, then revert.

**Exit:** three rules in `CheckArchitecture`, each with a false-positive-free run over the tree and a
test that shows it can fire (`ApplicationReferenceRuleTests` is the shape). #1276 decided and closed.

### P1 — The generator and the first attribute authors (≈ 1 EM)

`Vixen.Editor.Plugin.Generator`; `EditorMenuAttribute.Title`; `DeclaredParityTest`. Migrate
**Blockout** first — 11 registrations, the smallest clean module — then **Water** (11) and
**Terrain** (22). Each migration deletes the module's hand registrations and is proved by the
`ReachTest` staying green with the attributes and going red without them.

**Exit:** three modules with zero hand registrations; the seven attributes have ≥ 3 production users
each or a stated reason one cannot (`[DrawGizmo]` may have one); `DeclaredParityTest` green.

### P2 — The services, one context at a time, in place (≈ 3 EM)

The extraction. Order by the census's *"cheap moves"* first (methods touching one concern's fields)
and by what a plugin most plausibly wants:

| Step | Service | Out of | Moves with it |
|---|---|---|---|
| 2a | `IPlaySession` | `EditorParity.cs:855-954, 2880-2999`, `PlayPhysics.cs`, `DrainPlayDiagnostics` | the smallest; proves the shape |
| 2b | `ISceneEditing` | `EditorParity.cs:684-855, 2413-2880`, `EditorSelectionVerbs.cs`, `EditorApplication.cs:4337-5532` | `SceneEntity.cs`; the gizmo recording decision (§ P5) |
| 2c | `IContentLibrary` | `EditorParity.cs:411-684, 1512-2271`, `EditorApplication.cs:3315-3440, 4663-4847` | `AssetCollections`, `ThumbnailCache`, `ContentTasks`; the `NewAssetKinds` literal becomes `LibraryModule`'s |
| 2d | `IProjectSession` | `EditorProjects.cs`, `EditorApplication.cs:1682-2103` | `ProjectAssemblies`, `ProjectHistory`, the watcher |
| 2e | `IDocumentHost` + `IWorkspace` | `EditorApplication.cs:1434-1502, 2223-2855, 3452-3529` | § D4's two registries; `Inspecting` |
| 2f | `IPlayerDelivery` | `EditorBuilds.cs`, `AnalyseContent` | the deploy contribution (frees `Debugger`) |

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
`ImporterContributions` like a plugin's.

**Exit:** `NotYetMoved` is empty and the rule says so (it must be deleted rather than left empty —
an empty shrink-only list asserts nothing).

### P4 — The feature-side god files (≈ 2 EM, parallelisable per feature)

The same three moves inside each feature: model out of the view, verbs onto a service, registration
through attributes. Targets in order of what a user hits: `SceneViewport.cs` (1,668 — the thing every
tool is handed; doc 36 § Part 5 says narrow it *when a consumer can say which members matter*, and
after P2 the consumers can), `ProjectBrowser.cs` (1,491), `NodeGraphView.cs` (1,392),
`LayerStackView.cs` (3,783) and `TexturingModule.cs` (2,381), `BlockoutMode.cs` (1,462),
`ComponentsView.vxml` (1,117). Each is its own issue with its own ceiling in `ApplicationSizeRule`'s
table, which is renamed `FileSizeRule` the day it gains a second assembly.

### P5 — The gizmo decision (≈ 0.25 EM, a decision before a change)

Doc 36 § Part 6 measured the two options — a recording entry point (`EditProperty.Record(before,
after)`) or per-frame writes coalesced by `SetValuesCommand.TryMergeWith`. **This document
recommends the first**: *"the surface applied it and is telling you"* is a real case, and `ISceneEditing`
is its first non-gizmo customer (a script that moved an entity wants the same entry). It lands in 2b.

---

## Part 5 — What this document does not do

* **It does not move the command system.** Docs 45/46/49 own that; this document registers into
  whatever they leave and calls whatever they resolve.
* **It does not add a DI container, a mediator or a message bus.** `PluginServices` by name and
  `IEditorRegistry` by kind are the two lookups, and they are enough.
* **It does not turn every feature assembly into a disk plugin.** In-process modules through
  `Activate` are the front door; #1276 decides whether Texturing stays the one disk-shaped
  exception.
* **It does not redesign `.vxml`, the inspector, or the editing pipeline.** It gives them more
  callers.
* **It does not touch the runtime**, and it makes no change under `Core/` except a possible new
  home for `IDeviceDeploy`'s records if the delivery context wants one.

---

## Part 6 — Estimate and risk

≈ 7.5 EM sequential; P4 parallelises across features and P2's steps parallelise poorly (all touch the
constructor). The risk is concentrated in P2b and P2c — the two largest verb sets, both with
drag-and-drop and both read by the outliner, the browser and the viewport at once — and the mitigation
is P0: a ceiling that must go down each merge, a name list that must shrink, and a reach test that
says whether the menu still has its lines. A phase that lands without moving those numbers has not
landed, whatever its branch says.
