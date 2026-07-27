# Vixen.Editor.Assets

The importer framework: what an importer is, what it is handed, what it must declare it read, and the
cache key that decides whether it runs at all.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § Import.

```csharp
[Importer(".png", ".jpg", ".tga")]
public sealed class TextureImporter : AssetImporter<TextureImportSettings> {
    public override int Version => 3;

    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context, TextureImportSettings settings, CancellationToken cancellationToken) {
        await using var source = await context.OpenSourceAsync(cancellationToken);
        …
        context.Write(SubAssetId.Main, "Texture", encoded);
        return context.Finish();
    }
}
```

An importer's **name** is its settings type's `[DataContract]` name, so one attribute defines the
`.meta` tag, the settings type, the serializer and the cache key with nothing to keep in sync.

## Declaring what you read is mandatory, and enforced

`context.Files` refuses to open anything the import has not declared. An importer that quietly reads
a palette, a shared configuration or a sibling texture produces an artefact that is **correct today
and stale for ever**: the file it read can change and nothing will re-run it. That does not surface
as a crash — it surfaces as an artist changing a file, rebuilding, and getting the old result, once,
on one machine, in a way nobody can reproduce.

Catching it at the moment of the read, with the path in the message, turns a week of that into a line
of code. Doc 08 puts the check in debug builds; here it is on by default, because an import runs at
most once per asset per change and the check is a set lookup per file open — and an incrementality
bug that only manifests in the configuration nobody develops in is the worst possible place for one.

Existence and metadata are *not* reads. An importer legitimately probes for a sibling before deciding
whether it depends on one.

The asset's own source is declared for it: making an importer say it depends on the file it exists to
read would be ceremony, and forgetting it is the one mistake nobody would ever be caught making.

## The cache key

`xxh128(importerType, importerVersion, sourceHash, settingsHash, target, sorted(dependencyIds))`.
Every part names something that, when it changes, must produce a different artefact — a version bump
is how "I fixed the mip filter, re-import everything" works; the target is why the same texture is
BC7 on a desktop and ASTC on a phone without sharing a cache entry; the dependencies are why a
material re-imports when the texture it points at is replaced.

**The dependencies are sorted**, so that two machines with identical inputs compute an identical key.
Doc 08 wants the artefact cache shared across CI machines, and a key that depended on set enumeration
order would turn that from a speed-up into a source of confusion.

Settings are hashed as their **emitted YAML** rather than field by field, so this knows nothing about
any importer's schema and an added setting changes the hash without anybody remembering to include
it. The dialect's byte fidelity is what makes that stable.

## The importer is generic; the context is not

Doc 08 sketches `ImportContext<TSettings>`. Building one for a settings type the pipeline only knows
at run time needs `MakeGenericType`, which NativeAOT does not have. The importer's own type parameter
costs nothing — it is closed at the `class TextureImporter : AssetImporter<TextureImportSettings>`
declaration — so the settings arrive as a typed parameter and everything stays statically bound.

## Importers are told, never discovered

An assembly scan for `[Importer]` would read metadata a trimmed publish has already deleted, and
would make "which importers does this build have" a question with different answers in the editor and
in the asset compiler. Two importers claiming one extension is an error naming both, because
last-one-wins means an artist's file being imported as the wrong kind of thing depending on load
order.

`RawImporter` is the fallback and copies a file verbatim, so "this format has no importer yet" is a
shrug rather than a blocker. It is what [doc 14](../../docs/plan/14-roadmap.md) calls
`DefaultImporter`; there is one of it under the name doc 08 uses, rather than two under both.
`FolderImporter` produces nothing — a folder is an asset because that is where an addressable group
is inherited from and where a GUID has to live so renaming a directory does not orphan everything
under it.

## `NativeFormatImporter`, whose job is the graph and not the conversion

`.vxmat`, `.vxscene`, `.vxprefab`, `.vxgroup`, `.vxanim`, `.vxvfx`. There is nothing to convert —
these files are already in the engine's own format, which is the point of doc 08's YAML dialect. What
is *not* already known is what each one **points at**, and that is what makes a material re-import
when the texture it names is replaced.

