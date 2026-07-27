# 08 — Asset Pipeline and Addressables

Two systems are being merged here: **Unity's project layout and `.meta` sidecar pattern** (the better
*authoring* model) and **Stride's content-addressed object database + bundle runtime** (the better
*runtime* model). They compose cleanly because they solve different halves of the problem.

Per ADR-005, Unity's **sidecar pattern** is adopted exactly — one `.meta` per imported file, one per
folder, GUID-as-identity — while the **content** of those files is Vixen's own schema: YAML with type
tags that deserialise into strongly-typed C# records, in the spirit of Stride's asset files. Unity's
actual schema was read from a real project on this machine (`/Users/jiu/Projects/Shinobi Wars`) to
decide deliberately what to keep and what to drop; the table in ADR-005 records that.

## The two halves

```
┌─ AUTHORING (Unity's sidecar pattern, Vixen's schema) ──────────────────────┐
│  Assets/Textures/hero.png                                                  │
│  Assets/Textures/hero.png.meta   ← GUID + importer settings, source of truth│
│                    │                                                        │
│                    │  Importer (guid, settings, source bytes) → AssetItem   │
│                    ▼                                                        │
│  Library/          the local, disposable, machine-specific import cache     │
│    ├─ ArtifactDb/  content-addressed imported artefacts                     │
│    ├─ GuidIndex    guid → path, path → guid (rebuilt from .meta on demand)  │
│    └─ SourceHashes for incrementality                                       │
└─────────────────────────────────────────────────────────────────────────────┘
                     │  Content build (compilers)
                     ▼
┌─ RUNTIME (Stride's model, + Unity's addressables) ─────────────────────────┐
│  ObjectDatabase:  ObjectId(xxh128 of content) → chunk                       │
│  Bundles:         .bundle files grouping chunks, LZ4 or Zstd                │
│  Catalog:         address (string) → ObjectId + bundle + provider           │
│  Providers:       local file · APK asset · remote HTTP(S) with cache        │
└─────────────────────────────────────────────────────────────────────────────┘
```

The GUID is the authoring identity and never appears in a shipped build. The address is the runtime
identity. The `ObjectId` is the storage identity. Keeping the three distinct is what makes remote
content updates work: the same address resolves to a new `ObjectId` after an update, and unchanged
chunks are not redownloaded.

## Project layout (consumer projects)

```
MyGame/
├── MyGame.csproj                    # references Vixen.Sdk (Tools/Vixen.Sdk)
├── MyGame.vxproj                    # Vixen project settings (YAML)
├── Assets/                          # everything here is imported
│   ├── Textures/hero.png  + hero.png.meta
│   ├── Models/hero.fbx    + hero.fbx.meta
│   ├── Materials/hero.vxmat + .meta
│   ├── Scenes/Level1.vxscene + .meta
│   ├── Shaders/Water.rvn  + .meta
│   ├── Ui/MainMenu.vxml   + .meta
│   ├── Ui/theme.vcss      + .meta
│   └── Addressables/Groups/*.vxgroup + .meta   # bundle/group policy assets
├── Packages/                        # package manifest + local packages
│   └── manifest.json
├── ProjectSettings/                 # per-project settings assets (YAML, source-controlled)
├── Library/                         # gitignored: import cache, artefact db, shader cache
└── Build/                           # gitignored: content build output, bundles, catalogs
```

`Assets/` and `ProjectSettings/` are committed; `Library/` and `Build/` are not. This is Unity's
split, and it is right: import artefacts are reproducible from source + `.meta`, so committing them is
pure churn.

## The `.meta` file

### The pattern (from Unity, unchanged)

- **One `.meta` per imported file**, named `<file>.<ext>.meta` — `hero.png` → `hero.png.meta`.
- **One `.meta` per folder**, named `<folder>.meta` alongside the folder.
- **Committed to source control** next to the asset. `Library/` is not.
- **The GUID is the identity.** Generated on first import, never regenerated, path-independent. Moving,
  renaming, or reorganising folders changes no references anywhere.
