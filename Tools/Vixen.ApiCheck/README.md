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

The `RUNTIME` profile of `Directory.Build.props`: every non-test, non-generator project under
`Core/` and `Platform/` that packs — the same set that gets `IsPackable=true`, because the set whose
surface is a promise is the set somebody can install from nuget.org.

The `net10.0-ios`, `-android` and `-browser` projects are not covered. They are outside
`Vixen.slnx` for the reason `CompileMobile` documents, so `Compile` has not built them and there
would be nothing to read. `Editor/`, `Raven/` and `Tools/` are not covered either: they are
applications and build-time tooling. `Vixen.Editor.Plugin` will need covering when it exists —
[docs/plan/11](../../docs/plan/11-editor.md) § Plugins asks for a stricter promise there than
anywhere else.
