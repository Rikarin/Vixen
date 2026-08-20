---
title: Networked prefabs
slug: engine/networked-prefabs
kind: guide
area: Networking
summary: What may be spawned, on both ends of the wire — and filling that list from the content build rather than from a start-up path.
api: [T:Vixen.Net.Engine.NetworkPrefab, T:Vixen.Net.Engine.NetworkPrefabRegistry, T:Vixen.Net.Engine.Content.NetworkPrefabContent, T:Vixen.Net.Engine.Content.NetworkPrefabLoad]
tags: [networking, prefabs, spawning, addressables, content]
since: 0.1
status: preview
related: [engine/networked-players, gameplay/definitions, assets/content-in-a-game]
---

## What it is

A **`NetworkPrefabRegistry`** is what may be spawned, on both ends of the wire: an address, the id
the wire uses for it, the template, and which of its nodes get a `NetworkId`. A **`NetworkPrefab`** is
one entry.

**`NetworkPrefabContent`** fills a registry out of a build's content catalog, by label, and gives
back a **`NetworkPrefabLoad`** — what went in, and anything that was labelled a networked prefab and
turned out not to be one.

## What it is for

A spawn on the wire is twelve bytes: a prefab id, a scene id and an owner. The prefab id is the hash
of the addressable's address (`docs/plan/08-asset-pipeline-and-addressables.md`), so a
server that registers `prefabs/crate` and a client that registers the same address agree on the
number without a handshake — nothing about the registry is ever sent.

What still had to agree was the **list**, and until this existed it agreed by being typed twice.
`Register(address, prefab)` is a call in a start-up path, so a prefab added on the server and
forgotten on the client is a spawn the client receives, cannot resolve, and drops — with the bug
sitting in two files that look nothing like each other. A label is a property of the content, so both
ends read the same list out of the same build and neither maintains it.

## Using it

Put the label on the group your networked prefabs live in — `networked-prefabs`, which is
`NetworkPrefabContent.Label`:

```yaml
# Assets/Prefabs/Networked.vxgroup
name: NetworkedPrefabs
loadPath: Local
packing: PackTogether
labels:
  - networked-prefabs
```

Then fill the registry once, at boot, on every peer:

```csharp compile
using System;
using System.Threading.Tasks;
using Vixen.Assets;
using Vixen.Net.Engine;
using Vixen.Net.Engine.Content;
using Vixen.Net.Replication;

public static class Boot {
    public static async Task<NetworkSpawner> SpawnerAsync(AssetManager assets, NetworkIdAllocator ids) {
        var prefabs = new NetworkPrefabRegistry();
        var load = await NetworkPrefabContent.LoadAsync(prefabs, assets);

        if (load.Problems.Length > 0) {
            // A .vxgroup broad enough to sweep up a texture. Refuse rather than log: a peer that
            // starts with half a prefab table hands out spawns nobody can build.
            throw new InvalidOperationException(string.Join(Environment.NewLine, load.Problems));
        }

        return new(prefabs, ids);
    }
}
```

Several groups fill one registry — `LoadAsync(prefabs, assets, ["creatures", "vehicles"])` — and a
game whose networked prefabs are a list in code rather than a group uses
`LoadFromAsync(prefabs, assets, addresses)` instead.

Whatever is already in the registry stays, so a game may mix a build's prefabs with templates it
built itself. Loading the same build twice registers once.

### A bad label is a problem; a bad address is an exception

The distinction is deliberate, and it is the same one [definitions](gameplay/definitions) draws. A
group broad enough to sweep up a texture is a **content** mistake: the rest registers and the problem
is named, in address order. An address that is not in the build's catalog at all is the **caller**
being wrong — a typo in a hand-written list — and it throws `AddressNotFoundException`, because
swallowing it would turn that typo into a prefab that can silently never be spawned.

A missing bundle, a corrupt chunk, or content from a newer build than the binary still throws. That
is not content being wrong; it is the build being broken.

### The template is held and the asset is not

A `Prefab` is a template world, captured once and stamped out for the life of the build, so the
`AssetHandle<PrefabAsset>` is released as soon as the template exists. What a prefab's components
point at — a mesh, a material, a sound — is an `AssetReference` inside the component and is loaded on
the ordinary ref-counted handle path by whoever draws it.

⚠ **A registered template is a world held for the process.** That is why the label says *"this may
arrive over the wire"* rather than *"this is a prefab"*: a game has far more prefabs than it
replicates, and there is no reason to pay for the rest.

## Examples

Only the nodes that asked for an id get one — a hundred-entity set piece where one turret rotates
costs one id and one record:

```csharp no-compile="a fragment; `prefabs` is the caller's registry"
var entry = prefabs.Require("prefabs/turret");

// The root, plus every template node carrying a NetworkId, in capture order.
Console.WriteLine($"{entry.Prefab.EntityCount} entities, {entry.IdCount} ids");
```

⚠ **Through a content build that number is 1 today, whatever the author marked.** A compiled scene
may only name a component that is `[Component]` **and** `[DataContract]`; `NetworkId` is only the
first, so the marker does not survive compilation and is dropped without a word. A prefab captured
from a live world keeps it. `ANetworkIdMarkerCannotSurviveTheContentBuildYet` in
`Vixen.Net.Engine.Content.Tests` asserts both halves so the gap cannot close by accident or widen
unnoticed, and
`Core/Vixen.Net.Engine.Content/README.md` § What is
owed carries the argument about how to fix it.

Two addresses that hash alike are refused where both names are still in hand:

```csharp no-compile="a fragment; the two templates are the caller's"
// Throws, naming both: two prefabs the wire could not tell apart.
prefabs.Register("prefabs/crate", crate);
prefabs.Register("prefabs/kratee", other);
```

Through `NetworkPrefabContent` that refusal comes back as a problem rather than an exception, because
it is a property of the content rather than of the call.

## See also

- [networked players](engine/networked-players) — the other half of spawning: a connection getting a body.
- [definitions](gameplay/definitions) — the same label-driven load, one layer down.
- [content in a game](assets/content-in-a-game) — catalogs, labels and addresses.
- `Core/Vixen.Net.Engine/README.md` — spawning as a replicated component, and the rest of the wire.
- `Core/Vixen.Net.Engine.Content/README.md` — why the loader is an assembly of its own, and what it still owes.
