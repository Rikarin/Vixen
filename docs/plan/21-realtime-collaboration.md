# 21 — Realtime Collaboration

> ⚠️ **Extends [11](11-editor.md) and [20](20-editor-parity.md). Depends on [16](16-networking.md).**
> Doc 20's Part G puts collaborative multi-user editing out of 1.0, and **that decision stands.** What
> Part G does not do is say what "it stays *possible*" costs, in what order the pieces have to arrive,
> or which of them is separable enough to be worth building before the rest is funded. This document is
> that, written against the code rather than against the aspiration — because "the architecture allows
> it" is a claim that decays silently until somebody tries.

Reference implementation studied: **Unreal's Multi-User Editing**, internally *Concert*, together with
*Disaster Recovery*, which is a second product built on the same transaction log. As with PurrNet in
doc 16, it is a *reference* and not a compatibility target: its shape is worth taking, its mechanism
is not available to us and turns out not to be the one we want.

## What Unreal gets right, and the one thing it gets away with

Three decisions in Concert are worth copying outright, and each of them is a decision to *not* solve
something:

1. **Locks instead of merges.** Concert's conflict story is an exclusive lock taken when you start
   touching a thing and released when you stop. There is no operational transform, no CRDT, no
   three-way merge of a property. Real conflict *resolution* appears in exactly one place — restoring
   an archived session — which is the one place it cannot be avoided.
2. **Presence is a separate system from the edit stream.** Avatars, name tags and follow-user share no
   code with transaction replication, depend on none of it, and would still work if it were switched
   off. That separation is why presence is cheap, and it is why it can be built first.
3. **Persisting is one user's act.** The shared state lives in memory across the session; one person
   writes it to disk. Everybody saving is how you get a session whose participants disagree about
   what is on disk.

And the thing it gets away with: **undo stays local, and undoing something a peer has built on is a
documented hazard rather than a solved problem.** That is not a gap in Concert. It is the honest
position, because every fix for it is more expensive than the problem — [`CommandStack`](../../Editor/Vixen.Editor.Core/CommandStack.cs)
already makes the same call inside a single editor, where a cross-document command discards the
affected document's redo stack rather than rebasing it, "which is a research project".

## Part A — The Concert feature set, and the verdict on each

