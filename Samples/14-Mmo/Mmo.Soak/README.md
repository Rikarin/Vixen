# Mmo.Soak

Doc 27 and doc 28's shared exit criterion, measured rather than asserted. Eight realms over three
maps, five hundred connections, thirty minutes of ticks, continuous transfers, and a rolling upgrade
in the middle.

```bash
dotnet run -c Release --project Samples/14-Mmo/Mmo.Soak
```

```bash
dotnet run -c Release --project Samples/14-Mmo/Mmo.Soak -- --ticks 3000 --upgrade false
```

Thirty minutes of simulated fleet takes about ten seconds of wall clock. It exits non-zero when a
budget is missed, which it currently does — see **What it found**.

## What it measures, and what it does not

**The fleet, not eight engine hosts.** Eight `RealmApp`s in one process would mostly measure eight
engine hosts, and `13-ThirdPersonShooter` already does one of those properly. What doc 27's budgets
are about is the part that only exists when there is more than one shard: conservation *across* it,
the transfer path *through* it, and what a shard costs per tick to keep a player's durable state
straight. Each shard here is the four bridges a realm holds — identity, ledger, lockouts, social —
over one shared journal.

**Deterministic, from one seed.** A soak that cannot be re-run identically is a soak whose failures
are anecdotes. `--seed` is the whole of a run's identity.

**The oracle is outside the clock, and so is the rollout.** Walking five hundred balances a tick is
the harness's cost, not the fleet's; a rollout step is not a tick. `09-NetworkSoak` makes the same
separation for the same reason — measuring the measurement is how a budget stops meaning anything.

## The conservation oracle

Doc 27 calls it *"the test the whole design exists to pass"*: total currency across the whole fleet,
after every tick, over a hundred and forty thousand transfers. Everything else here is scaffolding to
make it mean something.

⚠ **`MemoryEconomyLedger.Total` is the trap, and it cost a debugging pass.** That method sums *every*
account, and a double-entry journal's every-account total is zero by construction — every movement
has two legs. It is a fine assertion that the ledger is balanced and a useless one about whether
money was created, because **a duplicate is two balanced legs**. What conservation means here is that
the sum over the *players* is what was minted.

⚠ **Checked every tick, not at the end.** A conservation bug that self-corrects — a duplicate that is
later spent — is invisible to a final total and is still a duplicate.

⚠ **A player in transit is counted.** They have been released by one shard and not yet admitted by
the other, and their purse is a number the fleet is holding. Counting only the shards would show a
dip on every transfer and a total that is right again a moment later, which is exactly what a
duplication bug looks like from the other side.

## What it found

Apple M-series, .NET 10, Release. 8 shards, 500 players, 54 000 ticks, rolling upgrade at the halfway
mark.

| | |
|---|---|
| transfers | 142 005 (20 586 refused — a refusal is a player who stayed) |
| conservation violations | **0** |
| disconnected by the rollout | **0** |
| version spread at the end | **0** |
| tick p99 | 178 µs |
| allocation | 35.5 KB a tick |
| **memory grown** | **13 MB**, from 168 |

### Three things a realm has to take away again

Eight shards holding five hundred players are a fixed working set, so a settled fleet should not
grow — and this one grew by roughly a megabyte a minute, for ever. **The interesting part is that the
first answer was wrong.** The obvious suspect was the idempotency-key set, it was written up as the
whole cause, and bounding it removed forty-five megabytes of a hundred and sixty-eight. The rest was
found by ablation: turning off map travel dropped the growth to 8 MB, and everything below follows
from that one measurement.

**The key set, which is the one that cannot simply be cleared.** Every posted intent adds
`(player, kind, operation)` to `MemoryEconomyLedger`'s guard, and it is what makes doc 27's rule true
— *"a retried trade, a retried mail claim, a retried auction settlement writes nothing the second
time"*. So it gets a `KeyHorizon`, and **how long that horizon is is safety-critical rather than a
tuning knob**: a retry arriving after it is applied again. The type is built from the *retry window*
instead of from the horizon, so the number in this sample's source is the two minutes a client would
go on resending for, and a horizon shorter than the window it must outlive is unrepresentable.

