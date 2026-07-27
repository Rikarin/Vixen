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
shrug rather than a blocker. `FolderImporter` produces nothing — a folder is an asset because that is
where an addressable group is inherited from and where a GUID has to live so renaming a directory
does not orphan everything under it.

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

## Still to come

The pipeline that ties this together — resolving overrides for a target, computing the key, checking
the artefact database, writing what comes back — and the importers with native dependencies
(`ModelImporter` via Assimp, `TextureImporter` via the BCn/ASTC encoders). The out-of-process,
crash-isolated worker doc 08 specifies is a separate piece again.

Licensed under Apache-2.0.
