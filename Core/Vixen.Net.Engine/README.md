# Vixen.Net.Engine

Where `Vixen.Net` and `Vixen.Engine` meet. `SyncVar`, `SyncList` and `NetworkModule` — the
behaviour-facing authoring style over the same replication mechanism.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § State replication.

## Why this is a package of its own

`Vixen.Net` and `Vixen.Engine` are siblings: both sit on `Vixen.Core` and `Vixen.Ecs`, and **neither
references the other**. Networking is optional and nothing below the engine is allowed to depend on
it. So a type that has to see both a `Behavior` and a `NetworkId` lives above both, which is here.

## Two authoring styles, one mechanism underneath

```csharp
// ECS-native
[Replicated] struct Position { [Quantize(-1000f, 1000f, 16)] public Vector3 Value; }

// Behavior-facing
sealed class PlayerState : NetworkModule {
    public SyncVar<int> Score { get; }
    public VitalsModule Vitals { get; }

    public PlayerState() {
        Score = Declare(new SyncVar<int>(0), nameof(Score));
        Vitals = Nest(new VitalsModule(), nameof(Vitals));
    }
}
```

**A `SyncVar` gets delta encoding, per-connection baselines, priority shedding and per-field
bandwidth attribution without a line of code here doing any of it.** A field declares the fixed lanes
it occupies; a module's lanes are its fields' lanes end to end; a lane layout is exactly what
`DeltaCodec` needs. `SyncStateReplicator<T>` is an ordinary `IComponentReplicator`, so behaviour state
joins the pipeline at the same place a `[Replicated]` struct does.

The join to the ECS's change versions is `SyncStateVersion`, a counter component. A behaviour's state
lives in managed fields the ECS cannot see, so `MarkChanged()` touches something in a chunk — that
write is the whole point, and nothing reads the number.

`NetworkModule` is the primitive and `SyncVar` is a field in one, which is doc 16's instruction rather
than a preference: *"building the built-ins out of the same primitive users get is the right
discipline and proves the primitive."* Modules nest, and a nested one's fields are flattened into the
outer one's layout with their path as their name — so the bandwidth report says
`PlayerState.Vitals.Health`.

## SyncList does not use the delta packer, and stretching it to would be wrong

That machinery rests on a **fixed** lane layout — the server checks that declared lanes add up to what
was written and falls back to whole records when they do not. A list is variable-length, so it would
fail that check on every send: correct, and useless. Worse, lane-by-lane differencing is *actively
wrong* for a list, because inserting at the front shifts every element and a one-item insert would
difference as "all of it changed".

So a list goes **whole**, on the reliable channel, on the tick it changes.

**This paragraph used to say the opposite, and wiring it up is what showed the difference.** It said a
list replicates as *what happened to it* — append, insert, remove, replace, clear — and that ops
travelling reliably and in order makes per-connection bookkeeping unnecessary, because everyone
receives every op exactly once. That is true of a broadcast and false of a snapshot: a snapshot goes
to the connections an interest resolver returns, so somebody who was not observing has received
nothing at all, and an object crossing into their interest has to be told the list rather than the
last thing that happened to it. It was never wired up, which is the shape that claim leaves behind.

Sending the state instead makes a late joiner, a reconnect, a lost snapshot, an interest change and a
player who was in another scene the **same case**: here is the list. Nothing had to be added to the
wire for it — the record format was never fixed-width, only the *delta* path is, and it correctly
declines a replicator that declares no lanes.

The cost is bandwidth proportional to the list on the tick it changes. A hundred-item inventory is a
few hundred bytes when somebody picks something up, seconds apart, on a channel that will deliver it.
A list changing every tick is a list being used as something it is not — and if one genuinely must be,
the shape that fixes it is the one `NetworkAnimatorParameters` and `NetworkBones` use: a fixed
capacity, which buys back per-element delta encoding at one bit for an element that did not move. That
needs a fixed-width element type, which a general `SyncList<T>` does not have.

**The op log is not wasted.** It still drives `SyncList<T>.Changed`, which is what a UI binds to — "one
item was inserted at index three" is exactly the notification a caller wants, and exactly not what a
receiver should be sent.

## Spawning is a replicated component, not a message

`NetworkSpawn` — a prefab id, a scene id and an owner — is an ordinary `IComponentReplicator` at the
top of the priority list. That is the whole design, and everything else follows from it:

- **Interest, loss and late joiners are already answered.** A spawn is carried by the snapshot, so it
  goes to exactly the connections the interest resolver returns, it is re-sent until acknowledged and
  then never again, and a player who connects an hour in receives it with everything else. A message
  on its own route would have needed its own answer to all three.
- **Ordering is free.** Records are written in descending priority, so a spawn precedes every state
  record about the same entity in the same snapshot — and it is the last thing the bandwidth budget
  sheds, which is the right way round.
