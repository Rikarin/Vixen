# 47 — Prefab Overrides and Nested Prefabs

⚠️ **Extends [08](08-asset-pipeline-and-addressables.md) and [11](11-editor.md), and resolves
[15](15-risks-and-open-questions.md)'s R7 into a format.** R7 is the largest unstarted item in the
scene layer and it is a risk because it is a *format* decision: a prefab instance's shape in a
`.vxscene` is the thing that cannot be changed afterwards without a migration over every level in
every project. This document decides the format, costs the three models that were candidates, and
names the slice built first.

R7's own mitigation already fixed the target: **"a restricted model in 1.0 — prefab instances with a
sparse property-override list, single-level nesting, no prefab variants"**
([15](15-risks-and-open-questions.md) § R7). This is that, made precise, plus one correction it could
not have known about — the content build cannot resolve an asset id to a path, which rules out the
one model everybody reaches for first.

Every claim below was checked against the tree rather than recalled.

---

## 1. What exists

There are two unrelated things called a prefab in this repository, and conflating them is the first
way to get this wrong.

| | `Prefab` (runtime) | `PrefabInstances` / `Prefab` (editor) |
|---|---|---|
| Where | `Core/Vixen.Engine/Scenes/Prefab.cs` | `Editor/Vixen.Editor.SceneView/PrefabInstances.cs`, and `Prefab` in `Editor/Vixen.Editor.AssetEditors/Prefabs/Prefabs.cs` |
| What it is | A captured `World`, stamped out with one `CreateMany` per archetype | A dictionary from `Entity` to `PrefabLink(AssetId, EntityId)` |
| Source link | **None.** `CaptureFrom` is a copy; the doc comment says so in as many words | The link, and only for the editing session |
| Serialised | Never. `PrefabAsset` is the compiled chunk, already flattened | The `prefab`, `source`, `overrides` and `removed` keys, since slice 2 |

The **authoring** format is `SceneFile` / `SceneEntityData` in
`Editor/Vixen.Editor.Core/Scenes/SceneFormat.cs`, read and written by `SceneSerializer`
(`Editor/Vixen.Editor.SceneView/SceneSerializer.cs`) and compiled by `SceneImporter` →
`SceneCompiler`. A `.vxprefab` is the same format with a one-root rule.

What is already built and must not be duplicated:

- **`EntityId`** (`SceneFormat.cs:37`) — a GUID identity that survives a save. Its own doc comment
  already names "a prefab override" as one of the three things it exists for.
- **`PrefabLink`** (`Prefabs.cs:22`) — `(AssetId Prefab, EntityId Source)`. The link's shape is
  decided; only its persistence is not.
- **`SceneSerializer.Instantiate(..., sources)`** (`SceneSerializer.cs:323`) — instantiating a
  template into a document *without* adopting its ids, filling a map of entity → the id the file
  gave it. Its remarks already say this "is also exactly what an override comparison needs".
- **`IPrefabSource`** (`Editor/Vixen.Editor.Inspector/InspectorField.cs`), `PrefabSource`
  (`Editor/Vixen.Editor.AssetEditors/Prefabs/PrefabSource.cs`), `InspectorField.IsOverridden` /
  `RevertToPrefab` and the revert item (`InspectorView.cs`) — the inspector's whole override
  *presentation* exists and works at **member** granularity, which constrains the format: anything
  coarser than a member would be a file that cannot express what the UI already shows. ⚠ **What it
  was fed on, when this was written, was a value comparison and nothing at all** — see § 7c.

So the overview's row is right in letter — nothing named `Override` exists under
`Core/Vixen.Engine/Scenes` — and understates what is there. Roughly half of an override system is
built. What is missing is the half that survives closing the project.

⚠ **`PrefabInstances` and `Prefab.Instantiate` had no caller outside
`Editor/Vixen.Editor.AssetEditors.Tests/PrefabTests.cs` when this was written.** Nothing in the
editor shell placed a prefab into a scene, which was worth knowing before plumbing anything through
`SceneSerializer` to serve a caller that did not exist. **Slice 2 built the verb** — a `.vxprefab`
dropped into the viewport or the outliner places an instance — and the pipe with it; see § 7a. The
type also moved: it is `Vixen.Editor.SceneView`'s now, because `SceneSerializer` is, and a link the
writer cannot see is a link that cannot be written down.

