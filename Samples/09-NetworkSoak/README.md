# 09 — Network soak

Phase 9's soak criterion, measured rather than asserted. A hundred connections, five thousand
entities, and the three budgets the roadmap says have to hold.

```bash
dotnet run -c Release --project Samples/09-NetworkSoak
```

```bash
dotnet run -c Release --project Samples/09-NetworkSoak -- --ticks 54000   # thirty minutes
```

It exits non-zero when a budget is missed, which it currently does. What it found is below.

## What it measures, and what it does not

**The replication pipeline, not the transport.** There are no sockets and no sessions: a hundred
sessions over a hundred transports would mostly measure the transports, which have a conformance
suite each. What is under test is capture, differencing, per-connection baselines and the budget —
the part whose cost grows with connections × entities, and where a regression is invisible until it
is enormous.

**Interest management is a flag, not a constant.** `--interest all` tells every connection about every
entity: five hundred thousand records a tick before anything is encoded, which no amount of
bit-packing rescues. The default gives each connection a slice, which is what any real resolver
produces.

## What it found

Apple M-series, .NET 10, Release. 5 000 entities, 100 connections, 250 observed each, 20 % moving.

| | first run | now |
|---|---|---|
| Allocation per tick | 4 588 KB | **24 KB** |
| Gen0 collections (600 ticks) | 464 | **4** |
| Mean tick | 5 041 µs | 3 876 µs |

**Three allocation bugs, all in code written earlier in this phase, none visible at eight entities.**

1. **The delta memo allocated per value per tick.** `ReplicationServer` cached each encoded difference
   in a dictionary keyed by (value, baseline) and cleared it every capture — so every entry was a
   fresh object every tick. It is one slot on the capture ring now, which is right for the reason the
   table was wrong: connections cluster, so they nearly all ask for a difference from the same
   capture and the second one gets the answer the first paid for.
2. **`ConnectionBaseline` allocated a list per tick per connection.** A hundred connections at 30 Hz
   is three thousand lists a second, opened and dropped a round trip later. Pooled.
3. **`BandwidthLedger` built a string per field per record.** `$"{typeName}.{lane.Name}"` on every
   differenced value — eight strings per record, and most of a megabyte a tick. The names are
   constants; they are composed once per type now.

That is a 190× reduction, and none of it would have been found by reading the code.

## What is still failing, and why it is left failing

| Budget | State |
|---|---|
| memory | ok — the heap settles and stops growing |
| bandwidth | ok — 80 kbit/s a client against 128 |
| allocation | **31 KB a tick against 4 KB** |
| tick time | **worst 63 ms against a 33 ms tick** |

**Bandwidth is fixed, and it was the biggest item.** A record used to be re-sent every tick until it
was acknowledged, so a four-tick round trip sent every change four times. It now waits a round trip
*plus one* before repeating itself — 286 → **80 kbit/s a client**, ten million records down to three.

The plus-one is the part that is easy to lose: an acknowledgement becomes useful when it is folded
in, not when it arrives, and that is the tick after the snapshot which had already been written. Four
ticks measured 137 kbit/s; five measured 80.

**It broke convergence the first time, and the reason is worth keeping.** Cumulative acknowledgement
— folding every pending tick up to the one acknowledged — was only sound *because* every snapshot
repeated everything unacknowledged, so acking a later one proved the earlier. Backoff removes the
repeating, and the moment it does, folding an unacked tick claims a connection holds a value that was
in a packet it never received. It is then never sent again, because the baseline says they have it: a
value stuck for ever rather than for a while, which is exactly how it looked. `Acknowledge` folds
only the tick it was given now.

**Worst-tick is a garbage-collection pause**, not the pipeline: the mean is 3.9 ms against a 33 ms
tick, and there were four Gen0 collections in the whole run. It is real — an 83 ms stall on a game
server is a stall — but it is what is left of the allocation problem rather than a separate one.

**The budgets are left failing rather than adjusted.** The roadmap names the criterion without naming
numbers, so these are figures chosen here; moving them to make the run green would make this file a
decoration. They are what a dedicated server ought to hold to, and three of them do not yet.
