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

## References, and the direction the runtime needs

An address is what a person types. A **reference** — `vx:9e8a44c9…`, optionally `#<subAssetId>` — is
what a scene, a material or a component *stores*, because an id survives renaming the file and an
address does not. Everything that loads takes an address, so a catalog that only went one way left a
component holding an id with nothing it could do with it.

```csharp
var mesh = new AssetReference(entity.Mesh);      // what the component holds

catalog.TryGetAddress(mesh, out var address);    // → "characters/hero#Hero_Mesh"
assets.LoadAsync<MeshData>(mesh);                // or skip the address entirely
```

Every entry carries its reference, and the reverse index is derived in the constructor rather than
stored — a second table that disagreed with the entries indexing it is a bug nothing would report, and
it would disagree first on exactly the entry a content update replaced.

⚠ **An address and a reference name the same thing and neither can be computed from the other.** A
sub-asset's address carries its *name* (`#Hero_Mesh`, so it is typeable) and its reference carries its
*id* (`#2b9e5f13`, so it is fixed-width). `BuildPlanner` is the only place in the build holding both,
which is why the reference is written down there or not at all.

⚠ **A reference nobody shipped raises `ReferenceNotFoundException`, not `AddressNotFoundException`.**
A missing address is usually a typo in a call somebody wrote; a missing reference is content — an asset
excluded from the build, or one deleted after something saved a reference to it. Nobody typed the
identity, so "check the spelling" is the wrong advice.

⚠ **Two addresses cannot claim one reference**, the same refusal as two entries claiming one address
and for the same reason: it is a build that cannot say what a component points at. Entries with *no*
reference are exempt and common — any chunk no authored asset claims.

## Content updates

`MergedWith` lays a remote catalog over the shipped one. An address in both takes the update's
version; an address only in the shipped one survives. That asymmetry is deliberate — an update
**cannot make an address disappear**, because the shipped application still has that bundle on disk
and a runtime that forgot the address would fail to load something sitting right there.

Merging across targets or format versions is refused. Applying an Android catalog to a Windows one
would resolve addresses to chunks in a format the device cannot read, which otherwise surfaces as a
corrupt texture rather than as a build mix-up.

⚠ **An update that moves an asset drops the address it left.** A merge is keyed by address, so the old
entry would otherwise survive and claim the same reference as the new one — which the constructor
refuses. The reference is the only thing that says the two entries are the same asset, so this is the
one case where a merge does remove an address, and it is not an exception to the paragraph above: the
asset is still reachable, under the address the update gave it.

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

**A dependency is loaded before the thing that needs it, and shared with it.** The closure comes back
dependency-first, each address is deserialised in that order, and a resolver is in force while it
happens — so a material's `ContentReference<Texture>` lands on the very object the manager already
loaded rather than a second copy. Two materials sharing a texture means one texture.

## Downloaded content

`BundleCache` keeps downloaded bundles on the device and `RemoteBundleSource` reads them;
`RoutedBundleSource` puts a local and a remote source behind one `IBundleSource`, picking by whether
the bundle has a URL. Nothing above them knows the difference — an address in a downloadable pack is
asked for exactly like one that shipped in the install.

```csharp
long bytes = assets.DownloadSize("dlc/pack-2/*");        // what is actually missing, not what it weighs
await assets.DownloadAsync(["dlc/pack-2/*"], progress);  // fetch it, without loading any of it
assets.ClearCache("dlc/pack-2/*");                       // give the space back
```

**Keyed by content hash, not by name.** A bundle called `dlc-pack-2` that gets rebuilt is a different
file with the same name, and a cache that trusted the name would serve the old one for ever. Filing
it under its hash makes a rebuilt bundle an ordinary miss, and makes two catalog versions that share
an unchanged bundle share the download.

**Downloads resume.** Bytes accumulate in `<hash>.part` and a fetch that finds one asks the server to
continue from where it stopped. On the connections this feature exists for that is not an
optimisation — a 400 MB pack over a link that drops every few minutes never finishes without it. A
server that ignores the range and sends the whole resource is detected and started again rather than
appended to.

**Nothing is committed unverified.** A completed download has to be the length the catalog says *and*
hash to the CRC the catalog says before it is moved into place, which catches a corrupted transfer and
a URL serving something else entirely. A cache *hit* checks length only: re-hashing hundreds of
megabytes in front of every loading screen is not where that check belongs, and `VerifyAsync` is
there for a caller who wants it.

**An open bundle is not evicted.** A backend is a window onto a mapped file; deleting the file
underneath it is refused on Windows and, on Unix, quietly leaves a reader on something nothing can
find. Refusing everywhere is the behaviour that is the same everywhere.

## Content updates

`ContentUpdate` is step 2 of doc 08's boot sequence: fetch the tiny hash file beside the catalog,
and if it names something new, download the catalog and lay it over the shipped one.

```csharp
var result = await update.ApplyAsync(shippedCatalog);
var assets = new AssetManager(result.Catalog, bundles);
```

**The hash file is checked first because it is tiny.** A catalog for a real game is hundreds of
kilobytes and almost always unchanged; 32 bytes next to it turns the common case — launch, nothing is
new — into one request the size of a packet. It also gives the downloaded catalog something to be
checked against, which a catalog fetched alone does not have.

**Nothing the server does throws.** Unreachable, half-published, built for another platform, corrupt
— each comes back as an outcome with a reason and the best catalog available, because all of them
happen in the field and none is a reason for a game not to start. The distinction that matters in a
log is `Offline` against `Rejected`: offline is a player in a tunnel and fixes itself, rejected is a
broken publish and will not.

**Nothing is cached until it has been parsed and merged.** A catalog that cannot be used must not
overwrite one that can, or the next launch is broken with nothing left to fall back to. The hash file
is written second and read first, so a crash between the two writes reads as "nothing cached" and is
refetched.

**An update can replace an address but not remove one** — the shipped application still has the
bundle on the device, and a runtime that forgot the address would refuse to load something it is
sitting on.

## Content that is streamed rather than loaded

Not everything wants to become an object. A two-minute cutscene is a hundred megabytes, and turning
it into one would mean a loading screen for a cutscene longer than the cutscene — so what the catalog
holds is a small record naming it, and the bytes come back as a stream:

```csharp
using var stream = assets.Open("cutscenes/intro#container");
```

⚠ **It claims nothing and caches nothing**, unlike every `Load` here. There is no object to share, so
there is nothing for a second caller to be given and nothing to release; the stream is the caller's.
That also means two callers get two independent streams over the same bytes, which is exactly what a
video whose picture and sound both seek needs — see
[Vixen.Video](../Vixen.Video/README.md#two-readers-one-seeker).

It is also the only way to get back a payload the content build produced with a tool that is not this
serializer. `ObjectDatabase.Read<T>` demands a matching type id and `ReadObject` demands a registered
serializer; a WebM container, a compressed texture and an audio bitstream have neither, which is what
`WriteRaw` was always for and `ReadRaw` is the other half of.

**Build streamed payloads uncompressed.** A chunk is LZ4-packed by default, so there is no slice of
the memory-mapped bundle that *is* the payload and `Open` has to decompress into an array. Content
that is already compressed — which a video is — pays the build time and saves nothing.

## Still to come

The streaming manager. Reloading in place, so a hot-reloaded asset updates the references pointing at
it rather than replacing them. `Tools/Vixen.ContentServer` — the client half of the update story is
here and tested (including doc 08's byte-count assertion), but there is no tool yet that serves a
content build over HTTP for a developer to point a device at.

Licensed under Apache-2.0.