⚠ **Note also the collision the table above is about.** `Vixen.Engine.Scenes.Prefab` and
`Vixen.Editor.AssetEditors.Prefabs.Prefab` are both called `Prefab`, so a file naming both gets
CS0104 — which is how `EditorApplication` came to alias the editor's one. The compiler says, at the
call site, exactly what this section says in prose.

---

## 2. The constraint that decides the format

⚠ **An importer cannot turn an `AssetId` into a path, so it cannot open the prefab a scene names.**

`ImportContext` (`Editor/Vixen.Editor.Assets/ImportContext.cs`) offers the importer its own `Guid`
(:38), its own `SourcePath` (:41), an `IFileProvider` over *paths* (:56), and `DependsOn(AssetId)`
(:108) — which **declares** an edge for cache invalidation and returns nothing. There is no
`Resolve(AssetId) → VirtualPath` and no way to read another asset's source.

This is not a new discovery; it is the same wall recorded against navigation's placement bake in
[`overview.md`](../overview.md):

> ⛔ Navigation: bake placements from a scene — NOT K1's after all. An importer can declare an asset
> GUID and cannot resolve one to a path

It has a consequence R7 could not have anticipated: **a `.vxscene` that stored only the link and the
overrides would not compile.** `SceneCompiler` would reach an instance root, find an asset id and a
patch, and have no way to obtain the template the patch is against. It would emit an entity with the
overridden members and nothing else.

That is the single fact that picks the model.

---

## 3. The three models, costed

### (A) Implicit — no format at all

Keep writing every value in full, as today, and compute "is this overridden" by comparing against the
template whenever the prefab happens to be open. This is exactly what `PrefabSource.IsOverridden`
(`Prefabs.cs:242`) does now.

**Rejected.** Two failures, and the second is fatal:

1. It cannot tell "the author deliberately set this to the template's value" from "not overridden".
   That is only cosmetic — a revert button that is greyed out when it should not be.
2. **A template change can never propagate.** The file holds a resolved value for every member, and
   nothing says which of them the author chose. So the reconciler has no safe move: taking the
   template's value everywhere discards the author's edits, and taking the file's value everywhere is
   what already happens and means a prefab is a stamp rather than a link. *Propagation is the entire
   reason a prefab exists*, so a model that structurally cannot do it is not a candidate.

### (B) Pure sparse patch — Unity's model

The scene stores the link and the overrides and **nothing else**; the template supplies the rest,
resolved at load.

**Rejected, and not on taste.**

- **It does not compile** — § 2. Fixing it means giving `ImportContext` an asset-id → path resolver,
  which is a change to the asset pipeline's contract with every importer, and is the same unblocking
  work navigation's bake needs. That may well be worth doing; it is not worth doing *inside* the
  format decision it would unblock.
- **A missing prefab becomes destructive.** A renamed, unbuilt or not-yet-imported `.vxprefab` turns
  every instance of it into an entity with a transform and no content. Under (C) the same case
  degrades to an ordinary subtree with its links intact.
- **It cannot be reviewed.** A `.vxscene` is a file people merge by hand — that is the stated reason
  the whole authoring format is YAML (`SceneFormat.cs:484-487`). A level whose contents are a hundred
  asset ids and a patch list is not a file anybody can read a diff of.

It is the right long-term model and it is blocked on work outside this document.

### (C) Resolved, with provenance — **chosen**

The file keeps carrying every entity's full, resolved values exactly as it does today, and gains
three additive keys per entity saying **where the entity came from** and **which of its members are
the instance's own**. Reconciliation against a changed template is an editor-side pass at open time
that rewrites the non-overridden members in place.

- The compiled path does not change at all. `SceneCompiler`, `SceneAsset`, the runtime and every
  saved scene are untouched: the new keys are three more keys the binder ignores when they are
  absent, and three the compiler never looks at.
