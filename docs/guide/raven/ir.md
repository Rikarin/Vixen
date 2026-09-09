---
title: The target-independent IR
slug: raven/ir
kind: guide
area: Raven
summary: The model every back end consumes and none of them may look behind — a structured body over SSA values, storage reached only through an access chain, a deliberately small type system, and a verifier that runs before a back end ever sees a module.
api: [T:Vixen.Raven.IR.IrModule, T:Vixen.Raven.IR.IrShader, T:Vixen.Raven.IR.IrFunction, T:Vixen.Raven.IR.IrEntryPoint, T:Vixen.Raven.IR.IrBinding, T:Vixen.Raven.IR.IrBindingKind, T:Vixen.Raven.IR.IrStream, T:Vixen.Raven.IR.IrStageIo, T:Vixen.Raven.IR.IrSharedVariable, T:Vixen.Raven.IR.IrPermutation, T:Vixen.Raven.IR.IrValueParameter, T:Vixen.Raven.IR.IrValue, T:Vixen.Raven.IR.IrVariable, T:Vixen.Raven.IR.IrVariableKind, T:Vixen.Raven.IR.IrPlace, T:Vixen.Raven.IR.IrAccess, T:Vixen.Raven.IR.IrFieldAccess, T:Vixen.Raven.IR.IrIndexAccess, T:Vixen.Raven.IR.IrSwizzleAccess, T:Vixen.Raven.IR.IrStatement, T:Vixen.Raven.IR.IrBlock, T:Vixen.Raven.IR.IrInstruction, T:Vixen.Raven.IR.IrConstantInstruction, T:Vixen.Raven.IR.IrLoadInstruction, T:Vixen.Raven.IR.IrStoreInstruction, T:Vixen.Raven.IR.IrUnaryInstruction, T:Vixen.Raven.IR.IrBinaryInstruction, T:Vixen.Raven.IR.IrConvertInstruction, T:Vixen.Raven.IR.IrCallInstruction, T:Vixen.Raven.IR.IrArgument, T:Vixen.Raven.IR.IrConstructInstruction, T:Vixen.Raven.IR.IrExtractInstruction, T:Vixen.Raven.IR.IrSelectInstruction, T:Vixen.Raven.IR.IrIntrinsicInstruction, T:Vixen.Raven.IR.IrIntrinsic, T:Vixen.Raven.IR.IrAtomicInstruction, T:Vixen.Raven.IR.IrAtomicOp, T:Vixen.Raven.IR.IrArrayLengthInstruction, T:Vixen.Raven.IR.IrIfStatement, T:Vixen.Raven.IR.IrLoopStatement, T:Vixen.Raven.IR.IrReturnStatement, T:Vixen.Raven.IR.IrBreakStatement, T:Vixen.Raven.IR.IrContinueStatement, T:Vixen.Raven.IR.IrDiscardStatement, T:Vixen.Raven.IR.IrUnaryOp, T:Vixen.Raven.IR.IrBinaryOp, T:Vixen.Raven.IR.IrConversionKind, T:Vixen.Raven.IR.IrType, T:Vixen.Raven.IR.IrTypeKind, T:Vixen.Raven.IR.IrScalarType, T:Vixen.Raven.IR.IrVectorType, T:Vixen.Raven.IR.IrMatrixType, T:Vixen.Raven.IR.IrArrayType, T:Vixen.Raven.IR.IrStructType, T:Vixen.Raven.IR.IrField, T:Vixen.Raven.IR.IrTextureType, T:Vixen.Raven.IR.IrTextureDimension, T:Vixen.Raven.IR.IrDepthTextureType, T:Vixen.Raven.IR.IrSamplerType, T:Vixen.Raven.IR.IrComparisonSamplerType, T:Vixen.Raven.IR.IrStorageImageType, T:Vixen.Raven.IR.IrCapability, T:Vixen.Raven.IR.IrCapabilities, T:Vixen.Raven.IR.IrVerifier, T:Vixen.Raven.IR.IrPrinter]
tags: [raven, shaders, compiler, codegen]
since: 0.1
status: preview
related: [raven/compiling-a-shader, raven/library-ir, raven/compiled-artefacts]
---

## What it is

