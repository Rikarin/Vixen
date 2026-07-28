# 08 — Multiplayer

The whole of Phase 9 at once: eight players, one authority, and no trust. Every layer of
`Vixen.Net` meets here, which is the only place any of them can be shown to actually join up.

```bash
dotnet run -c Release --project Samples/08-Multiplayer
```

```bash
dotnet run -c Release --project Samples/08-Multiplayer -- --loss 20 --latency 60
```

A console program with no window, because Phase 9 draws nothing — the picture is Phase 4's. What it
shows is what crosses the wire and what the other end makes of it.

## What it is

A round arena, eight fighters, movement and shooting. The server owns all of it: it is the only code
that writes a position, a health or a score. A client sends a **direction** and a **trigger pull**,
and gets snapshots back.

| | |
|---|---|
| **Movement** | `Rpc.Steer(x, z, facing)` — a `[ServerRpc]`, unreliable, owner-only |
| **Shooting** | `Rpc.Fire()` — a `[ServerRpc]`, reliable, owner-only, and it takes no direction |
| **Effects** | `Rpc.Hit(shooter, fatal)` — a `[ClientRpc]`, unreliable, to everybody |
| **State** | `NetworkTransform`, `Combatant` and `Vitals`, replicated as deltas |
| **Transport** | in-process by default, real UDP with `--mode server` / `--mode client` |

`Fire` takes no arguments on purpose. Where the shot goes is decided by where the server thinks the
shooter is looking, which it already knows from `Steer`; a client that sent a direction could send
any direction.

## The three modes

```bash
dotnet run -c Release --project Samples/08-Multiplayer -- --help
```

**`--mode local`** (the default) runs the server and every client in one process over
`Vixen.Net.Transport.Local`, driven by a fixed 16 ms step, optionally through a seeded
`NetworkSimulation`. Nothing about it is wall-clock: time is a parameter everywhere in `Vixen.Net`,
so the same arguments produce the same match, byte for byte. It ends by checking that every client
agrees with the server, and **exits non-zero if they do not** — which is what makes it the mode worth
putting in CI.

**`--mode server`** and **`--mode client`** are the same `GameServer` and `GameClient` over real UDP
sockets, driven by a `Stopwatch`. Nothing above the transport changes, and that is the claim being
made: `TransportConformance` holds both transports to the same executable contract, so the session,
replication and RPC layers have nothing to tell apart.

## What it measured

Apple M-series, .NET 10, Release. Eight players, thirty seconds of play, 30 Hz ticks.

| | clean | 20 % loss, 60 ms | 40 % loss, 120 ms |
|---|---|---|---|
| Snapshot, mean | 41 B | 66 B | 79 B |
| **Bandwidth, per client** | **9.7 kbit/s** | 14.5 kbit/s | 16.2 kbit/s |
| Records sent as a difference | 95 % | 82 % | 73 % |
| Shots that hit | 49 % | 35 % | 32 % |
| Snapshots rejected | 0 | 0 | 0 |
| Converged | yes | yes | yes |

Eight fighters at 30 Hz for **under 10 kbit/s a client**, and that is with `ReplicateEverything` —
every player is told about every other one, because interest management is the thing to replace
first and not the thing to start with.

Against the same run before delta encoding landed — 82 B, 98 B and 110 B — that is **half the
bandwidth on a good connection and a third off a bad one**. A `NetworkTransform` costs 88 bits sent
whole; a fighter that walked for a thirtieth of a second is three position axes that moved a few
quantized levels and a rotation that turned slightly, which is around forty.

Two of the other rows are worth reading twice.

**Bandwidth still goes *up* under packet loss, not down.** A snapshot only carries what the
connection has not acknowledged, so a lost acknowledgement means the next snapshot carries the same
records again — and a connection far enough behind stops being sent differences at all, because the
value they would be measured from has fallen out of the history. Both effects are visible in the
same two columns: fewer differences, larger snapshots.

**Hit rate falls from 49 % to 35 %, and that is the missing lag compensation.** The bot aims at where
it last *saw* its target, which is half a round trip old; the server resolves the shot against where
that target is when the call lands. Fourteen points of hit rate is what lag compensation would give
back, and `Arena.Resolve` is the one method that would change. It is Phase 9's single deferred item
— it rewinds colliders, and `Vixen.Physics` is Phase 8.

## Where the bandwidth goes

The run ends with a breakdown, because a total is not an answer:

```
  by field
    Motion.NetworkTransform.Position.Z            4.2 KiB     5,027 ×     6.8 bits
    Motion.NetworkTransform.Position.X            4.2 KiB     5,027 ×     6.8 bits
    Motion.NetworkTransform.Rotation.C            2.0 KiB     5,027 ×     3.3 bits
    Motion.NetworkTransform.Rotation.B            1.7 KiB     5,027 ×     2.7 bits
    Motion.NetworkTransform.Rotation.Dropped      0.6 KiB     5,027 ×     1.0 bits
    Motion.NetworkTransform.Position.Y            0.6 KiB     5,027 ×     1.0 bits
    Motion.NetworkTransform.Rotation.A            0.6 KiB     5,027 ×     1.0 bits
    Motion.NetworkTransform.TeleportCount         0.6 KiB     5,027 ×     1.0 bits
```

**Four of those eight fields cost exactly one bit each**, which is what a field that never changes
costs — the arena is flat, so `Position.Y` never moves, and the fighters only turn about Y, so one
component of the rotation never moves either. That is the report noticing that this game is using a
general-purpose component for a two-dimensional problem, and it is the sort of thing nobody finds by
reading the code. The same report breaks down by component, by remote call, by connection and by
object, and finishes by taking one snapshot apart record by record.

## What it checks

The exit criterion is convergence, and the check is not "the positions look close":

- every client holds exactly the entities the server has;
- every position is within **3.1 cm** of the server's — twice the half-level of the position
  quantizer, which is the error a position is *supposed* to have and the only error allowed to
  survive;
- every health, score and death count matches **exactly**.

It checks after a **settle phase**: input stops, and the match is pumped for a few more seconds
before anything is compared. That is not a fudge. While fighters are moving a client is *meant* to
disagree, by its interpolation delay — what must not survive the quiet is a disagreement that nothing
corrects. Under 20 % loss the last snapshot describing the final position is dropped for somebody,
and it is the unacknowledged baseline that sends it again.

## Five things the sample exists to say

**The order inside a tick is load-bearing.** `ReplicationServer.Capture` takes everything written
since the previous capture, so a write on the far side of `AdvanceVersion` is never sent — no error,
no warning, just a client that never learns about it. That is why joins are queued out of the
session's event and applied inside the tick rather than where they arrive: a player spawned from the
event handler would be invisible until the next thing about them changed.

**Split components by how often they change, not by what they mean.** Whether to send a component at
all is decided per component: either it goes or it does not. `Combatant` (owner and team, set once)
is separate from `Vitals` (health and score, set on every hit) because putting them together would
put the owner id in front of the change-detection every time somebody was shot. Within a component
the fields are differenced individually, so the split matters less than it did — but the decision to
send is still all-or-nothing, and that is the one this is about.

**Nothing is written twice.** `Move` compares before it writes, because writing the same value back
marks the chunk changed and puts that fighter in every capture from then on — which is how a
change-version filter is turned back into a full state sync by accident.

**Intent expires.** A fighter stops half a second after its owner stops asking it to move. Input is
unreliable and supersedes itself, so "keep going until told otherwise" means a player whose
connection dies mid-stride walks into the sea.

**The acknowledgement is the game's message, not the engine's.** `ReplicationServer` has to be told
the newest tick a client applied cleanly, and `Vixen.Net` deliberately does not define how it gets
there — the game already has a message channel. `MatchProtocol` is the whole of it: one opcode, one
tick, sent `Sequenced`, because an acknowledgement that arrives *late* would walk the baseline
backwards and an acknowledgement that is lost costs one tick.

## What is not here

- **Lag compensation**, as above. Deferred within the phase, blocked on Phase 8.
- **Client-side prediction.** Explicitly not in Phase 9 — see
  [docs/plan/16](../../docs/plan/16-networking.md). The owner's fighter is interpolated like everyone
  else's, so it answers a round trip late; `OwnerSmoothing` is built and this sample does not need
  it, because a bot does not mind.
- **Interest management.** `ReplicateEverything`, deliberately, because the convergence check wants
  every client to hold every fighter. A distance resolver is a class and a `--interest` flag away,
  and the seam it plugs into is `IInterestResolver`.
- **Anything drawn.** `Vixen.Engine` and `Vixen.Net` have not met yet: the system that copies between
  `NetworkTransform` and the engine's transform hierarchy is owed, and it is the first place the two
  will have to.
