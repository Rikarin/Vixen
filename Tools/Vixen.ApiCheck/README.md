# Vixen.ApiCheck

The subject of `nuke CheckApi`. It reads the public surface of a compiled assembly and compares it
with the baseline committed beside the project that produced it, so that adding, changing or
removing public API is a reviewed diff rather than something noticed after a package shipped.

Spec: [docs/plan/12](../../docs/plan/12-build-ci-and-testing.md) § Nuke, and
[docs/plan/00](../../docs/plan/00-vision-and-principles.md) § Non-negotiables — *`internal` by
default; `public` requires a reason and a `PublicAPI.Unshipped.txt` entry.*

```bash
./build.sh CheckApi                 # fails on any difference
./build.sh CheckApi --update-api    # rewrites the baselines, then read the diff
```

The tool itself takes assemblies and finds their baselines by walking up to the project directory:

```bash
dotnet run --project Tools/Vixen.ApiCheck -- Core/Vixen.Core/bin/Release/net10.0/Vixen.Core.dll
```

**The subject is always Release**, whatever `--configuration` says, and this target is the one place
in the build where ignoring it is right. A public surface is a promise about a shipped package,
`Pack` ships Release, and the two configurations genuinely disagree: `LeakTracker.IsSupported` and
`JobScheduler.SafetyChecksEnabled` are `public const bool` feature flags whose values are `#if DEBUG`,
exactly as intended. Baselining Debug would write `IsSupported = true` down as the promise and fail
every CI run; baselining Release and then checking whatever a developer last built would fail on
their machine instead. So the gate has one subject, and `nuke CheckApi` builds it.

⚠ **And `--update` now refuses an assembly that was not built in Release**, which is the half of that
argument the tool itself could not previously make. The gate hard-codes the configuration; the tool
takes a path, and `bin/Debug` is what is lying around — especially for an agent forbidden to run
gates, for whom running this tool directly *is* the documented escape. A `const`'s **value** is part
of the surface, so a regeneration from Debug rewrote `UiDiagnostics.RecordsRegions` from `false` to
`true` and broke `CheckApi` on master twice in one session; both times it was one changed literal
inside a fifty-line diff of additions, which is exactly the edit "read the diff before committing"
does not catch. The configuration is read from the assembly's `AssemblyConfigurationAttribute`, an
assembly that carries none is refused too — *unknown* is not *Release* — and every rewritten baseline
now names the build it came from in the log.

## The two files

Beside every covered `.csproj`:

| File | Holds |
|---|---|
| `PublicAPI.Shipped.txt` | what a released package published. Never rewritten by `--update-api`. |
| `PublicAPI.Unshipped.txt` | everything approved since, plus a `*REMOVED*` line for shipped API that has been taken away. |

At a release the second is folded into the first. Until the first release both are honest as they
stand: `Shipped` is empty everywhere, because nothing has shipped, and writing 22 000 entries into it
would claim a compatibility promise nobody has made. The file format is the one
`Microsoft.CodeAnalysis.PublicApiAnalyzers` uses, deliberately — if the analyzer is ever turned on
for the IDE experience, these files are what it reads.

The gate fails in **both** directions. An entry the assembly has and no baseline approves is an
unapproved addition; an entry a baseline approves and the assembly no longer has is a break, and it
is the one nothing else in the build would notice — a deleted `public` method compiles perfectly.

## What a line looks like

One line per API element, sorted ordinally, so that an addition is one added line and a signature
change is one line removed and one added.

```
Vixen.Core.DisposeBag -> sealed class
Vixen.Core.DisposeBag : System.IAsyncDisposable
Vixen.Core.DisposeBag : System.IDisposable
Vixen.Core.DisposeBag.Add<T>(T disposable) -> T
Vixen.Core.DisposeBag.Count.get -> int
Vixen.Core.SurfaceKind -> enum : byte
static Vixen.Core.GameTime.Zero.get -> Vixen.Core.GameTime
const Vixen.Audio.AudioFormat.MaxChannels = 8 -> int
```

