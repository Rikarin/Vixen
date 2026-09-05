# Vixen.Sdk

MSBuild integration: `dotnet build` imports the project's assets, builds its content, and puts the
result beside the binary. A user never runs a separate content step.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § `Vixen.Sdk`.

```xml
<Project Sdk="Vixen.Sdk/0.1.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
```

or, for a project that already has an SDK it likes:

```xml
<PackageReference Include="Vixen.Sdk" Version="0.1.0" PrivateAssets="all" />
```

Both forms land on the same `build/Vixen.Sdk.props` and `build/Vixen.Sdk.targets`, so there is one
implementation and no way for the two to drift.

## What it does to a build

| Target | When | What |
|---|---|---|
| `VixenImport` | before `CoreCompile` | `vixen import` for the target |
| `VixenContentBuild` | after `Build` | `vixen content build`, then copies the result to `$(OutDir)Content` |
| `VixenAddContentToPublish` | before the publish list is computed | puts the same files in the publish output |
| `VixenCleanContent` | after `Clean` | removes what it copied, and nothing else |

**Import runs before the compiler** because generated C# — VXML components, shader parameter keys —
has to exist before `CoreCompile` reads the compile items. Nothing generates C# yet; having the
ordering right now is what makes that a scheduling detail later rather than a redesign.

**The content build runs after the assembly exists**, so a compile error is reported before a content
build nobody can use yet, and the content can be copied beside the binary it belongs to.

## What it deliberately does not do: `.vxml` and `.vcss`

⚠ **There is no markup or stylesheet item type in this package, and adding one would break the
projects that work.** A project with an interface in it references `Vixen.Ui` — directly, or through
`Vixen.Ui.Controls`, which is what an application actually writes — and that package ships its own
`buildTransitive/Vixen.Ui.targets`, which declares the `**/*.vxml` and `**/*.vcss` globs and the two
properties the VXML generator asks MSBuild for. `Vixen.Ui.Styling.Utilities`, reached the same way,
brings the utility stylesheet step and the tool that runs it. Declaring the same globs here would
give any project using both an item declared twice, which the SDK refuses by name.

So the on-ramp is already flat, and it is flat with or without this package:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Vixen.Ui.Controls" Version="0.1.0" />
  </ItemGroup>