So it walks the node tree and declares every `vx:` scalar it finds. **A walk and not a regular
expression over the text**, because a GUID inside a comment or a quoted description is not a
reference — and a dependency on one would never change and never break anything, which is exactly the
kind of wrongness that is never found.

**A scalar beginning `vx:` that does not parse fails the import.** Whoever typed the prefix meant a
reference; the alternatives are failing here with the file and the text named, or shipping an asset
whose pointer resolves to nothing on a player's machine. Anything without the prefix is left alone,
because a string field holding arbitrary text is ordinary.

**An empty document is a warning, not an error.** The reader turns an empty file into an empty mapping
deliberately, so a truncated `.meta` re-imports rather than stopping the editor from opening — which
means an asset caught mid-save arrives here looking exactly like a valid one with no fields set.
Failing the build would punish an author who is still typing; silence would let a material that was
never saved ship as one.

**What it writes is the document, and that is a deliberate stopping point.** Doc 08 splits import from
compile: import produces editor-domain objects and the *compiler* turns them into the runtime chunks a
player loads — a `MaterialAsset` with named parameters and asset references becomes a `Material` with
a resolved pipeline and `ObjectId`s. That compiler does not exist yet. Emitting a half-resolved binary
here would move its decisions inside the importer, where the artefact cache key cannot see them.

## The pipeline, and the key's chicken and egg

`ImportPipeline` reads the sidecar, decides which importer claims the file, resolves the per-target
overrides, hashes the source and the resolved settings, computes the key, and only then decides
whether to run anything. Artefacts go into the content-addressed `ObjectDatabase`, so two assets that
import to identical bytes are one chunk.

**The key includes what the import depended on — which is only known once it has run.** So the key
tested against is computed from what the *previous* import declared, and the key *stored* is
recomputed from what this one actually declared. Storing the speculative key instead would mean every
asset imported twice on a first build: the first run knew no dependencies, the second would know them
and compute something different. A newly-declared dependency is therefore respected from the second
import onwards, which is right — the first import is the one that ran.

**The settings hash covers the author's settings, not the fields the import writes back.** An import
records `sourceHash` and `version` into the sidecar when it finishes; hashing those would mean every
import changed the thing it had just hashed and nothing would ever hit the cache. Both are already
first-class parts of the key.

**A failure writes nothing and discards nothing.** A record is a true statement about the input it
was made from, so a failure on a *different* input does not falsify it — an author who breaks a file
and reverts it should not pay for a re-import.

**An importer that throws fails that asset and not the run** — the difference between "one bad asset"
and "the editor won't open". The out-of-process worker doc 08 specifies takes this further by
surviving a crash rather than an exception; this is the in-process half of the same promise.

The sidecar is written back through the node tree, so an import is a diff of the two lines it changed
and not of the whole file.

## Deciding is parallel; importing is not

In a project where nothing has changed, the entire cost of an import is deciding that: a sidecar read,
a parse, a hash of the source, a lookup in the cache. None of it writes anything, so `ImportAllAsync`
does all of it at once and then imports, sequentially, only what needs it. On a ten-thousand-asset
project that was the difference between missing this phase's one-second budget by half and meeting it.

The imports themselves stay sequential and every semantic stays exactly as it was: one importer at a
time, writing chunks, sidecars and cache records, in path order. Running *importers* in parallel is
what doc 08's out-of-process worker is for, and it buys crash isolation along the way.

**A decision is discarded if something it depends on re-imported first.** A dependency's artefact ids
are part of a dependent's key, so a decision taken before that dependency ran was taken against ids
that no longer exist. Without that rule everything still converges — one run later — which is exactly
"I changed the texture and the material did not update until I imported twice".

**An asset's own source is hashed once, not twice.** It is in the declared file dependencies, because
the importer is allowed to read it, and it is `sourceHash` in its own right — so the key computation
was opening and reading every source file in the project a second time on every run. The already
computed hash is handed to the dependency walk instead, which keeps the key bit-for-bit what it was
rather than dropping a contributor and invalidating every artefact in existence.

