# Vixen.Core.Serialization

Binary serialisation with no reflection, no `Reflection.Emit`, and no IL weaving. Annotate a type;
the serializer is emitted at compile time as C# you can read and step through.

```csharp
[DataContract]
public sealed class SaveGame {
    public int Level { get; set; }
    public string? PlayerName { get; set; }
    public float[]? Checkpoints { get; set; }
}

var bytes = Serializer.ToBytes(save);
var loaded = Serializer.Read<SaveGame>(bytes);
```

## What is here

| | |
|---|---|
| `SerializationWriter` / `SerializationReader` | The wire format. `ref struct`s over spans. |
| `DataSerializer<T>` | What a serializer is. |
| `SerializerRegistry` | Where they are found — a static field read, not a dictionary lookup. |
| `Serializer` | The short way to say it for whole values. |
| `Vixen.Core.Serialization.Generator` | Reads `[DataContract]`, writes the serializers. |

## The format

Little-endian, always, through `BinaryPrimitives` with an explicit endianness rather than whatever
the CPU prefers. Lengths and counts are LEB128, so the overwhelmingly common short collection costs
one byte instead of four. Strings are UTF-8 with a length that encodes null as `0` and length *n* as
`n+1`, so null and empty stay distinct without a separate flag. Floats are written **by their bits**,
so `-0f` stays negative and every NaN payload survives — content determinism is a byte comparison,
and a format that normalised either would produce two files for one asset.

Arrays of numeric primitives are one bulk copy. `bool` is deliberately not among them: a byte that is
neither 0 nor 1 is a valid `bool` in memory and would survive a bulk copy as one.

**Reading is span-only, with no stream form**, and that is a deliberate pair with `Vixen.Core.IO`'s
memory mapping. A bundle on disk is mapped rather than read, so "the whole file in a span" costs no
copy and no allocation, and the pages holding the assets nobody asked for are never faulted in.

## Schema evolution

Every object writes two varints ahead of its members: the contract version, and how many members
were written. Two bytes, and they buy the following.

**Appending a member is free, in both directions.** Old data has a smaller count, so the reader stops
where the data stops and leaves the new members at their defaults. No version bump, no migration, no
ceremony — and this is the great majority of real schema changes.

**Removing or reordering a member is not free, and says so.** Data with more members than this build
knows about is refused with a message naming the numbers, rather than read into the wrong fields.

**A version bump means "the layout changed incompatibly".** The reader then looks for

```csharp
public static bool TryMigrate(int fromVersion, ref SerializationReader reader, ref T value)
```

on the contract, and throws `SerializationVersionException` — naming both versions and the method to
declare — if there is none.

**`[DataAlias]` on a member is not used by this format.** Names are not in the stream, because
positional is smaller and faster and the count already handles the case that matters. Member aliases
are for the YAML serializer, where names *are* the format; type aliases will be used here when the
polymorphic type table arrives.

## What the generator does, and does not

Serializers are emitted into their own namespace as standalone classes, so **no type has to be
declared `partial`**. That is the difference between a serialisation library and one that has an
opinion about how every type in the engine is declared. The cost is that only public members are
reachable.

It handles mutable fields and settable properties by assignment, and get-only members — a positional
`record` — by finding a constructor whose parameters match the members by name. A type it cannot
reconstruct is a **build error** (`VXS0101`) rather than a crash on the machine that loads the save
file, which is most of the argument for doing this at compile time.

**A property with no setter is derived; a `readonly` field is not.** That distinction is load-bearing
and was missing. When no constructor matches the members as they stand, the *computed properties*
come off first and the match is retried; only if that fails does everything unassignable come off.
Dropping both in one step is what an immutable struct looks like — `readonly` fields, a constructor
that takes them, a handful of derived properties — and it took the fields with the properties, left
nothing for any constructor to match, and generated a serializer with **no members at all**: two
varints out, every field back as its default, silently. Every type in `Vixen.Core.Mathematics` has
that shape, and nothing had written one, so nothing had noticed.