⚠ **And a horizon on its own is not enough, because a key can age out while its write is still in
flight.** `LedgerBridge` now asks the outbox before it asks the projection — the outbox is an exact
record of what this realm has started and not finished, so inside that window a horizon set too short
cannot double anything. It shrinks what the horizon has to cover to "retries after the write is
durable", and `LedgerBridge.Deduplicated` is the counter that says it happened.

**Every departed player's purse, left behind by a line that did nothing.** `Shard.Release` called
`Ledger.Restore(…, 0)`, which reads as the mirror of admission and is a no-op — `Restore` refuses a
non-positive amount. So every player who ever left a shard left a balance row on it. The real mirror
is `MemoryEconomyLedger.Release`, which hands the rows to the world account they were seeded out of
and drops them; it is deliberately **not** an intent, because letting a player go is not a movement of
value and writing one would put a lie in the journal.

**Every departed player's social graph, and their seat in everybody else's.** This was a hundred and
thirty of the hundred and sixty-eight megabytes. `SocialGraphs.Of` makes a graph on demand and nothing
ever took one away, so a shard that admits and releases a player five hundred times an hour — which is
what map travel is — kept five hundred graphs an hour. ⚠ **Dropping the departed player's own graph is
only half of it:** a gameplay id is never issued twice, so a friend still online is left holding an id
that no re-admission will ever replace — they come back as a different number and are seated beside
their own ghost. Only the durable set knows who held a tie to whom, so the sweep lives in
`SocialBridge.Forget` and is the exact mirror of `SocialBridge.Admitted`.

### A drained outbox that is never settled is a leak nothing looks like

Before the soak flushed, growth was 206 MB rather than 168, and the extra was `LedgerBridge`'s outbox
behaving exactly as documented: *"a drained write is not removed — in flight is not done, and losing
a ledger intent is losing an item."* Nothing was settling them, so the outbox was every intent the
fleet had ever posted.

Which is worth saying plainly, because it is a mistake a real realm can make: **a realm that drains
and forgets to settle has an unbounded leak that looks like nothing at all for the first ten
minutes.** `LedgerBridge.Pending` is the number that says so, and a fleet should alarm on it. The
soak budgets it at eight per shard — a tick's own writes are legitimately in flight.

### A rollout is not a thing that happens in one tick

The first version drained all eight shards in a single tick. It "worked" — eight of eight upgraded,
nobody disconnected — and it was not a rollout: the interesting state is the *middle*, where half the
fleet is on the old build, one shard is draining, and players are still trading and travelling across
the seam. It also moved two hundred people at once, which is the admission spike a drain exists to
avoid.

`Rollout` is stepped over ticks now: emptiest shard first, four players a tick, and a shard that will
not empty is **waited on rather than killed**. Doc 27's two assertions about a rollout are separate
here on purpose — a rollout that finished by disconnecting everybody would reach a version spread of
zero.

## The budgets

| | budget | why that one |
|---|---|---|
| conservation violations | 0 | The design exists to pass this. |
| ledger divergences | 0 | The projection checked first, so a disagreement means single-writer broke. |
| cold reads | 0 | A lockout read cold admits somebody to a raid they are saved to. |
| state-shaped guild writes | 0 | A partial roster written back deletes everybody offline. |
| outbox left unsettled | 8 per shard | A tick's own writes are in flight; growth is a leak. |
| replays answered by the outbox | 0 | This fleet never re-posts an operation, so it can only mean a key aged out before its write was durable. |
| disconnected by the rollout | 0 | Every step of a rollout is a drain and never a kill. |
| version spread at the end | 0 | Doc 27's rollout assertion. |
| allocation | 64 KB a tick | Catches the regression `09-NetworkSoak` found: an allocation per player per tick. |
| tick p99 | 2 000 µs | Generous: eight shards on one core of a laptop. It is a regression guard, not a latency claim. |
| memory grown | 32 MB | Held at 13 MB. It was missed at 168 for the three reasons above. |

## See also

- [`Samples/09-NetworkSoak`](../../09-NetworkSoak/README.md) — the same shape for the replication
  pipeline, and the precedent for a soak that reports what it found by exiting non-zero.
- [`docs/plan/27`](../../../docs/plan/27-mmo-framework.md) § Testing — where these numbers come from.