- A missing or newer prefab degrades to an ordinary subtree carrying dead links, which come back the
  moment the asset does. Nothing is deleted, ever.
- The diff stays reviewable, and gains one line per instance entity saying what it is.
- It is a superset of (B): if `ImportContext` later learns to resolve an id, dropping the resolved
  values is a writer change and a format version bump, with every override already recorded.

**What it costs:** redundancy. The file holds values the template also holds, so a template edited
while a level is closed leaves that level stale until it is next opened. That is a real cost and it
is the price of not blocking on the pipeline. It is bounded — the staleness is visible, reported, and
repaired on open — and it is the only one of the three costs above that is not a data-loss bug.

---

## 4. The format

Three keys on `SceneEntityData`, all additive, all with a default meaning "not an instance".

```yaml
- id: 7f3a…c1
  name: Turret
  position: {x: 4, y: 0, z: 2}
  prefab: vx:9c2e4f1a8b7d6e5f0a1b2c3d4e5f6071      # which prefab asset
  source: 1a2b3c4d5e6f70819a0b1c2d3e4f5061        # which entity inside it
  overrides: [Position, Light.Intensity]           # which members are this instance's own
  removed: [4b1c…9f]                               # which of the template's children the author deleted
  components:
    - !Light
      intensity: 0                                 # ← overridden *to zero*, and the list says so
```

| Key | Type | Absent means |
|---|---|---|
| `prefab` | `string`, `vx:` reference text | not from a prefab |
| `source` | `EntityId` | not from a prefab |
| `overrides` | `List<string>` | every member is the template's |
| `removed` | `List<EntityId>` | the author has deleted none of the template's children |

### Four decisions inside that

**⚠ `prefab` is the reference *text*, not a bare `AssetId`.** The reason is `SceneEntityData.Asset`'s,
spelled out at `SceneFormat.cs:311-321`: `ReferenceIndex` answers "what breaks if I delete this" by
scanning for `vx:` followed by thirty-two hex digits, and an `AssetId` bound as a bare scalar is
invisible to it. A scene whose prefab reference the index could not see is a scene the editor would
offer to delete the prefab out from under.

**⚠ Both keys on every node of an instance, not `prefab` on the root alone.** The root-only form is
smaller and is wrong: `PrefabInstances.Forget` (`Prefabs.cs:70`) deliberately allows unpacking *one*
entity of an instance, so an author can unpack the root and keep the children linked — which under
the root-only form leaves `source` keys with nothing above them to interpret against. Making each
entity's record complete on its own means unpacking, reparenting and hand-merging are all local
edits. The cost is one extra line per instance entity in a format that already writes `shape: ''` and
`light: null` on every entity.

**⚠⚠ `overrides` is an explicit list of names, never inferred from the values.** This is the zero-value
trap, and it is the single most likely way to get an override system subtly wrong. If overridden-ness
were "differs from the template" or "is not the default", then an author who turns a lamp's intensity
down to `0` has said something the file cannot represent — the next reconcile would see a default and
restore the template's brightness. Presence in the list *is* the override; the value is whatever it
is, including zero, including a value identical to the template's.

**⚠ Member granularity, addressed as `Alias.Member` or a bare `Member`.** A bare name is one of the
entity's own keys (`Name`, `Position`, `Rotation`, `Scale`); a dotted name is a `[DataContract]`
alias and a member on it, resolved through `TypeRegistry.TryGetByAlias` →
`TypeDescriptor.FindMember`. Aliases are unique per entity — an ECS entity has one of each component
type, and `SceneBehaviorRegistry.Register` refuses a name a component already holds — so the alias is
enough to name the entry without an index into `Components`. An index would be worse: it moves when
the sorted component list changes.

Coarser granularities were considered and rejected. Component granularity means an author who nudged
an instance's lamp loses the template's later change to that lamp's *colour*; entity granularity means
they lose the template's change to anything on that entity. Both are also strictly less than the
inspector already displays.

---

## 5. What happens when the template changes underneath

The decision `overview.md` names as owed. **Reconciliation is an editor-side pass at open time. It
never runs in the content build and never runs at run time**, because neither can resolve the prefab
(§ 2) and neither has anything to do with an authoring decision.

