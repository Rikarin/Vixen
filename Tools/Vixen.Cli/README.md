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

## `import`

Scans, then imports every asset whose source, settings, importer version, target or declared
dependencies have moved. **The scan repairs**: a file with no sidecar gets one, an orphaned sidecar is
quarantined, two assets claiming a GUID are separated. That is what opening a project does, and what
somebody running this is asking for.

An importer that throws fails that asset and not the run.

## `content build`

Imports first — incrementally, so it costs nothing when nothing changed — then plans, packs and
writes. A build that packed a stale artefact because somebody forgot a step is a bug report about the
wrong thing.

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

Told, never discovered — `TextureImporter`, `FolderImporter`, and `RawImporter` as the fallback. An
assembly scan would read metadata a trimmed publish has already deleted, and would make "which
importers imported this project" a question with different answers in the editor and here.

## Still to come

**`new`, `run` and `build` are absent rather than stubbed.** `new` needs the `Vixen.Sdk` package
layout to scaffold a project against; `build` and `run` wrap `dotnet publish` of a game project,
which is [doc 17](../../docs/plan/17-app-heads-and-shipping.md)'s shipping story and needs the
platform packaging that lands with Android and iOS. A verb that parses and then apologises is worse
than one that is not there, because a build script can only discover the second kind at run time.

Also owed: `vixen doctor systems` from [doc 04](../../docs/plan/04-ecs-and-scripting.md), which needs
a game assembly to load; and the GPU and driver checks, which need `Vixen.Graphics.Vulkan`'s loader
probe and would put a graphics dependency in a tool that today needs none.

Licensed under Apache-2.0.
