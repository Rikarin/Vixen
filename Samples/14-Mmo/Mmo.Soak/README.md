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
| tick p99 | 76 µs |
| allocation | 34.6 KB a tick |
| **memory grown** | **168 MB** |

### The idempotency-key set grows without bound

Eight shards holding five hundred players are a fixed working set, so a settled fleet should not
grow — and this one grows by roughly a megabyte a minute, for ever.

It is `MemoryEconomyLedger`'s idempotency guard. Every posted intent adds `(player, kind, operation)`
to a set, nothing ever removes one, and the run finishes holding **199 379 keys**. A shard that runs
for a week keeps every key of that week.

⚠ **The guard cannot simply be cleared.** It is what makes doc 27's rule true — *"a retried trade, a
retried mail claim, a retried auction settlement writes nothing the second time"* — and it is what
makes the auction, the trade and the mail safe. What it needs is a **horizon**, and how long that
horizon is is a safety-critical number rather than a tuning knob: a retry arriving after it is no
longer recognised as a replay and is applied again. Task **#43**.

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
| disconnected by the rollout | 0 | Every step of a rollout is a drain and never a kill. |
| version spread at the end | 0 | Doc 27's rollout assertion. |
| allocation | 64 KB a tick | Catches the regression `09-NetworkSoak` found: an allocation per player per tick. |
| tick p99 | 2 000 µs | Generous: eight shards on one core of a laptop. It is a regression guard, not a latency claim. |
| memory grown | 32 MB | **Currently missed at 168 MB.** See above. |

## See also

- [`Samples/09-NetworkSoak`](../../09-NetworkSoak/README.md) — the same shape for the replication
  pipeline, and the precedent for a soak that reports what it found by exiting non-zero.
- [`docs/plan/27`](../../../docs/plan/27-mmo-framework.md) § Testing — where these numbers come from.