For each entity carrying a `prefab` and a `source`, against the template entity of that id:

| Case | What happens | Why |
|---|---|---|
| Member **not** in `overrides` | Takes the template's value | This is propagation, and it is the point |
| Member **in** `overrides` | The file's value is kept, untouched | This is the override, and it is the point |
| `source` names no entity in the template | Entity is **kept**, reported | The template deleted it. Deleting the author's entity on open is unrecoverable; the editor offers *unpack* or *delete* and a person decides |
| A path in `overrides` names no member | Kept in the file verbatim, reported | A component removed and re-added, or a rename in flight. **Never silently pruned** — a round trip that loses an override is silent, which is the failure this whole document exists to prevent |
| Template has a component the instance does not | Reported, **not added** | Adding one means constructing a value the instance never had. `Apply` writes members it can see on both sides and nothing else, which is what keeps it a value copy rather than a merge |
| Instance has a component the template does not | Left alone, not reported | An addition, and additions are the case that needs no syntax — § 6 |
| Template has an entity no instance node names | **Added**, and reported — unless the instance's `removed` list names it, in which case nothing happens and nothing is said | Slice 3. See § 6 and § 7b |
| The prefab asset is missing entirely | The instance loads as an ordinary subtree, links intact, reported | An unbuilt or renamed asset must not be a data loss |

The invariant, and it is the one rule to keep: **reconciliation writes values and never removes
entities, keys or override entries.** Everything it cannot resolve is reported and left in the file.

---

## 6. What this model cannot yet say, and when that bites

**An added child needs no syntax and works today.** It is a `SceneEntityData` inside an instance's
subtree with no `source` key. Because the file carries everything, it survives with no further
machinery.

**⚠ A removed child was the hole, and it is what slice 3 stands on.** With the file carrying
resolved values and reconciliation not adding template entities back, a child the author deleted is
simply absent, and absence is stable — so the model is sound *as long as the add-back rule does not
exist*. The moment reconciliation learns to add a template's new children, absence becomes ambiguous:
"the author deleted this" and "the template added this since" look identical. **Adding the add-back
rule therefore requires an explicit `removed: [<EntityId>, …]` list on the instance root in the same
change.** Landing one without the other is how a level quietly regrows the entities its designer
deleted. The list landed in slice 2 and add-back in slice 3, which is the ordering this paragraph
asked for, made a matter of fact rather than of care.

**Nested prefabs — single level, per R7.** A `.vxprefab` may contain an instance of another prefab;
because `prefab` and `source` are written on every node (§ 4), the inner nodes carry the inner link
and the outer file carries them verbatim with no new syntax. What "single level" restricts is
reconciliation: one pass, outer over inner, not a fixpoint. **Prefab variants — a prefab that is
itself an override of another prefab — are out of 1.0**, as R7 requires.

⚠⚠ **And "one pass, outer over inner" turned out to mean something sharper than it reads.** The
tempting reading is that the passes are independent because every entity carries at most one link.
The link sets are indeed disjoint; the *entities* are not. A scene node inside an instance of A that
carries B's link is reachable from both templates, and they disagree — B's file holds none of A's
overrides over B, so reconciling that node against B is every override A's author made, discarded on
open, silently, every time the level is opened. § 7b is what that cost.

---

## 7. The slice built first

**Format and reconciliation, in `Vixen.Editor.Core`, with no world and no document.**

1. The three keys on `SceneEntityData`, with the doc comments this file's decisions come from.
2. `PrefabOverrides` — pure functions over `SceneEntityData` and `SceneFile`: is a member overridden,
   mark and clear one, and reconcile a scene against a template with the report table of § 5.
3. Round-trip tests including the two silent failures: an override of a value to zero, and a
   save → load → save byte comparison.

It is testable without a project on disk, without a `World` and without a `SceneDocument`, and it is
precisely the half `overview.md` says does not exist. `SceneSerializer` and `PrefabInstances` are
**deliberately not touched**: nothing in the shell places a prefab yet (§ 1), so plumbing them now
would be building the pipe before the tap.

Owed, in order:

| | Owed | Blocked on | State |
|---|---|---|---|
| 1 | `SceneSerializer` writes and reads the three keys; `PrefabInstances` is filled from them | An editor verb that places a prefab | **Landed** — slice 2 |
| 2 | Reconcile on open, wired to the asset database that can resolve an id to a path | Nothing | **Landed** — slice 2 |
| 3 | The `removed` list of § 6, recorded on delete | Slice 1 | **Landed** — slice 2 |
| 4 | Add-back of template children, reading the `removed` list | Slice 2 | **Landed** — slice 3 |
| 5 | Nested reconciliation, one level | Slice 2 | **Landed** — slice 3 |
| 6 | The inspector's override marks fed from `SceneDocument.Prefabs` rather than paired by hand | Slice 2 — and one decision, § 7b | **Landed** — slice 4 |
| 7 | Model (B) — drop the resolved values, bump the format version | `ImportContext` resolving an `AssetId` to a path; shared with navigation's placement bake | Owed |

⚠ **Rows 3 and 4 were one row and are two, and the split is the ordering rule of § 6 written into the
plan.** Recording what the author deleted and adding back what the template gained are one feature
only in the sense that the second is unsafe without the first. Landing them together would have made
the ordering a matter of care inside one change; landing the list first makes it a matter of fact,
and every scene saved between the two carries the data the second one needs.

---

## 7a. The second slice — the verb, the pipe and the list

**Placement, persistence, reconciliation on open, and the removed-child list. Rows 1 to 3.**

The blocker named in § 7 was real and is gone: `PrefabInstances` now has a caller.

- **The verb.** Dropping a `.vxprefab` into the viewport or the outliner places an *instance* rather
  than an entity holding an `AssetInstance`, which is the one asset kind for which those two differ.
  `Prefab.TryPlace` resolves the GUID through `AssetDatabase`, reads the file, and goes through
  `SceneDocument.Place` so that one Ctrl+Z takes the whole subtree back —
  `InstantiateSubtreeCommand`, which is `CreateEntityCommand`'s argument at subtree scale.
- **The table moved to where the writer is.** `PrefabLink` and `PrefabInstances` are in
  `Vixen.Editor.SceneView` and a document owns one (`SceneDocument.Prefabs`). They were in
  `Vixen.Editor.AssetEditors`, which `SceneSerializer` cannot see — so a link recorded there was a
  link that could never be written down. The links now travel with the names through `PruneNames`,
  `Remap` and a delete's `SubtreeSnapshot`; ⚠ every one of those was a way to lose them silently,
  and the snapshot is the sharpest: without it, deleting an instance and undoing gives back a subtree
  that is correct in every respect except that it no longer came from anywhere.
- **The keys are read and written.** `Capture` writes `prefab`, `source` and `overrides` from the
  table; `Create` fills the table from them, and does so whether or not the ids are being adopted —
  which is what lets a prefab file carry an inner instance. ⚠ `Prefab.Instantiate` then declines to
  record the outer link over an inner one, or one level of nesting would be flattened on every
  placement.
- **Reconcile on open.** `SceneSerializer.Open` — what the editor's factories use, as against `Load`,
  which is what a test or a template uses — reconciles the parsed file before a world is built.
  `PrefabReconcile` is the half of § 2's wall that has a door in it: an importer cannot resolve an
  `AssetId` to a path and the editor can. ⚠ The file on disk is not rewritten and the document does
  not open dirty: an editor that wrote to a level because somebody looked at it would put unasked-for
  changes in a working tree, in every level naming a prefab that moved.
- **The removed list, and nothing that reads it back into a scene.** `removed:` is written on the
  instance root when a designer deletes one of a template's children, and taken back when they undo.
  Its use *today* is to stop a reconcile reporting that deletion as something the template gained —
  a warning that would otherwise appear on every open of that level, for ever, which is the state in
  which the warning that matters is ignored too. Its use *tomorrow* is row 4, and this is the
  ordering § 6 demands.

---

## 7b. The third slice — propagation over structure, and the nesting

**Add-back and nested reconciliation. Rows 4 and 5.**

