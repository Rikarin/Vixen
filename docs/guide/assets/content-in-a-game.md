---
title: Getting content into a running game
slug: assets/content-in-a-game
kind: guide
area: Assets
summary: What a build has to know, what it ships, and the two shapes a shipped chunk comes in.
api: [T:Vixen.Editor.Assets.Compositors.CompositorImporter, T:Vixen.Editor.Assets.Compositors.CompositorImportSettings, T:Vixen.Cli.GameAssemblies, T:Vixen.Assets.RawPayload, T:Vixen.Assets.LooseContentSource, T:Vixen.Editor.Assets.Gameplay.AddressConstants, T:Vixen.Editor.Assets.Gameplay.AddressConstantsResult, T:Vixen.Cli.AddressRunner]
tags: [assets, content, importers, build]
since: 0.1
status: stable
related: [engine/players-and-possession, engine/booting-an-application]
---

## What it is

The three pieces that decide whether a file under `Assets/` becomes something a game can load:

| Piece | Answers |
|---|---|
| `GameAssemblies` | Which types the build knows about — including the ones the game itself declares |
| `CompositorImporter` | How a `.vxcompositor` becomes the chunk a host reads, rather than bytes |
| `RawPayload` | What a dependency turns out to be when it was never a serialized object at all |

## What it is for

Shipping a project whose levels name the project's own components, whose frame is its own, and whose
models carry blobs no serializer should ever see. All three of those are ordinary, and each used to
fail in a way that produced a running game and a wrong picture rather than an error.

You do not need any of this for a game that loads only textures and meshes by address. It is what
the awkward cases need, and the awkward cases are where a content pipeline is judged.

## Using it

**A game's own scene components reach the build through its assembly.** `vixen import` and
`vixen content build` both take `--assembly`, and `Vixen.Sdk` passes the project's own:

```bash
dotnet vixen content build --assembly bin/Debug/net10.0/MyGame.dll
```

⚠ **Loading is not enough; the module initializer has to be run.** A `[Component]` beside a
`[DataContract]` reaches `SceneComponentRegistry` through a `[ModuleInitializer]` the generator wrote,
and the CLR runs one lazily — at the first access to a type in that module. An assembly loaded and
never otherwise touched has registered nothing. `GameAssemblies.Load` runs the module constructor
explicitly, then walks the assembly's `Vixen` references and does the same for each.

That walk is not thoroughness for its own sake. `MeshRenderable` lives in `Vixen.Rendering`, which the
build tool references and may never touch, so whether a level naming one compiled came down to which
importers happened to run — a full build worked and an incremental one did not.

⚠ **The import that runs before the compiler cannot see the assembly, because the compiler has not
produced it yet.** `Vixen.Sdk` therefore treats that pass as advisory and lets the content build after
`Build` be the authority: it has the assembly, it re-imports incrementally, and it is the one that
fails a build.

**A format with a compiler of its own gets an importer of its own.** `CompositorImporter` reads the
YAML, checks the version, and writes the binary chunk a host loads:

```csharp compile
using Vixen.App;

public sealed class MyGame : Game {
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        // The address is the project-relative path, extension and all.
        config.Graphics.Compositor = "Assets/Frame.vxcompositor";

        // Node kinds the builder cannot know itself. Also what first touches that assembly, which is
        // what registers the aliases the document names.
        config.Graphics.Factories.Add(new Vixen.Rendering.PostFx.PostEffectFactory());
    }
}
```

⚠ **`Factories` belongs in `OnConfigure` and nowhere else.** The compositor is built inside
`AppGraphics`' constructor, which runs before `OnInitialise` — a factory added later is added to a
frame that has already been built and has already thrown.

## Loose content, and why an editor reads it

A content build packs bundles. An import does not: it leaves each asset's chunk in the project's
artefact store and a catalog beside it, and `LooseContentSource.Open` is what turns that pair into an
`AssetManager`.

```csharp no-compile="a fragment; the directory is already mounted"
var assets = LooseContentSource.Open(files, root, out var refusal);
```

⚠ **The same addresses a shipped build resolves.** The catalog is written by the same planner from
the same sidecars; what differs is that an entry names no bundle and the bytes stay where the import
put them. A host that read content through a path of its own would agree with the game by
coincidence, which is the property that makes testing against an editor worth anything.

⚠ **The artefact store is mounted up front where a bundle is mounted on demand.** An entry that names
no bundle asks the asset manager for nothing, so a store opened lazily would be opened never.

⚠ **A `VirtualPath` that is already mounted, not a directory on the host.** Engine code addresses
files through the virtual file system and the architecture analyzer enforces it — turning a physical
directory into a mount is a head's job, and that division is why this lives here rather than inside
one host.

⚠ **A refusal rather than an exception for a project that has never been imported.** It is the state
every new project is in, and a host that threw could not open one.

## Examples

**Reading a chunk that is not an object.** A model's cluster hierarchy and page blob are byte spans
`VirtualGeometrySystem` reads directly; they carry no `[DataContract]` because nothing should hand
them to a serializer:

```csharp compile
using Vixen.Assets;

public static class Pages {
    public static async Task<byte[]> Read(AssetManager assets, string address) {
        ArgumentNullException.ThrowIfNull(assets);

        await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
```

Those are still dependencies, so they are still in an address's closure and their bundles are still
mounted. What the closure walk does *not* do is preload them into objects — it records a
`RawPayload` instead. Failing there made every model with a distance field unloadable by anything
that referenced it, which is to say by every scene in the project.

**Naming an artefact's type.** An importer's type string is the `[DataContract]` alias, not a
friendly name for it:

```csharp no-compile="an importer's own call, shown out of the ImportAsync that surrounds it"
context.Write(context.DeclareSubAsset("Mesh", mesh.Name), "MeshData", Serializer.ToBytes(mesh));
```

⚠ `ImportPipeline.TypeIdOf` resolves it through the type registry and falls back to
`ImportedArtifact` — an editor type — when it does not resolve. Writing `"Mesh"` there stamped every
mesh chunk in every content build with a type no game process has ever heard of, and the symptom was
"nothing registered in this process claims it" at load, about content the build had just declared
good.

**Two sub-assets may not share a name.** A sub-asset's address is built from its name alone, so a
mesh and its distance field both called `Crate` claim one address, `BuildPlanner` refuses the
collision, and the model ends up with no address at all — which fails every scene that references it.
`ModelImporter.ClusterName`, `PageName` and `FieldName` are why the others do not collide.

## See also

- [Players and possession](engine/players-and-possession) — what loads a level and puts somebody in
  it.

The design record is `docs/plan/08-asset-pipeline-and-addressables.md`. `Samples/13-ThirdPersonShooter`
is the project that found every failure named on this page.
