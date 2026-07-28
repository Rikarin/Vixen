# Vixen.Net.Fuzz

Malformed and hostile bytes, pushed into every decoder a peer can reach, with something watching what
happens.

Phase 9's security exit criterion is "packet-reader fuzzing clean" ([docs/plan/16](../../docs/plan/16-networking.md)
§ Testing). This is the harness that makes that a measurement rather than a claim.

## What is fuzzed, and why that list

"The packet reader is fuzzed" is a much smaller statement than it sounds. The reader is the bottom of
the stack; above it sit a handshake that reads four fields from a connection that is nobody yet, a
router that dispatches on a pair of indices, an applier that creates and destroys entities, and a list
that takes an index off the wire and then mutates itself with it. Every one of those is reachable by
anybody who can send a packet, so every one of those is a target.

| Target | What it is | Why it is exposed |
|---|---|---|
| `packet` | `PacketReader`, driven through every read | the bottom of every receive path |
| `bits` | `BitReader`, likewise | what replication, RPC and sync lists actually read |
| `handshake` | `NetworkSession`'s server half | **the most exposed code in the engine** — reached before authentication |
| `client` | `NetworkSession`'s client half | a client believes rather a lot about the peer it dialled |
| `snapshot` | `ReplicationClient.TryApply` | a packet that creates and destroys entities |
| `inspect` | `SnapshotInspector` | aimed at malformed traffic by definition |
| `delta` | `DeltaCodec.TryDecode` | writes into a buffer from widths a packet chose |
| `rpc` | `RpcRouter.Receive` | the one thing a client can make a server do work for |
| `synclist` | `SyncList.Apply` | the only index that arrives from the network |

## The three oracles

Pushing bytes at a decoder proves nothing on its own. What makes this a test is what is measured while
it happens.

**Nothing throws.** The whole never-throws design in `PacketReader`'s remarks exists because an
exception out of a receive path is a denial of service if it unwinds a frame and a crash if it does
not. The harness catches everything and reports the top stack frame, because
"ArgumentOutOfRangeException: specified argument was out of the range of valid values" names no method
and no line and hands you an afternoon.

**Nothing amplifies.** Allocation is measured against an allowance proportional to the input, so a
hundred bytes cannot cost a megabyte. It is summed over a window of cases rather than checked one at a
time, for two reasons that both make a per-case check lie: a list that doubles pays for the next
thousand appends in one, and `GC.GetAllocatedBytesForCurrentThread` settles up a thread's allocation
context when a collection happens, so a case that contains a Gen0 collection reads several kilobytes
high through no fault of its own. Over a window both are noise, and a decoder amplifying every packet
is over the line within a millisecond.

**Nothing is retained.** The oracle an allocation budget cannot be: a packet that costs a hundred bytes
is proportionate and passes every ratio, and a hundred bytes that are never given back is a server that
dies on the second day. Targets that accumulate declare what they are holding and what the bound is.

A fourth thing — that a case finishes quickly — is checked after the fact rather than enforced. A
decoder that can be made to loop for ever hangs the run, which is a legible failure with the offending
frame sitting in the stack trace, and cancelling it would mean running decoders on another thread when
their whole contract is about what they do on the frame's.

## How it finds anything

Uniform random bytes are a bad fuzzer for a codec. A snapshot begins with a 32-bit tick that a client
compares against the last one it applied; random input is refused at the first field about four billion
times out of four billion. So:

- **The seeds are produced by the real encoders.** `ReplicationServer` writes the snapshot seeds,
  `PacketWriter` writes the handshakes, `SyncList` writes its own operations. A change to a wire format
  changes the seeds with it, and a corpus cannot quietly go stale.
- **The mutator is AFL's havoc stage**, with the operations that matter for a binary protocol: bit
  flips, byte nudges (off-by-one on a length field is four bit flips away and one nudge away), splices,
  chunk duplication and deletion, truncation — every prefix of a well-formed packet is what a real
  network produces — and runs of the bytes that mean something. `0x80` is a varint continuation, so a
  run of them is the encoding that walks a reader forward without terminating.