The type's own line carries its kind and modifiers, and its base type and interfaces get a line
each. That is a deliberate addition to the analyzer's format: sealing a class, turning a struct into
a `ref struct`, narrowing an enum's underlying type and dropping an interface are all breaking
changes that add and remove no member at all, and a baseline that cannot see them is one somebody
has to remember to think past.

## What is read, and what is not

The surface is read from the **assembly**, not from source. Source would mean reproducing what the
compiler does with partial types, generators and conditional compilation; the assembly is what a
consumer references and what `Pack` ships.

Public and protected members of publicly visible types are surface. `internal` is not, and neither
is anything `InternalsVisibleTo` reaches — that is a test seam, not a published API.

Left out on purpose, each because the type's own line already says it or because no caller can act
on it:

- **Property and event accessors as methods.** A property contributes `.get`, `.set` or `.init`
  lines instead, because removing a setter and keeping a getter is a break that one line for the
  property could not show.
- **Compiler-written members** — a record's `EqualityContract`, `PrintMembers` and clone helper, and
  the generated equality members. They are consequences of the `record` keyword.
- **A delegate's constructor and its `BeginInvoke`/`EndInvoke`.** `Invoke` is the signature the
  declaration means and the only one a change is visible in.
- **Static constructors and finalizers**, which no caller can invoke.

Two known gaps, written down rather than discovered:

- **Explicit interface implementations** are private in metadata, so they do not appear. They add no
  API a caller could not already reach through the interface.
- **`record struct` reads as `struct`.** A record *class* is identifiable in metadata by its clone
  helper; a record struct carries no such marker, so the keyword cannot be recovered. Its members
  are handled correctly either way.

## Coverage

Every non-test, non-generator, non-analyzer project that packs and targets `net10.0` under six roots,
which is what `ApiCheckedProjects()` in [`build/Build.Api.cs`](../../build/Build.Api.cs) globs:

```
Core/**   Gameplay/**   Platform/**   Live/**   Editor/Vixen.Editor.Plugin   Raven/Vixen.Raven
```

The criterion is packing rather than a profile, because the set whose surface is a promise is the set
somebody can install from nuget.org. ⚠ **That is not the same thing as the `RUNTIME` profile of
`Directory.Build.props`, which this section used to name.** `RUNTIME` is `Core/`, `Gameplay/` and
`Platform/`; the last three roots above are each a named exception argued for in
`ApiCheckedProjects()`'s own remarks — `Live/` ships and is operated, `Vixen.Editor.Plugin` is the one
editor assembly doc 11 asks a compatibility policy of, and `Vixen.Raven` is a compiler useful without
the engine. Naming the profile instead of the globs described two of the six and made the other four
read as uncovered. `ApiCoverageTests.TheReadmeNamesEveryRootCheckApiGlobs` now holds this list to that
call.

The `net10.0-ios`, `-android` and `-browser` projects are not covered. They are outside
`Vixen.slnx` for the reason `CompileMobile` documents, so `Compile` has not built them and there
would be nothing to read.

⚠ `Tools/` is not covered either, and the reason this file used to give — *"build-time tooling that
ships to nobody"* — is false. Seven projects under `Tools/` declare `IsPackable=true`. Three of them
raise no question (`Vixen.Sdk` and `Vixen.Templates` set `IncludeBuildOutput=false`, so the package
has no `lib/` and there is no surface to read; `Vixen.Cli` is a `dotnet tool`, and what a tool
promises is a command line). **Two are libraries a consumer compiles against** — `Vixen.App`, whose
`VixenApp.Run<TGame>` is a game's entry point, and `Vixen.ShaderCompiler` — and they are in exactly
the condition `Vixen.Raven` was in before it was named here. That is #749.

