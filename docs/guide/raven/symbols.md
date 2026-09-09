---
title: The Raven symbol model
slug: raven/symbols
kind: guide
area: Raven
summary: What binding produces — the entity hierarchy a tool asks questions of, why type identity is by reference for the intrinsics and structural for the seven constructed shapes, how generics are read through a substitution rather than instantiated, and why this model is deliberately richer than the IR the back ends see.
api: [T:Vixen.Raven.Symbols.Symbol, T:Vixen.Raven.Symbols.SymbolKind, T:Vixen.Raven.Symbols.NamespaceSymbol, T:Vixen.Raven.Symbols.TypeSymbol, T:Vixen.Raven.Symbols.TypeKind, T:Vixen.Raven.Symbols.SpecialType, T:Vixen.Raven.Symbols.NamedTypeSymbol, T:Vixen.Raven.Symbols.PrimitiveTypeSymbol, T:Vixen.Raven.Symbols.BuiltInNamedTypeSymbol, T:Vixen.Raven.Symbols.BuiltInTypes, T:Vixen.Raven.Symbols.ErrorTypeSymbol, T:Vixen.Raven.Symbols.ArrayTypeSymbol, T:Vixen.Raven.Symbols.TupleTypeSymbol, T:Vixen.Raven.Symbols.SequenceTypeSymbol, T:Vixen.Raven.Symbols.BufferTypeSymbol, T:Vixen.Raven.Symbols.SampledTextureTypeSymbol, T:Vixen.Raven.Symbols.StorageImageTypeSymbol, T:Vixen.Raven.Symbols.ImageFormat, T:Vixen.Raven.Symbols.ImageFormats, T:Vixen.Raven.Symbols.MethodSymbol, T:Vixen.Raven.Symbols.MethodKind, T:Vixen.Raven.Symbols.FieldSymbol, T:Vixen.Raven.Symbols.PropertySymbol, T:Vixen.Raven.Symbols.ParameterSymbol, T:Vixen.Raven.Symbols.RefKind, T:Vixen.Raven.Symbols.LocalSymbol, T:Vixen.Raven.Symbols.ResourceKind, T:Vixen.Raven.Symbols.ResourceSet, T:Vixen.Raven.Symbols.ShaderStage, T:Vixen.Raven.Symbols.WorkgroupSize, T:Vixen.Raven.Symbols.StageBuiltIn, T:Vixen.Raven.Symbols.StageBuiltInInfo, T:Vixen.Raven.Symbols.StageBuiltIns, T:Vixen.Raven.Symbols.Intrinsics, T:Vixen.Raven.Symbols.TypeParameterSymbol, T:Vixen.Raven.Symbols.ConstructedNamedTypeSymbol, T:Vixen.Raven.Symbols.TypeMap, T:Vixen.Raven.Symbols.SubstitutedSymbols, T:Vixen.Raven.Symbols.SubstitutedFieldSymbol, T:Vixen.Raven.Symbols.SubstitutedPropertySymbol, T:Vixen.Raven.Symbols.SubstitutedMethodSymbol, T:Vixen.Raven.Symbols.SubstitutedParameterSymbol, T:Vixen.Raven.Symbols.SynthesizedFieldSymbol, T:Vixen.Raven.Symbols.SynthesizedMethodSymbol, T:Vixen.Raven.Symbols.SynthesizedParameterSymbol, T:Vixen.Raven.Symbols.Conversion, T:Vixen.Raven.Symbols.ConversionKind, T:Vixen.Raven.Symbols.Conversions]
tags: [raven, shaders, compiler, tooling]
since: 0.1
status: preview
related: [raven/compiling-a-shader, raven/ir, raven/shader-reflection, raven/syntax]
---

## What it is

Binding turns a syntax tree into **symbols**, and `Vixen.Raven.Symbols` is that model. It is what a
`SemanticModel` hands back, what overload resolution runs over, what the lowerer reads, and what any
tool asking "what does this name mean" gets an answer in.

`Symbol` is the root: a namespace, a type, a member, a parameter or a local, told apart by
`SymbolKind`. Every symbol knows its `ContainingSymbol` and can walk up to its `ContainingType` and
`ContainingNamespace`; `DeclaringSyntax` is the node that declared it, and is `null` for everything
the compiler supplies rather than reads. `NamespaceSymbol` is the top of that chain — `package A.B`
builds `<global> → A → B` and every type in the file lands in `B`.

`TypeSymbol` is everything usable as a type, classified by `TypeKind`. Shader languages care about
scalars, vectors and matrices, so those are their own kinds rather than being folded into `Struct`,
and `SpecialType` names the intrinsic a type *is* — every special case in the binder (numeric
promotion, literal typing, swizzles, intrinsic signatures) keys off that enum rather than off a name.

The type shapes divide into three groups.