- **Novel behaviour is kept.** There is no instrumentation and therefore no edge coverage. What a
  target returns instead is a cheap number summarising how the decode went — which reads succeeded,
  which counter moved, where it stopped — and an input producing a number nothing before it produced
  is added to the corpus. That is a weaker signal than libFuzzer's and it is deliberately not called
  coverage; it is enough to walk a decoder into its branches, which random bytes will not do.

The whole thing is deterministic. The generator is seeded from the target name, the mutations are a
pure function of it, and the corpus grows in a fixed order — so a failure on a CI runner is reproduced
locally from the seed printed in the message. A fuzzer whose findings cannot be replayed has handed you
a rumour.

## Running it

The gate runs on every build, in `Vixen.Net.Fuzz.Tests` — roughly nine million cases in nine seconds,
bounded by **case count rather than by the clock**, because a run bounded by time executes a different
number of cases on a loaded machine than on a laptop and a green build then proves nothing in
particular.

For a longer run, give it seconds instead:

```bash
VIXEN_FUZZ_SECONDS=600 dotnet test Core/Vixen.Net.Fuzz.Tests -c Release
```

## What it found

Four defects on the first run, all in code that had tests and review and none of which either had
caught.

- **`PacketReader` threw on a length above `int.MaxValue`.** Two mistakes deep, which is why it took a
  fuzzer: a blob's length is a `uint` and its cap is an `int`, so the comparison is unsigned and a
  *negative* cap is a cap above every length there is. The length then reaches the bounds check as
  `(int)length`, which for anything above `int.MaxValue` is negative, sails past `count > Remaining`,
  and throws out of `Span.Slice`. Fixed at the single choke point where bytes are taken, so the
  invariant is in one place rather than at four call sites and missing from a fifth.
- **`TickManager` threw `OverflowException` on one tick value in four billion.** A tick error is a
  modular distance and therefore takes every value an `int` can hold, including `int.MinValue` — the
  one value `Math.Abs` throws on. The tick arrives straight off the wire in a `Pong` and in a
  `ConnectAccepted`. One packet, one crash, on the frame's own thread.
- **A client kept a player record per `ConnectAccepted`.** Measured at fifty kilobytes from a
  thirty-two byte packet. A server of ours sends exactly one; a peer that sends more costs the client a
  record and two dictionary entries per packet, for any id it cares to invent. Now ignored, which is
  the mirror of the rule the server half already kept for a second handshake on a live connection.
- **A client kept its player record after the connection was lost.** The older of the two and not
  hostile at all: a pure client's player list is exactly itself, but losing the connection only cleared
  `LocalPlayer`. Every reconnect added another record that nothing would ever look up again.

Each is pinned by a named test next to the code it broke — `Vixen.Net.Tests` — rather than only by a
corpus file, because two of them need a *sequence* to reproduce and a corpus entry is one input.

## The corpus on disk

`Vixen.Net.Fuzz.Tests/Corpus/<target>/<fingerprint>.bin` holds inputs that have broken something. They
are replayed before every run, so a defect found once is a test from then on — the difference between
fuzzing and having fuzzed. The name is an FNV-1a of the bytes rather than a hash code, so two machines
that find the same input write the same file and the second one does not add a duplicate to the review.

The *grown* corpus is deliberately not persisted: it would be a large binary directory whose contents
depend on the machine that produced it, and the seeds plus a fixed generator seed reproduce a run
without it.

## Owed

- **`SharpFuzz` and a nightly with real coverage.** [docs/plan/12](../../docs/plan/12-build-ci-and-testing.md)
  § Test infrastructure asks for `SharpFuzz` over the parsers, and the packet reader belongs in that
  job. What is here is a behaviour signature rather than edge coverage and says so; libFuzzer with
  instrumentation would find in an hour what this finds in a week. The targets are already the right
  shape for it — each is `(ReadOnlySpan<byte>) -> outcome` — so the wrapper is a few lines once the
  nightly infrastructure exists. This runs on every build, which that never will.
- **The transports themselves.** `Udp`'s reliability layer reassembles fragments and tracks
  acknowledgement windows from bytes off the wire, and `WebSocket` parses RFC 6455 frames. Both are
  more exposed than anything in this list — they are *below* the handshake — and both want a target of
  their own.
- **Structure-aware mutation.** The mutator does not know a snapshot from a handshake. A mutator that
  understood the record format could keep the tick and break the payload, rather than spending most of
  its budget on inputs the first field refuses.