**Settings are bound after the cache check, not before.** A cache hit needs the settings' *hash*, not
the settings, and the object was being built for every asset on every run to be thrown away. It costs
no safety: a record only exists because an import succeeded, so settings that still hash the same
still bind.

## Planning a build

`BuildPlanner` is the step between "every asset has been imported" and "there is a build". Imports
produce chunks and know nothing about addresses; `ContentBuilder` takes addresses and knows nothing
about imports. This reads the `addressable:` block out of each sidecar, resolves what it leaves
unsaid, and finds the mistakes that would otherwise surface as a load failure on a device.

**Group is inherited from the nearest folder that names one**, which is what makes "everything under
`Assets/UI` ships together" one line rather than one line per file. The walk stops at the first
ancestor that names a group, so a subfolder can override its parent.

**Labels are not inherited.** A folder-wide label would be impossible to remove from one of its
children, and a label is a query — the thing you most want to say "all of these except that one"
about.

**An asset with no address is not an error.** It is not shipped by name, which is the ordinary state
of most files in a project: a source texture only a material refers to is reached through the chunk
graph and never asked for.

**An addressable asset depending on one that is not addressable *is* an error**, and it is the check
worth having. The catalog records dependencies by address, so a dependency with no address is in no
bundle — the build succeeds, ships, and fails at load on a chunk that was never packed.

**Every error leaves its asset out of the plan.** Two assets claiming one address are both refused
rather than one winning by enumeration order; an asset whose group nothing defines, whose import
never ran, or whose dependency has no address is left out too. A tool reading the plan never sees an
entry that a diagnostic elsewhere calls unbuildable.

**A project that configures nothing still builds**, in a `Default` group the planner invents and
reports. Silence would be worse in both directions: demanding a `.vxgroup` before one `address:` does
anything is friction, and inventing one quietly leaves a project wondering where its compression
policy came from.

## Addressing sub-assets

An import can produce more than one chunk — a model is a model, and also a mesh, a skeleton and four
animation clips. `ImportRecord` keeps the `SubAssetId` alongside each chunk id, and the planner gives
each one an address under its owner's: `characters/hero`, `characters/hero#Hero_Mesh`. The `#` is what
a `vx:` reference already uses to mean "something inside this asset". The *name* goes after it where a
reference carries the *id*, because an address is typed by a person into a call to `LoadAsync` and
eight hex digits would be unusable there; both break identically when a sub-asset is renamed, since
the id is derived from the name, so the readability costs no stability.

**They are in the catalog, not merely in a bundle.** A chunk is reachable only once the bundle holding
it is mounted, and what mounts a bundle is an address in the load closure. So the asset **depends on
its own parts** — that is what mounts them, and what deserialises them first so the model's reference
to its mesh resolves to the object rather than to nothing. A group that packs every address separately
would otherwise load a model whose meshes are in a file nobody opened.

**A part carries its owner's group, labels and dependencies.** The group and the labels keep an
asset's pieces in one bundle and make "preload everything labelled `level1`" reach a labelled model's
meshes. The dependencies are over-claimed on purpose: which part uses which is not recorded, and a
mesh loaded on its own with its material's bundle unmounted fails at load, while claiming one bundle
that was going to be there anyway costs nothing.

**A chunk that cannot be named refuses the whole asset.** An artefact whose sub-asset the sidecar does
not declare, two chunks for one sub-asset, or an import with no main object at all — each is an error
and none of the asset is packed. Shipping the parts that happened to be nameable is how a model
arrives on a device with its meshes missing, and that failure names the mesh rather than the thing
that dropped it.

**A dependency on an asset is a dependency on the asset**, never on a part of it. A dependent names
the address and gets everything inside through its closure, so nothing has to record which part of a
model another asset was pointing at.

## Still to come

The importers with native dependencies (`ModelImporter` via Assimp) and the out-of-process,
crash-isolated worker doc 08 specifies.

Licensed under Apache-2.0.
