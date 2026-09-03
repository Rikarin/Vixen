# Vixen.Ui.Markup.Generators

The build half of the markup channel. `Vixen.Ui.Markup` is the VXML compiler — lexer, parser,
binder, emitter — and this is what runs it: an `IIncrementalGenerator` over the `.vxml` files a
project contains.

Editing a file therefore changes a method body in the compilation, which is what `dotnet watch`
turns into a metadata update and what `Vixen.Ui.HotReload` has been waiting for on the other side.
Until this existed the markup channel was testable and not useful on a file save.

## Drop a file in

```
Assets/Ui/Widgets/Counter.vxml   →   Demo.Ui.Widgets.Counter
```

No item in the `.csproj`. A `PackageReference` to `Vixen.Ui` brings `build/Vixen.Ui.targets`, which
globs `**/*.vxml` into `AdditionalFiles` and makes two MSBuild properties visible to the compiler;
the analyzer travels in the same package. Set `EnableVixenMarkup=false` to turn the glob off.

**The namespace is the root namespace plus the file's own folders** — the convention a hand-written
`.cs` file in the same directory already follows. It comes from the build, so nothing has to be
written down in the ordinary case.

⚠ **And `@namespace` overrides it**, because the ordinary case is not every case: a component whose
folder is not what its namespace should be has no other way to say so, and renaming the folder is
not a fix a library can rely on. The file wins over the build.

## Why the front end is compiled twice

A generator runs inside the compiler, so it targets `netstandard2.1`. `Vixen.Core.Syntax` and
`Vixen.Ui.Markup` are `net10.0`, and a `net10.0` assembly cannot be referenced from here. The source
files are linked into this project instead. Three routes were considered:

| | Compile | Load |
|---|---|---|
| Multi-target the two projects | ✅ | ❌ |
| ILMerge the dependencies in | ✅ | ✅, by hiding it, and one more tool in the build |
| **Link the sources** | ✅ | ✅ |

**Multi-targeting fixes the compile and leaves the load.** An analyzer's `ProjectReference`
dependencies do not travel to the analyzer path — `OutputItemType="Analyzer"` contributes one DLL —
so both assemblies would still have to be put there by hand-written MSBuild, and mis-versioned
against the `net10.0` copies in every project that references `Vixen.Ui.Markup` as well.

Two things make linking cheap here rather than merely tolerable. Neither project touches the file
system, the environment or the console — **checked, not assumed** — so RS1035 has nothing to say
about code that was never written to run inside a compiler. And `Vixen.Ui.Markup` reaches
`Vixen.Core.Syntax`'s internal green tree through `InternalsVisibleTo`; compiled into one assembly
that stops being a question.

The cost is a second language surface, and `Compat/Netstandard.cs` is where it is paid: `init`
accessors need `IsExternalInit`, and 116 guard clauses call throw helpers that .NET 6 and .NET 8 put
*on* framework exception types, where no extension method can reach them. The simple names are
aliased compilation-wide onto subclasses that carry the helpers. ⚠ That is the only global using
alias shadowing a framework type in the repository, it is confined to this project, and the runtime
assemblies keep the idiomatic form.

**The other cost is real and was paid immediately**: the analyzer rule set found
`CultureInfo.CurrentCulture` formatting every diagnostic message in `Vixen.Core.Syntax`, which makes
one machine's compiler output differ from another's. Fixed there rather than suppressed here.

## What is incremental about it

Every step is keyed on values — paths, contents, namespaces. Editing a C# file re-runs the cheap
head and never reaches the parser; editing one `.vxml` re-compiles that file alone.

⚠ **The pipeline never calls `Collect()`.** Batching every file into one array would make every edit
invalidate every output, which is the usual way a generator ends up incremental in name only — and
the failure is silent, because the output is still correct.

⚠ **`ImmutableArray<T>` compares by reference.** A model carrying one never equals the model the
previous compilation produced, so `EquatableArray<T>` exists. What it buys is narrow and worth
naming: a file re-compiled to the *same* result is reported `Unchanged` rather than `Modified`, so
its source is not re-added and its diagnostics are not re-reported.

## What comes out when the file is wrong

**Syntax errors stop the emit; binding errors do not.** A `VXML1xxx` means the tree came out of
error recovery and is a guess, and C# emitted from a guess may not parse — which buries the one
diagnostic the author needs under a page about generated code they cannot see. A `VXML2xxx` means
the tree is right and its meaning is wrong, so the class is still emitted: the type keeps existing,
and the error count stays at the one real cause instead of one per use site across the project.

