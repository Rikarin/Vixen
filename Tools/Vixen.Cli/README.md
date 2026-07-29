# Vixen.Cli

`vixen` — the command line over a project's assets: import them, pack them, say what is wrong with
them, and serve the result to a device.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md),
[docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 3.

```bash
vixen import                     # import everything that changed
vixen content build              # pack it into bundles and write the catalog
vixen content serve --any        # serve that build to a phone on the same network
vixen doctor                     # say what is wrong, and change nothing
```

A project is a directory with an `Assets/` folder. With no `--project`, the working directory and
then each of its ancestors is tried, the way `git` does — so the tool works from wherever in a
project you happen to be standing.

## Exit codes are a contract

`0` did what was asked, `1` the project was read and something in it is wrong, `2` the command line
was wrong or there is no project where one was expected. A build script needs to tell "I invoked you
wrong" from "the content is wrong", because one of those is a script to fix and the other is an asset
to fix.

## `--format msbuild`

Diagnostics come out as `<absolute path>: error VX1001: <message>` instead of the human column, which
is what MSBuild parses into the IDE's error list and a CI log's summary. The path is absolute because
a relative one is resolved against whatever directory the build is running in, which is not the
project's, so the IDE would open nothing. The codes are registered in
[docs/manual/diagnostic-codes.md](../../docs/manual/diagnostic-codes.md); a code is the contract and
the wording is not.

Information-level lines carry no code and are not dressed as diagnostics, because "this project has
no addressable assets" does not belong in a failure summary.

[`Vixen.Sdk`](../Vixen.Sdk/README.md) passes this flag. Nobody else needs to.

## Where the work happens

⚠ **The pipeline itself is `ContentPipeline`, in
[`Vixen.Editor.Assets`](../../Editor/Vixen.Editor.Assets/README.md).** It moved there when the editor
grew Import and Build commands of its own: two orchestrations over the same components drift, and
this drift would appear as the editor and this tool producing different output for one project. What
lives here is the console — exit codes, `--format msbuild`, the verbose line — and the worker pool,
which is a command-line option rather than something an editor's background task starts unasked.

⚠ **`PublishRunner` went the same way and is now `PlayerBuild`, beside the pipeline.** Doc 20's B7
asks the editor for a Build Settings window "over `Tools/Vixen.Cli`'s existing calls", and a window
can only be over calls that are somewhere it can reach — so the target shapes, the `dotnet publish`
and the launch moved, and `vixen build` and the editor's Build and Run are now literally the same
three calls. The one thing that did not move is `ShaderBuildRunner`: it links Raven's compiler, which
is a build-time library the editor deliberately does not carry, so the ahead-of-time shader bundle
stays a thing this command does and the editor's does not.

## `import`

Scans, then imports every asset whose source, settings, importer version, target or declared
dependencies have moved. **The scan repairs**: a file with no sidecar gets one, an orphaned sidecar is
quarantined, two assets claiming a GUID are separated. That is what opening a project does, and what
somebody running this is asking for.

An importer that throws fails that asset and not the run.

`--isolated` runs importers in worker processes, through
[`Vixen.AssetCompiler`](../Vixen.AssetCompiler/README.md). What that buys is the failure an exception
handler cannot catch: an importer that takes its **process** down — a malformed FBX inside a C++
library — fails that asset instead of the whole command. It costs a process start and a copy of every
artefact over a pipe, and doc 08's parallelism is not there yet, which is why it is off by default.

## `content build`

Imports first — incrementally, so it costs nothing when nothing changed — then plans, packs and
writes. A build that packed a stale artefact because somebody forgot a step is a bug report about the
wrong thing.

`--no-import` exists for exactly one caller: [`Vixen.Sdk`](../Vixen.Sdk/README.md), which runs
`vixen import` as its own MSBuild step so that generated C# precedes the compiler. Importing again
inside one build would repeat a full scan and every decision in the project for nothing. It is not a
flag to reach for otherwise, and the SDK only passes it when its own import step actually ran.

The output directory holds `catalog.bin`, `catalog.bin.hash` and the bundles. **The hash file is
written even though `Vixen.ContentServer` would synthesise one**, because the shipping path is a CDN
and a CDN synthesises nothing; the server's synthesis is what makes a directory copied from anywhere
work. `ContentUpdate` reads the hash before the catalog, since a hash is tiny and a real catalog is
not.

**A rebuild removes the bundles it is replacing, and nothing else.** Bundle file names carry a
content hash, so changed content writes a new name — a directory that accumulated every bundle ever
built is one somebody eventually uploads. Only `*.bundle`, the catalog and its hash are deleted,
because a build directory is also where a person keeps the one-line script that publishes it.

Two builds of the same content are byte-identical, and there is a test that says so.

### The shader bundle

After the content, into the same directory: `shaders.effects`, which is the only effect source a
shipping build has. A variant that is not in it is a miss at run time and an object that does not
draw, because the code that could have compiled one was never linked in.

It is driven by `ProjectSettings/Shaders.effects.json` — a list of shader variants, committed,
reviewed in a diff, merged when two branches each add a material. There is no manifest by default and
that is not a failure: a project runs against a compiler in development, and the build says how to
make one rather than refusing to finish. Write it from `EffectSystem.Requests` after a development
run and the next build compiles exactly what that run asked for.

**Not "compile everything", and the reason is worth knowing.** A pass with `compose` slots does not
compile at all without something in them, so "every variant of `ForwardPlus`" is not a well-formed
question — every variant of `ForwardPlus` *with these features* is, and which features a project has
lives in its materials rather than its shaders.

`--shader-target` picks the Raven backend, `spirv` by default. It is not derived from `--target`,
because the mapping is a device's business rather than a platform's: an Android build may want SPIR-V
for Vulkan or GLSL for GLES, and so may a desktop one.

A variant that will not compile fails the build with everything the compiler said. A variant no
shader answers to is a warning — the usual cause is a manifest older than the material it was
captured from, and failing a build over a line somebody can delete would be the wrong trade.

## `doctor`

**Repairs nothing, on purpose.** `import` scans in the repairing mode; a person asking what is wrong
wants the answer rather than a working tree with edits in it, and a build server asking the same
question wants it more. `ScanOptions.ReadOnly` exists for this and this is its first caller.

Everything it checks is something that otherwise fails later and further away: an asset that is
addressable and was never imported fails at the content build, a catalog naming a bundle that is not
in the directory fails on a device, and a duplicate GUID fails when the wrong texture appears on a
model.

## `content serve`

The same server `Vixen.ContentServer` is, defaulting to this project's build for this target, so a
project has one entry point rather than two things to install. `--any` binds every interface, which
is what a phone on the same network needs and what a laptop in a café does not.

A development server: no TLS, no authentication, no access control.

## Which importers this has

`TextureImporter`, `ModelImporter`, `AudioImporter`, `NativeFormatImporter`, `FolderImporter`, and
`RawImporter` as the fallback — doc 14's `DefaultImporter` under the name doc 08 uses.

**Told, never discovered.** An assembly scan would read metadata a trimmed publish has already
deleted, and would make "which importers imported this project" a question with different answers in
the editor, here, and in a worker process. The list lives in `BuiltInImporters.Create()` and every one
of those three calls it, because a worker whose registry differs from its coordinator's produces
different artefacts for the same file.

## `new`, `build`, `run`

A step-by-step version of this, including the dedicated server, is in
[docs/manual/building-a-game-and-a-server.md](../../docs/manual/building-a-game-and-a-server.md).

```
vixen new game Asteroids     # a project the SDK drives
vixen new app Painter        # Vixen.Ui, a window, and no engine
vixen new lib Physics        # a library either of them can reference
vixen build --target iOS     # content, then dotnet publish
vixen run -- --vixen-frames 5
```

⚠ **`TemplateCatalog` and the scaffold moved to `Vixen.Editor.Core`**, for the reason `PublishRunner`
and `ContentPipeline` did before them: the editor's New Project needs the same scaffold, and an
editor whose new projects have no `.csproj` is one whose Build and Run is greyed for every project it
makes. What is left here is the console — the listing, the exit code, and the two lines saying what
to type next.

**`new` writes the same files `dotnet new vixen-game` writes, because it reads the same files.**
[`Vixen.Templates`](../Vixen.Templates/README.md) owns one tree; the template package ships it and
this assembly embeds it, and `TemplateCatalog` is the fifty lines that apply the one substitution the
templates use. Until that existed the scaffold was C# string literals beside a template pack that did
not exist yet — two copies of every file, waiting to disagree. Which templates `new` offers is
therefore the pack's answer rather than this tool's: `game`, `app` and `lib` today, and the pack's
short names with the `vixen-` prefix taken off in general.

**`new` scaffolds against the SDK rather than against a package list.** A game project is
`<Project Sdk="Vixen.Sdk/x.y.z">` plus one `PackageReference` for the host, and the import-before-compile
and content-build-after-build wiring arrives with the SDK. A template that listed every package the
engine currently needs would be wrong one release later. The version it writes is read from this
assembly, so a scaffolded project asks for the engine matching the tool that made it.

It refuses rather than overwriting, and refuses *entirely*: every collision is found before anything is
written, because a half-scaffolded directory is worse than an untouched one.

**`build` runs the content build and then `dotnet publish`.** That ordering is the reason the command
exists — content is stale unless something rebuilt it, and a publish that copies last week's bundles is
a bug that looks like caching. The variant travels as `-p:VixenVariant`, not as the compiler
configuration, because doc 17's five variants are orthogonal to Debug/Release: Development is optimised
*and* keeps its diagnostics.

**It turns the SDK's own content steps off**, with `VixenImportOnBuild=false` and
`VixenContentBuildOnBuild=false`. They are right for `dotnet build`; here the work has just been done,
and leaving them on repeats a full scan inside the publish — and requires the `vixen` tool on the PATH
of a process this tool started, which is a strange thing for the tool to demand of itself.

**`run` is host-target and Debug by default**, and returns the application's own exit code rather than
translating it: a game that crashes exits 1 by `VixenApplication.Run`'s contract, and flattening that
into this tool's would lose the difference between a failed build and a bad run.

### What they do not do

**Nothing is signed, notarised or bundled beyond what `dotnet publish` emits.** Doc 17's packaging
table ends in notarised DMGs, provisioned IPAs and AABs with per-ABI splits; those are Nuke's job and
need credentials. `--target iOS` produces what the iOS SDK produces and says so.

**A consumer still needs the engine packages.** `vixen build` works against a feed that has them —
verified end to end against a local one — and until they are on nuget.org a scaffolded project needs a
`nuget.config` pointing somewhere they exist.

**`plugin` and `tool` templates are not written.** Doc 17 lists five; `game`, `app` and `lib` are
here. Neither of the two is blocked any more — `plugin` was waiting on `Vixen.Editor.Plugin` and
`tool` on `Vixen.Platform.Headless`, and both of those exist. See
[`Vixen.Templates`](../Vixen.Templates/README.md) § Still to come.

## Still to come

Also owed: `vixen doctor systems` from [doc 04](../../docs/plan/04-ecs-and-scripting.md), which needs
a game assembly to load; and the GPU and driver checks, which need `Vixen.Graphics.Vulkan`'s loader
probe and would put a graphics dependency in a tool that today needs none.

Licensed under Apache-2.0.