Until this, a prefab propagated *values* and not *shape*: a child added to a template reached no level
that already used it. That is half of the reason a prefab is a link rather than a stamp, and it is the
half § 6 refused to build until the `removed` list existed to make absence unambiguous. It does, so:

- **An instance is a run, not an entity.** The unit of both rules is the topmost entity of a
  contiguous run sharing one `prefab` — ⚠ *the same definition* `SceneDocument.TryGetInstanceRoot`
  uses when a delete writes its removals. It has to be the same one: a reader and a writer that
  disagree about which entity is the root is a level that regrows what its designer deleted, and the
  disagreement would be invisible in every test that used one instance.
- **Add-back grafts the template's child, and `removed:` is what stops it.** The graft is a *copy* of
  the template's subtree with a fresh `EntityId` per node, the template's id kept as each `source`,
  the prefab's reference, and no overrides — which is exactly what a placement writes. ⚠ The copy goes
  through the format rather than member by member: a `SceneEntityData`'s components are objects whose
  own members may be reference types, so a member-wise copy leaves the level and the prefab sharing
  one, and the *next* reconcile writes a level's edit back into the template and out to every other
  instance in the project.
- **Add-back is per instance, which the report was not.** Slice 2's suppression was scene-wide, and
  said so: two instances of one prefab where only one deleted the child silenced both. That is an
  honest reading of a *report* and a wrong one for a *graft*, so the removal is now read from the
  instance root the delete wrote it to.
- ⚠⚠ **`NoteRemoved` had to grow a case, and it is the sharpest thing in this slice.** Deleting a
  whole *nested* instance out of an outer one recorded nothing at all — the nested run's own root is
  the deleted subtree's root, so the old code returned early — which under add-back is a level that
  regrows the nested prefab its designer deleted, on every open, for ever. The removal now walks one
  step out to the nearest linked ancestor, and records it by the **inner** link's `source`, because
  the outer file names that node the same way the scene does. The delete stops at that boundary and
  does not descend: what is below belongs to the nested run, and naming it in the outer instance's
  list would say something about the outer prefab that is not true. ⚠ The same walk covers a second
  hole of the same shape — a deleted subtree with no link of its own. An unpacked node keeps its
  children linked on purpose (§ 4), so deleting one carries an instance's entities away while
  carrying no link; so does an empty somebody grouped them under, and grouping stacks, so the walk is
  through every unlinked entity rather than one step.
- **Nesting is one lookup outward.** A run whose root sits inside an instance of a *different* prefab
  is paired with the node **that** template carries for the same `(prefab, source)` link; only a run
  with no such outer instance is paired with its own prefab's file by `source`. The key is uniform
  because a template entity's identity is its inner link when it has one and its own id when it does
  not — which is precisely what `SceneSerializer.Create` and `Prefab.Instantiate` between them write
  onto a scene node.
- **And the templates are composed before the scene is.** Every opened `.vxprefab` is first reconciled
  against the prefabs *it* holds instances of, so its nested nodes hold the inner prefab's current
  values under the outer author's overrides — which is what an instance of the outer should show. One
  level: the prefabs opened for that step are not composed in their turn, which is R7's restriction
  written as an absence rather than as a depth counter, and is also what makes a prefab that names
  itself terminate rather than have to be detected. ⚠ No `.vxprefab` is written either, for the same
  reason no `.vxscene` is.
- ⚠ **A run inside an instance whose template could not be opened is left exactly as the file has
  it.** Without the outer template there is no telling a nested node from a separate instance dragged
  in under one, and the two want opposite treatments — so the pass declines rather than guesses. The
  guess that is available is the destructive one.

**What is left, and why it is not wiring.** Row 6 — the inspector's override marks. It landed as
slice 4; § 7c is what it cost, and the two paragraphs below are the diagnosis it was built from.

⚠ **`PrefabSource` had no caller outside `PrefabTests.cs`**, so the revert button § 1 counted as
"already built and working" had never been shown a pairing in the running editor. It is the tree's
commonest defect — a finished consumer nothing calls — and it is why § 1's "roughly half an override
system is built" was generous. Feeding it is what row 6 means, and it reads as plumbing and is not:

