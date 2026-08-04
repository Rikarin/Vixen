---
title: Writing a realm
slug: live/writing-a-realm
kind: tutorial
area: Live
summary: A dedicated server that is a shard — the one-line entry point, the four hooks it seals, and the map it opens.
api: [T:Vixen.Live.Realms.Realm, T:Vixen.Live.Realms.RealmApp, T:Vixen.Live.Realms.RealmHost, T:Vixen.Live.Realms.RealmHostOptions, T:Vixen.Live.Realms.MapLifetime, T:Vixen.Live.Realms.MapState]
tags: [live, mmo, realm, dedicated-server]
since: 0.1
status: preview
related: [live/shards-and-specs, live/admission-and-health]
---

## What it is

A realm is a normal Vixen application built as a dedicated server, plus the pieces that make it a
shard. `Realm` is the `Game` you derive from, `RealmApp.Run` is the entry point, and `RealmHost` is
the state machine underneath: `Starting → Ready → Draining → Stopping → Stopped`.

## What it is for

Everything a game does on a listen server it does here, unchanged — replication, RPC, interest,
prediction's server half. What the realm adds is a lifecycle somebody else can operate: it is told
what shard it is, it says when it is ready, it admits only ticketed players, it reports what it is
costing, and it can be asked to drain.

## Using it

```csharp compile
using Vixen.Live;
using Vixen.Live.Realms;

public sealed class QueensdaleRealm : Realm {
    protected override void OnRealmInitialise() {
        // Host.Session is doc 16's server. Replication, RPC and interest are wired here, exactly as
        // they would be in a listen server — a realm is not a different kind of server.
    }

    protected override TransferReadiness ReadinessOf(RealmPlayer player) =>
        // The hook that stops a rollout from ending a raid.
        TransferReadiness.Ready;
}

public static class Program {
    public static int Main(string[] arguments) => RealmApp.Run<QueensdaleRealm>(arguments);
}
```

The process is launched with one argument — `--realm-spec shard=…;map=…;port=…` — which a placement
backend writes. A process handed no spec says so on standard error and exits `2`, which is
distinguishable from a crash and not worth a launcher retrying.

### What `Realm` decides for you

`Realm` seals four of `Game`'s hooks and offers realm-shaped ones in their place. `OnConfigure` is
where the shard's boot decisions are made, and they are not a realm's to undo:

| | |
|---|---|
| `Variant` | `BuildVariant.Server` — headless host, `Vixen.Graphics.Null`, server content profile |
| `Window`, `Graphics.Enabled` | none, off |
| `StartupScene` | `RealmSpec.Key.Map` |
| `FrameRateLimit` | the spec's tick rate |

`OnRealmConfigure` runs after all of it, and everything else is fair game.

⚠ **The map is `AppConfig.StartupScene`, not a loader of its own.** The host already opens it before
`OnInitialise`, reports its own failures and survives them. A realm that loaded the map a second way
would be a second code path for content failures, tested half as often.

## Examples

### Is the map up?

That is the one question the host does not answer, and it is what separates `Starting` from `Ready`.
`MapLifetime` answers it by looking for a loaded scene whose name matches the map address's leaf —
which is not a workaround, it is the same identity `NetworkSceneId` hashes.

```csharp no-compile="RealmHost calls this for you; shown to explain what it is looking for"
var map = new MapLifetime(spec.Key);

map.Resolve(scenes);     // false until the host's startup scene appears
map.Quiesce();           // draining: still simulating, no arrivals
map.Unload(scenes);      // and down again
```

⚠ **A realm whose map never appears never becomes ready**, and that is the correct failure. It will be
started, it will not be placed on, and the orchestrator will eventually stop it — a shard that quietly
did nothing rather than one that admitted players into an empty world.

A realm whose map is not a startup scene — a generated map, a persistent shard rehydrating authored
state — calls `MapLifetime.Ready` instead, which exists so that such a realm does not have to fake a
scene name.

### Draining

```csharp no-compile="the launcher's side is a stdio line; this is what it turns into"
host.Drain();   // stops arrivals, quiesces the map, and moves nobody by force
```

The players already on the shard leave at moments `ReadinessOf` approved of, and the shard stops once
it has been empty for `RealmHostOptions.IdleGrace`. The grace is not zero: a player who was moved out
may have a reconnect in flight, and a shard that vanished the instant its population hit zero would
turn a lost packet into a lost session.

### Overriding the transport

`Realm.CreateTransport` is virtual and binds UDP to every interface on the port placement published.
The spec's *host* is what a client is told — a node's external address, a relay's name — and is
frequently not an address the process could bind even if it wanted to. Swapping in a relay-allocated
endpoint or a composite that accepts both direct and relayed clients is a placement decision rather
than an architecture change, precisely because nothing above `ITransport` knows the difference.

### Why not `VixenApp.RunRealm`

[Doc 27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) writes the entry point as `VixenApp.RunRealm<MyRealm>`, and
it cannot be: `VixenApp` lives in `Tools/Vixen.App`, which sits *below* `Live/`, and a static class
cannot be extended from outside. So the entry point moved rather than the layering, and `RealmApp`
mirrors the original call for call.

## See also

- [Shards, keys and specs](shards-and-specs) — what a realm is told it is.
- [Admission and health](admission-and-health) — the door, the heartbeat, and the control plane.
- [Placing realms](placing-realms) — what launches it.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § The realm.
