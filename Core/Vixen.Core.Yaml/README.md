# Vixen.Core.Yaml

The dialect Vixen's `.meta` and `.vxasset` files are written in: a node model that remembers how it
was written, a reader, and an emitter that reproduces what it read byte for byte.

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

## What is not in the dialect

**Anchors and aliases.** An asset reference is a `vx:` scalar, which answers the same question
without admitting the cycles that come with them; reading one is an error naming the file.

**Complex keys.** A mapping or a sequence used as a key is refused rather than flattened, so nothing
downstream has to be defensive about a shape that cannot occur.

Licensed under Apache-2.0.
