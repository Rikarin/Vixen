---
title: The .rvnlib IR
slug: raven/library-ir
kind: guide
area: Raven
summary: The lowered half of a compiled library — how a function body is written down so a later compilation can link it, why every cross-reference travels as a key or an index rather than as an object, and what a body may not contain if it is to travel at all.
api: [T:Vixen.Raven.Artefacts.LibraryIr, T:Vixen.Raven.Artefacts.LibraryIrStruct, T:Vixen.Raven.Artefacts.LibraryIrField, T:Vixen.Raven.Artefacts.LibraryIrVariable, T:Vixen.Raven.Artefacts.LibraryIrFunction, T:Vixen.Raven.Artefacts.LibraryIrValue, T:Vixen.Raven.Artefacts.LibraryIrTypeReference, T:Vixen.Raven.Artefacts.LibraryIrStatement, T:Vixen.Raven.Artefacts.LibraryIrBlock, T:Vixen.Raven.Artefacts.LibraryIrConstant, T:Vixen.Raven.Artefacts.LibraryIrLoad, T:Vixen.Raven.Artefacts.LibraryIrStore, T:Vixen.Raven.Artefacts.LibraryIrUnary, T:Vixen.Raven.Artefacts.LibraryIrBinary, T:Vixen.Raven.Artefacts.LibraryIrConvert, T:Vixen.Raven.Artefacts.LibraryIrIntrinsic, T:Vixen.Raven.Artefacts.LibraryIrArgument, T:Vixen.Raven.Artefacts.LibraryIrCall, T:Vixen.Raven.Artefacts.LibraryIrConstruct, T:Vixen.Raven.Artefacts.LibraryIrExtract, T:Vixen.Raven.Artefacts.LibraryIrSelect, T:Vixen.Raven.Artefacts.LibraryIrIf, T:Vixen.Raven.Artefacts.LibraryIrLoop, T:Vixen.Raven.Artefacts.LibraryIrReturn, T:Vixen.Raven.Artefacts.LibraryIrBreak, T:Vixen.Raven.Artefacts.LibraryIrContinue, T:Vixen.Raven.Artefacts.LibraryIrDiscard, T:Vixen.Raven.Artefacts.LibraryIrPlace, T:Vixen.Raven.Artefacts.LibraryIrAccess, T:Vixen.Raven.Artefacts.LibraryIrFieldAccess, T:Vixen.Raven.Artefacts.LibraryIrIndexAccess, T:Vixen.Raven.Artefacts.LibraryIrSwizzleAccess]
tags: [raven, shaders, packaging, content-build]
since: 0.1
status: preview
related: [raven/compiled-artefacts, raven/compiling-a-shader, raven/ir]
---

## What it is

`LibraryIr` is the lowered half of a `.rvnlib`: `Structs` and `Functions`, and nothing else. It is
a mirror of the compiler's own `IrModule` minus its shaders — a library exports types and functions,
while a pipeline stage belongs to the effect that declares it.

A `LibraryIrStruct` carries a `Key`, a `Name` and its `Fields`, each a `LibraryIrField`. A
`LibraryIrFunction` carries the same `Key`/`Name` pair, a `ReturnType`, its `Parameters` and
`Locals` (both `LibraryIrVariable`), a `Values` table of `LibraryIrValue`, a `ValueCount`, and a
`Body`. Every type in either travels as a `LibraryIrTypeReference`, which shares the compiler's
`IrTypeKind` rather than restating it — a second enum could only drift from the first.

A body is a tree of `LibraryIrStatement`, serialised polymorphically under an `"op"` discriminator
so the artefact reads the way an IR dump does. Nineteen shapes cover it: `LibraryIrBlock`,
`LibraryIrConstant`, `LibraryIrLoad`, `LibraryIrStore`, `LibraryIrUnary`, `LibraryIrBinary`,
`LibraryIrConvert`, `LibraryIrIntrinsic`, `LibraryIrCall` with its `LibraryIrArgument`s,
`LibraryIrConstruct`, `LibraryIrExtract`, `LibraryIrSelect`, `LibraryIrIf`, `LibraryIrLoop`,
`LibraryIrReturn`, `LibraryIrBreak`, `LibraryIrContinue` and `LibraryIrDiscard`.

Storage is addressed by a `LibraryIrPlace` — a root index plus a chain of `LibraryIrAccess` steps,
which are `LibraryIrFieldAccess`, `LibraryIrIndexAccess` and `LibraryIrSwizzleAccess`.

## What it is for

