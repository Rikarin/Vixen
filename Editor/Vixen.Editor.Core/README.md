# Vixen.Editor.Core

The editor's model of a project: what is open, what has been changed, how to take it back, and the
asset database everything is named through.

Spec: [docs/plan/11](../../docs/plan/11-editor.md) § "`Vixen.Editor.Core` — the document model",
[docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § "GUID index and conflict
handling".

```csharp
var project = new EditorProject(new ProjectPaths("/path/to/MyGame"));
foreach (var issue in project.Open().Issues) { … }

var document = new SceneDocument(project, sceneGuid);
document.Root.Roughness.Set(0.4f);      // one command on the document's stack
document.Stack.Undo();
document.Save();
```

## One mutation vocabulary

Everything the editor edits goes through `IEditorCommand`, so undo/redo, dirty tracking and the
remote inspector work uniformly instead of once per feature.

**Undo is a command stack, not a snapshot diff.** Snapshots are simpler and they cannot represent
"renamed this asset, which updated four hundred references" as one step, because the four hundred
documents were never opened. They also cannot collapse a drag: `TryMergeWith` is what makes three
hundred mouse-moves one undo entry that goes back to the value from before the drag, and a test
measures exactly that rather than asserting it.

**Merging ends where the shell says it does.** `Seal()` closes the newest entry, and the shell calls
it on mouse-up or on focus loss. The alternative — a time window — makes how many undo steps an edit
produced depend on how fast somebody moved a mouse, which is neither predictable for the user nor
testable without a fake clock.

**A transaction is the other half.** Merging is two commands discovering afterwards that they were
one edit; `BeginTransaction` is a caller saying up front that everything inside it is. It nests, it
records nothing when it collected nothing, and `Cancel` rolls back immediately rather than at commit,
so the viewport can be redrawn from the model the instant a drag is escaped.

## Two kinds of stack, and one rule where they meet

Each document has a stack. The project has one more, for the operations that are not inside any one
document — renaming an asset, moving a file — because putting a cross-document operation on whichever
document it happened to touch makes undoing it depend on which tab has focus.

The rule: **a command that touches a document other than its own stack's discards that document's
redo stack and marks it modified.** Those redo entries were recorded against a world that has since
moved and replaying them would write stale values back. Discarding is the only answer that cannot
silently corrupt a project; rewriting the affected entries needs every command type to know how to be
rebased, which is a research project and not a feature.

A command declares what it reached through `EditorContext.Touch`, so the blast radius is stated by
the operation that knows it rather than guessed at by the stack.

## Dirty has two sources, and only a save clears both

The stack's position covers edits made inside the document. A change that arrived from outside it is
tracked separately, because no amount of undoing inside the document takes it back.

The position is compared against where the last save was, and that comparison has one case worth
naming: undoing past the save point and then editing produces a stack of the same *depth* holding
different *content*. The saved point is on a branch that no longer exists, so it is dropped rather
than counted, and the document stays dirty. A dirty flag that only counted entries would call that
state clean and lose the file.

## One editing pipeline, and it is not the object model

`EditTarget` is what is being edited — some objects, the document they record into, and an
`IEditProvider` that reaches their members. `EditProperty` is one member bound to all of them.
Between them they answer, once, the four questions every editing surface otherwise answers for
itself: undo, editing N objects at once, what a disagreement looks like (`EditValue.IsMixed`, never
one of the values), and telling the rest of the editor that something moved.

⚠ **This is deliberately not `EditorProperty<T>`, which is one field on one object.** That is the
document model: storage, a signal, a typed value. This is a *binding* with no storage of its own,
over a member somebody else described and a selection somebody else made. They meet at the command
stack and nowhere else.

`IEditMember` is the whole contract a provider has to satisfy — a name, a type, a read, a write, and
the command that makes the write undoable — and `SetValuesCommand` supplies that last one for
anything without typed accessors, so merging and per-object old values come with the pipeline rather
than being rewritten per surface. The inspector's generated descriptors implement `IEditMember`
directly; a graph port, a settings row or a plugin's own member is a few lines over
`SetValuesCommand`.

The point of it being here rather than in the inspector is doc 36 § D1: an editor with five edit
paths has five answers to "what happens when twenty things are selected", and a plugin cannot join
an undo stack there is no shared way in to.

⚠ **`EditProperty.Apply` is the one funnel every write goes through, and it is `virtual` for that
reason.** `Write`, `WriteEach` and whatever a deriving type adds all end there, so a subclass hangs
"a write landed" on one method instead of a list of callers. `InspectorField` is why: a prefab
instance has to record that it now claims the member, for every write and exactly once per write.
⚠ Anything hung there has to earn its transaction — a committed transaction records a
`CompositeCommand` and a `SetMembersCommand` cannot merge with one, so an unconditional wrapper turns
a three-hundred-frame slider drag into three hundred undo entries.

A binding raises `EditProperty.Changed`, and the target aggregates all of them into
`EditTarget.Changed`. Both, because they answer different questions: a surface that built a row knows
which binding it is watching, and one whose body was filled in by a markup tree or by somebody else's
builder never sees the individual bindings at all. ⚠ **Walking `Properties()` afterwards and
subscribing to each is the near miss** — it covers what existed at the moment of the walk and nothing
bound after it, which is a foldout opened later or a `.vxml` re-bound by a hot reload. Only bindings
the target handed out are heard; an `EditProperty` constructed beside one is not among them.

## One contribution registry, and it is not the only registry

`EditorRegistry` is a typed multimap: `Add(contribution)` files something under its own type and
hands back the removal, `All<T>()` reads a kind back, `Changed` says which kind moved. Three
producers write it — a generated registration, a plugin's `Activate`, and eventually a project's own
`Editor/` scripts — and a consumer cannot tell them apart, which is the whole property. A fourth
producer is a new producer and not a new consumer.

Adding a contribution *kind* is declaring a record in the assembly that owns it: `NewAssetKind` here,
`CustomInspector` in `Vixen.Editor.Inspector`, `SceneTool` in `Vixen.Editor.SceneView`. Nothing in
this file changes when one arrives, and neither does the plugin contract.

⚠ **What goes here is what had no owner.** Commands, panels, layouts and modes belong to
`EditorShell`; drawers belong to `DrawerRegistry`; described types belong to `InspectorRegistry`.
Copying any of them here would make a plugin's drawer land in whichever of two registries the
inspector was not reading — which is the mistake doc 36 § F10 reports at scale, and it is worse than
having two ways to register because it looks like one.

## The object model is signal-backed

An `EditorProperty<T>` is a `Signal<T>` with the write routed through the command stack. The
inspector binds to the property; a gizmo drag writes it; the inspector updates. There is no change
event to raise, no listener list to unsubscribe from on tab close, and no path by which two views of
one value disagree — which is the signal investment from
[docs/plan/09](../../docs/plan/09-ui-framework.md) paying off outside the UI framework.

Reading is through the signal and writing is through `Set`, and that asymmetry is the point: the
write goes to the stack, so "every edit produces a command" is true by construction rather than by
every drawer remembering to do it. Everything a menu binds to is a signal too — `CanUndo` for
enablement, `UndoName` for the label, `IsDirty` for the asterisk in the title bar.

## Project settings are assets

A settings type is an ordinary `[DataContract]` type under `ProjectSettings/`, read by the same YAML
binder and drawn by the same inspector as a material. Adding a project setting is declaring a type,
not also writing a dialog.

One file per type, **named after the contract's alias rather than the C# type**, so renaming the type
does not orphan every project's settings. A missing file means the defaults and is not an error, and
nothing is written until something asks for it — a fresh checkout does not acquire a directory of
files full of defaults. An unknown key is ignored so that a project written by a newer editor still
opens, and reported through `UnknownKeys` so that it is not ignored *silently*.

## Making one

`ProjectScaffold` writes a new project from a template, and `TemplateCatalog` reads the templates out
of this assembly — where the build embeds `Tools/Vixen.Templates`' one tree, so `dotnet new
vixen-game`, `vixen new game` and the editor's New Project all write the same files.

⚠ **Here rather than in `Tools/Vixen.Cli`, which is where it was, and for `ProjectWorkspace`'s
reason.** The editor's New Project made two directories and called it a project — true enough for
`AssetDatabase`, which tolerates every directory being absent, and false for everything downstream: a
project with no `.csproj` is one `dotnet publish` has nothing to publish, so doc 20's Build and Run
was greyed for every project the editor had ever made, with a message naming a terminal command. A
second copy of the scaffold is the thing `TemplateCatalog` was written to prevent one level down, so
the type moved and the payload moved with it.

⚠ **The decisions are values and the console is the CLI's.** Which template, whether the name can be
a namespace, what would be overwritten, what was written: `ScaffoldRunner` formats a `ScaffoldResult`
and nothing more, which is the split `ImportRunner` and `ContentPipeline` already make.

⚠ **A new project carries a `.vxproj`, which doc 08 named and nothing wrote for two years.**
`ProjectMarker` is that file, and it is a *marker* rather than the "project settings (YAML)" doc 08
called it — the settings half was answered by `ProjectSettings/` while this went unbuilt, and a
second place to put project settings is what that split exists to avoid. What it answers is what
`Assets/`-exists could not: a source tree that happens to contain a folder called `Assets` is not a
project, and a project whose assets have all been deleted still is. Both rules are live, because
every project made before the marker has to go on opening.

Two fields, and each has a reader: `format` is refused when it is newer than this build understands,
and `engine` is what makes the editor say "this project was made with a newer Vixen" at the door
rather than failing later and stranger on a component it has never heard of. It does not record the
project's *name* — that is `ProjectInfoSettings.ProductName`, and two files answering "what is this
called" is the disagreement doc 20's A4 spends a page preventing.

⚠ **`NameFrom` exists for the editor and the CLI does not use it.** A name typed as an argument
should be refused when it cannot be a namespace; a name that *is* whatever folder somebody picked in
a file dialog should not — "my game (2)" is an ordinary directory and an impossible identifier, and
refusing it would be the editor rejecting a folder it had just watched them create.

## The asset database

`Assets/` is scanned into a GUID index; `ReferenceIndex` answers "what breaks if I delete this".

### The GUID is the identity; the path is a fact about today

Everything stored in a file is a GUID, so moving, renaming or reorganising folders changes nothing
anywhere. This is what makes that true: the one place that knows which GUID is currently at which
path.

**Rebuilt by reading only envelopes.** Doc 08 budgets a hundred-thousand-asset rebuild at under ten
seconds. That is achievable because `MetaScanner` reads three lines of each sidecar and stops, and
because the files are read in parallel — an I/O walk over thousands of small files leaves the cores
idle otherwise. Ten thousand assets are measured in the test suite; the assertion is loose on purpose,
because it exists to fail when someone makes the scan read whole documents again, not to police a
machine's disk.

**Insertion is sequential and in path order.** Duplicate resolution has to give the same answer on
two machines scanning one checkout, and directory enumeration order is not a promise any filesystem
makes.

### Freshness is per entry, so a miss costs what changed

`Library/GuidIndex` records, beside each entry, the **size and write time of the sidecar it was read
from**. A scan over a loaded index opens only the sidecars whose stamp has moved, so a cold start
after one file changed reads one file. It used to be that freshness was asked once about the whole
database — a `.meta` count and a newest write time — and the answer was a single yes or no, so any
change at all cost a full rebuild of the index. Measured on a ten-thousand-asset project, that case
went from roughly 460–900 ms to 110–160 ms; what remains is one directory walk, which at that size is
about 70 ms and is the floor.

**A size and a write time, because the honest answer costs a read** and the read is the thing being
avoided. Hashing the sidecar would never be wrong and would mean opening every file in the project on
every launch. A size alone is far too weak: a sidecar is mostly fixed-width fields, so most edits
leave it exactly as long.

⚠ **What that gets wrong, and in which direction.** An edit that changes neither size nor write time
is invisible — hand-editing a GUID is the realistic one, since a GUID is fixed-width. A checkout or a
copy stamps the files it touches with the time it ran, so it reads as *changed* and costs a re-read it
did not strictly need. That is the direction to be wrong in: a false "stale" wastes a scan, a false
"fresh" loses an asset.

⚠ **A scan records no stamp at all for a sidecar it wrote itself** — one it minted, or one it
re-GUIDed to settle a duplicate. A filesystem cannot tell an edit that lands a moment after that write
from the write itself when both fall in one write-time tick, so the next scan opens the file. It does
that on a fact the scan knows rather than on a timestamp, which is why it holds at every resolution
and why a project settles in exactly two scans everywhere rather than in however many the machine's
clock happens to allow.

It used to be a timestamp: trust no stamp whose write time is not strictly older than the moment the
recording scan began. ⚠ **That is only sound where the clock and the filesystem's write times share a
resolution, and on Windows they do not** — `DateTime.UtcNow` reads the precise system clock while NTFS
stamps a write from the coarse one, so a sidecar written *after* a scan started carries a write time
up to a tick *before* it and the next scan trusted it. No cutoff fixes this: flooring it to the
filesystem's resolution is sound and then refuses every file written in the tick before a scan, which
costs an untouched project a full re-read. The cutoff stays as a second, weaker filter, for the one
thing the stamps cannot know — an edit by *somebody else* that raced the walk — with the hole that it
under-fires wherever the clock is the finer of the two.

⚠ **A partial rescan cannot leave the index wrong**, which is the case a crash makes real. The index
is written beside itself and renamed over, and it ends with a terminator naming its own entry count,
so a torn file is refused outright rather than read as a short but plausible index. And every reuse is
checked against the disk rather than believed: an entry whose stamp does not match is read, and an
entry with no asset under it is dropped. Both failures land on "rescan".

`ScanReport.Reused` and `ScanReport.Rescanned` are what make the cost legible, and `Rescanned` is what
the tests assert — how many sidecars a scan opened is a property of the algorithm and is the same on
every machine, where a wall-clock threshold under a parallel test run measures the machine.

### Nothing is silently tolerant

Every one of these is a thing that happens to real projects weekly, and silent tolerance is how
projects rot.

| Found | Done |
|---|---|
| A file with no sidecar | One is created with a fresh GUID |
| A sidecar with no file | Moved to `Library/OrphanMeta/`, **never deleted** |
| Two assets claiming one GUID | The one whose recorded `sourceHash` still matches its bytes keeps it; the other is re-GUIDed |
| A sidecar with no readable GUID | Reported and left alone |

The orphan is moved rather than deleted because a mis-ordered git operation is recoverable if the
GUID is still somewhere on disk, and is not if the editor helpfully tidied it away. The unreadable
sidecar is left alone because minting a new GUID would break every reference to that asset — an asset
the editor refuses to touch until a person looks at it is the better outcome.

When no hash settles a duplicate, the first path in order keeps the GUID. A rule, so that two
machines agree, rather than whichever file the filesystem handed over first.

`ScanOptions.ReadOnly` reports all of it and changes nothing, because a build server asking "is this
project clean?" wants the answer and not a working tree with edits in it.

### `AssetTree` — the index as a browser sees it

The database is a dictionary keyed by path and by GUID, which is the right shape for "what is this
GUID" and the wrong one for "what is in this folder". `AssetTree.Build` is the difference: a flat
`IReadOnlyCollection<AssetEntry>` in, an immutable tree out.

It is here rather than beside the panel that shows it because **`Vixen.Editor.Core` does not
reference the interface framework** — the same split `DockLayout` and `NodeGraph` make, and it means
the ordering and the search can be asserted on without a document, a stylesheet or a font.

⚠ **A folder with no entry is still a folder.** `ScanOptions.ReadOnly` creates no sidecars, so
folders come back unindexed while the files inside them are still indexed by path. Requiring an entry
per level would silently drop every asset under such a folder — a browser that is empty for a project
that is not.

⚠ **The order is imposed, not inherited.** `Entries` is a dictionary's values and says so. Folders
sort before files, then by name case-insensitively, then ordinally — the last of those because
ignoring case leaves `README` and `readme` equal, and equal means whichever the enumeration reached
first, which is not an order.

⚠ **A search keeps a folder for what is in it, not for what it is called.** Matching folder names and
dropping the rest would hide every file in a folder whose name does not contain the search, which is
the opposite of what typing a file name is for. A folder whose own name matches keeps its whole
contents, because that is navigation rather than a search.

### The reference index is a grep, and that is deliberate

`ReferenceIndex` scans text for `vx:` followed by thirty-two hex digits. That is sound *because of
how the reference format was chosen*: doc 08 picked a single prefixed scalar over Unity's three-key
flow mapping partly so that `rg 'vx:9e8a44c9'` finds every referrer. This is that grep, done once and
kept.

Parsing instead would mean binding every scene, material and prefab in the project — the expensive
half of opening one — to answer a question asked about one asset at a time. It would also fail on
exactly the files most likely to matter: an asset whose importer has been uninstalled cannot be
bound, but it can be read, and "which of my scenes still references this" is the question you ask
about *that* asset.

What a scan can do that a parse cannot is find a reference inside a comment. That is a false positive
in a report nobody is harmed by; the alternative is a missed reference in a "safe to delete" answer,
which corrupts a project.

Sidecars are scanned as part of the asset they belong to — a model importer's `materialMapping` holds
references, so a `.meta` is as much a referrer as a scene.

## The scene file format

`Scenes/SceneFormat.cs` is what a `.vxscene` binds to: `EntityId`, `SceneEntityData`, `SceneFile`,
and the scalar converters that make `position: 1 2 3` one line instead of fifteen.

**It is here rather than beside the viewport because two things read it** — the panel that edits a
scene (`Vixen.Editor.SceneView`) and the importer that compiles one (`Vixen.Editor.Assets`) — and
neither should have to reference the other. It is the file format and not the document: no ECS world,
no command stack, nothing but the shape on disk and the version check that refuses a file from a
newer build.

### Prefab instances and their overrides

`Scenes/PrefabOverrides.cs` is the other half: four keys on `SceneEntityData` — `Prefab`, `Source`,
`Overrides` and `Removed` — plus the pure logic that reads a member by path, marks and clears an
override, and brings a scene back in step with a prefab that has changed underneath it.
`Scenes/PrefabReconcile.cs` is what finds the template: it turns the `vx:` reference a scene carries
into a file on disk through `AssetDatabase`, and reports every prefab it could not open rather than
refusing to open the level.
[plan/47](../../docs/plan/47-prefab-overrides-and-nested-prefabs.md) is the decision record and
[the guide page](../../docs/guide/editor/prefab-overrides.md) is how to use it.

Six things are worth knowing before touching it:

⚠⚠ **The override list is names, never a comparison.** If overridden-ness were "differs from the
template" or "is not the default", an author who turns a lamp's intensity down to `0` has said
something the file cannot represent, and the next reconcile turns it back on. Presence in the list
*is* the override.

⚠ **The file keeps every value in full**, rather than only the overrides, because an importer is
handed an `AssetId` and no way to resolve one to a path — so `SceneCompiler` could not open the
prefab an instance names. That makes the three keys additive: the compiled path, the runtime and
every scene already on disk are untouched, and a missing prefab degrades to an ordinary subtree
rather than an empty one.

⚠ **A reconcile writes values and removes nothing** — not an entity, not a key, not an override
entry. Everything it cannot resolve comes back as a `PrefabReport` and stays in the file, because a
subtree deleted on open is unrecoverable and a dropped override is silent.

⚠⚠ **`Removed` had to land before anything adds a template's children back.** With resolved values in
the file, a child the author deleted is simply absent — and "the author deleted this" and "the
template gained this since" are the same absence. That day has come: a child added to a template is
now grafted into every instance of it, and the *only* thing that keeps a designer's deletion deleted
is that list. It landed a slice earlier on purpose, so every scene saved in between already carries
the answer.

⚠ **The unit of both structural rules is a *run*** — the topmost entity of a contiguous span sharing
one `prefab`, which is what "the instance root" means. It is deliberately the same definition
`SceneDocument.TryGetInstanceRoot` uses when a delete writes a removal down. A reader and a writer
that disagree about which entity is the root is a level that regrows what its designer deleted, and
every test using a single instance would pass.

⚠⚠ **Nesting cannot be seen from one template, so `Reconcile` takes them all.** A scene node inside an
instance of A that carries B's link is reachable from both, and they disagree — B's file holds none of
A's overrides over B. A run whose root sits inside an instance of a *different* prefab is therefore
paired with the node **that** template carries for the same `(prefab, source)`, and `PrefabReconcile`
composes each template against its own inner prefabs before touching the scene. One lookup outward,
never a fixpoint. ⚠ A run inside an instance whose template could not be opened is left exactly as the
file has it: the guess that is available is the destructive one.

`PrefabOverrides` is pure over the format: no `World`, no `SceneDocument`, no project on disk.
`PrefabReconcile` is one step up — it reads files — and is still not a document: it rewrites a parsed
`SceneFile` before anything builds a world out of it, which is what makes reconciliation testable
without an editor. The wiring is in `Vixen.Editor.SceneView` (`SceneSerializer.Open`,
`SceneDocument.Prefabs`) and `Vixen.Editor.AssetEditors` (`Prefab.TryPlace`, the drop verb).

⚠ **Migration**: the keys are additive and `SceneFile.Current` stays at `1`, but `OmitDefaults` is off
for this format — so the first save of any existing scene gains `prefab: ''`, `source: 00000000…`,
`overrides: []` and `removed: []` on every entity. Nothing is read differently and no data moves.

⚠ **Migration, add-back**: no key, no version change, no rewritten file — the cost is *entities*. The
first open of a level whose prefab has gained children gains those entities, and the next save the
author makes writes them out. Nothing is added that the instance's `removed:` list names, nothing is
added twice (the check is by the template's id, which the graft records as `source`), and the open
itself writes nothing to disk.

## What is not here yet

The document model is the vocabulary and the stacks; the concrete documents live where their subject
does. ✅ `SceneDocument` is in [`Vixen.Editor.SceneView`](../Vixen.Editor.SceneView/README.md) —
there rather than here because a scene *is* an ECS world and this project deliberately does not
reference `Vixen.Ecs`, so the command stack and the asset database stay testable without one. ✅
Multi-object editing and the generated drawer descriptors are in
[`Vixen.Editor.Inspector`](../Vixen.Editor.Inspector/README.md).

✅ **The asset editors are in [`Vixen.Editor.AssetEditors`](../Vixen.Editor.AssetEditors/README.md)**
— a material, a texture, a model, a prefab, a shader, a stylesheet, an addressable group and a
graphics compositor each have a document and a view, and one registry says which of them claims a
file. They are there rather than here for the reason `SceneDocument` is: a document belongs beside
its subject, and this project deliberately references neither the interface framework nor the
importers.

**Following the disk.** The head owns an `IFileWatcher` over `Assets/` and drains it on the frame;
this project holds the half that knows what a change *means* to something that is open.
`ExternalEdits` turns a watched path into the document editing that asset — through the GUID index,
so it has to run after the rescan — and applies the one policy in the seam: a document that can
re-read its file and has nothing unsaved is reloaded, and one with unsaved edits is left exactly as
it is, marked `EditorDocument.IsStale`, and reported. Memory is the only copy of itself and a file is
not, so the reversible choice is to keep both and let a person pick.

It is also what stops the editor arguing with itself. `EditorProject.DocumentSaving` fires *before*
`SaveCore`, which is the only ordering at which `IFileWatcher.Suppress` can beat the write to the
disk — that call is what keeps a Ctrl+S from arriving back as somebody else's edit.

**Watch-driven re-import** is still the import pipeline's in `Vixen.Editor.Assets` and is still not
built: a change rescans the database and reaches the open documents, and does not re-run an importer.

Licensed under Apache-2.0.