Either way the diagnostic lands on the markup:

```
Ui/Broken.vxml(3,6): error VXML1002: '<span>' is never closed.
```

⚠ **Getting there found a real bug in the parser.** Both `VXML1002` and `VXML1003` read their span
off a node still under construction — a node with no parent, whose position is relative to itself —
so every unclosed element was reported a few characters into the file whichever one it was about.
Invisible to the parser's own tests, which assert *which* diagnostics were reported and never where.

## Hint names

`Ui/Widgets/Counter.vxml` becomes `Ui_Widgets_Counter.g.cs`. ⚠ **The encoding is injective and it
has to be**: Roslyn throws when two generated files share a hint name, and naming the file after the
component collides between two folders — with a message about neither of them. Underscores double
before separators fold into single ones, so `Ui/Counter.vxml` and `Ui_Counter.vxml` cannot meet.

The path is relative to the project directory, because a hint name is part of the compilation and an
absolute one would make two machines' builds differ.

## Tests

19, in two kinds.

Most drive a `CSharpGeneratorDriver` directly: what is generated, where diagnostics land, what the
tracked steps' reasons were on the second run, and one that emits the assembly, loads it and drives
the component with a signal.

Two run a real `dotnet build` of a real project, for the same reason `Vixen.Sdk.Tests` does:
**there is no way to test MSBuild integration except by running MSBuild.** A glob in a `.targets`
and two `CompilerVisibleProperty` items do not exist until a build engine reads them, and the
namespace assertion covers all of it at once — it can only be right if the glob found the file and
both properties came through.

**Verified by sabotage**: folding the hint name's underscores fails 1, emitting from a recovered
tree fails 1, withholding the class on any error fails 1, keeping the absolute path fails 4,
dropping the namespace fails 4, taking the diagnostic span off a detached node fails 2 here and 1 in
`Vixen.Ui.Markup.Tests`, treating every additional file as markup fails 1, and dropping the
diagnostic message's arguments fails 1.

⚠ **Three sabotages failed to fail.** Two were test gaps and are closed: nothing reached the
namespace's leading-digit guard, because every fixture used folders that were already identifiers;
and nothing reached `EquatableArray`'s equality at all, which takes an edit that re-runs the compile
step and then agrees with itself — plus its mirror, an error that becomes a *different* error, since
an equality that always says "same" passes the first and leaves a corrected file showing its old
message.

The third was a false claim in a comment. Passing the formatted message as a composite format string
was written up as a `FormatException` surfacing as CS8785; **it is not.** Roslyn catches it and falls
back to the unformatted template, which here is the finished message, so the brace arrives intact and
nothing crashes. The `{0}` indirection is kept — the fallback silently discards arguments and has no
contract — and is now labelled as insurance rather than as a covered claim.

## Owed

~~A `vixen` CLI path for the same compile, for a build that wants the generated C# on disk.~~
**Refused, and it had been refused elsewhere the whole time this bullet asked for it.**

Three documents disagreed. `docs/overview.md` § 1.7 marks the CLI-emit row ✅ and says in the same
breath that it **will never carry the VXML components or the shader parameter keys**, on ADR-002
grounds; `docs/plan/08-asset-pipeline-and-addressables.md` § `Vixen.Sdk` gives the long form, which
`ShaderBindingsGenerator`'s header repeats — a build task writing `.cs` into `obj/` is right
eventually and wrong on the first build after the input changes, because the compile it feeds has
already read the previous answer. This bullet was the only one of the three asking for the verb, and
it named no purpose.

⚠ **The purpose it would have named is already served, and not by a CLI.** ADR-002's motive for
generated C# on disk is that it be steppable, and `Directory.Build.props` sets
`EmitCompilerGeneratedFiles=true` for every project in the tree — so the output of this generator is
on disk after an ordinary `dotnet build`, under `obj/`, with the `#line` spans that make it step back
into the `.vxml`. A `vixen markup` verb would add a *second* producer of the same type from the same
input, which is the one thing a single source of truth cannot survive.

The pieces are all public, and `Vixen.Ui.Markup.Tests` drives them standalone —
`SyntaxTree.ParseText` → `Binder.Bind` → `ComponentEmitter.Emit`. So the verb is a small thing to
write on the day something needs it. What is missing is not the code; it is the need.

Licensed under Apache-2.0.