It is what makes a shader *package* possible. `Brdf.rvnlib` can be referenced by a project that
never sees its source, and a call into it costs nothing at run time because it is resolved before
any back end runs. The declaration half of the library (see
[Compiled artefacts](compiled-artefacts.md)) is what a consumer *binds* against; this half is what
it *links*.

That division is why the shapes here are a second model rather than the IR itself. The IR is an
in-memory graph with object references in it; this is a wire format, and a wire format has to be
readable by a program that has not yet built the thing being pointed at.

## Using it

**Every cross-reference is a key or an index, never an object.** A call names its callee by
`LibraryIrFunction.Key`; a struct-typed field names the struct by `LibraryIrStruct.Key`; a place
names its root by position. That is what lets a reader rebuild a graph with cycles in it — two
structs may hold each other, and a function may be called before it has been read.

⚠ **`Key` and `Name` are two different things, and conflating them was a real defect.** A name only
has to be unique inside the module that coined it, but a consumer links every library it references
into *one* module. Keyed by name, two packages that each declared a `static func Of` became one
entry and a call got whichever library loaded first — with no diagnostic, because nothing was
missing. The same happened one version later for structs: two `struct Shape`s collapsed into one
object carrying the first one's fields, loud only when the field counts differed. A key is
`<library>::<struct>` for a struct and a qualified signature for a function, and it is matched
rather than parsed, so making it total costs nothing.

⚠ **A tuple and a monomorphised generic keep a bare key on purpose.** Neither has a declaration to
match on, so for those the *name* is the identity: qualifying it would split a type that has to stay
one, and a library function returning `(float, float)` would stop being assignable to the caller's
local. Removing that exemption prints `RVN3010: store: Tuple_f32_f32 does not match
Tuple_f32_f32#1`.

**`Values` is a table, not an array.** The numbering has gaps — dead-branch elimination drops a
folded branch after its condition was numbered — so `ValueCount` travels separately rather than
being inferred from the table's length. Writing each type once and referring to a value by id alone
is also what makes the reader's interning trivial, which is what the IR verifier's define-once check
needs.

**A `LibraryIrArgument` carries a value id or a root index, not both.** By value, `Value` is the
id; by reference, `Reference` is the root's index in parameters-then-locals — a root index because
there is no value, what crosses is the storage.

**Some bodies cannot travel and are refused rather than exported.** A body that reads a shader
binding (`RVN5001`), that touches a `stream` (`RVN5007`) or that touches `groupshared` storage
(`RVN5008`) names storage belonging to the *consuming* shader. The refusal is transitive: a
function that merely calls such a helper is just as unexportable, because linking it would drag the
helper along. A `LibraryIrPlace.Root` therefore indexes parameters followed by locals and nothing
else — a library function has no global root, and that is a consequence of the refusal rather than
a limitation of the format.

## Examples

Reading a library and walking what it exports:

```csharp no-compile="a fragment; the path is the caller's"
var library = CompiledLibraryReader.ReadFile("Math.rvnlib");

foreach (var function in library.Ir.Functions) {
    Console.WriteLine($"{function.Name} ({function.Key}) — {function.Values.Length} values");
}
```

Finding what a body calls, which is the question a linker asks:

```csharp no-compile="a fragment; `body` is a LibraryIrBlock"
static IEnumerable<string> Callees(LibraryIrStatement statement) =>
    statement switch {
        LibraryIrCall call => [call.Function],
        LibraryIrBlock block => block.Statements.SelectMany(Callees),
        LibraryIrIf branch => Callees(branch.Then).Concat(Callees(branch.Else ?? new LibraryIrBlock())),
        LibraryIrLoop loop => Callees(loop.Condition).Concat(Callees(loop.Body)),
        _ => []
    };
```

⚠ The `switch` above is the shape to copy and also the shape to be careful with: a missing arm does
not fail to build, it silently never matches. `LibraryIrStatement`'s `JsonDerivedType` list is the
closed set to check a walker against.

For inspection rather than code, `CompiledLibraryWriter.WriteJson` prints the whole library —
declarations and this IR — as indented JSON with no container around it. It is readable in a diff
and deliberately not accepted by the reader.

## See also

- [Compiled artefacts](compiled-artefacts.md) — the `.rvnlib` container this is the second half of,
  its declaration half, and why a reader refuses a version it does not know.
- [Compiling a shader](compiling-a-shader.md) — the phases that produce it, and `RavenReference`,
  which is how a `.rvnlib` reaches the next compilation.
- [The target-independent IR](ir.md) — the in-memory model this one mirrors, and why a wire format
  could not simply be that model serialised.