- **A missing `.meta` is created; an orphaned one is quarantined, not deleted.**

That is the whole of what is inherited. Everything below is Vixen's.

### The schema

```yaml
# Assets/Textures/hero.png.meta
guid: 9e8a44c9930c64e388ca034c5fe4c426
metaVersion: 1
importer: !TextureImporter
  version: 3
  sourceHash: 8f3a2c91d04e5b76a1c8e5f2b73d9048
  colorSpace: Srgb
  usage: Albedo               # Albedo | Normal | Mask | Hdr | Ui | Lut | Cubemap
  generateMips: true
  mipFilter: KaiserAlphaWeighted
  wrap: { u: Repeat, v: Repeat }
  filter: Trilinear
  anisotropy: 8
  maxSize: 2048
  compression: Bc7
  quality: 0.85
  premultiplyAlpha: false
  streaming: true
  overrides:
    - target: Android
      compression: Astc6x6
      maxSize: 1024
    - target: iOS
      compression: Astc6x6
    - target: Web
      compression: Etc2Rgba
      maxSize: 1024
addressable:
  address: ui/textures/hero
  group: UiCore
  labels: [ui, hd]
subAssets:
  - { id: 3f7a91c2, name: hero, type: Texture }
extensions: {}
```

```yaml
# Assets/Textures.meta  — folder
guid: f54d1bd14bd3ca042bd867b519fee8cc
metaVersion: 1
importer: !FolderImporter
  version: 1
addressable:
  group: UiCore               # inherited by descendants that do not override it
```

```yaml
# Assets/Models/hero.fbx.meta  — a container asset with sub-assets and remapping
guid: 1a2b3c4d5e6f70819a2b3c4d5e6f7081
metaVersion: 1
importer: !ModelImporter
  version: 5
  sourceHash: 55c1a0f3e9d24b18
  scale: 0.01
  importAnimations: true
  importBlendShapes: false
  generateTangents: WhenMissing
  generateLods: { count: 3, reduction: 0.5 }
  optimizeIndexOrder: true
  materialMapping:           # replaces Unity's opaque `externalObjects`
    Body: vx:9e8a44c9930c64e388ca034c5fe4c426
    Cloth: vx:c1d2e3f4a5b60718c1d2e3f4a5b60718
  mainSubAsset: 8c1d4a70
addressable:
  address: characters/hero
  group: Characters
subAssets:
  - { id: 8c1d4a70, name: Hero,      type: Model }
  - { id: 2b9e5f13, name: Hero_Mesh, type: Mesh }
  - { id: 7d4c8a21, name: Skeleton,  type: Skeleton }
  - { id: 91f0b3e6, name: Idle,      type: AnimationClip }
```

Field by field:

| Key | Meaning |
|---|---|
| `guid` | 32 lowercase hex, no dashes. Identity. Never rewritten. |
| `metaVersion` | Envelope schema version. A real version with a real migration chain, not a magic constant. |
| `importer` | **A YAML type tag selects the importer and its settings type.** `!TextureImporter` deserialises into `TextureImportSettings`, resolved through the generated type registry ([03](03-core-foundation.md)) — so no reflection, AOT-safe, and adding an importer is adding a record plus an attribute. |
| `importer.version` | The importer's own version. Bumping it invalidates every artefact it produced ([see caching](#import)). |
| `importer.sourceHash` | XxHash128 of the source bytes at last import. Used for incrementality and for duplicate-GUID resolution. |
| `importer.overrides` | **Sparse** per-target overrides — only the fields that differ. A texture that needs no overrides has no `overrides` key, and a texture `.meta` stays ~20 lines instead of Unity's 100+. Implemented as a partial-record patch applied over the base settings. |
| `addressable` | Address, group, labels. Optional — an asset with no `addressable` block is not shipped. |
| `subAssets` | Declared sub-assets with **stable IDs** (see below), their names, and their types. Explicit, typed, and human-readable, replacing `internalIDToNameTable` + `mainObjectFileID`. |
| `extensions` | A typed, tagged map for user and plugin metadata, replacing the untyped `userData` string. |