1. ⚠ `PrefabSource.IsOverridden` **compared values**, which is model (A) of § 3, rejected there. It
   cannot see an override *to zero* and cannot see an override to a value equal to the template's.
   Feeding it from `SceneDocument.Prefabs` unchanged would put the rejected model back on screen over
   a file that has the right answer written down. What the inspector wants is a source backed by the
   list.
2. ⚠ `SceneEntity.Position`/`Rotation` are **world space** and `SceneEntityData`'s are **relative to
   the parent**. The two objects a pairing would join do not mean the same thing by "position", so a
   naive pairing marks every child of a moved instance as overridden and a revert writes a local value
   into a world-space setter.

Neither is hard; both are decisions, and making them silently inside a wiring change is how the thing
this document exists to prevent gets reintroduced at the layer nobody tests.

---

## 7c. The fourth slice — the marks, and the two halves the pairing was missing

**The inspector, fed from the claim list. Row 6.**

`InspectorView.Prefab` is assigned, per selection, from `EditorApplication.ShowSelection` — for the
entity's own rows and, through `ComponentsView`, for every component foldout under them. `PrefabSource`
is rewritten against `SceneDocument.Prefabs` and `PrefabReconcile`; the five tests that pinned its
value comparison are gone, replaced by tests that fail if the comparison comes back.

- **The pairing is an object, an entity and an alias.** An inspector edits *objects*, so the shell says
  which entity each object stands for and — for a component's box — the `[DataContract]` alias the
  format spells its path with. Everything else is `Member` or `Alias.Member` built from those two,
  which is the same spelling `SceneSerializer` writes.
- ⚠ **The template's transform is taken through the instance's parent.** § 7b's second objection, paid:
  `Position` and `Rotation` are converted from parent-relative to world before the inspector sees them,
  and `Scale` is not, because `SceneEntity.Scale` already reads `LocalScale`. ⚠ The rotation is
  `local * parentWorld` and the order is not interchangeable — composition here reads left to right,
  which is the same equation `Transform.Rotation`'s setter solves in the other direction.
- ⚠⚠ **`IPrefabSource` grew `Release`, and it is what makes reverting an override to the template's own
  value do anything.** The write is a no-op by definition in that case, so a revert that consisted only
  of the write would leave the row marked, the file still claiming the member, and the template's next
  change to it still blocked — a button that looks like it worked. It is called *outside* the value
  comparison, deliberately, and the placement is what the mandated sabotage kills.
- ⚠ **It also grew `Claim`, because nothing recorded an override.** `PrefabInstances.Mark` had no
  production caller either: a freshly placed instance claims nothing, so a panel that only *read* the
  list would have been correct and permanently empty — the same defect one level along, which is the
  thing this slice was for. An edit through any inspector row now records the claim on the scene's own
  stack, so one Ctrl+Z takes the value and the claim together.
- ⚠ **`EditProperty.Apply` is virtual so that every write path funnels through one hook**, and the claim
  opens a transaction only when there is a claim to make. A committed transaction records a
  `CompositeCommand`, a `SetMembersCommand` cannot merge with one, and wrapping every write would turn a
  three-hundred-frame slider drag into three hundred undo entries. The predicate is `TryGetPrefabValue`
  rather than `IsOverridden`, because the latter is false for every object that never came from a
  prefab, which is nearly all of them. The price is one extra entry — "Override Intensity" then "Set
  Intensity" — on the first edit of a member an instance had not claimed.
- **Nesting is the same lookup outward the reconciler uses**, restated over the live world: a run inside
  an instance of a different prefab is paired with the node *that* template carries for the same link,
  and a run inside an instance whose template cannot be opened is declined rather than guessed at. Each
  template opened for a pairing is composed against its own inner prefabs first, for the reason the open
  path composes a scene's.

⚠ **Two things a value comparison would still get right by accident, and the test that separates them.**
Writing `0` over a template's `7` leaves a difference, so the obvious "override to zero" test agrees
with the claim list and stays green under the sabotage — which proves nothing. The case that separates
the models is a template *already* at zero and an author who says off is theirs: equal values, equal to
the type's default, and still an override. That is the test the model hangs on, and the shape of it is
worth remembering, because "a sabotage that leaves the test green" was the first thing this slice found.