**An `init` setter is a setter**, reached through `[UnsafeAccessor]` — the same failure with the same
silence, found separately and fixed separately. The two together are why `VXS0102` has still never
been reported: it is declared for "written but cannot be read back", and both of the shapes that
actually hit it are now handled rather than warned about.

Members are ordered by `[DataMember(Order)]` and then by declaration, base class first, so adding a
member to a base type appends to the stream rather than shifting everything a derived type wrote.

## Polymorphism

A member declared as a base class, or a collection of one, keeps whatever each value actually is. The
concrete type's **serialised name** goes in the stream, and the reader looks the serializer up by
that name.

The name comes from `[DataContract("Alias")]`, defaulting to the bare type name, and `[DataAlias]`
records former names — so a type can be renamed, moved between namespaces, or moved between
assemblies without invalidating a byte of existing data. Two types claiming one name is an error at
start-up rather than last-one-wins, because the alternative fails as data loading as the wrong type
in whichever assembly happened to initialise second.

**A sealed type pays nothing.** The generator picks between the two paths by whether the declared
type can have a subtype at all; a sealed class cannot, so its name is never written. That is the
common case for engine data, and it costs one null byte rather than a string.

Writing a derived instance through its *base* serializer directly — rather than through a
polymorphic member — is still refused rather than silently truncated, because that path has no name
to write and would drop everything the derived type adds.

## The object database

`ObjectId` is the xxh128 of a chunk's content, and that one decision buys three things at once:
**deduplication** — two materials with identical parameters are one chunk, without anybody comparing
them; **integrity** — a chunk that does not hash to its own name is corrupt, and `Verify` says so;
**delta detection** — an update knows what changed by comparing names, so a patch ships only the
chunks whose content differs.

```csharp
var db = new ObjectDatabase(new FileOdbBackend(vfs, MountPoints.Database));
var id = db.Write(material, references: [textureId]);
var again = db.Read<Material>(id);
```

**Compression sits outside the hashed region, and that is the load-bearing detail.** The *chunk* —
header plus payload — is what gets hashed. The *blob* — a compression byte, the uncompressed length,
and the possibly-compressed chunk — is what a backend stores. So two builds that disagree about
whether to LZ4 a mesh still produce the same id for it: an incremental update sees no change, a
bundle built with different settings still deduplicates against the loose files, and the determinism
gate compares content instead of comparing settings.

Compression that would make a chunk bigger is not used, which is not hypothetical — it is what every
already-compressed BCn or Ogg payload does. Chunks under 256 bytes are stored raw, because
compressing them spends a frame header to save a handful and costs a decode on every load forever.

**Two backends.** `FileOdbBackend` puts one file per chunk under `<root>/ab/cdef…` — git's layout,
for git's reason: a project accumulates hundreds of thousands of artefacts and a single directory
holding all of them is slow everywhere and unusable somewhere. `BundleOdbBackend` reads a `.bundle`:
a header, an index sorted by id, and one payload region, so a lookup is a binary search with no
allocation and no dictionary built at load time. Backed by a memory-mapped file, a read is a slice of
the map.

A database searches its backends in order and only the first takes writes — loose files first, then
the last content build's bundles, so a rebuilt artefact shadows the packed one without either knowing.

The chunk header carries its references, so loading is read-header → resolve → recurse → deserialise
without deserialising anything to find out what to load. `Closure` is that walk, and the bundle packer
needs the same answer for a completely different reason.

## Still to come

**`ContentReference<T>` / `UrlReference<T>`,** which serialise as a URL plus a type and resolve
through `Vixen.Assets` — a Phase 3 assembly.

**The catalog and the bundle packer.** Which chunks go in which bundle, and the address → id map
that turns `"UI/MainMenu"` into something loadable, are content-build policy — [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md),
Phase 3. The format they will produce is here and tested; the policy is not.

**Generic contracts.** A generic `[DataContract]` needs one serializer per instantiation, which the
registry cannot express without either open-generic construction (reflection, AOT-hostile) or the
generator seeing every closed use. It is a build error today rather than a silent gap.

Licensed under Apache-2.0.