**`target` values** in `overrides` are the engine's build targets (`Windows`, `Linux`, `MacOS`,
`Android`, `iOS`, `Web`), optionally narrowed (`Android/Vulkan`, `Windows/x64`). They resolve
most-specific-first, so a base + `Android` + `Android/Vulkan` chain layers predictably.

**YAML dialect.** Two-space indent, block style with flow style for small structs
(`{ u: Repeat, v: Repeat }`), no document-start marker, keys emitted in declaration order (which is
stable because it comes from the C# record), and a trailing newline. `Vixen.Core.Yaml` owns both reader
and emitter; a round-trip test over the fixture corpus asserts byte fidelity so that reading and
rewriting a `.meta` never produces a spurious diff. That property is what makes `.meta` files safe to
rewrite on migration.

### Sub-asset IDs

`id` is an 8-hex-digit stable hash of `(importerType, subAssetKind, subAssetName)`.

This is the one place Unity's *behaviour* is worth keeping while discarding its representation. Unity's
`fileID` values are internal magic numbers; deriving the ID from the sub-asset's identity instead means
re-importing an FBX whose mesh order changed does not break every reference to it. That failure — "my
prefab lost its mesh after an artist re-exported" — is one of the most common in real Unity projects,
and it is avoidable by construction.

Collisions within one asset are detected at import and reported as an error naming both sub-assets,
rather than silently resolved.

### Reference format

Cross-asset references inside `.vxscene` / `.vxmat` / `.vxprefab` are a **single scalar**:

```yaml
albedo:   vx:9e8a44c9930c64e388ca034c5fe4c426            # the asset itself
mesh:     vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13   # a sub-asset
material: null                                            # explicitly unset
```

- `vx:` prefix marks it as an asset reference to both the parser and a human reading a diff.
- `#<subAssetId>` selects a sub-asset; absent means the asset's main object.
- Unity's `type:` field is dropped — nothing reads it.

One scalar instead of a three-key flow mapping is a real gain, not cosmetics: it diffs on one line,
merges cleanly, greps trivially (`rg 'vx:9e8a44c9'` finds every referrer), and is unambiguous to
round-trip.

### Addressable metadata placement

The per-asset facts (address, labels, group membership) live in the `.meta`; the group *policy*
(compression, packing mode, local/remote, CRC, update restriction) lives in a `.vxgroup` asset.

Unity splits this differently — addressable metadata sits in `AddressableAssetSettings` plus group
`ScriptableObject`s, separate from the `.meta`, which produces a second identity system, a second source
of merge conflicts, and the "asset is in two groups" state. Since Vixen has addressables from day one
(ADR-013) rather than bolted on later, the facts belong with the asset and only the policy needs its own
file.

### GUID index and conflict handling

- `Library/GuidIndex` is a persistent `guid ↔ path` map, rebuilt by scanning `.meta` files when
  missing or stale. Rebuild of a 100 k-asset project must complete in **< 10 s** (parallel parse; only
  the envelope is parsed, not the importer block).
- **Duplicate GUIDs** (the copy-pasted-folder disaster) are detected on scan. Resolution: the asset
  whose `importer.sourceHash` matches its file wins; the other is re-GUIDed with a loud warning listing
  both paths. Silent tolerance here is how projects rot.
- **Orphan `.meta`** (no source file): moved to `Library/OrphanMeta/` rather than deleted, so a
  mis-ordered git operation is recoverable.
- **Missing `.meta`**: created on import with a fresh GUID.
- The fast scan parses **only the envelope** — `guid`, `metaVersion`, and the `importer` tag name — and
  skips the tagged settings node entirely. This is why the 100 k-asset rebuild budget is achievable, and
  it is a property the schema was designed for: the envelope keys come first and are fixed.
- `.meta` files are **not** `merge=union`. Because the schema is typed and small, a real conflict in a
  `.meta` is a genuine conflict (two people changed the same import setting) and should be resolved, not
  silently concatenated. `.gitattributes` marks them `text eol=lf` with a merge driver that fails loudly.

## Import

```csharp
[Importer(".png", ".jpg", ".tga", ".exr", ".hdr", ".psd", ".dds", ".ktx2")]
public sealed class TextureImporter : AssetImporter<TextureImportSettings>
{
    public override int Version => 3;        // bump ⇒ re-import everything this importer produced
    public override ValueTask<ImportResult> ImportAsync(
        ImportContext<TextureImportSettings> ctx, CancellationToken ct);
}

[DataContract("TextureImporter")]            // ← this name is the YAML tag in the .meta
public sealed record TextureImportSettings : IImportSettings
{
    public ColorSpace ColorSpace { get; init; } = ColorSpace.Srgb;
    public TextureUsage Usage { get; init; } = TextureUsage.Albedo;
    // ...
    public TargetOverride[] Overrides { get; init; } = [];   // see the note below
}
```

The `[DataContract]` name is the discriminator: one attribute defines the `.meta` tag, the settings
type, and the serializer, with no separate registration table to keep in sync. `[DataAlias]` handles
renames without breaking existing `.meta` files, exactly as it does for runtime types
([03](03-core-foundation.md)).

> ✅ **Built, and two things in the sketch above turned out to be unbuildable as written.**
> `Core/Vixen.Core.Yaml/` binds a document to types through the generated type registry, exactly as
> this section claims — tag to type by alias, member access through generated lambdas, no
> `Type.GetType` and no assembly scan. 50 tests. What changed:
>
> - **`ImmutableArray<T>` became `T[]`.** Building an `ImmutableArray<T>` for a `T` known only at run
>   time needs `MakeGenericMethod`; `Array.CreateInstance(elementType, n)`, `MakeGenericType` and
>   `Activator.CreateInstance(Type)` are all `RequiresDynamicCode` for the same family of reasons.
>   This repository compiles `IL3050` as an error, so the build refused the obvious binder outright —
>   which is precisely the discovery [14](14-roadmap.md) schedules this phase early to force. The fix
>   is the one the rest of the engine already uses: **a generator saw the type, so a generator writes
>   the constructor.** `CollectionFactory` holds a `static count => new TargetOverride[count]` per
>   collection type reachable from any described member, emitted by the reflection generator, and the
>   binder asks for one instead of building a type. A list *interface* is backed by an array, which
>   satisfies it with no copy. `ImmutableArray<T>` is refused by name with the reason in the message;
>   in an init-only record a `T[]` is just as immutable.
> - **`TargetOverride` is not generic.** The reflection generator describes closed types only
>   (`VXS0201`), so `TargetOverride<TextureImportSettings>` has no descriptor and cannot be bound. It
>   does not need to be generic: an override is a *sparse patch*, and a patch is better expressed at
>   the node level — read the base, apply the matching overrides as node-level merges, bind once — than
>   as a partial record per settings type. That is how the `.meta` layer applies them.
>
> Also settled here: init-only setters. `{ get; init; }` is this section's shape for every settings
> record, and a descriptor could not write one until the reflection generator learned to reach the
> setter through `[UnsafeAccessor]`. See [03](03-core-foundation.md).

`ImportContext` gives virtual-path source access, the deserialised settings, the target build platform,
a dependency registrar (`ctx.DependsOn(guid)` / `ctx.DependsOnFile(path)`), a diagnostics sink, and an
artefact writer. **Dependency registration is mandatory and is what makes incrementality correct** —
an importer that reads another asset without registering it produces stale artefacts, so the debug
build wraps the VFS and fails the import if an unregistered read occurs.

Importer set for 1.0:

| Importer | Extensions | Produces |
|---|---|---|
| `TextureImporter` | png jpg tga bmp exr hdr psd tif dds ktx2 | `Texture` (BCn/ASTC/ETC2 per platform, mips, sRGB flags) |
| `ModelImporter` | fbx gltf glb obj dae 3ds blend¹ | `Model`, `Mesh`, `Skeleton`, `AnimationClip`, `Material` stubs — via `Silk.NET.Assimp` |
| `AudioImporter` | wav ogg mp3 flac | `AudioClip` (Ogg/Opus for streaming, PCM/ADPCM for SFX) |
| `FontImporter` | ttf otf woff2 | `Font` (MSDF atlas + metrics + kerning, via HarfBuzz) |
| `ShaderImporter` | rvn | `.rvnlib` / effect registration |
| `MarkupImporter` | vxml | parsed component + generated C# partial |
| `StyleImporter` | vcss | parsed stylesheet + utility-class extraction |
| `AssetImporter` | vxmat vxscene vxprefab vxgroup vxanim vxvfx … | Vixen-authored YAML assets |
| `ScriptImporter` | cs | script metadata (execution order, default field values) |
| `VideoImporter` | mp4 webm | `VideoClip` |
| `FolderImporter` | folders | folder assets (group inheritance, addressable roots) |
| `RawImporter` | anything unmatched | verbatim copy, addressable as a byte blob |

¹ `.blend` requires a Blender install; detected and reported clearly rather than failing obscurely.

**Out-of-process, parallel, crash-isolated.** `Tools/Vixen.AssetCompiler` runs N worker processes
(default = cores − 1). A worker that crashes on a malformed FBX marks that one asset failed and the
import continues — Stride does this and it is the difference between "one bad file" and "the editor
won't open". IPC over a named pipe with the engine's binary serializer.

**Artefact cache key** = `xxh128(importerType, importerVersion, sourceHash, settingsHash, platform,
sorted(dependencyArtefactIds))`. A cache hit skips the importer entirely. This makes CI content builds
cacheable across machines when the artefact DB is shared (an S3/Azure-backed `ArtifactDb` provider is
a phase-2 feature but the key design supports it from the start).

## Content build (compilers)

Import produces *editor-domain* objects. Compile produces *runtime-domain* chunks. Stride's split, and
it matters: the editor holds a `MaterialAsset` with named parameters and asset references; the runtime
loads a `Material` with a resolved pipeline and `ObjectId` references.

```
IAssetCompiler<TAsset>
    void   Compile(AssetCompilerContext ctx, TAsset asset, CompilerResult result);
    IEnumerable<BuildDependency> GetInputFiles(...);
    IEnumerable<Type> GetRuntimeTypes(...);
```

Build steps form a DAG (Stride's `BuildStep`/`CommandBuildStep`/`ListBuildStep` model), executed on the
job system with the same content-addressed caching. Notable compilers:

- `TextureCompiler` — final GPU format + mip tail, streaming-mip split.
- `ModelCompiler` — vertex layout optimisation, meshlet generation, index reordering for cache
  locality (Forsyth/`meshoptimizer`), LOD generation, bounds, tangent generation.
- `MaterialCompiler` — resolves the material feature tree to a permutation set and emits the effect
  requests that `EffectCompiler` consumes.
- `EffectCompiler` — the build-time permutation pre-generation from [06](06-rendering-pipeline.md)/[07](07-raven-shader-pipeline.md).
- `SceneCompiler` — flattens entity/component data into archetype-ordered blobs for bulk world load.
- `PrefabCompiler` — the "instantiate plan" from [04](04-ecs-and-scripting.md).
- `UiCompiler` — VXML/VCSS → compiled component + resolved stylesheet + utility-class output.
- `FontCompiler`, `AudioCompiler`, `VfxCompiler`, `AnimationCompiler`.

## Runtime: object database, bundles, catalog

### ObjectDatabase

`ObjectId` = xxh128 of the chunk's content. Content-addressed storage gives free deduplication (two
materials with identical parameters are one chunk), free integrity checking, and free delta detection
for updates.

Two backends, as in Stride:
- `FileOdbBackend` — loose files under `Library/ArtifactDb/xx/xxxxxx…`, used at edit time.
- `BundleOdbBackend` — reads chunks from `.bundle` files, used at runtime. Handles bundle resolution
  through a `BundleResolver` delegate, which is where the remote provider hooks in.

Chunk format: `[header: serializerTypeId, referenceCount, ObjectId[] references, flags][payload]`.
Loading is header-read → resolve references → recursive load → deserialise, with reference counting so
shared dependencies load once.

### Bundles

A `.bundle` is a container of chunks with a manifest, per-chunk compression (LZ4 / Zstd / raw), and a
dependency list of other bundles. Grouped by `.vxgroup` policy:

```yaml
# Assets/Addressables/Groups/UiCore.vxgroup
name: UiCore
buildPath: Local                 # Local | Remote
loadPath: Local
packing: PackTogether            # PackTogether | PackSeparately | PackTogetherByLabel
compression: Lz4                 # Lz4 | Zstd | None
includeInBuild: true
crcCheck: OnLoadForCachedOrRemote
updateRestriction: CanChangePostRelease   # ← Unity's "content update" semantics
bundleNaming: FilenameHash
```

`updateRestriction` is the mechanism that makes remote updates safe: groups marked
`CannotChangePostRelease` are baked into the shipped app and never redownloaded; changed assets in
those groups get moved into a generated "content update" group at build time. This is Unity's content
update workflow, and it is the non-obvious piece that people discover they need six months after
shipping. Building it in from the start costs a day; retrofitting it costs a release.

### Catalog

```
catalog.json / catalog.bin
  version, buildHash, targetPlatform
  entries: address → { ObjectId, bundle, provider, dependencies[], labels[], size }
  bundles: name → { url, hash, size, crc, compression, dependencies[] }
  labels:  label → address[]
```

The catalog itself is versioned and addressable. Boot flow on mobile:

```
1. Load the built-in local catalog from /app/
2. If a remote catalog URL is configured:
     fetch remote catalog hash (a tiny .hash file — cheap, cacheable)
     if it differs from the cached one: download the remote catalog, merge over local
3. Resolve addresses through the merged catalog
4. On load of a remote-provider address: check the bundle cache (/cache/bundles/<hash>);
   download with resume + CRC verify if absent; then read chunks
```

### Runtime API

```csharp
// handle-based, ref-counted, cancellable, no reflection
AssetHandle<Texture> h = Assets.LoadAsync<Texture>("ui/textures/hero");
await h;                       // or poll h.Status / h.Progress
Texture tex = h.Result;
h.Release();                   // ref-count decrement; unloads at zero

// batch / label
await Assets.LoadByLabelAsync<Texture>("ui");
await Assets.PreloadAsync(["level1/*"]);           // glob over the catalog
long bytes = await Assets.GetDownloadSizeAsync("dlc-pack-2");
await Assets.DownloadDependenciesAsync("dlc-pack-2", progress);
Assets.ClearDependencyCache("dlc-pack-2");
```

- **Ref-counted with explicit release**, plus a scope helper (`using var scope = Assets.Scope();`) that
  releases everything acquired in it. Unity's `Addressables` release semantics are a documented source
  of leaks; making the scope pattern the idiomatic one avoids inheriting that.
- **Synchronous load is available and honest**: `Assets.Load<T>` blocks, is legal at load screens, and
  is flagged by an analyzer inside `Update` methods.
- **Streaming.** Textures stream mip tails; audio streams; meshes stream LODs. A `StreamingManager`
  with a memory budget and a priority heuristic (distance, screen coverage, view frustum) — Stride has
  `Streaming/` for exactly this.

## Editor integration

- **Asset database watches `Assets/`** and re-imports on change, debounced, off the main thread, with
  a progress bar and cancellability.
- **Right-click → Reimport / Reimport All / Show in ArtifactDb / Copy GUID / Find References.**
  "Find references" is a reverse index over the GUID graph, maintained incrementally — indispensable in
  a large project and cheap to maintain if built in.
- **Move/rename** updates nothing but the filesystem (GUIDs are path-independent); the GUID index is
  updated and no reference changes. Deleting an asset that is referenced produces a warning listing
  referrers rather than a silent null.
- **Addressable analysis view**: duplicate assets across bundles, bundle size breakdown, dependency
  cycles, assets included but unreferenced. Unity ships this; it is the difference between a 40 MB and
  a 400 MB build.
- **`.meta` files are never hidden from the user.** They are shown in the project view behind a toggle
  and are directly editable, because the format is the API.

## `Vixen.Sdk` — MSBuild integration

`Tools/Vixen.Sdk` ships props/targets so `dotnet build` on a consumer project:

1. Restores the Vixen tool versions matching the referenced packages.
2. Runs `vixen import` (incremental) before `CoreCompile`, so generated C# from VXML/shaders exists.
3. Runs the VXML/VCSS/shader source generators as analyzers.
4. Runs `vixen content build` for the target platform after `Build`.
5. Copies bundles + catalog into the output, or into the platform package (APK assets, iOS bundle,
   `wwwroot`).
6. Emits import/compile diagnostics as MSBuild errors/warnings with file+line, so IDE error lists and
   CI logs work without a custom parser.

This is how Stride integrates (`Stride.AssetCompiler.targets`) and it is the right pattern: a user
should never have to run a separate content build step manually.

## Testing

| Area | Test |
|---|---|
| `.meta` round-trip | Read → rewrite must be **byte-identical** for a golden corpus covering every importer, folders, sparse overrides, sub-asset lists, and empty/absent optional blocks. This property is what makes automatic migration safe. |
| `.meta` schema coverage | Every `IImportSettings` record has a golden `.meta` fixture; a new setting without a fixture fails a reflection-free generated completeness test |
| `.meta` migration | v(N−1) reads correctly and rewrites to vN, for both `metaVersion` and each `importer.version`; a golden file per importer per version; `[DataAlias]` renames resolve |
| Override resolution | Base + `Android` + `Android/Vulkan` layers most-specific-first; absent keys inherit; a golden expected-effective-settings table per target |
| Envelope fast-scan | The scan-only parser returns the same `guid`/importer tag as the full parser, over the whole corpus, including files whose settings node is deliberately malformed |
| GUID stability | Move/rename/copy sequences preserve GUIDs; duplicate-GUID detection triggers with the right resolution |
| Sub-asset IDs | Reordering meshes in an FBX changes no sub-asset `id`; renaming one changes exactly that `id`; within-asset collisions are reported, not silently resolved |
| Reference scalars | `vx:<guid>[#<sub>]` parses, round-trips, and rejects malformed input with a positioned diagnostic; `null` distinguishes from absent |
| Incrementality | Touch one texture ⇒ exactly one importer runs; touch a material ⇒ its texture is not re-imported; bump an importer version ⇒ all its assets re-import and nothing else does |
| Dependency correctness | The unregistered-read detector is itself tested: an importer that reads without declaring fails the build |
| Determinism | Full content build on Windows/Linux/macOS runners produces identical `ObjectId`s and identical bundle bytes |
| Bundle round-trip | Write bundle → read chunks → deserialise → compare; corrupt a byte ⇒ CRC check fails cleanly |
| Catalog merge | Local + remote catalog merge precedence; `CannotChangePostRelease` violations detected at build time |
| Remote update | `Tools/Vixen.ContentServer` serves v1, client caches, server publishes v2, client fetches only the changed bundles — asserted by byte counts |
| Ref counting | Load/release balance under randomised interleavings; leak detector reports non-zero counts at shutdown |
| Streaming | Memory budget respected under a synthetic camera path; no visible pop beyond a tolerance |
| Scale | 100 k-asset synthetic project: index rebuild < 10 s, incremental import of one asset < 1 s, full build within the [00](00-vision-and-principles.md) budget |