⚠ **The mark is a class on the row and the theme draws it by un-muting the label.** That is a deliberate
choice made when the theme was written — "a mark that survives every palette, which a chosen colour
would not" — and it is a *quiet* mark. Worth knowing before somebody reports that overrides are not
shown.

---

## 8. Compatibility

**The serialised form does not change for any existing file.** The three keys are additive and their
defaults mean "not an instance", so every `.vxscene` and `.vxprefab` in every project reads and
writes byte-identically. `SceneFile.Current` stays at `1`: a version bump exists to stop an *older*
reader from binding half a newer file, and an older reader meeting these keys binds a scene that is
correct in every respect except that it does not know an entity came from a prefab — which is exactly
what it believed before.

⚠ **No component layout changes**, so task #325's decision is not touched. Nothing here reaches
`SceneComponentRegistry`, the compiled chunk, or any `[Component]` struct.

⚠ **What it does cost: four lines per entity on the next save of every scene.** `OmitDefaults` is a
property of the whole document and is deliberately off for this format (`SceneEntityData.Shape`'s
remarks), so a newly written scene already carries `shape: ''` and `light: null` on every entity and
will now carry `prefab: ''`, `source: 00000000…`, `overrides: []` and `removed: []` beside them.
Reading is unaffected and no data moves; what happens is that the first save after this lands
rewrites the file. This is the same churn the format took when `Shape` and `Light` became components,
and the same answer applies: it is the price of a format with no member-level omission, and the fix —
if it is ever worth one — is `OmitDefaults` or member-level omission for the whole document rather
than a special case for these keys.

⚠ **`removed` is the fourth key and it arrived in the second slice, not the first.** It is additive on
the same terms as the other three: absent means "the author has deleted nothing from this instance",
`SceneFile.Current` stays at `1`, and an older reader meeting it binds a scene that is correct except
that it does not know which of a template's children were deliberately dropped — which is exactly
what it believed before. The one-line increase in the churn above is its whole migration cost.

⚠ **Slice 3 changes no key and no version, and its migration cost is entities rather than bytes.**
Add-back and nested reconciliation add nothing to the format: `SceneFile.Current` stays at `1`, no
key is added, removed or given a new meaning, and a `.vxscene` written before this reads and writes
byte-identically. What it costs instead is that **the first open of a level whose prefab has gained
children gains those entities**, and the next save the author makes writes them out — a diff of
entity blocks nobody typed. That is the feature working, and it is still worth stating as a cost,
because it is the first time a reconcile does anything a person would see in `git diff` beyond a
changed number. Three properties bound it: nothing is added that the instance's `removed:` list
names, nothing is added twice (the check is by the template's id, which the graft records as
`source`), and nothing is written to disk by the open itself.

⚠ **One behaviour of an existing name changed.** `PrefabReportKind.AddedByTemplate` meant "the
template has an entity this instance does not, and it was **not** added"; it now means "…and it has
been". It is still reported, deliberately — a level gaining entities is the one thing a reconcile does
that shows up in a diff, and telling somebody only about the failures would make that the quiet case.

⚠ **Slice 4 changes no key, no version and no file.** The inspector reads and writes the claim list a
scene already carries, so a `.vxscene` written before it reads and writes byte-identically. What it
does change is that a level edited through the inspector now *gains* `overrides:` entries it did not
gain before — which is the feature: until this landed, nudging a prefab instance in the editor recorded
nothing, so the next reconcile took the template's value back and the author's edit was gone on the
following open. That was a data-loss bug wearing a missing-feature's clothes, and it is fixed rather
than migrated.

⚠ **`SceneScalars.Register` already covers the new keys.** `EntityId` is registered as a scalar there
(`SceneScalars.cs`), and `MathScalars.Register()` is called from the same place — so an override on a
`Vector3`-valued member reads back as the value and not as zero. The trap is real and this format is
downstream of the fix rather than needing its own; a *new* asset type carrying these keys would need
its own `Register` call before anything scene-shaped runs.
