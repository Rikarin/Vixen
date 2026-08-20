# Vixen.Net.Engine.Content

The step that finds a build's networked prefabs: addresses out of a content catalog, `PrefabAsset`s
out of `Vixen.Assets`, and one `NetworkPrefabRegistry` filled.

Spec: [docs/plan/16](../../docs/plan/16-networking.md) § Spawning, and the line
[`Vixen.Net.Engine`'s README](../Vixen.Net.Engine/README.md) owed — *"`Register(address, prefab)` is
called by hand today. It should be filled from the content catalog by label, so 'networked prefab' is
something an asset* is *rather than something a start-up path remembers to say."*

## State

**Built: fill by label, fill by address, a problem list for a group that is too broad. 13 tests.**

| | |
|---|---|
| `NetworkPrefabContent` | `LoadAsync` by label, `LoadFromAsync` by address. |
| `NetworkPrefabLoad` | What went in, and what was labelled a networked prefab and is not. |

## Why this is not in `Vixen.Net.Engine`

`Vixen.Net.Engine.csproj` says what that assembly is: *"The only package that references both, and
the only direction that works."* Both is `Vixen.Net` and `Vixen.Engine`. A third would make every
game that spawns from a template it built in code — a test, a soak, a sample with no content build,
which is every caller of `NetworkPrefabRegistry` in this repository today — link the addressables
runtime for a path it does not take.

So the assembly that needs the asset system is the one that carries it. That is
`Vixen.Gameplay.Content`'s split, and `Vixen.Net.Telemetry`'s before it — *"so an offline game links
no protobuf serializer"*.

## The three things worth knowing before reading the code

### The registry is still built on both peers, and nothing about it is sent

A prefab's id is the hash of its address
([doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)), so a server that registers
`prefabs/crate` and a client that registers the same address agree without a handshake. What this
changes is only **where the list of addresses comes from**: a label is a property of the content, so
both ends read the same list out of the same build and neither maintains it.

What the hand-written call gets wrong is not the ids — it is that the two lists are written twice.
A prefab added to the server's start-up path and forgotten on the client's is a spawn the client
receives, cannot resolve, and drops.

### A bad label is a problem; a bad address is an exception

⚠ **The line between the two is deliberate**, and it is the same one `DefinitionContent` draws. A
`.vxgroup` broad enough to sweep up a texture is a content mistake, so the rest registers and the
problem is named. An address that is not in this build's content catalog at all is the *caller* being
wrong — a typo in a hand-written list — and swallowing it would turn that typo into a prefab that can
silently never be spawned.

A missing bundle, a corrupt chunk, or a prefab compiled by a **newer** build still throws, because
that is not content being wrong, it is the build being broken. The last of those is a
`NotSupportedException` from `PrefabAsset.ToPrefab` and it is deliberately not caught: one prefab from
a newer build means all of them are, and reporting five hundred problems describes a version mismatch
badly.

### The template is held and the asset is not

`Prefab` is a template world — built once, stamped out for the life of the build — so the
`AssetHandle<PrefabAsset>` is released as soon as the template is captured. What a prefab's components
point at (a mesh, a material, a sound) is an `AssetReference` inside the component, loaded on the
ordinary ref-counted handle path by whoever draws it, long after this has run. That is the same
division [`Vixen.Gameplay.Content`](../../Gameplay/Vixen.Gameplay.Content/README.md) makes between a
definition and what a definition points at.

⚠ **Registering twice is a refusal, not an update.** Two templates under one address would make the
same spawn build different things depending on which peer applied it, so `NetworkPrefabRegistry`
refuses the second — which means an address already in the registry is skipped here rather than
re-read. A content update replaces the registry wholesale, the way `DefinitionRegistry.Reload` does.

## Not every prefab, deliberately

`Label` is `networked-prefabs` rather than `prefabs`. A game has far more prefabs than it replicates
and each registered one costs a template world held for the process, so the label says *"this may
arrive over the wire"* rather than *"this is a prefab"*.

## What is owed

- ⚠ **A prefab out of a content build can only ever have one networked node, and the loader cannot
  fix it.** `NetworkPrefabRegistry` decides which nodes get an id by asking whether the template node
  carries `NetworkId` — *"so a designer opts an entity into being addressable by putting the component
  on it"*. A designer cannot: what a compiled scene may name is a component that is `[Component]`
  **and** `[DataContract]`, and `NetworkId` (`Core/Vixen.Net/Replication/NetworkId.cs`) is only the
  first, so `SceneContent.Capture` drops the marker silently. `ANetworkIdMarkerCannotSurviveTheContentBuildYet`
  asserts the A/B: the same three entities captured in code have two ids and through the build have
  one. The fix is not local — `Vixen.Net` runs neither the serialization nor the engine's component
  generator and must not reference `Vixen.Engine` — so it wants either a binder registered from this
  tier or an authorable marker of its own, and that is a decision with an owner.
- **No shipped program fills a registry this way yet**, because no sample has a `.vxprefab` in a
  wired content build: `Samples/08-Multiplayer` and `Samples/09-NetworkSoak` build their arenas in
  code, and the two samples with a content build (`03-PbrShowcase`, `13-ThirdPersonShooter`) are not
  networked. The tests stand up a real in-memory build — importer-shaped chunks, a bundle, a catalog,
  `AssetManager` — so the path is exercised end to end, but a sample that spawns a labelled prefab
  over a wire is the demonstration this still owes.
- **Nothing checks that two peers registered the same set.** The registry's ids agree by
  construction; that both ends *have* the same addresses is `ContentCatalog.BuildHash`'s business and
  the handshake does not yet compare it for this purpose.
