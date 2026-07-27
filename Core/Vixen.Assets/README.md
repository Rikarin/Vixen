# Vixen.Assets

The runtime half of the asset pipeline. A content build produces chunks and a catalog; this is what
turns an address into one of those chunks, and what says whether reaching it costs a lookup or a
download.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md).

```csharp
var catalog = CatalogFormat.Read(File.ReadAllBytes("catalog.bin"));

foreach (var address in catalog.Closure("ui/hero")) {
    var entry = catalog.Get(address);          // chunk id, bundle, provider, size
}

var bytes = catalog.DownloadSize(catalog.Match("dlc/pack-2/**"));
```

## The catalog

An address is a name a build chose — `ui/textures/hero`. It is slash-separated because that is what
people type, and `Match` globs over it, but nothing here resolves `..`, normalises case or touches a
filesystem.

`Closure` returns everything an address needs, **dependency-first**, so a caller loading the result
in order never reaches something before the thing it points at exists. A dependency the build dropped
is skipped rather than thrown for: it is a build problem and deserves reporting as one, but failing
the whole load turns one missing texture into a black screen.

**One star stops at a slash and two do not**, as in every shell. `level1/*` is what sits directly
under `level1`; `level1/**` is everything beneath it. Collapsing that distinction is how
`Preload(["level1/*"])` quietly downloads the game.

**Download size is counted per bundle**, not per address, because two addresses in one bundle cost
one download. Summing entry sizes is the mistake that tells a player a 4 MB pack is 40 MB. It is
computed over the closure, so a remote dependency of a local address still counts.

## Content updates

`MergedWith` lays a remote catalog over the shipped one. An address in both takes the update's
version; an address only in the shipped one survives. That asymmetry is deliberate — an update
**cannot make an address disappear**, because the shipped application still has that bundle on disk
and a runtime that forgot the address would fail to load something sitting right there.

Merging across targets or format versions is refused. Applying an Android catalog to a Windows one
would resolve addresses to chunks in a format the device cannot read, which otherwise surfaces as a
corrupt texture rather than as a build mix-up.

Catalogs are immutable, so a half-applied update cannot exist: either the merge produced one or the
old one is still in use.

## `catalog.bin`

Binary, because the catalog is parsed before anything can load and therefore sits on the boot time of
every session. A header, a string table, and fixed-width records.

**Strings are stored once.** Every address appears in its own entry and again in the dependency list
of everything pointing at it. The table also makes the file comparable, which is what lets a content
update ship a diff.

**Deterministic by construction** — the table is sorted, entries are written in address order and
bundles in name order, so nothing depends on the order a dictionary enumerated. Doc 12 gates the
content build on byte-identical output across three operating systems, and a catalog that reordered
itself per run would fail that gate for no reason anyone could find.

A trailing CRC is verified on read. A catalog arrives over the network on a content update, and a
truncated one would otherwise parse into a plausible catalog missing its last few hundred addresses —
failing later, somewhere else, as an asset that will not load.

`CatalogEntry` and `CatalogBundle` write out their own equality rather than taking the compiler's:
a record compares members with `Equals`, and `ImmutableArray` compares the identity of its backing
array rather than its contents, so two entries read from the same file twice would be unequal. The
first question anyone asks of an update — "did this change anything?" — would have answered yes,
always.

## Loading

`AssetManager` joins the three pieces: the catalog says what an address is and what it needs, an
`IBundleSource` says where those bytes are, and the object database turns bytes into objects. What it
adds is the part none of them can do alone — two callers asking for the same texture get one texture,
and it goes away when both are done.

**A handle is a claim, not a reference.** Holding one keeps the asset and everything it depends on
alive. Loading a material claims the texture it points at, so the texture survives exactly as long as
some material needs it.

**Releasing twice throws.** A no-op is the tempting choice and it is exactly what turns a double
release into *someone else's* asset being unloaded: the second call decrements a count another holder
is relying on, and the failure surfaces much later as a disposed object nobody released.

**Loading is deduplicated by the task, not by the result.** Two callers arriving while a load is in
flight get the same `Task`, so the work happens once. Checking "is it loaded yet" instead would start
it twice under exactly the concurrency the check exists for.

```csharp
using var scope = assets.Scope();
var hero = await scope.LoadAsync<Texture>("ui/hero");   // released when the scope ends
```

**Scopes are explicit, not ambient** — a deviation from doc 08's sketch, and a deliberate one. Ambient
capture reads beautifully until the first `await`: a load started inside the block and finishing after
it has to belong somewhere and neither answer is right. Since the scope exists *because* implicit
release semantics leak, replacing one implicit-lifetime rule with another would defeat the point.

**What a claim on a dependency is, and is not.** It mounts the bundle and ties the lifetime; it does
not deserialise. Turning a reference *inside* a chunk into the object it points at is the
content-reference machinery, which doc 03 deferred out of Phase 1 and which is the next piece here.

## Still to come

Content references, so a dependency's deserialised object is shared and not just its bundle and
lifetime. The remote provider and the bundle cache with resume and CRC verification. The streaming
manager. `.vxgroup` files and the build side that emits catalogs.

Licensed under Apache-2.0.
