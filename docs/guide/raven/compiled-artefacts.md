---
title: Compiled artefacts
slug: raven/compiled-artefacts
kind: guide
area: Raven
summary: The two things the compiler writes — `.rvnfx`, one shader compiled for one target and one permutation, and `.rvnlib`, a library a later compilation binds and links against — what is in each container, and why both carry a version a reader refuses rather than guesses at.
api: [T:Vixen.Raven.Artefacts.CompiledEffect, T:Vixen.Raven.Artefacts.EffectModule, T:Vixen.Raven.Artefacts.CompiledEffectFormat, T:Vixen.Raven.Artefacts.CompiledEffectReader, T:Vixen.Raven.Artefacts.CompiledEffectWriter, T:Vixen.Raven.Artefacts.CompiledLibrary, T:Vixen.Raven.Artefacts.CompiledLibraryFormat, T:Vixen.Raven.Artefacts.CompiledLibraryReader, T:Vixen.Raven.Artefacts.CompiledLibraryWriter, T:Vixen.Raven.Artefacts.LibraryBuilder, T:Vixen.Raven.Artefacts.LibraryType, T:Vixen.Raven.Artefacts.LibraryField, T:Vixen.Raven.Artefacts.LibraryMethod, T:Vixen.Raven.Artefacts.LibraryProperty, T:Vixen.Raven.Artefacts.LibraryParameter, T:Vixen.Raven.Artefacts.LibraryTypeParameter, T:Vixen.Raven.Artefacts.LibraryTypeReference, T:Vixen.Raven.Artefacts.LibraryTypeKind, T:Vixen.Raven.Artefacts.LibraryValue, T:Vixen.Raven.Diagnostics.LibraryDiagnostics]
tags: [raven, shaders, packaging, content-build]
since: 0.1
status: preview
related: [raven/compiling-a-shader, raven/shader-reflection, raven/library-ir]
---

## What it is

Two containers, for two different jobs.

**`.rvnfx` — a compiled effect.** One shader, compiled for one target, with one set of permutation
values. `CompiledEffect` holds its `Name`, its `Target`, its `Modules` (one `EffectModule` per
stage, carrying the stage's bytes and whether they are binary or text), the `Reflection` a host
builds a pipeline from, the `PermutationKey` this variant was produced under, and a `SourceHash`.
`CompiledEffectWriter` and `CompiledEffectReader` are the two directions;
`CompiledEffectFormat` holds the magic number `RVNFX\x1a` and the version.

**`.rvnlib` — a compiled library.** The declarations a later compilation binds against *and* the
lowered IR its bodies are made of. `CompiledLibrary` holds `Name`, `Types` and `Ir`;
`LibraryBuilder.Build` produces one from a bound and lowered compilation, and
`CompiledLibraryWriter` / `CompiledLibraryReader` / `CompiledLibraryFormat` mirror the effect side.

A library's declaration half is a flat list of `LibraryType`, each with its `Fields`, `Methods`,
`Properties` and `TypeParameters` — `LibraryField`, `LibraryMethod`, `LibraryProperty`,
`LibraryParameter`, `LibraryTypeParameter`. A type reference travels as a `LibraryTypeReference`,
tagged with a `LibraryTypeKind`: a primitive as its special type, a declared type as a qualified
name, and only arrays and tuples carrying their shape, because those have no name to be resolved
by. A constant travels as a `LibraryValue`.

## What it is for

An effect is what ships: the content build compiles shaders once and the runtime loads bytes. A
library is what makes a shader *package* possible — `Brdf.rvnlib` can be referenced by a project
that never sees its source, and a call into it costs nothing at runtime because it is resolved
before the back end runs.

Nested types are recorded flat, with `ContainingType` naming the outer one, because a reader has to
resolve a type reference by qualified name before it knows whether the target is nested.

## Using it

**Both containers are magic, version, length, payload.** The length prefix is not redundant with
"the payload runs to the end of the file": it is what makes a truncation report itself as a
truncation rather than as a parse error a few thousand lines in.

**A reader refuses a version it does not know rather than guessing.** ⚠ That is the whole point of
`CompiledLibraryFormat.Version`, and the history says why: version 3 split a *function*'s identity
from its name, and version 4 did the same for a *struct*. A version-3 reader handed a version-4
artefact would take the struct keys for names, and two libraries that each declared a `struct
Shape` would collapse into one object carrying the first one's fields — which is exactly the bug
the split exists to stop, reintroduced by the reader. So the bump turns a confusing JSON error into
"this library was built by a different compiler".

**What a library exports is what its own compilation declared.** Types and functions linked in from
*another* library are not re-exported: a call into one travels as the key that library published,
and the consumer resolves it against its own references. That is what keeps one struct from having
two identities in a module that references both libraries.

**Some bodies cannot travel, and are refused rather than exported.** A body that reads a shader
binding (`RVN5001`), one that touches a `stream` (`RVN5007`) and one that touches `groupshared`
storage (`RVN5008`) each name storage that belongs to the *consuming* shader. The refusal is
transitive — a function that merely calls such a helper is just as unexportable, because linking it
would drag the helper along. A stage entry point is not something a library supplies either, and
that is said (`RVN5002`) rather than silently dropped.

## Examples

Writing a library, then consuming it:

```csharp no-compile="a fragment; the compilation and the paths are the caller's"
var lowered = Lowerer.LowerWithLinks(compilation, bag);
var library = LibraryBuilder.Build(compilation, lowered, bag);

CompiledLibraryWriter.WriteFile("Math.rvnlib", library);
```

```csharp no-compile="a fragment; the trees are the caller's"
var reference = RavenReference.FromFile("Math.rvnlib");
var consumer = Compilation.Create("Brdf", [reference], trees);
```

Reading an effect back:

```csharp no-compile="a fragment; the path and the stage are the caller's"
var effect = CompiledEffectReader.ReadFile("Lit.rvnfx");
var fragment = effect.ModuleFor(ShaderStage.Fragment);
```

⚠ Both `Read` overloads take bytes and both check the magic number, so a `.rvnfx` handed to
`CompiledLibraryReader` fails as "not a `.rvnlib`" rather than as a JSON error. That is what the
distinct magic numbers are for, and it is asserted rather than assumed.

For inspection there is `CompiledLibraryWriter.WriteJson`, which is the library as indented JSON
with no container around it — readable in a diff, and deliberately *not* accepted by the reader.

## See also

- [Compiling a shader](compiling-a-shader.md) — the phases that produce what these containers hold,
  and `RavenReference`, which is how a `.rvnlib` reaches the next compilation.
- [Reading a shader's reflection](shader-reflection.md) — the `Reflection` an effect carries, and
  what a host does with it.
- [The `.rvnlib` IR](library-ir.md) — the lowered half a library carries beside its declarations,
  and why every cross-reference in it is a key or an index rather than an object.
