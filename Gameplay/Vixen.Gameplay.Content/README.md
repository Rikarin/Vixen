# Vixen.Gameplay.Content

The step that finds a build's definitions: addresses out of a content catalog, bytes out of
`Vixen.Assets`, and one `DefinitionCatalog` with its tag table baked.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Definitions, the runtime load path
owed from **G0**.

## State

**Built: load by label, load by address, and a problem list for a group that is too broad. 12 tests.**

| | |
|---|---|
| `DefinitionContent` | `LoadAsync` by label, `LoadFromAsync` by address. |
| `DefinitionLoad` | The catalog, and what was labelled a definition and is not. |

## Why this is not in the kernel

`Vixen.Gameplay.csproj` says it, and it is worth quoting rather than paraphrasing: *"Not
`Vixen.Engine`, not `Vixen.Assets`, not `Vixen.Net`, and none of the three by accident."* Every
gameplay library depends on the kernel, so a dependency there is one every game that touches an item
pays for — including a test that builds its catalog by hand with `DefinitionCatalogBuilder`, which is
most of them.

So the assembly that needs the asset system is the one that carries it. That is
`Vixen.Net.Telemetry`'s split — *"so an offline game links no protobuf serializer"* — one tier up.

## The three things worth knowing before reading the code

### A definition is copied out of its bundle, not held by a handle

⚠ **Doc 28's sketch is wrong about this, and it is worth saying so.** § Definitions writes
`defs.Get<ItemDefinition>(id)` as *"resolved through `Vixen.Assets`, ref-counted"*. Ref-counting the
definitions themselves is the wrong shape twice:

- it puts a load call on the damage path, where a rule resolves half a dozen ids per hit;
- it admits a state in which a sword sitting in somebody's bag names a definition that has been
  unloaded — and **a `DefId` that sometimes resolves is worse than one that never does**.

The catalog is loaded whole, at boot, and held for the life of the build. A live content update
replaces it wholesale through `DefinitionRegistry.Reload`, which is the only swap that cannot leave
two halves of one build in force at once.

**What *is* ref-counted is what a definition points at** — the sword's mesh, its icon, its sound.
Those are `AssetReference`s inside the definition, loaded on the ordinary handle path by whoever draws
them, long after this has run.

### Definitions are found by label

The content build already has a mechanism for "everything of this kind", and inventing a second one
would be a second thing to keep in step. `DefinitionContent.Label` is the conventional string;
`LoadAsync` takes whichever labels a game actually used.

⚠ **Several labels bake one table, and that is why the overload exists.** A game that bundles its
items separately from its quests must not load two catalogs — they would number their tags
separately, so `Slot.Weapon` would be a different integer in each and every rule that crossed them
would be asking about the wrong tag.

### A bad label is a problem; a bad address is an exception

⚠ **The line between the two is deliberate.** A `.vxgroup` broad enough to sweep up a texture is a
content mistake, so it reads like every other content mistake in doc 28: the rest loads and the
problem is named. An address that is not in the content catalog at all is the *caller* being wrong,
and swallowing that would turn a typo in a hand-written list into a rule that silently never fires.

A missing bundle or a corrupt chunk still throws, because that is not content being wrong — it is the
build being broken.

## Two hashes, and neither substitutes for the other

`DefinitionCatalog.BuildHash` covers the addresses and the tag table: what two peers must agree on
before a tag index means the same thing at both ends. `ContentCatalog.BuildHash` covers every byte a
build shipped, and is what [doc 27](../../docs/plan/27-mmo-framework.md)'s placement filters on
(ADR-022).

## What is owed

- **The `.vxgroup` the template ships carries no label**, so a game currently writes one itself. The
  `vixen-mmo` template should put `definitions` on its group.
- **`CatalogEntry.Shape` is not populated by the definition importer.** The field exists and doc 27
  § Upgrades depends on it — until an importer records a definition's field list there, no content
  update can be applied live, which is the correct-but-blocking state that section already describes.
