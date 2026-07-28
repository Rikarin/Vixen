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
| allocation | **24 KB a tick against 4 KB** |
| tick time | **worst 83 ms against a 33 ms tick** |
| bandwidth | **286 kbit/s a client against 128** |

**Bandwidth is a design gap, not a tuning problem.** A record is re-sent every tick until it is
acknowledged, so with a four-tick round trip each change goes out four times. The server should not
re-send a record whose previous send could still be in flight — that is retransmission backoff, it is
what TCP does, and it is not built.

It has been *prototyped*, which is how the size of the prize is known rather than guessed:
suppressing a re-send of the same value within a round trip plus one took this run from **286 to 80
kbit/s a client**, a 3.6× saving, and ten million records to three. It is not in the tree, because
the same prototype stopped `Samples/08` converging under packet loss — a client stuck permanently on
an old value rather than slowly, since nine hundred settle ticks did not clear it. Something about
suppression interacts with the acknowledged baseline in a way that is not yet understood, and a 3.6×
saving is not worth a desync. The measurement stands; the mechanism needs its own sitting.

**Worst-tick is a garbage-collection pause**, not the pipeline: the mean is 3.9 ms against a 33 ms
tick, and there were four Gen0 collections in the whole run. It is real — an 83 ms stall on a game
server is a stall — but it is what is left of the allocation problem rather than a separate one.

**The budgets are left failing rather than adjusted.** The roadmap names the criterion without naming
numbers, so these are figures chosen here; moving them to make the run green would make this file a
decoration. They are what a dedicated server ought to hold to, and three of them do not yet.