`IrModule` is the whole compilation, lowered. It is **the boundary the back ends work against**:
GLSL, SPIR-V and everything downstream of them consume an `IrModule`, and none of them reads the
bound tree or the syntax tree. A module holds its `Structs`, its free `Functions` — free functions
and struct methods — and its `Shaders`.

An `IrShader` is one shader's `Bindings`, `Functions`, `Streams`, `SharedVariables`, `Permutations`,
`ValueParameters` and `EntryPoints`. An `IrBinding` is a resource the host must supply, classified by
`IrBindingKind` — `Uniform`, `Texture`, `Sampler`, `StorageBuffer`, `StorageImage` and the rest — and
an `IrEntryPoint` pairs a stage with the `IrFunction` that is it, its `IrStageIo` interface, and, for
a compute stage, its workgroup size. An `IrStream` is an interstage value; an `IrSharedVariable` is
workgroup storage; an `IrPermutation` is a compile-time key and an `IrValueParameter` a compile-time
number.

Inside a function the shape is **structured control flow over SSA values**. An `IrFunction` owns its
value numbering, so `%0` means the same thing throughout one function and nothing outside it. An
`IrValue` is produced by exactly one instruction and never reassigned; `IrVariable` is the storage,
classified by `IrVariableKind`, and it is the only thing that mutates.

Everything that reads or writes storage goes through an `IrPlace`: a root `IrVariable` plus a chain
of `IrAccess` steps, which are `IrFieldAccess` (a field by index), `IrIndexAccess` (an array, vector
or matrix by a runtime value) and `IrSwizzleAccess` (a set of lanes). `IrLoadInstruction` and
`IrStoreInstruction` are the only two instructions that touch memory at all.

A body is a tree of `IrStatement`. `IrBlock` sequences; `IrIfStatement`, `IrLoopStatement`,
`IrBreakStatement`, `IrContinueStatement`, `IrReturnStatement` and `IrDiscardStatement` are the
control flow; and `IrInstruction` is the rest — `IrConstantInstruction`, the loads and stores,
`IrUnaryInstruction` and `IrBinaryInstruction` over `IrUnaryOp` and `IrBinaryOp`,
`IrConvertInstruction` over `IrConversionKind`, `IrCallInstruction` with its `IrArgument`s,
`IrConstructInstruction`, `IrExtractInstruction`, `IrSelectInstruction`, `IrIntrinsicInstruction`
over the `IrIntrinsic` table, `IrAtomicInstruction` over `IrAtomicOp`, and
`IrArrayLengthInstruction`.

The type side is `IrType` and its `IrTypeKind`: `IrScalarType`, `IrVectorType`, `IrMatrixType`,
`IrArrayType`, `IrStructType` with its `IrField`s, and the resource types — `IrTextureType` with its
`IrTextureDimension`, `IrDepthTextureType`, `IrSamplerType`, `IrComparisonSamplerType` and
`IrStorageImageType`. `IrCapability` and `IrCapabilities` record what a module requires of a device.

Two tools sit beside the model: `IrVerifier`, and `IrPrinter`, which writes a module out in the
readable form the `--emit-ir` dumps and the golden IR tests compare.

## What it is for

**One lowering, many back ends.** Everything that is hard about a shader language — overload
resolution, generics, `compose` slots, inheritance, permutation folding, stream plumbing — happens
*above* this model and is gone by the time a back end runs. What arrives is a program a code
generator can walk in one pass.

That is why the type system here is **deliberately much smaller than the symbol model's**. `IrType`
holds only what every GPU target can represent, so a back end can switch on `Kind` exhaustively and
anything unrepresentable is refused during lowering — with a diagnostic naming the construct —
rather than surfacing as a missing switch arm in one emitter and a wrong picture in the other. A
missing arm does not fail to build; it silently never matches.

It is also the level the two back ends are compared at. The differential oracle runs Raven's SPIR-V
against glslang's reading of Raven's GLSL, and because both start from *this* module, a disagreement
is a claim about the emitters and not about the lowering. The corollary is worth stating plainly: a
bug in the IR itself shows up identically in both paths and is invisible to that comparison, which is
why the numeric device gates exist beside it.

## Using it

**Storage is reached through a place, and only through a place.** There is no pointer arithmetic and
no address-of. `d.tint.xy = v` lowers to one `IrStoreInstruction` whose `IrPlace` is the variable
`d`, a field access, and a swizzle access — not to a load, a modify and a store of the whole struct.
That is what lets a back end emit an access chain in SPIR-V and a member expression in GLSL from the
same node.