- **Named** — `NamedTypeSymbol`, which is a source `shader`/`struct`/`class`/`protocol`/`enum`, or a
  type the compiler supplies. `PrimitiveTypeSymbol` is a scalar, vector or matrix;
  `BuiltInNamedTypeSymbol` is `string`, `object` and the fixed GPU resource types; both are
  singletons owned by `BuiltInTypes`. `ErrorTypeSymbol` stands in for a name that did not resolve.
- **Constructed** — built on demand rather than declared: `ArrayTypeSymbol` (`T[4]`, `T[]`, `T[,]`),
  `TupleTypeSymbol`, `SequenceTypeSymbol` (what a range expression produces and `for (i in …)`
  consumes), and the three resource shapes written with angle brackets that are *not* generics —
  `BufferTypeSymbol` (`Buffer<T>`, `RWBuffer<T>`), `SampledTextureTypeSymbol` (an integer-sampled
  `Texture2D<uint4>`) and `StorageImageTypeSymbol` (`RWTexture2D<float4>`, whose format comes from
  `ImageFormats`' table of `ImageFormat`).
- **Generic** — `TypeParameterSymbol` for a `T`, `ConstructedNamedTypeSymbol` for a `Box<int>`.

Members are `MethodSymbol` (classified by `MethodKind` — ordinary, constructor, accessor, observer,
operator, conversion, local function, intrinsic), `FieldSymbol`, `PropertySymbol`, `ParameterSymbol`
with its `RefKind`, and `LocalSymbol`.

The GPU-facing facts live on those members: `ResourceKind` says how a field binds, `ResourceSet` says
which of the engine's four descriptor sets (plus the bindless one) it lands in, `ShaderStage` and
`WorkgroupSize` say what an entry point is, and `StageBuiltIn` — described by `StageBuiltInInfo` and
tabulated in `StageBuiltIns` — says which pipeline-supplied value a stage parameter carries.
`Intrinsics` is the built-in function library, and every entry in it is an ordinary `MethodSymbol` in
global scope.

Two supporting pieces finish the model. `TypeMap` substitutes type arguments for parameters, and
`SubstitutedSymbols` produces the members of a constructed generic through one —
`SubstitutedFieldSymbol`, `SubstitutedPropertySymbol`, `SubstitutedMethodSymbol` and
`SubstitutedParameterSymbol`, beside the `Synthesized*` family the compiler invents outright (a
tuple's elements, for instance). `Conversions.Classify` answers whether one type reaches another,
returning a `Conversion` over a `ConversionKind`.

## What it is for

**It is the model a question is asked of, and the IR is the model an answer is emitted from.** Both
exist because they are answering different things, and the difference is deliberate:
`Vixen.Raven.IR`'s type system is *smaller* than this one on purpose, so a back end can switch on
`Kind` exhaustively. This one is larger because it has to describe what the author wrote — including
the parts that will not survive lowering at all.

That is what makes it the right model for tooling. `Compilation.GetAllTypes`,
`Compilation.GetEntryPoints` and `SemanticModel.GetSymbolInfo` are the reflection surface the shader
compiler, the editor's shader graph and the `.rvnlib` writer all read; none of them can work from the
IR, because by the time a module exists the generic was monomorphised, the protocol conformance was
resolved and the `compose` slot was filled.

**`Conversions` is where implicit conversion is decided, and it is decided from the two types alone.**
Everything expression-aware — a constant literal that happens to fit the target — is layered on top by
the binder. Keeping the type-level half here is what lets overload resolution rank candidates by
`Conversion.Cost` without holding an expression: identity 0, numeric or constant 1, splat 3,
reference 4, and everything else `int.MaxValue`, which is how a splat never beats a widening.

## Using it

⚠ **Type identity is by reference for the intrinsics and structural for exactly seven shapes.** The
seven that override equality are `ArrayTypeSymbol`, `TupleTypeSymbol`, `SequenceTypeSymbol`,
`BufferTypeSymbol`, `SampledTextureTypeSymbol`, `StorageImageTypeSymbol` and
`ConstructedNamedTypeSymbol` — the ones the binder *builds* rather than declares, which is why they
need no interning table. Everything else compares by reference, and that is correct because
everything else is either a singleton in `BuiltInTypes` or a single symbol built once from a
declaration. So `Equals` is the call that is right in both cases and `ReferenceEquals` is the one
that silently works until the first `float[4]`.

⚠ **This is the opposite choice from the IR's, in the same compiler.** `IrScalarType.Float` is one
interned object and reference equality *is* type identity there. The two models can disagree because
they have different jobs: the IR is closed and small enough to intern exhaustively; the symbol model
has to be able to name `Box<Box<int>>[7]` without having been told about it first.

**Read a generic's members through the construction, not through the definition.**
`ConstructedNamedTypeSymbol.GetMembers` already substitutes on the way out, so `Box<int>.Value` has
type `int`. Reaching for `OriginalDefinition.GetMembers` gets you `T` and a bug that only appears for
a caller who instantiated with something other than what you tested.

**A resource asks the type, not a name.** `TypeSymbol.ResourceKind` is on the base rather than only on
`BuiltInNamedTypeSymbol`, because `Buffer<Particle>` and `Buffer<Light>` are two different type
symbols that bind identically. `TypeSymbol.IsWritableResource` is the read/write half — false for
everything the host uploads.

**An unresolved name is a symbol, not a null.** `ErrorTypeSymbol.Instance` flows through the rest of
the expression, and `Conversions.Classify` deliberately says `Identity` for it in either direction, so
one mistake reports one diagnostic instead of a wall of them. A tool that treats an error type as a
real type will report nonsense; check `TypeSymbol.IsErrorType` before believing an answer.

⚠ **`WorkgroupSize.Invalid` is `default`, and it is not the same as absent.**
`MethodSymbol.WorkgroupSize` is `null` when no `[ComputeShader(…)]` was written and `Invalid` when one
was written and could not be read. Both are errors, but collapsing them sends the author looking for
a missing attribute that is right there.

## Examples

Walking every entry point and reporting what the pipeline has to supply:

```csharp no-compile="a fragment; `compilation` comes from Compilation.Create"
foreach (var entryPoint in compilation.GetEntryPoints()) {
    var workgroup = entryPoint.Stage == ShaderStage.Compute
        ? entryPoint.WorkgroupSize?.ToString() ?? "unset"
        : "—";

    Console.WriteLine($"{entryPoint.Stage} {entryPoint.ToDisplayString()} workgroup {workgroup}");

    foreach (var parameter in entryPoint.Parameters) {
        // A parameter's `[Semantic("…")]` plus the stage is what names a built-in; the
        // same spelling means different things on different stages, so both are the key.
        var builtIn = StageBuiltIns.Of(parameter.SemanticName, entryPoint.Stage);
        var role = builtIn is null ? "located input" : builtIn.BuiltIn.ToString();
        Console.WriteLine($"    {parameter.Name}: {parameter.Type.ToDisplayString()} — {role}");
    }
}
```

Classifying a shader's fields into the descriptor sets the host will bind, which is the question a
pipeline builder asks of the symbol model rather than of the IR:

```csharp no-compile="a fragment; `shader` is a NamedTypeSymbol with TypeKind.Shader"
var bindings = shader.GetMembers()
    .OfType<FieldSymbol>()
    .Where(field => field.Type.ResourceKind != ResourceKind.None);

foreach (var group in bindings.GroupBy(field => field.Type.ResourceKind)) {
    Console.WriteLine($"{group.Key}: {group.Count()}");
}
```

Asking whether an argument type is acceptable where a parameter type is expected — the type-level
half of what overload resolution does on every call:

```csharp no-compile="a fragment; `argument` and `parameter` are TypeSymbols"
var conversion = Conversions.Classify(argument, parameter);

if (!conversion.Exists) {
    // No conversion at all: the call is not a candidate.
} else if (conversion.IsImplicit) {
    // Applicable, and Cost is how it ranks against the other candidates.
    Console.WriteLine($"{conversion.Kind} costs {conversion.Cost}");
} else {
    // Explicit only: applicable to a cast, never to an argument.
}
```

⚠ Note what the third example does *not* do: it never compares `Kind` against a list of implicit
kinds of its own. `Conversion.IsImplicit` is that list and `Conversion.Cost` is the ranking, and a
copy of either is a second place to forget the kind that lands next. `ConversionKind` has nine
members today and five of them are implicit, which is exactly the size where writing the check out by
hand reads as harmless.

Reading a constructed generic's members, and the reference-versus-structural rule in one line:

```csharp no-compile="a fragment; `box` is a ConstructedNamedTypeSymbol for Box<int>"
var value = box.GetMembers("Value").OfType<FieldSymbol>().Single();

// True: substitution happened on the way out of GetMembers.
Console.WriteLine(value.Type.Equals(BuiltInTypes.Int));

// Also true, because BuiltInTypes.Int is a singleton — but this is the comparison
// that stops working the moment the field's type is `int[4]` rather than `int`.
Console.WriteLine(ReferenceEquals(value.Type, BuiltInTypes.Int));
```

## See also

- [Compiling a shader](compiling-a-shader.md) — the phase that produces this model, and
  `SemanticModel`, which is how a caller reaches a symbol from a syntax node.
- [The target-independent IR](ir.md) — what this model is lowered *to*, why its type system is
  deliberately smaller, and the opposite identity rule that goes with it.
- [The Raven syntax tree](syntax.md) — the model this one is built *from*, and what a
  `DeclaringSyntax` points back into.
- [Reading a shader's reflection](shader-reflection.md) — the same facts about bindings and entry
  points, packaged for a host that never sees a symbol.
- `Raven/README.md` in the repository — the language these symbols are the meaning of, and ⚠ the
  trap where a newline ends a statement.
