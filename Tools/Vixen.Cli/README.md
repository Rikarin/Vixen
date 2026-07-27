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

```
vixen new game Asteroids     # a project the SDK drives
vixen build --target iOS     # content, then dotnet publish
vixen run -- --vixen-frames 5
```

**`new` scaffolds against the SDK rather than against a package list.** A game project is
`<Project Sdk="Vixen.Sdk/x.y.z">` plus one `PackageReference` for the host, and the import-before-compile
and content-build-after-build wiring arrives with the SDK. A template that listed every package the
engine currently needs would be wrong one release later. The SDK version it writes is read from this
assembly, so a scaffolded project asks for the SDK matching the tool that made it.

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

**`app`, `plugin` and `tool` templates are not written.** Doc 17 lists five; `game` and `library` are
here. `app` in particular is the practical test that `Vixen.Ui` does not depend on `Vixen.Engine`, and
it should be written when `Vixen.Ui` is far enough along to be worth scaffolding against.

## Still to come

Also owed: `vixen doctor systems` from [doc 04](../../docs/plan/04-ecs-and-scripting.md), which needs
a game assembly to load; and the GPU and driver checks, which need `Vixen.Graphics.Vulkan`'s loader
probe and would put a graphics dependency in a tool that today needs none.

Licensed under Apache-2.0.
