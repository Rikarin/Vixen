---
title: Compiling a shader
slug: raven/compiling-a-shader
kind: guide
area: Raven
summary: The four phases a `.rvn` goes through — parse, bind, lower, generate — the handful of types a host touches to drive them, where each phase's diagnostics come from, and why a permutation value or a compose binding makes a separate compilation rather than a flag.
api: [T:Vixen.Raven.Compilation, T:Vixen.Raven.ParseOptions, T:Vixen.Raven.PermutationValues, T:Vixen.Raven.ComposeBindings, T:Vixen.Raven.RavenReference, T:Vixen.Raven.SemanticModel, T:Vixen.Raven.SymbolInfo, T:Vixen.Raven.TypeInfo, T:Vixen.Raven.Lowering.Lowerer, T:Vixen.Raven.Lowering.LoweringResult, T:Vixen.Raven.CodeGen.TargetBackends, T:Vixen.Raven.CodeGen.ITargetBackend, T:Vixen.Raven.CodeGen.GeneratedSource, T:Vixen.Raven.CodeGen.ShaderStageNames, T:Vixen.Raven.CodeGen.CallGraph, T:Vixen.Raven.Diagnostics.SyntaxDiagnostics, T:Vixen.Raven.Diagnostics.SemanticDiagnostics, T:Vixen.Raven.Diagnostics.LoweringDiagnostics, T:Vixen.Raven.Diagnostics.BackendDiagnostics, T:Vixen.Raven.Cli.RavenCommand, T:Vixen.Raven.Cli.CompileDriver, T:Vixen.Raven.Cli.CompileRequest, T:Vixen.Raven.Cli.ExitCode]
tags: [raven, shaders, compiler, tooling]
since: 0.1
status: preview
related: [raven/shader-reflection, raven/compiled-artefacts, raven/ir]
---

## What it is

Raven is the engine's shader language, and `Vixen.Raven` is its compiler as a library. A `.rvn`
file goes through four phases, and each one is a type you can hold:

| Phase | What it produces | The type |
| --- | --- | --- |
| Parse | A syntax tree, per file | `SyntaxTree.ParseText` |
| Bind | Symbols and semantic diagnostics | `Compilation` |
| Lower | One `IrModule` for the whole compilation | `Lowerer` |
| Generate | One unit per entry point | `ITargetBackend` |

Every phase reports through a `DiagnosticBag`, and each has its own descriptor class —
`SyntaxDiagnostics`, `SemanticDiagnostics`, `LoweringDiagnostics`, `BackendDiagnostics`. The
prefix on an id says which phase refused: `RVN1xxx` is the parser, `RVN2xxx` the binder,
`RVN3xxx` lowering and the IR verifier, `RVN4xxx` a back end.

`Vixen.Raven.Cli` is the same four phases behind a command line. `RavenCommand.Create` builds the
`raven` command, `CompileRequest` is what a `compile` invocation parses into, `CompileDriver.Run`
executes one, and `ExitCode` is what the process returns.

## What it is for

Most of the engine never calls this: shaders are compiled by the content build and loaded as
`.rvnfx`. You reach for the compiler directly when you are

- **building tooling** — an editor that recompiles a graph as it is edited, a bake step, a test
  that asserts what a shader generated;
- **adding a target** — `TargetBackends` is an open table, so a dialect this assembly cannot
  reference registers itself into it;
- **shipping a shader library** — a `.rvnlib` is built from a compilation, and consuming one is a
  `RavenReference` on the next.

## Using it

**A compilation is one variant, not one file.** `PermutationValues` and `ComposeBindings` are
constructor arguments rather than switches because both change what the code *means* — a
permutation key folds into the bodies, and a compose slot decides which implementation a call
reaches. Two variants are two `Compilation` objects.

`ParseOptions.Default` is the only option set today; it exists so that a future dialect switch is a
parameter that already threads through rather than a new one to add everywhere.

**Diagnostics are asked for, never thrown.** `Compilation.GetDiagnostics()` returns parse and bind
diagnostics together; lowering and generation take a `DiagnosticBag` and add to it. Check
`IsError` before using the result of a phase — a phase that reported an error still returns
something, so that one bad declaration produces one message rather than a cascade.

**Ask the semantic model what a piece of syntax meant.** `Compilation.GetSemanticModel(tree)`
returns a `SemanticModel`; `GetSymbolInfo` gives a `SymbolInfo` (what a name bound to, and the
candidates when it did not) and `GetTypeInfo` a `TypeInfo` (the expression's type, and the type it
was converted to). This is the half an editor needs and a batch compile never touches.

**`Lowerer.Lower` for a module, `Lowerer.LowerWithLinks` for an artefact.** The plain overload
returns an `IrModule`, which is deliberately symbol-free — that is what lets a back end be written
against the IR alone. `LowerWithLinks` returns a `LoweringResult`, which is the module *plus* the
map from each member to the function its body became; only `.rvnlib` needs it.

**A back end returns a `GeneratedSource` per entry point.** `Name`, `Stage`, `Code` and, for a
binary target, `Binary`. `IsBinary` says which, and `ShaderStageNames.Suffix` gives the
conventional file suffix for a stage, so two tools do not disagree about whether a fragment stage
is `.frag` or `.fs`.

`CallGraph` is the reachability helper the back ends use: `Calls` for the direct callees of a
statement, `Reachable` for everything an entry point can reach, `InCallOrder` for a definition
order that never forward-references.

## Examples

The whole pipeline, which is what every test in `Vixen.Raven.Tests` does:

```csharp no-compile="a fragment; the source text and the diagnostic bag are the caller's"
var tree = SyntaxTree.ParseText(source, path: "Lambert.rvn");
var compilation = Compilation.Create("Lambert", tree);

var bag = new DiagnosticBag();
var module = Lowerer.Lower(compilation, bag);

var generated = TargetBackends.Create("spirv")!.Generate(module, bag);

foreach (var unit in generated) {
    File.WriteAllBytes($"{unit.Name}.{ShaderStageNames.Suffix(unit.Stage)}.spv", unit.Binary!);
}
```

⚠ `TargetBackends.Names` is a property of the *process*, not of this assembly. `glsl` and `spirv`
are built in; `essl` and the rest arrive from `Vixen.Raven.Transpile`, which pushes them in. A
caller building a menu of targets has to register first, which is why the CLI registers before it
builds its command rather than inside the handler.

One variant, with a permutation and a compose slot bound:

```csharp no-compile="a fragment; the trees and the slot names are the caller's"
var compilation = Compilation.Create(
    "Lit",
    PermutationValues.Parse(["ShadowQuality=2"]),
    ComposeBindings.Parse(["Lit.surface=Standard"]),
    references,
    trees
);
```

Both have a `TryParse` beside the `Parse`, which is what a command line wants: an unparseable
`--define` is a message, not an exception.

Binding against a compiled library:

```csharp no-compile="a fragment; the path is the caller's"
var reference = RavenReference.FromFile("Math.rvnlib");
var compilation = Compilation.Create("Brdf", [reference], trees);
```

## See also

- [Reading a shader's reflection](shader-reflection.md) — what the compiler can tell a host about
  the shader it just produced, which is how a pipeline gets built.
- [Compiled artefacts](compiled-artefacts.md) — `.rvnfx` and `.rvnlib`: what is in each, and which
  one a consumer wants.
- [The target-independent IR](ir.md) — what lowering produces and every back end consumes, and why
  the type system there is deliberately smaller than the symbol model's.
- `Raven/README.md` in the repository — the language itself, the diagnostic ranking heuristic and
  the traps, including ⚠ the one where a newline ends a statement.
