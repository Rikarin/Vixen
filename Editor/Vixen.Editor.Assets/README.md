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

## `ModelImporter`, the first one that produces more than one thing

A model is a model, and also a mesh per material, a skeleton and a clip per animation. Each of those
is separately addressable, separately deduplicated by the object database and separately loadable —
which makes this the first real consumer of the sub-asset addressing `BuildPlanner` already had.

`ModelReader` does the conversion and `ModelImporter` is the plumbing, so the part where every
decision lives is testable against a file with no import context in the way. The fixtures are OBJ and
glTF, both text and both written in the test that needs them; a binary model checked in beside the
tests is a thing nobody can edit and nobody can read the diff of.

**Every matrix is transposed on the way in.** Assimp's `aiMatrix4x4` is row-major storage of a
*column-vector* matrix, so a node's translation sits in its fourth column; Vixen is row-major storage
of a *row-vector* matrix, where it sits in the fourth row. A field-for-field copy compiles, runs, and
assembles every hierarchy inside out — consistently and quietly wrong rather than obviously broken.
Two tests fail when the transpose is removed.

**No axis conversion.** Assimp's convention is right-handed and Y-up, which is `Vector3`'s. A file
authored Z-up therefore arrives Z-up, and correcting it is a rotation on the root node an artist can
see rather than a silent transform in a build step. `MakeLeftHanded` and `FlipWindingOrder` are
deliberately absent.

**Parts are named, not numbered.** An exporter reorders its meshes whenever an artist adds a material
and re-exports, which would break every reference stored by position. A sub-asset id is derived from
the name, so renaming breaks a reference and reordering does not. Two meshes called the same thing —
which is what an exporter does all the time — would derive one id and be refused outright, so names
are made distinct before anything is written.

**Skinning weights are renormalised, not just truncated.** Dropping a fifth influence leaves the
remaining weights summing to less than one, and a vertex whose weights sum to 0.9 is drawn ten per
cent of the way towards the model's origin. A weight against a bone the skeleton walk never reached
is dropped and reported rather than silently indexed to joint 0, which would attach part of a mesh to
the root.

**The skeleton is collected across every mesh**, because a character's body, coat and hat deform by
one skeleton, and it is ordered by the node tree rather than by whichever mesh listed its bones
first — so a joint always precedes its children.

## `AudioImporter`, and a WAV reader written rather than taken

Decode, mix, convert, write. The decode is an `IAudioDecoder` — the same licence seam as
`IImageDecoder`, and audio has *more* codec churn than images, not less.

**The WAV reader is written here**, which is the opposite of the choice made for images. A PNG
decoder is a compression implementation and writing one would be foolish; a WAV file is a chunk
header and then the samples. A dependency for it would be a licence, a supply-chain entry and a
version to track, in exchange for about a hundred lines.

**The chunks are walked, not assumed.** The naive reader — seek 44 bytes, take the rest — works on
the files a tool writes and fails on the ones a DAW writes, which carry `LIST`, `fact`, `bext` and
`cue ` between the header and the samples. It fails by reading metadata *as audio*: a burst of noise
at the start of the clip, diagnosed by ear rather than by a stack trace. Odd-length chunks are
followed by a pad byte that is not counted in their size, and missing that shifts every chunk after
the first odd one.

Three more places the format bites, each with a test that fails when the line is removed:

- **8-bit WAV is unsigned**, centred on 128. Read as signed it comes out inverted around the
  midpoint, which sounds like distortion rather than like silence.
- **`WAVE_FORMAT_EXTENSIBLE` hides the real format code in a GUID** at the end of the `fmt ` chunk.
  Anything above two channels or above 16 bits is written that way, so a reader that stops at `0xFFFE`
  rejects most of what a DAW exports.
- **24-bit is rounded to 16, not truncated.** Truncation biases every sample towards negative
  infinity, which is a DC offset across the whole clip and a click at each end.

`ForceMono` is the one setting that earns its place: **a stereo clip cannot be positioned in the
world.** It already says which ear it is in, so panning does nothing and the sound stays in the
listener's head wherever its emitter is. It averages rather than sums, because summing two correlated
channels clips anything mastered near full scale.

**It claims `.ogg`, `.mp3` and `.flac` without being able to read them**, which is a deviation from
`TextureImporter` and deliberate. That importer claims only what it decodes, so an `.exr` falls to
`RawImporter` and ships as a blob. Doc 08's table promises those three formats, and an artist who
drops an `.ogg` in and finds it silently became an unplayable byte blob has learned nothing; failing
with the name of what is missing is the more useful of the two silences.

## `NavMeshImporter`, the first one whose output is computed rather than converted

Every other importer here reads a file and turns it into engine data. This one reads a file that
names *another* file, and produces something neither of them contains: a `.vxnavmesh` says which
collision mesh a navmesh is for, its `.meta` says how finely and for what agent, and what comes out is
a baked `NavMeshAsset` — voxelised, eroded, partitioned and polygonised by `Vixen.Navigation`.

**The geometry is a declared dependency, which is the whole reason this is an importer.** A navmesh
that quietly describes the level as it was last week is worse than no navmesh: nothing about it looks
wrong until an agent walks into a wall that was added on Tuesday. Declaring the collision mesh means
re-exporting it re-bakes the navmesh, through the same cache key that re-imports a material when its
texture is replaced.

**The bake parameters are settings rather than content**, so the per-target overrides the meta format
already has do something useful: the same level bakes at a coarser cell size for a phone without a
second asset and without a branch in anybody's build script.

**Nothing walkable is a warning, not an error.** An author who has just set the agent radius wider
than their corridors wants to be told; a level whose collision is genuinely all walls is a level with
an empty navmesh rather than a broken build.

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

**The compilers.** Doc 08 splits import from compile, and only the first half exists. `ModelCompiler`
is what does vertex-layout packing, meshlets, LOD generation and index reordering — none of which can
be decided one mesh at a time, which is why they are not in the importer. `MaterialCompiler` is what
turns a `.vxmat`'s named parameters into a resolved pipeline, which is why `NativeFormatImporter`
carries the document forward rather than emitting a half-resolved binary.

**The importers that need a decoder nobody has chosen.** Ogg, MP3 and FLAC for audio; `.exr`, `.tif`,
`.webp` and `.dds` for textures. Fonts, shaders, VXML, VCSS and video have their own phases.

**The out-of-process, crash-isolated worker** doc 08 specifies. `ImportPipeline` already survives an
importer that *throws*; surviving one that takes the process with it — a malformed FBX inside a C++
library — needs a separate process, and that is what `Tools/Vixen.AssetCompiler` is for.

Licensed under Apache-2.0.