| Concert feature | What it is | Verdict |
|---|---|---|
| **Multi-user server + UDP discovery** | A headless server process found over the messaging bus, with version/project compatibility checks on join | **Take, reduced.** `Tools/Vixen.CollabServer`, next to [`Vixen.ContentServer`](../../Tools/Vixen.ContentServer/README.md) and with the same candour about being a development tool. Discovery is a typed address for now; a LAN beacon is ten lines whenever it is wanted |
| **Sessions** — browse, create, join, leave | The session lifecycle and its participant list | **Take.** `NetworkSession` already owns the handshake, the clock and the player list |
| **Session settings** — ignore-lists of classes and packages | Per-session filters on what replicates | **Defer.** It exists in UE because reflection-driven capture replicates *everything* by default and has to be told not to. Intent-based replication has the opposite default (Part B), so the need mostly evaporates |
| **Transaction replication** | Editor edits captured from the `UObject` transaction buffer as property deltas and applied on peers | **Take the behaviour, reject the mechanism.** This is the whole cost of the feature and the whole of Part B |
| **Package replication** | Saved assets pushed to peers in memory, so a new material appears without touching disk | **Take, late.** C4 |
| **Persist session changes** (+ source control check-in) | One user writes the shared state to disk | **Take the persist. Reject the source-control half** — Vixen has no SCM integration and should not grow one here; git over a text scene format is the user's, and doc 08 already made it work |
| **Presence** — avatars, name tags, colours, follow-user | Where everybody is looking | **Take first.** The highest value per line in the list, and the only item that touches no document code |
| **Locking** | Auto-acquired on transaction start, released on completion; explicit lock/unlock | **Take, and centre the design on it**, the way doc 16 centres on `NetworkRules` |
| **Local-only undo** | Undo is not replicated; an undo is sent as a new transaction | **Take the rule, and refuse loudly** where UE merely warns — see rule 3 |
| **Session history / activity feed** | Who did what, when, inspectable per activity | **Take.** It nearly falls out of the command log, and it is the only way the rest of this is debuggable |
| **Archived sessions, restore, transaction conflict detection** | Replaying a stored session onto changed content, detecting the conflicts | **Reject.** This is version control wearing a session's clothes. UE needs it because its content is opaque binary packages; Vixen's scenes are text with GUID references (doc 08), so the tool for "reconcile two people's week" is git, and it is better at it than this would be |
| **Disaster Recovery** | The same log recorded locally, offered back after a crash | **Defer, and note the price.** Once C1 exists this is the log written to a file and replayed — the cheapest feature in the table. It is deferred for want of a reason to sequence it, not for want of a design |
| **High-frequency property streams** (5.4's Replication tab) | Transform/Live Link data streamed at frame rate, separate from transactions | **Reject.** It is virtual-production plumbing for VCam and Live Link. Vixen has neither, and building the pipe before the thing that fills it is how you get a subsystem nobody can explain |
| **Shared Sequencer, multi-user Take Recorder, nDisplay** | VP workflows | **N/A.** No equivalents exist to collaborate on |
| **PIE is not shared** | Play-in-editor stays local to each user | **Take the rule.** Play mode is already a `WorldSnapshot` swap; sharing it would mean sharing simulation, which is doc 16's job and a different product |

## Part B — Intent, not diffs, and what that trade actually costs

Unreal harvests *state*: the transaction buffer records which `UProperty` on which `UObject` changed,
reflection serializes the delta, peers apply it. Vixen has no such buffer and cannot grow one —
runtime reflection is out under ADR-002 and the AOT non-negotiables, which is the same argument doc 16
used to reject PurrNet's `NetworkReflection`. What Vixen has instead is
[`IEditorCommand`](../../Editor/Vixen.Editor.Core/IEditorCommand.cs): every edit in the editor is
already one reversible, named *operation*, which is exactly the thing doc 11 said the single mutation
vocabulary would make possible.

That is a better substrate in three ways and a worse one in one, and the worse one is the one to design
against:

| | Unreal — diffs | Vixen — intent |
|---|---|---|
| **Coverage** | Automatic. Anything transacted replicates without being told to | **Opt-in per command type.** A type with no wire form does not replicate. *This is the liability*, and it is answered by a gate rather than by discipline (Part H) |
| **What crosses the wire** | The 400 property writes a rename caused | The rename. `IEditorCommand`'s own remarks name this case: "renamed this asset, which updated four hundred references" is one command because it is one operation |
| **Meaning at the far end** | A value landed on a field | An operation ran, with its name, its author and its blast radius intact — which is what makes the activity feed real rather than a diff log |
| **Reversibility** | Invertible generically | Hand-written `Undo`, valid only against the state it was applied to. Fine locally; the reason rule 3 exists |

**The hazard the substrate creates, stated once and enforced twice.** Applying an operation requires
both machines to compute the same result from the same recorded parameters. A command whose `Do` reads
*local* state at execution time diverges silently — and the editor has such state by design:
[`Selection`](../../Editor/Vixen.Editor.Core/Selection.cs) is explicit that "commands that need it read
`EditorContext.Selection` at execution time", and a selection is per-user. So is the camera, the
current folder, and the clock.

> **Rule 4, below, is the whole of it: a replicable command's `Do` must be a pure function of its
> recorded parameters and the shared document state.** A command that resolves "the selection" does it
> when it is *constructed*, on the machine where that selection exists, and records the resolved list.
> Most of the scene commands already work this way — `TransformTargetsCommand` is handed targets — so
> this is a rule that mostly ratifies existing practice, which is the only kind of rule that survives.

## Part C — What this is built on, all of which exists

| Piece | Where | Why it matters here |
|---|---|---|
| Transports — `Local`, `Udp`, `WebSocket`, `Composite` | [Core/Vixen.Net](../../Core/Vixen.Net/README.md) | `Local` is why a two-editor test needs no socket, which doc 16 calls the single best testability decision it took from PurrNet |
| `NetworkSession` — handshake, clock, players | Core/Vixen.Net/Sessions | The join/leave lifecycle is not new work |
| Four channels with behaviour queried, not switched on | Core/Vixen.Net | Presence is `Sequenced`, the command log is `Reliable`; both already mean something to the layers below |
| `PacketWriter`/`PacketReader`, non-throwing, cap-taking | Core/Vixen.Net/Messaging | A peer's bytes are untrusted input. This is the security property the whole design leans on |
| `NetworkSimulation`, seeded and replayable | Core/Vixen.Net | "The bug that only happens at 20 % loss" is a test here, not an anecdote |
| One mutation vocabulary; per-document and global stacks | [Editor/Vixen.Editor.Core](../../Editor/Vixen.Editor.Core/README.md) | The substrate of Part B |
| `MarkModifiedExternally` → `ClearRedo` | [EditorDocument.cs:105](../../Editor/Vixen.Editor.Core/EditorDocument.cs) | **The rule a remote edit needs is already written and already the documented answer**, for the local cross-document case. A remote command is the same situation arriving from further away |
| `CommandTransaction`, open/commit/cancel | Editor/Vixen.Editor.Core | The exact bracket a lock should be acquired and released on |
| `EntityId` (GUID), `SceneDocument.IdOf`/`TryGetEntity` | [Editor/Vixen.Editor.SceneView](../../Editor/Vixen.Editor.SceneView/README.md) | Cross-machine addressing for scene entities exists, and the README already says an `EntityId` is what "a multi-user session" has to be expressed in |
| `AssetId` GUIDs in a prefixed scalar | doc 08 | Cross-machine addressing for assets, unchanged by a move or a rename |
| `WorldSnapshot`, `SubtreeSnapshot`, `SceneSerializer` | Editor/Vixen.Editor.SceneView | Late join. Built for play mode; the doc-16 pattern of the primitive arriving for another reason |
| `Vixen.Editor.Testing`, `PlayerSessions` | Editor/ | Driving two editors in one test is existing machinery |
| `SceneViewport`, `EditorCamera`, `SceneLines`, `GizmoGeometry` | Editor/Vixen.Editor.SceneView | Presence avatars are a line-drawing job against surfaces that already draw lines |

## Part D — What is missing, named precisely

| Gap | What it actually needs |
|---|---|
| **Commands have no wire form** | [`SetPropertyCommand<T>`](../../Editor/Vixen.Editor.Core/SetPropertyCommand.cs) holds an `EditorProperty<T>` *reference*; `RenameEntityCommand` holds an `Entity`, which is a slot and a version in one world and names nothing off-machine. The wire form addresses `(AssetId document, EntityId entity, string property)` and resolves through `SceneDocument`'s existing id table |
| **`DelegateCommand` cannot be replicated at all** | It is a closure. It must be *detectably* local rather than silently local — the session says so, and CI catches new ones |
| **Nothing in `Editor/` references `Vixen.Net`** | Layering permits it (the rules run one way and the editor is above `Vixen.Engine`); what is needed is a `Poll` site in the editor frame, which is the same "nothing is delivered outside `Poll`" contract the game gets |
| **No authority model for documents** | `NetworkRules` is a policy over game entities. The collaboration equivalent is a lock table and a join policy, and it belongs in the new assembly rather than in `Vixen.Net` |
| **No user identity** | `ISessionAuthenticator` exists; a display name, a colour and a permission set do not |
| **Late join** | `WorldSnapshot` copies a world in-process. Over a wire the answer is cheaper: send the serialized document plus the command log since it was serialized. `SceneSerializer` is that half |
| **Documents that are not scenes** | A material graph or a VXML document has no `EntityId`-shaped identity for its interior. Locking the *document* is the honest answer for those, and it is what C2 ships |

## Part E — The design

Two new projects, and no change to `Vixen.Net`:

```
Editor/Vixen.Editor.Collaboration     session, presence, lock table, command codecs, the apply path
Tools/Vixen.CollabServer              headless relay and lock authority; dev-grade, and says so
```

**Security posture, stated up front, because this one deserves it.** A collaboration client executes
operations described by another machine. That is a remote-code-execution surface if the codec is
sloppy, which is why every command decoder goes through `PacketReader` — never believing a length,
never allocating on one it was told, never throwing — and why the decoders belong in the nightly
`Vixen.Net.Fuzz` harness from the first commit rather than after the first incident. The server is
LAN-and-trusted like `Vixen.ContentServer`: no TLS, no relay, authentication via `ISessionAuthenticator`
and nothing more. Sessions across the open internet want a relay and a transport story that this plan
does not have, and pretending otherwise in a README is how people get hurt.

**Six rules.**

1. **Nothing is delivered outside `Poll`.** Inherited from the transport contract, and it is what keeps
   a remote edit from arriving between two panels' updates.
2. **A remote command never enters a local undo stack.** It applies through the same `Do`, and then the
   document is marked modified-externally — which already discards redo, for exactly the reason that
   rule was written.
3. **Undo is local and may refuse.** If the entry's target is locked by someone else, or the state it
   would restore has been superseded, undo declines with a message. UE warns; refusing is cheaper than
   the corruption and more honest than the warning.
4. **A replicable command's `Do` is a pure function of its recorded parameters and the shared document
   state.** No selection, no camera, no clock, no RNG read at execution time. Part B is the argument;
   the loopback replay in C1 and the divergence detector in C3 are the enforcement.
5. **A lock is taken when a transaction opens and released when it commits or cancels.** The
   `CommandTransaction` bracket is already the right shape and already a `using` block.
6. **A command type with no wire form is local-only *and says so*** — reported in the session UI, and
   caught in CI by the registry gate.

**The messages**, with the channel each earns:

| Message | Channel | Note |
|---|---|---|
| `Join` / `Welcome` | `Reliable` | Project id, content hash, protocol version. A mismatch is refused with a reason, not tolerated |
| `Presence` | `Sequenced` | Camera pose, active document, selection. Unreliable and self-superseding: an old one is discarded rather than applied |
| `LockRequest` / `LockGrant` / `LockDenied` / `LockRelease` | `Reliable` | Server-arbitrated. One map, and the whole conflict story |
| `CommandApplied` | `Reliable` (ordered) | The command log. Ordering is the point: the log is the shared history |
| `DocumentSnapshot` | `Reliable` | Late join, and reconnect |
| `Persist` | `Reliable` | One user writes; everyone is told what landed |

## Part F — Milestones

Effort in engineer-months, on doc 14's scale. **~5.75 EM total**, which is not a feature — it is
roughly half of doc 20's entire editor-parity programme, and sizing it honestly is most of what this
document is for.

**Only C0 is separable.** It is worth building on its own, before the rest is funded, and it is the
recommendation this document ends on.

### C0 — A session, and the people in it (1.0 EM)

`Tools/Vixen.CollabServer`. Join, leave, participant list, display name and colour. Presence avatars in
the viewport — camera frustum, name tag, remote selection highlight — plus follow-user and a
participants panel. `Poll` wired into the editor frame.

**Nothing under `Vixen.Editor.Core` changes.** That is the point of doing it first: it proves the
transport, the frame integration, the identity model and the server tool against a feature that cannot
corrupt a scene, and it is the half people actually notice.

**Exit:** two editors against one server, each drawing the other's frustum and selection at 20 Hz
through `NetworkSimulation` on `Broadband`; a golden screenshot of a viewport containing a remote
avatar; disconnecting a client removes it from every other client within one timeout.

### C1 — The wire form (1.5 EM)

`IReplicableCommand` over `IEditorCommand`: a stable type id, `Write`/`TryRead` through
`PacketWriter`/`PacketReader`, addressing by `AssetId` / `EntityId` / property name. A codec registry.
Wire forms for the command types that carry the editor's weight — property set, entity create/delete,
reparent, transform, rename. The CI registry gate. The fuzz targets.

**No replication in this milestone, deliberately.** It ships a codec, a gate and a proof, so the part
that can silently corrupt a project is provable before any behaviour depends on it.

**Exit:** a recorded command log round-trips under fuzz without a throw; and replaying a log against a
second copy of the same document produces a **byte-identical save** — the same standard the repo
already holds serialization to across three OSes.

### C2 — Replicated editing, with locks (1.5 EM)

The apply path and rule 2. Lock acquisition on `CommandTransaction`, arbitration in the server,
locked-by-someone-else shown in the outliner and the inspector. Late join by snapshot plus log.
Undo refusal per rule 3.

**Exit:** the `Vixen.Editor.Testing` two-editor scenario — create, transform, reparent, rename, on both
sides, at 20 % loss on a seeded simulation — ends with both editors saving byte-identical scenes; a held
lock demonstrably prevents the second transform rather than merging it; a third editor joining midway
converges on the same bytes.

### C3 — The session's history, and its honesty (0.75 EM)

Activity feed panel: who, what, when, per entry. Local-only commands reported rather than dropped
silently. Reconnect via session cookies. And the **divergence detector**: a periodic document hash
compared across clients, which is the only thing that catches a rule-4 violation in the wild —
`Samples/08-Multiplayer` already ends in a convergence check that exits non-zero when a client
disagrees with the server, and this is that check with a different subject.

**Exit:** a deliberately impure command is detected by the hash compare within one interval and named in
the feed.

### C4 — Assets, and persisting (1.0 EM)

In-memory asset push on save. Reference-index invalidation on the receiving side. `Persist Session
Changes` — one user writes to disk, everyone is told.

**Exit:** one user imports a texture, another assigns it to a material and neither has touched disk;
persisting writes it once, on one machine, and the other editors report themselves clean.

## Part G — Out of scope

| Not doing | Why |
|---|---|
| **Archived sessions and restore-with-conflict-detection** | Version control in a session's clothing. Text scenes plus GUID references mean git already does this job better (Part A) |
| **Source-control integration** | No SCM story exists anywhere in the plan, and a collaboration feature is a bad place to start one |
| **High-frequency property streams / virtual production** | No VCam, no Live Link, no nDisplay to fill the pipe |
| **Merging concurrent edits to one property** | Locks. Stated as the design's centre rather than as a limitation discovered later |
| **Collaborative play-in-editor** | Sharing simulation is doc 16's subject and a different product |
| **Cross-internet sessions** | LAN and trusted, like `Vixen.ContentServer`. A relay and TLS are a separate piece of work with a separate threat model |
| **Disaster Recovery** | Deferred rather than rejected: once C1 exists it is the log written to a file and replayed |

## Part H — Testing

| Mechanism | What it buys |
|---|---|
| **Two editors over `LocalTransport`** | The whole feature, in a unit test, with no socket and no second process — the reason doc 16 took `Local` wholesale |
| **Seeded `NetworkSimulation`** | Loss, jitter and reordering as a repeatable input. The same seed produces the same deliveries on every machine |
| **Byte-identical convergence** | The exit criterion for C1 and C2 both. The repo already gates bit-exact serialization on three OSes and two architectures; this reuses the standard rather than inventing a weaker one |
| **Fuzzing the command decoders** | They parse bytes from a machine we do not control. `Vixen.Net.Fuzz`'s nightly harness runs 10 min/target |
| **The registry gate** | A test that walks every `IEditorCommand` type in the editor assemblies and asserts each is either registered with a codec or explicitly attributed local-only. Doc 20's Part F found five menu lines naming commands nobody registered with a fifteen-line test of the same shape; this is that test for a case where the failure is silent divergence instead of a dead menu item |
| **Golden screenshots with presence** | An avatar that draws in the wrong place is invisible to every behavioural test, which is the lesson `Vixen.Editor.Ui`'s README already paid for |

## Risks

| Risk | Mitigation |
|---|---|
| **Rule 4 is a rule, not a mechanism.** A command that reads local state compiles, passes review and diverges in the field | Two independent nets: C1's replay-to-byte-identical exit catches it at authoring time, C3's hash compare catches it at runtime and names it. Neither prevents it; both make it loud |
| **Every new command type is a tax, forever** | About ten lines per type, and the gate makes the omission visible rather than expensive. The alternative — reflection-driven capture — is banned by ADR-002 and would not survive AOT |
| **Locks can make collaboration feel like single-user with extra steps** | Lock the smallest thing the model can name. C2 locks documents because that is what a material graph can express; the scene path should lock entities from the start, and the gap between those two is the thing to watch |
| **Undo across users is unsolved and stays unsolved** | Accepted, and refused loudly (rule 3) rather than warned about. Recorded here so it is a decision rather than a surprise |
| **It is an RCE surface** | The non-throwing reader, capped reads, and fuzz targets from the first commit — not the first incident |
| **It competes with doc 20's E-milestones for the only engineer there is** | Build C0 and stop. It is 1.0 EM, it changes nothing that can corrupt content, and it is the part a person sees. C1–C4 wait for a reason to exist |

Licensed under Apache-2.0.