- **Despawn already existed.** Destroying the entity takes it out of what the resolver returns, and
  leaving interest already means "drop it". A client cannot tell destruction from walking over the
  horizon, and does not need to.

**The prefab id is the hash of the addressable's address** ([08](../../docs/plan/08-asset-pipeline-and-addressables.md)),
so both peers compute it from content neither had to send. `NetworkPrefabRegistry` refuses two
addresses that hash alike, which is the one place both names are still in hand.

**Only the parts of a prefab that asked for an id get one.** A template node carrying a
`NetworkObject` opts in; a hundred-entity set piece where one turret rotates costs one id and one
record, not a hundred of each. The instance's ids are one reserved run — root first, then the marked
nodes in capture order — so a spawn is a fixed twelve bytes however large the prefab is.

⚠ **The marker is `NetworkObject` and deliberately not `NetworkId`.** A `NetworkId` is a number the
server allocated; `NetworkObject` is a designer's claim about content, which is the only one of the
two an asset can hold — it is `[Component]` **and** `[DataContract]`, and a compiled scene may name
nothing else. Marking with the handle meant the marker did not survive a content build at all, and
letting the handle into a scene file would let a play-mode save write live ids into content.
`NetworkPrefabRegistry` still counts a node carrying a `NetworkId`, because `Prefab.CaptureFrom`
takes live subtrees and a live networked subtree carries them.

**The receiver builds the instance *over* whatever was already standing there.** A snapshot names
entities by id, so a record whose spawn is a few ticks behind makes a bare stand-in that is already
holding the object's real position. `Prefab.InstantiateOnto` merges the two and the stand-in wins
wherever they overlap: the wire's position is where the object is, and the prefab's is where the
artist left it. That path only happens under loss — which is to say never on a developer's machine —
so it has a test of its own rather than a comment.

## Rules a prefab carries

A `.vxnetrules` is a `NetworkRulesAsset`: a name and a `NetworkRules`. A node names one with a
`NetworkRulesReference`, and `NetworkSpawner` resolves the name against
`NetworkRulesRegistry.TryGetNamed` at the moment it allocates that node's id — the one instant at
which the authored name and the runtime handle both exist.

⚠ **Both types live here rather than in `Vixen.Net`, and it is the same reason `NetworkObject` does.**
What a compiled prefab may name is a component carrying `[Component]` **and** `[DataContract]`;
`Vixen.Net` may not reference `Vixen.Engine`, so it runs neither generator. The asset lives beside the
reference so one assembly answers for both halves.

⚠ **A name nothing loaded is not an error.** The node stays on `NetworkRulesRegistry.Default` —
server-authoritative, so nothing unsafe happens — and `NetworkSpawner.UnresolvedRules` counts it. The
counter exists because that failure is otherwise invisible: what an author sees is a game rule that
does not work, beside a policy file that reads exactly right. Same counter, same class of bug, as
`WaterZoneSystem.UnresolvedWaves`.

⚠ **The string is the part that can fail quietly.** `NetworkObject` is a tag — a compiled prefab has
the bit or it does not. A reference carries a `string` through `SceneContent.Capture`, a chunk, a
bundle and `AssetManager`, so it can arrive *present and empty*, which resolves to nothing.
`APolicyReferenceSurvivesTheContentBuild` reads it back off an instance for that reason.

~~**Owed: filling the registry from the catalog by label**~~ — **built**, as
`NetworkRulesContent` in [`Vixen.Net.Engine.Content`](../Vixen.Net.Engine.Content/README.md), the way
`NetworkPrefabContent` fills the prefab registry.
[Network rules as a file](../../docs/guide/engine/network-rules.md) is the authoring story.

## What becomes of an owner's objects when they leave

⚠ **`NetworkRulesRegistry.OnOwnerLeft` had exactly one caller in the repository and it was a test.**
Its own remarks hand the `Destroy` entries to "whoever owns spawning, because destroying an entity is
not this type's to do", and nothing was that — so `onOwnerDisconnect: Destroy` imported cleanly,
resolved onto a spawned node, and then the object outlived the session owned by a player who was gone.

`NetworkSpawner.OnOwnerLeft(world, player)` is the half that was missing. A server wires it to its
session's `PlayerLeft` and one call does all three behaviours: the registry transfers and persists,
and the spawner despawns what the policy condemned, subtree and ids included.

- **One sweep of the networked world, not an id-to-entity index held for the session.** There is no
  such index on the server — `ReplicationClient.TryGetEntity` is the receiving side's — and building
  one to serve an event that fires when a connection drops is the wrong trade. A player who owned
  nothing costs no sweep at all.
