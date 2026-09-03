---
title: Network rules as a file
slug: engine/network-rules
kind: guide
area: Networking
summary: Who may do what to a networked object, written down in a .vxnetrules and named by the prefabs it governs.
api: [T:Vixen.Net.Engine.NetworkRulesAsset, T:Vixen.Net.Engine.NetworkRulesReference, T:Vixen.Editor.Assets.Net.NetworkRulesImporter, T:Vixen.Editor.Assets.Net.NetworkRulesImportSettings]
tags: [networking, rules, ownership, authority, assets, prefabs]
since: 0.1
status: preview
related: [engine/networked-prefabs, engine/networked-players, engine/parent-relative-transforms, assets/content-in-a-game]
---

## What it is

A **`NetworkRulesAsset`** is a `.vxnetrules` file: a name, and a `NetworkRules` — who may spawn,
despawn, call, write, and hand on a networked object, and what becomes of it when its owner
disconnects.

A **`NetworkRulesReference`** is the component a designer puts on a prefab node to say which policy
governs it.

**`NetworkRulesImporter`** turns the first into the record a dedicated server deserialises.

## What it is for

`NetworkRules` was always a declaration rather than a switch — that is the whole argument for having
it: a co-operative game and a competitive shooter want different answers to every question in it, and
without a declaration they get them by being different engines. What was missing was somewhere to
write the declaration down. Setting it in C# means the answer lives in a start-up path, next to the
transport, in a file no designer opens; as an asset it lives beside the prefab it is about, and
relaxing server authority becomes a line somebody can read in a diff.

## Using it

A policy is seven fields, and every one of them has a default, so a file only says what it changes:

```yaml
# Assets/Rules/Pickup.vxnetrules — a dropped weapon anybody may take and nobody may steal.
name: Pickup
rules:
  changeOwner: Everyone
  claim: WhenUnowned
  onOwnerDisconnect: TransferToServer
```

`changeOwner` says **who** may ask for a change of owner and `claim` says **when**. They are
genuinely independent, and the pick-up rule is the case that needs both: "any client may take this,
but only if nobody has it" cannot be spelled with an audience alone.

Name it on the node it governs:

```yaml
# Assets/Prefabs/Sword.vxprefab
version: 1
name: Sword
roots:
  - name: Sword
    children:
      - name: Blade
        components:
          - !NetworkObject {}
          - !NetworkRulesReference { asset: Pickup }
```

The reference governs the **node that carries it**, not the whole instance — the same granularity
`NetworkObject` has, and for the same reason: a set piece where one thing is takeable should not make
the other ninety-nine takeable too.

Then load what the build shipped into the registry the server asks:

```csharp compile
using Vixen.Net.Engine;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;

public static class Policies {
    public static NetworkRulesRegistry Load(NetworkOwnership ownership, NetworkRulesAsset[] shipped) {
        var rules = new NetworkRulesRegistry(ownership);

        foreach (var policy in shipped) {
            rules.Load(policy.Name, policy.Rules);
        }

        return rules;
    }
}
```

and hand that registry to the spawner. `NetworkSpawner` resolves each node's reference at the moment
it allocates that node's `NetworkId`, which is the one instant at which the authored name and the
runtime handle both exist.

⚠ **Filling the registry from the content catalog by label — the way
[networked prefabs](engine/networked-prefabs) are filled — is not written yet.** Until it is, a game
loads its policies itself, which is what the snippet above is.

## Why a name and not a handle

A prefab is content, and content cannot hold a reference to something the content build has not
loaded. The name is what survives the build. It is the same decision `WaterZoneComponent.WaveAsset`
makes about a sea state, and it has the same consequence:

⚠ **A name nothing loaded is not an error.** The node falls back to
`NetworkRulesRegistry.Default` — server-authoritative, so nothing unsafe happens — and
`NetworkSpawner.UnresolvedRules` counts it. Check that counter when a game rule does not work: the
symptom of a policy that never arrived is a weapon nobody can pick up, beside a file that reads
exactly right.

## What the importer refuses, and what it only warns about

| | |
|---|---|
| `claim: WhenUnowned` with `changeOwner: ServerOnly` | **error** — the claim decides nothing, because it constrains clients and no client may ask. An author who wrote both lines meant the first to do something |
| `write: Everyone` | **warning** — the one setting that gives up server authority completely. A trusted prototype is a real reason to want it; `Owner` is what a co-operative game usually means |
| a policy with no `name:` | takes the file's own stem, because a prefab refers to it by name and a nameless asset is one nothing can refer to |

## Examples

A competitive shooter, where the server owns everything that matters and a client may ask for
nothing:

```yaml
# Assets/Rules/ServerAuthoritative.vxnetrules
name: ServerAuthoritative
rules:
  changeOwner: ServerOnly
  claim: Never
  onOwnerDisconnect: Destroy
```

A co-operative game's carried object, which the picker-up owns until they put it down or leave:

```yaml
# Assets/Rules/Carryable.vxnetrules
name: Carryable
rules:
  changeOwner: Everyone
  claim: WhenUnowned
  onOwnerDisconnect: ReleaseToUnowned
```

⚠ The two differ in **three** fields rather than one, and that is the point of writing them down:
"server-authoritative" is not a single switch, and a game that set only `changeOwner` would still
hand a disconnected player's rifle to nobody.

## What rules cannot do

**They never grant more than the code asked for.** Where a rule and an attribute have an opinion —
`[ServerRpc(RequireOwnership = true)]` on an object whose rules say `callServerRpc: Everyone` — the
stricter of the two wins. A policy file can narrow what a method declared about itself; it cannot
widen it, because a data file quietly granting more than the code asked for is the thing this design
exists to avoid.

**Three of the seven fields are declared and not yet enforced.** `spawn`, `despawn` and `write` are
answered by `NetworkRulesRegistry` and nothing calls those answers, because nothing can spawn a
networked object from a client or write replicated state from one. When those arrive they ask this
question rather than inventing a second policy.

## See also

- [Networked prefabs](networked-prefabs.md) — how a `NetworkObject` node becomes a spawnable, and
  where the rules reference is resolved.
- [Networked players](networked-players.md) — who a peer *is*, which is what `changeOwner` audiences
  are stated against.
- `Core/Vixen.Net/Rules/NetworkRules.cs` — the seven fields and their defaults.
