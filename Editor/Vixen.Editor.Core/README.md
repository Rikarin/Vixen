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

## What is not here yet

The document model is the vocabulary and the stacks; the concrete documents are not. A
`SceneDocument`, a `MaterialDocument` and the drawers that edit them arrive with
`Vixen.Editor.Inspector` and `Vixen.Editor.SceneView`, which is also where multi-object editing and
the generated drawer descriptors land. Watch-driven re-import belongs to the import pipeline in
`Vixen.Editor.Assets` and is wired to the database from the shell.

Licensed under Apache-2.0.