⚠ **The two halves of #749 are not one question, and the templates settle the first of them.**
`Tools/Vixen.Templates` is where this repository tells somebody outside it which packages to
install, and across all six templates that is ten `Vixen.*` ids — `Vixen.App` among them. So it is
not merely referenced by six samples by path: it is a package `dotnet new` puts in a stranger's
csproj, on top of being a published dependency of the covered `Vixen.Live.Realm`. Both routes rule
out un-packing it, and `ApiCoverageTests.EveryPackageATemplateReferencesStillPacks` now fails if a
future answer to #641 or #749 un-packs anything on that list. `Vixen.ShaderCompiler` is on neither
route — no template names it, and no covered package's closure reaches it — so its half stays
downstream of #641, exactly as `build/ApiUncovered.txt` records against the `library-undecided`
token.

## What is skipped, written down

⚠ A glob says nothing about what it does not match, which is the failure mode this gate is otherwise
built to prevent: for an assembly `CheckApi` has never heard of it prints *nothing*, the log reads
`Checking the public surface of 132 assemblies`, and the target succeeds whether the uncovered set
is one project or fifty.

So the skipped set is committed. [`build/ApiUncovered.txt`](../../build/ApiUncovered.txt) names every
project in `Vixen.slnx` that packs and has no baseline — thirty-one of them, twenty-three being the
`Editor/` assemblies of #641 — each with a reason token, and `ApiCoverageTests` holds the list to the
tree in **both** directions: a project that starts packing with nobody checking it fails, and so does
a line for a project that has since been covered, stopped packing, or been deleted. A stale exemption
list is one more instrument reporting success. ⚠ It grew from twenty-nine by itself: the `TOOLING`
profile sets no `IsPackable`, so two projects added under `Editor/` were packing before anybody had
an opinion, and the assertion going red is the only reason it was noticed.

⚠ **Two of those lines are not free to take the other option.** A `ProjectReference` without
`PrivateAssets=all` becomes a `<dependency>` in the `.nuspec`, and `Vixen.Editor.Plugin` — the one
Editor assembly named below — depends on `Vixen.Editor.Ui`, while `Vixen.Live.Realm` depends on
`Tools/Vixen.App`. Un-packing either would leave a published package declaring a dependency that does
not exist, so for those two "cover it or stop packing it" has one answer rather than two, and the
strictest compatibility promise in the tree currently rests on an assembly approved by nobody.
`ApiCoverageTests.APublishedDependencyOfACoveredPackageIsCoveredToo` holds that set at exactly those
two.

Two projects outside those folders are named explicitly, each because it makes a promise the folder
rule would miss.

`Vixen.Editor.Plugin` is not an application, it is the contract a third party compiles against, and
[docs/plan/11](../../docs/plan/11-editor.md) § `Vixen.Editor.Plugin` asks for a stricter
compatibility policy there than anywhere else. A stricter promise nobody diffed is not a promise.

`Vixen.Raven` is a package, not the CLI around it — its `.csproj` says *"the compiler is useful on
its own, without the engine"* and carries a description, tags and a readme to prove it. It was the
only shipped assembly in the tree with no baseline at all: 4 913 entries approved by nothing, in the
assembly with the most churn. Adding it cost 1 162 of those entries first, which is the point of
reading a baseline rather than generating one — the SPIR-V and GLSL emitters, the symbol table's
`Source` and `Metadata` construction, the binder and its bound tree, and the lexer's token kind were
public only because nothing had made them `internal`.

The tests beside it are not covered because they do not pack, and neither does
`Vixen.Raven.Transpile`, which sets `IsPackable=false`. ⚠ `Vixen.Raven.Cli` used to be named in that
same sentence and does not belong in it: it **packs** — `PackAsTool`, `PackageId=Vixen.Raven.Cli`, a
description, tags and a readme. Leaving it out is still right, for the reason
[`build/ApiUncovered.txt`](../../build/ApiUncovered.txt) now writes beside it: what a
`dotnet tool install` promises is a command line and its arguments rather than a type, and a
CLI-surface gate would be a different instrument from this one. A conclusion that is right for a
false reason is the shape that survives review, which is why the reason is what changed here.