**Control flow is structured, not a basic-block graph.** There is no `goto` and no φ. Raven's source
language has no unstructured jump either, so lowering never has to reconstruct structure — and both
targets want it structured anyway: SPIR-V needs its merge blocks and GLSL needs statements. A CFG
would have meant recovering what was thrown away.

**Run `IrVerifier.Verify` before a back end sees a module.** It checks that values are defined once
and used only where they are in scope, that types line up on every instruction, that access chains
are well formed and that control flow is sane, reporting `RVN3010` for each problem. A back end can
then assume the IR is valid instead of re-checking it — which means a hand-built module that skips
the verifier can crash an emitter rather than being rejected by it.

⚠ **An unsized `IrArrayType` is legal in exactly one position**: the element type of a
`StorageBuffer` binding, which is the spec's own rule that an unsized array may only be a storage
block's last member. Everywhere else it stays `RVN4001`. The IR expresses what the targets allow
rather than a superset of it, which is the whole reason the verifier can be believed.

⚠ **Reference equality is type identity for the interned types.** `IrScalarType` instances are
interned — `IrScalarType.Float` is one object — so a comparison by reference is a comparison of
types. That is a property to rely on, not one to reproduce: building a second `IrScalarType` for
`float` would compare unequal to every existing one and fail verification in a way that reads as a
type error in the source.

## Examples

Lowering a compilation and verifying the result before anything reads it:

```csharp no-compile="a fragment; `compilation` comes from Compilation.Create"
var diagnostics = new DiagnosticBag();
var module = Lowerer.Lower(compilation, diagnostics);

if (!IrVerifier.Verify(module, diagnostics)) {
    // RVN3010 for each problem; a back end may not run on a module that failed here.
    return;
}

Console.WriteLine(IrPrinter.Print(module));
```

Counting what a shader asks of the host, which is the question a pipeline builder asks:

```csharp no-compile="a fragment; `shader` is an IrShader"
foreach (var group in shader.Bindings.GroupBy(binding => binding.Kind)) {
    Console.WriteLine($"{group.Key}: {group.Count()}");
}

foreach (var entryPoint in shader.EntryPoints) {
    var size = entryPoint.WorkgroupSize is { } workgroup ? workgroup.ToString() : "—";
    Console.WriteLine($"{entryPoint.Stage} {entryPoint.Function.Name} workgroup {size}");
}
```

Walking a body, which every back end does exactly once:

```csharp no-compile="a fragment; `statement` is an IrStatement"
static IEnumerable<string> Callees(IrStatement statement) =>
    statement switch {
        IrCallInstruction call => [call.Function.Name],
        IrBlock block => block.Statements.SelectMany(Callees),
        IrIfStatement branch => Walk(branch.Then, branch.Else),
        IrLoopStatement loop => Walk(loop.Condition, loop.Body, loop.Continue),
        _ => []
    };

static IEnumerable<string> Walk(params IrStatement?[] parts) =>
    parts.Where(part => part is not null).SelectMany(part => Callees(part!));
```

⚠ The `switch` above is the shape to copy and also the shape to be careful with: **a missing arm does
not fail to build, it silently never matches** — which in an emitter is a construct that produces no
output rather than an error. `IrStatement`'s sealed hierarchy is the closed set to check a walker
against, and `IrVerifier` is what says the module only contains members of it.

⚠ And a statement's *parts* are as easy to miss as a statement's kind. An `IrLoopStatement` carries
three blocks, not one: `Condition` holds the instructions that recompute the test on every iteration
and `Continue` holds the step, so a walker that visited only `Body` would miss every call in a `for`
loop's header — a real answer, quietly short.

## See also

- [Compiling a shader](compiling-a-shader.md) — the phases that produce this model, and the
  `Lowerer` that is the one thing allowed to build it.
- [The `.rvnlib` IR](library-ir.md) — the *wire* form of the same idea, and why it is a second model
  rather than this one serialised: this is an in-memory graph with object references in it, and a
  wire format has to be readable by a program that has not built the thing being pointed at yet.
- [Compiled artefacts](compiled-artefacts.md) — what a back end's output is packaged as.