</Project>
```

A `.vxml` beside that file compiles; a `vixen.ui.vcss` beside it becomes the project's palette; every
class name in the markup and in the C# resolves against it. `Tools/Vixen.Templates/templates/vixen-app`
is that project, and it names `Microsoft.NET.Sdk` rather than this one because an application with no
`Assets/` has nothing to import.

⚠ **This was not always true, and the failure was silent.** Both files used to be packed under
`build/`, which NuGet imports only for a *direct* `PackageReference` — and nobody references
`Vixen.Ui` directly. A project referencing `Vixen.Ui.Controls` therefore loaded the markup generator
(analyzers *do* flow transitively) and handed it nothing: no `AdditionalFiles` item, no error, a build
that succeeded with no interface in it. Found by extracting a produced `.nupkg` and building against
it, which had never been done.

⚠ **Doc 43 § D1 asked for the fold again and the answer is still no, so the answer is written down
here rather than re-derived.** The queued task was *fold `.vxml`, `.vcss` and `vixen.ui.yaml` into
`Vixen.Sdk`*; the third of the three was dropped when `@theme` replaced it, and the other two are the
paragraph above — the globs live in `Vixen.Ui`'s `buildTransitive/`, every project with an interface
in it reaches that package, and a project with both would get `**/*.vxml` declared twice. The two
things D1 wanted carried along with them turn out to be in the right place already: `VixenStyleBase`
and `VixenStyleTokens` are *read* by `Vixen.Ui.Styling.Utilities.targets`, which is itself packed
into `buildTransitive/`, and the `Inputs` lesson — the declaring `.targets` and the project file are
in the input set, because a safelist, a namespace and a token source are arguments rather than files
— is a line in that same file. The only thing genuinely specific to the editor is which sheet is the
palette, and one `Import` in a `.csproj` is the right size for that.

⚠ **Inside this repository there are no packages**, so `Directory.Build.targets` stands in for the
reference: `<VixenUi>true</VixenUi>` in a project's `PropertyGroup` names the two generators, imports
the same two `.targets` the packages import, and orders the build against `Tools/Vixen.StyleGen`.
`Samples/14-Mmo/Mmo.Ui` is the worked example.

**One build imports once.** The content build is passed `--no-import` because `VixenImport` has
already run — on a ten-thousand-asset project the second one would be a full scan and ten thousand
decisions for nothing. The flag follows the same condition as the target: a project that turns the
import step off gets a content build that imports for itself, rather than one that packs a project
nothing imported.

**Clean does not touch `Build/<target>`.** It is where a person keeps the script that publishes it,
and deleting somebody's directory because they typed `dotnet clean` is not a trade this makes.
`vixen content build` already removes the bundles it is replacing.

## Diagnostics are MSBuild diagnostics

The tool is invoked with `--format msbuild`, so what an importer said about an asset arrives as
`<absolute path>: error VX1001: <message>` — an entry in the IDE's error list and a line in a CI log's
summary, rather than prose from a subprocess that scrolled past. The codes are registered in
[docs/manual/diagnostic-codes.md](../../docs/manual/diagnostic-codes.md).

The path is absolute because a relative one is resolved against whatever directory the build happens
to be running in, which is not the project's, so the IDE opens nothing.

**A build-plan diagnostic has no file.** Those messages name the asset inside their text, so a person
can act on them; only the IDE's jump-to-file loses. Fixing it means carrying a path on
`ImportDiagnostic`, which is a change to a type the planner and every importer share, and it is owed
rather than done here.

⚠ **The pre-compile import is passed `--advisory`, and does not put anything in that list.** It runs
`BeforeTargets=CoreCompile`, so on a clean build the game assembly does not exist and a level naming
the game's own components cannot resolve them — not a finding, a consequence of the ordering. Left to
itself the tool emitted `error VX1001` for each, `ContinueOnError` demoted them into the warning
summary, and its own non-zero exit added an `MSB3073` naming this file: three warnings per clean
build that nobody could act on, and among which a real `VX1001` was indistinguishable. That class had
already been misdiagnosed twice.

Under the flag the same lines are prose carrying the same code and the same absolute path, the run
does not fail, and a clean `-c Release` build of `Samples/13-ThirdPersonShooter` reports **0
warnings**. A genuinely broken asset still fails it, as one `error VX1001` from the content build —
which is now the only one in the list.

`ContinueOnError` stays on the same condition, for the failure the flag cannot cover: a tool that
crashed, rather than a project that has not compiled yet.

## Properties

| Property | Default | What it decides |
|---|---|---|
| `VixenProjectDirectory` | the project's directory | where `Assets/` is |
| `VixenTarget` | from `RuntimeIdentifier`, else the host OS | which platform's content to build |
| `VixenContentOutputDirectory` | `$(VixenProjectDirectory)/Build/$(VixenTarget)` | where the build lands |
| `VixenContentFolderName` | `Content` | the folder inside the app's output |
| `VixenImportOnBuild` | `true` | whether to import |
| `VixenContentBuildOnBuild` | `true` | whether to pack |
| `VixenCopyContentToOutput` | `true` | whether to copy the result beside the binary |
| `VixenToolPath` | the copy in this package, else `dotnet vixen` | which CLI to run |

**A `RuntimeIdentifier` decides the target when there is one** — somebody publishing for
`android-arm64` wants Android content — and the machine the build is running on decides it when there
is not, because that is what `dotnet build` on a laptop means.

## The one rule that cost a real build to find

**Anything derived from another property is computed in the `.targets`, never in the `.props`.**

A `.props` is imported before the consuming project's body and a `.targets` after it. A plain default
is safe in the `.props`: `<VixenTarget>Android</VixenTarget>` in a `.csproj` is an unconditional
assignment and overwrites whatever was defaulted. What is not safe is a property computed *from* one,
because that computation has already happened by the time the body runs and nothing recomputes it.

`VixenToolCommand` is the one that proves it — derived from `VixenToolPath`, so a consumer setting
`VixenToolPath` in its own body against a `.props`-derived command gets `dotnet vixen` and a
tool-not-found failure. It reads perfectly on the page and fails on the first real build.

Worth recording twice, because the first sabotage written to verify it was wrong: moving the
`VixenTarget` derivation into the `.props` changes nothing at all, for the reason in the paragraph
above. Moving `VixenToolCommand` fails six of this package's seven tests.

## The CLI ships inside this package

`tools/vixen.dll` is in the `.nupkg`, so a consumer who has restored this package has the tool the
targets run and installs nothing. That is the second rung of the ladder above, and it was written
before anything put a file there — until then every consumer fell through to `dotnet vixen`, and doc
08's "restores the Vixen tool versions matching the referenced packages" was not met.

**One package rather than two, and that is forced rather than preferred.** `<Project
Sdk="Vixen.Sdk/x.y.z">` is resolved by the MSBuild SDK resolver, which downloads exactly this package
and never walks its dependency graph — so a `Vixen.Cli.Tools` package next door would be restored for
the `PackageReference` form and silently absent for the `Sdk` form, which is the form the templates
scaffold. Being one file is also what makes the version unable to be wrong.

**One portable copy rather than three RID-specific ones.** A framework-dependent build carries every
runtime identifier's natives under `runtimes/` and chooses one at run time from its own `.deps.json`,
so the same package serves win-x64, linux-x64 and osx-arm64 — and linux-musl-x64, which is what a CI
container usually is and what a hand-maintained list of "the three desktop RIDs" would have left with
no tool at all.

⚠ **What it costs is size: ~170 MB unpacked, ~69 MB on the wire, downloaded by every consumer on
every one of the three desktop RIDs, of which the RID actually running uses about 30 MB.** Assimp
across five RIDs and HarfBuzz across twenty are most of it. `.pdb` and `.xml` are excluded, which is
another 82 MB that would otherwise have travelled. The way out is a smaller tool rather than a
cleverer package: the SDK calls `vixen import` and `vixen content build` and nothing else, while what
is packed is the whole CLI — `live`, `content serve`, Roslyn workspaces and the text stack included.

⚠ **One exception is taken, and it is the only lever that changes neither the reference graph nor the
RID coverage.** `Ultz.Native.Assimp` 6.0.2 ships **two majors** of the native in every Linux and macOS
RID — `libassimp.so.5` beside `libassimp.so.6`, `libassimp.5.dylib` beside `libassimp.6.dylib` — and
opens one of them. Windows ships a single unversioned `Assimp64.dll` and has no pair. The 6 is not
packed: **44 251 540 bytes** across the five RIDs that carry a pair, about a quarter of the unpacked
package.

⚠ **Which major is the dead one was filed the wrong way round, and the cost of getting it wrong is a
package that installs and then throws.** [#624](https://github.com/Rikarin/Vixen/issues/624) names the
**5** as the copy nothing loads; the binding's own `AssimpLibraryNameContainer` says otherwise —
`libassimp.so.5` on Linux and Android, `libassimp.5.dylib` on macOS, the unversioned Windows names,
and `__Internal` on iOS. `Silk.NET.Assimp` 2.23.0 binds Assimp 5's C ABI, which `ci.yml` already said
in a comment when it installs Ubuntu's `libassimp5` and warns that a 6 there "would load and then be
wrong in ways a signature cannot catch". Dropping the 5 would produce a package that restores,
installs, runs, and throws `FileNotFoundException` out of `Assimp.GetApi()` the first time anybody
imported a model — a failure no pack test that only checked the 6 was absent could have caught.

So the exclusion is held to the binding rather than to this paragraph.
`Vixen.Editor.Assets.Tests.AssimpSonameTests` reads the names the binding asks for and fails if
`Vixen.Sdk.csproj` excludes any of them, and `PackagedToolTests.OnlyTheAssimpSonameTheBindingOpensIsPacked`
asserts both halves against a real `.nupkg`. The day the binding moves to Assimp 6, the first goes red
and names the soname to keep.

⚠ **The apphosts are not packed.** `vixen`, `vixen-content-server` and `Vixen.AssetCompiler` sit in
the build output beside the assemblies with no extension: they are native launchers built for
whichever machine ran the build. Everything here is started as `dotnet "….dll"` — the targets, and
`CompilerPool` for its import workers — so nothing needs them, and packing them would put a Mach-O
binary called `vixen` into the package a Windows consumer restores.

⚠ **Packed by path, so packing has to follow a solution build.** `dotnet pack` on this project alone
produces a package with an empty `tools/`, and the targets then fall through to `dotnet vixen`
exactly as they did before. `Build.CheckCliIsShippable` extracts the produced package and *starts*
the tool, which is a stronger question than the file list its `Vixen.Ui.Styling.Utilities` sibling
asks and is available here only because `vixen --version` needs no project to point it at.

## Still to come

**Nothing generates C# yet**, so the `BeforeTargets="CoreCompile"` hook is ordering without cargo.
VXML and shader generators arrive in Phases 4d and 5.

**Platform packaging** — bundles into an APK's assets, an iOS bundle, `wwwroot` — waits for those
platforms.

Licensed under Apache-2.0.
