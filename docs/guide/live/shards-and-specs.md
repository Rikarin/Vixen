---
title: Shards, keys and specs
slug: live/shards-and-specs
kind: concept
area: Live
summary: What a shard is, what makes two of them interchangeable, and the one string a realm process boots from.
api: [T:Vixen.Live.ShardId, T:Vixen.Live.RealmInstanceId, T:Vixen.Live.PlayerKey, T:Vixen.Live.ShardKey, T:Vixen.Live.ShardKind, T:Vixen.Live.ShardState, T:Vixen.Live.ShardCapacity, T:Vixen.Live.RealmVersion, T:Vixen.Live.RealmEndpoint, T:Vixen.Live.RealmSpec]
tags: [live, mmo, orchestration, placement]
since: 0.1
status: preview
related: [live/writing-a-realm, live/placing-realms]
---

## What it is

A **shard** is a map being simulated: one process, one scene, a population. `ShardId` names it,
`ShardKey` says which map, region and version it is for, `ShardCapacity` says how full it may get,
and `RealmSpec` is everything a process needs in order to *be* one — encoded as a single string that
crosses the process boundary as one argument or one environment variable.

## What it is for

These types exist so that several processes can agree about a shard without any of them being able to
disagree quietly. Three separations are load-bearing and each of them is a bug the design refuses to
be able to express:

- **`ShardId` is not `RealmInstanceId`.** One shard may be carried by several processes over its life
  — a crash and a replacement, a version rollout — and one process never carries two shards. A lost
  shard is replaced by a *placement*, not a resurrection.
- **`PlayerKey` is not `Vixen.Net.Sessions.PlayerId`.** `PlayerId` numbers a player within one
  session; it survives a dropped connection and not the session. `PlayerKey` is who the database
  thinks they are, on every realm they ever visit. It is an account *and* a character, because two
  characters on one account are two sets of inventory.
- **`RealmVersion` is a build and a content hash, and both filter placement.** A client whose catalog
  hash does not match a shard's is never placed on it — it is placed on one that matches. That is what
  turns a hard rejection into a routing decision, and it is the whole of the incremental-upgrade
  story.

## Using it

An orchestrator decides a shard should exist and writes a spec. The endpoint may be left unbound —
with no port, or with nothing at all — which says *the backend chooses, and tells me*.

```csharp no-compile="the orchestrator that mints one of these is milestone L1"
var spec = new RealmSpec {
    Shard    = ShardId.New(),
    Key      = new ShardKey("maps/queensdale", "eu-west", new RealmVersion("0.1.0", catalogBuildHash)),
    Kind     = ShardKind.Public,
    Capacity = new ShardCapacity(SoftCap: 100, HardCap: 120),
    TickRate = 30
};

var arguments = spec.ToCommandLine();   // --realm-spec shard=…;map=…;region=…;port=0;…
```

On the other side of the boundary, a process asks whether it is a realm at all:

```csharp no-compile="the other half of a process boundary — see RealmApp.Run for the whole of it"
if (!RealmSpec.TryRead(args, environment: null, out var spec, out var why)) {
    Console.Error.WriteLine($"This process is not a realm — {why}.");
    return 2;
}
```

`TryRead` prefers the argument over `VIXEN_REALM_SPEC`, because a launcher that set both meant the
argument: the environment is what a pod template inherits, and inheriting a stale one is the accident
that order prevents.

### The states, and the one that matters

```
Requested → Starting → Ready → Draining → Stopping → Stopped
                 ↓        ↓        ↓
               Failed ← Lost ← (missed heartbeats)
```

**`Ready` is the only state that is a placement candidate.** That single rule is what makes both
elastic scaling and rolling upgrades work with no further mechanism: a shard stops taking arrivals the
instant it starts draining, and one that has not finished loading its map never takes any.

### The four kinds are one mechanism

`ShardKind` distinguishes a public map from a dungeon, a match and a player's house. They differ in
who is admitted and when the shard may stop, and in nothing else — which is what makes instanced
content a placement decision rather than a second server.

## Examples

Capacity is two numbers because it answers two questions. `HardCap` is a filter — a shard at it is not
scored at all — and `SoftCap` is where placement's fill term turns negative. The gap between them is
what a party arriving together fits into.

```csharp no-compile="the scoring function these feed is milestone L1"
var capacity = new ShardCapacity(SoftCap: 100, HardCap: 120);

capacity.Admits(119);      // true
capacity.Admits(120);      // false — never scored
capacity.FillAt(110);      // 1.1, which the placement score penalises steeply
```

A map address is an addressable address, and its leaf is the scene's name — which is what
`NetworkSceneId` hashes:

```csharp no-compile="shows the identity the wire uses; NetworkSceneId lives in Vixen.Net.Engine"
new ShardKey("maps/queensdale", "eu", version).SceneName;   // "queensdale"
```

So a client that has loaded the map already agrees with the realm about what the props are before a
packet arrives.

## See also

- [Writing a realm](writing-a-realm) — the process a spec boots.
- [Placing realms](placing-realms) — what writes one.
- [Transfer tickets](transfer-tickets) — how a player gets in.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § Placement, § Shard kinds.