- ⚠ **`UnresolvedDespawns` counts an id the policy condemned that no entity answered to.** That is a
  spawner and a registry built over two different `NetworkOwnership` tables, or an entity a game
  destroyed by hand — both wiring, both silent, and both indistinguishable from a policy that decided
  nothing. `UnresolvedRules` is the same counter for the other end of the same story.
- **`PlayerSpawner.Leave` deliberately does not ask.** A controller and its pawn are the session's
  rather than the world's and go when the connection does; everything else the player owned is what
  `OnOwnerLeft` answers for.

## Scenes

A `SceneHandle` is a number the local `SceneManager` hands out in load order, so the same level is
scene 2 on a server that loaded a lobby first and scene 1 on a client that did not. `NetworkSceneId`
is the hash of the scene's *name* — the thing both ends already agree on — and `NetworkSceneMap` is
the join between the two on each peer.

- **A spawn for a scene this peer has not loaded waits.** Not built-and-untagged, which would leave an
  object the scene's unload never sweeps, standing in the middle of the next map. `PendingCount` is
  where that becomes visible, and a number that never comes down is a client that will never have the
  content.
- **`SceneInterestRule`** is doc 16's first resolver, and it goes in an `InterestChain` beside the
  explicit overrides and the distance grid. It **hides and never shows**: being in the right scene is
  not a reason to be told about something, only the absence of a reason not to be, so an object in a
  loaded scene comes back `Undecided` and the grid after it gets its say. An entity in no scene is
  left to everybody — a rule whose default is "vanish" is one everybody debugs.
- **Scene-placed objects derive their ids** from the scene and their index in it rather than being
  allocated one, so a designer's crate is addressable the moment the scene loads and before anybody
  has connected. Those live above `NetworkId.FirstBaked`, which the allocator will not reach.

## Owed

- ~~**The sync system.**~~ **Built** — `SyncStateSweepSystem`. `MarkChanged()` and `MarkListsChanged()`
  had no production caller at all: every one in the tree was a test, so a game that set a `SyncVar` and
  forgot to mark it had state that never left the server, silently, because the entity never acquired a
  `SyncStateVersion` for the change filter to notice. The sweep walks `BehaviorRef` **and** `NetworkId`
  together — networked entities that carry behaviours, not every behaviour in the world — asks
  `NetworkModule.IsDirty` and `ISyncList.HasPending` (promoted onto the interface for it), and marks in
  a second pass because `MarkChanged` is a structural change and the first pass is holding the chunk.
  ⚠ **The ordering is the correctness argument and it is invisible when it is wrong**: a sweep before
  the behaviours marks last frame's writes and everything ships one frame late, which reads as
  interpolation being heavy rather than as a bug. `[UpdateInGroup(LateUpdate)]` +
  `[UpdateAfter(BehaviorLateUpdateSystem)]`, asserted off `SystemGraph.Plan` — `Unsatisfied` empty, so
  the edge is real and not a tie the registration order happened to break the right way. The other half
  of the ordering cannot be an attribute: `ReplicationServer.Capture` is the game's and is not a
  system, so **a capture must come after the `LateUpdate` phase in the same tick**, and a server that
  captures inside `FixedUpdate` calls `Sweep(world)` itself — public for the same reason
  `NetworkTransformCaptureSystem.Publish` is.
- **An adopter for the behaviour style, and the obstacle is the assembly graph.** `NetworkBehaviour`
  has no production subclass; every one in the tree is a test, and so is every caller of `Sweep` and
  of `Publish`. ⚠ **This is not "nobody got round to it".** No sample in the repository references
  both `Vixen.Engine` and `Vixen.Net`: `04`, `13` and `15` have a behaviour loop and no session, `08`,
  `09` and `14-Mmo` have a session and no behaviour loop, and **nothing references
  `Vixen.Net.Engine` outside `Core/` and `Vixen.Editor.Assets`**. So the intersection where this
  authoring style is usable at all is empty, and `Samples/08` — which two remarks in this assembly
  used to name as their caller, wrongly — cannot host one without gaining an `EngineLoop` and two
  project references it deliberately does not have. Tracked as issue 133.
- **Codecs beyond the built-in set.** `SyncCodecs.Register` is the door; only the types the generator
  already understands are through it.
- ~~**Registering prefabs from the catalog.**~~ **Built, in
  [`Vixen.Net.Engine.Content`](../Vixen.Net.Engine.Content/README.md)** — `NetworkPrefabContent.LoadAsync`
  fills a registry from the `networked-prefabs` label, so "networked prefab" is something an asset
  *is* rather than something a start-up path remembers to say. It is a separate assembly for the
  reason the comment in this one's `.csproj` gives: a game that spawns from templates it built in
  code should not link the addressables runtime for a path it does not take.
