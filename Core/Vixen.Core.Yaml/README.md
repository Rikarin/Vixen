# Vixen.Core.Yaml

The dialect Vixen's own text assets are written in — `.meta`, `.vxmat`, `.vxscene`, `.vxgroup`,
`.vxdef` and the rest: a node model that remembers how it was written, a reader, and an emitter that
reproduces what it read byte for byte.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § "The `.meta` file".

```csharp
var root = (YamlMapping)YamlReader.Read(File.ReadAllText("hero.png.meta"));
root.Set("metaVersion", new YamlScalar("2", YamlScalarStyle.Plain));
File.WriteAllText("hero.png.meta", YamlWriter.Write(root));
```

## Byte fidelity is the requirement, not a nicety

A schema migration touches every `.meta` file in a project. If reading and writing were not the
identity, the resulting diff would be a hundred thousand files of reformatting with the real change
buried in it, and nobody would review it. So the model carries everything a rewrite could otherwise
lose: flow style, quoting style, type tags, key order, and **comments**.

`RoundTripTests` reads each fixture in `Corpus/`, writes it back, and compares bytes. A property test
does the same over generated documents, so a disagreement between reader and emitter surfaces without
anyone having thought of the case — which is how the four rules below were found.

**One thing is normalised rather than reproduced:** a comment is written `# text`, one space.
YamlDotNet's scanner has already dropped the whitespace after the `#`, so there is nothing to
reproduce. It converges — writing twice changes nothing — which is what a migration actually needs.

## The dialect

Two-space indent, block style, no document-start marker, keys in the order the mapping holds them
(which is the order the C# record declares them), `\n` line endings, and a trailing newline.

**The root is always a block.** A two-key `.meta` rendered as `{ guid: …, metaVersion: 1 }` because
it happened to be short would turn the next edit into a whole-line diff.

**Flow style for small all-scalar collections** — `{ u: Repeat, v: Repeat }`, `[ui, hd]`. The
threshold is 60 columns including indent, which is the smallest round number that keeps every flow
collection in doc 08's worked examples on one line while sending that document's `materialMapping`,
whose two GUID values come to over ninety, to the block form it is shown in.

**Quoting answers two different questions.** Whether writing a value bare would break the document —
a leading indicator, a `": "`, a line break — and whether it would come back as something other than
a string. `YamlScalarStyle.Plain` skips the second: the object mapper marks numbers and enums plain
because it is the only layer that knows the type. `Any` means "a string", so `2048` and `true` are
quoted, and a version field typed as text does not silently become a number the first time someone
writes `1.20`. `-8` is not quoted: `-` is only an indicator when a space follows it.

## What is used from YamlDotNet, and what is not

The **event stream** — `Scanner`, `Parser`, `ParsingEvent` — and nothing else. Its object model and
its reflection-driven `Deserializer` are exactly what a `.meta` file must not go through: type
resolution here is the generated `TypeRegistry`, so reading an asset works on a trimmed NativeAOT
build where a reflective deserializer finds no members at all.

The emitter is Vixen's own. The dialect is narrower than anything YamlDotNet's emitter can be
configured into, and byte fidelity is not something to approximate.

## Binding to types

```csharp
var meta = YamlSerializer.Parse<AssetMeta>(File.ReadAllText("hero.png.meta"));
var importer = (TextureImportSettings)meta.Importer!;   // chosen by the !TextureImporter tag
```

A member is found through a `TypeDescriptor`, read and written through the lambdas the reflection
generator emitted, and a `!TextureImporter` tag is resolved by asking `TypeRegistry` what claims that
name. Nothing calls `PropertyInfo.GetValue` or walks an assembly.

Keys are camelCase on write and matched case-insensitively on read, because these files are
hand-edited and someone who typed `MaxSize` meant `maxSize`. An unknown key is **ignored** — a
project opened in an older editor after someone added a setting must still load — and reported
through `OnUnknownKey`, because dropping it silently is the other failure.

### The AOT constraint, and where it went

`Array.CreateInstance(elementType, n)`, `MakeGenericType` and `Activator.CreateInstance(Type)` are
all `RequiresDynamicCode`. A binder built on them works on a desktop and throws on a phone, and this
repository compiles `IL3050` as an error, so the build refused them outright — which is what
[docs/plan/14](../../docs/plan/14-roadmap.md) means by scheduling Phase 3 early.

The answer is the one the rest of the engine gives: a generator saw the type in the source, so a
generator writes the constructor. Every collection type reachable from a described member is
registered in `CollectionFactory` by the reflection generator — `static count => new
TargetOverride[count]` — and the binder asks for one rather than building a type. A list *interface*
is backed by an array, which satisfies it with no copy.

**`ImmutableArray<T>` is refused by name**, with the reason in the message: constructing one for a
`T` known only at run time needs `MakeGenericMethod`. Declare the member `T[]`; in an init-only
record it is just as immutable. Doc 08's worked example uses `ImmutableArray<T>` and is wrong about
this.

## `.meta` sidecars

```csharp
var meta = AssetMetaFile.ReadFile("Assets/Textures/hero.png.meta");
AssetMetaFile.WriteFile(AssetMetaFile.PathFor("Assets/Textures/hero.png"), meta with { … });
```

The pattern is Unity's, unchanged (ADR-005): one sidecar per file, one per folder, committed, and the
GUID is the identity — generated once, never rewritten, path-independent, so moving or renaming
breaks nothing. The schema inside is Vixen's.

**`MetaScanner` reads three lines and stops.** Doc 08 budgets a GUID-index rebuild of a hundred
thousand assets at under ten seconds; parsing a hundred thousand complete documents does not fit in
that, and reading each file's envelope does. It is a line scanner rather than the YAML parser, and it
only looks at column-zero keys — an importer's block has a `version:` of its own, and a scanner that
wandered into it would produce an index that is confidently wrong. Anything it cannot make sense of it
declines, and the caller falls back to a full parse.

**`metaVersion` has a real chain behind it.** Each step takes a document from *n* to *n+1*, so a file
five versions old is upgraded by five small reviewable functions rather than one that knows every
historical shape. Steps work on the **node tree**, which is the only place they can — a document old
enough to need migrating does not fit the current type — and which means everything a step did not
touch, comments included, is written back exactly as it was found. There are no steps yet; the
mechanism ships now so the first real migration is one function rather than a design.

**Sub-asset ids come from what the sub-asset is** — importer, kind, name, hashed with XxHash32 — never
from where it landed in the source file. Unity's `fileID` values are internal magic numbers, so
re-exporting an FBX whose mesh order changed renumbers everything and breaks every reference; "my
prefab lost its mesh after an artist re-exported" is avoidable by construction. A collision is
reported naming both, at import, rather than silently resolved.

**Per-target overrides are a node-level merge**, not a partial record per settings type. `Android` is
applied before `Android/Vulkan`, so the more specific target wins, and a prefix must be a whole
segment — `Windows` applies to `Windows/x64` and not to `WindowsStore`. Sparseness falls out of a key
being absent rather than a member being null, which also means an override cannot accidentally clear
something by not mentioning it.

## What is not in the dialect

**Anchors and aliases.** An asset reference is a `vx:` scalar, which answers the same question
without admitting the cycles that come with them; reading one is an error naming the file.

**Complex keys.** A mapping or a sequence used as a key is refused rather than flattened, so nothing
downstream has to be defensive about a shape that cannot occur.

Licensed under Apache-2.0.