- ~~**The marker a compiled prefab can carry.**~~ **Built** — `NetworkObject`, a `[Component]`
  `[DataContract]` tag, is what a designer puts on the nodes that should get an id. It replaces
  `NetworkId` in that role, which could never work: a `NetworkId` is a handle the server allocated,
  it is not `[DataContract]`, and `SceneContent.Capture` dropped it without a word, so a prefab out
  of a content build had exactly one networked node whatever its author said. The registry still
  counts a `NetworkId` on a node, because `Prefab.CaptureFrom` takes live subtrees that carry them.
  ⚠ **`Vixen.Editor.Assets` links this assembly for it**, the way it links `Vixen.Water` and
  `Vixen.Navigation`: a component is declared by a `[ModuleInitializer]` in the assembly that owns it,
  so an assembly the scene compiler does not load is one whose components a `.vxprefab` cannot name.
  `SceneNetworkComponentTests` compiles `!NetworkObject {}` and `!PlayerPawn {}` out of YAML and onto
  entities, which is the other end of the same path.
- **A scene format to derive indices from.** `NetworkSceneId.BakedId(index)` is the rule, and the index
  is whatever the game passes because scenes are built in code and not yet serialised. The moment a
  scene is an asset, the index is its position in that asset's list of networked objects and nobody
  chooses it. That is doc 08's territory.
- **Scene load and unload as session messages.** `SceneInterestResolver.Enter`/`Leave` is called by the
  game today. The server telling a client which scenes to load — and knowing when it has — is what
  turns "the spawn is waiting for its scene" from a state into a handshake.
- **Client-requested spawns.** `NetworkSpawner.MaySpawn` answers the default rule, which is all a rule
  registry can answer before the object exists. The per-prefab question ("clients may spawn projectiles
  but not vehicles") wants an RPC that receives the address, and that RPC is not written.
- **Disconnect behaviour reaching despawn.** `NetworkRules.OnOwnerDisconnect` and
  `NetworkRulesRegistry.OnOwnerLeft` decide what happens to an absent player's objects, and nothing
  yet hands those actions to `NetworkSpawner.Despawn`.
- **Composing resolvers.** Doc 16 wants scene scope, then explicit overrides, then a distance grid,
  then LOD falloff. `SceneInterestResolver` is the first and there is no chain to put it in yet.

## Players

```csharp
// Server: a connection joins.
var controller = players.Join(world, player, "gameplay/prefabs/avatar", LocalTransform.At(spawn));

// Client: say who you are, and it works out which body is yours.
var local = new LocalPlayerSystem { Local = session.LocalPlayer!.Id, Controller = mine };
```

[29](../../docs/plan/29-players-and-possession.md) is the design. `PlayerSpawner` is Unreal's
`AGameModeBase` with the god object removed: what is load-bearing in that class is "when a player
joins, make a controller, make a pawn, put one in charge of the other", and everything else it owns
already lives somewhere better here.

**The pawn is spawned owned, and that one argument is what makes prediction legal.**
`NetworkSpawn.Owner` is what `PredictedOwnershipSystem` reads to decide what to predict and what
`[ServerRpc(RequireOwnership = true)]` checks. Unreal reaches the same place by making the player
controller the `NetConnection` owner and letting ownership flow down; here it is said once, at the
spawn.

**A client is never told which entity is its pawn.** An entity carrying `PlayerPawn` — a tag that
rides on the prefab and therefore costs no wire bytes — owned by this connection and possessed by
nothing *is* the pawn. ⚠ **That only became true of a prefab out of a content build when this
assembly's generators were wired**: `PlayerPawn` carries `[Component]` and `[DataContract]`, which
is what a compiled scene needs, but nothing declared it to `SceneComponentRegistry` because analyzers
do not flow through a `ProjectReference` — the same defect `NetworkObject` was created to fix, one
attribute further along. A message saying "you are pawn 47" can arrive before the spawn it names, after
the pawn has died, or twice; a query over already-replicated state has none of those cases and is
right whenever it runs.

`PlayerMoveInput` is `MoveIntent` quantized: seven bytes, two axes at eight bits, two angles at ten,
sixteen of buttons. ⚠ **A client must round its own intent with `PlayerMoveInput.Round` before
predicting with it.** The server computes from the decoded numbers, so a client predicting at full
precision disagrees by the rounding on every tick, on a perfect connection, and it looks like jitter.

**Two footguns turned into systems.** `PlayerInputQuantizeSystem` rounds each player's intent to what
the wire carries, between `PlayerInputSystem` and `PossessionSystem` — so the pawn, the wire and the
prediction see the same numbers and nobody has to remember `PlayerMoveInput.Round`.
`PredictionSmoothingSystem` decays a rollback into an offset on a **visual child**, which is where
Unreal puts it too: ⚠ writing it onto the body would be adopted by `PhysicsScene` as a teleport, and
the error the smoothing was hiding would become one the simulation had made.
