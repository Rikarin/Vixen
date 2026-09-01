# Vixen.Fuzz

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
| `input` | `InputBuffer.TryReceive` | the other thing a client can make a server do work for, every tick |
| `udp` | `UdpTransport.Poll` | the code an attacker reaches *first* — below the handshake, on a public port |
| `upgrade` | `WebSocketUpgrade` | HTTP headers from a stranger, parsed before anything authenticates |
| `bundle` | `BundleOdbBackend`, opened with the checksum on | a file a content update downloaded |
| `chunk` | `ChunkFormat.Unpack` and the header behind it | a declared length that is what gets allocated |
| `heightmap` | `TerrainHeightmapPng.Decode` | a file somebody was handed and dropped on an importer |
| `meta` | `AssetMetaFile.Read` **and** `MetaScanner.TryScan`, compared | committed text merged and hand-edited by people |
| `stylevalue` | `StyleValueParser.Parse` | a declaration value, or a `var()` substitution ExCSS never saw |
| `layerrule` | `LayerRuleParser.TryParse` | a hand-written brace matcher over text a library gave up on |
| `vxml` | VXML parsed, printed, and reparsed incrementally against a full parse | a language, mutated a syntax node at a time |
| `raven` | Raven parsed, reparsed, bound, lowered, emitted, and the module handed to `spirv-val` | the whole compiler, and the only oracle here that is not marking its own homework |

**Three of these are files rather than packets, and the machinery never required one.** A target is a
decoder with bytes pushed into it; a bundle, a stored chunk and a heightmap PNG each have a length
prefix that decides an allocation, which is the only property this harness has ever cared about. They
are also why this is no longer called `Vixen.Net.Fuzz` — see **Naming**, below.

**And three take text rather than bytes, which also needed nothing new.** A `.meta` sidecar, a
declaration value and an `@layer` rule are characters; the corpus, the mutator and all four oracles
never learn that, because each target decodes at its own edge — which is what the real system does with
a file too. That was worth establishing on grammars this shallow *before* anything was built for the
deep ones: if a text target had turned out awkward, better to find out on an `@layer` prelude than
after a seam had been designed around it. The one constraint it does impose is worth writing down: a
UTF-8 decode never produces a **lone surrogate**, so that one shape is unreachable from the mutator
even though a C# string literal hands it to these parsers directly.

**Two of them compare two readers rather than watching one.** `meta` runs `MetaScanner`'s fast line
scan and `AssetMetaFile`'s full parse over the same input and requires the envelopes to agree;
`layerrule` requires the reader to reach a fixed point — print what it read, read that, get the same
rule. Neither is visible to the four oracles, because a *wrong* answer throws nothing, allocates
nothing and retains nothing. `TransportTargets` had the first of these, asserting that chunked reads
agree with whole reads.

**They also catch their own refusal, where the packet targets catch nothing.** A `Try…` method returns
false, so "nothing escapes" is checked by catching everything and finding nothing. A content format
refuses by *throwing*, and that is its contract — so each of these three catches exactly the type its
layer documents (`SerializationException`, `ArgumentException`) and lets everything else reach the
oracle. That is the stronger assertion: an `ArgumentOutOfRangeException` from a slice, an
`OutOfMemoryException` from a length nobody checked, a `ZLibException` out of an inflater are all
findings, and each was something one of these decoders actually did.

## Naming

**This was `Vixen.Net.Fuzz` until twenty targets made that name a lie.** Twelve of them are
`Vixen.Net` decoders and eight are not: a bundle, a stored chunk and a heightmap PNG; a `.meta`
sidecar, a declaration value and an `@layer` rule; VXML; and the whole Raven compiler. Nothing in
`FuzzSession`, `Mutator`, `Corpus`, `IFuzzTarget` or `IFuzzDomain` is network-specific — the harness is
a mutation loop, a set of oracles and a behaviour signature, and it took the first content format
without a line of change. `Vixen.Fuzz` is what that is called, and it stays in `Core` where it already
was.

⚠ **One thing the rename changed that a search-and-replace does not show.** Four files reached
`ConnectionId`, `Channel` and `DisconnectReason` without naming `Vixen.Net` in a `using`, because a
file in namespace `Vixen.Net.Fuzz` has `Vixen.Net` in scope by enclosure. `Vixen.Fuzz` does not, so
those files carry the `using` explicitly now. Nothing else in the harness depended on the old name for
anything but its own namespace.

## The four oracles

Pushing bytes at a decoder proves nothing on its own. What makes this a test is what is measured while
it happens.

**They are statements about behaviour, not about input shape**, which is why the structure-aware seam
below could be added without touching any of them — and is the entire reason to have grown this
harness rather than adopted `SharpFuzz`.

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

**Nothing takes long — where "long" means *reproducibly* long, and that word is the whole of this
oracle.** A case over `CaseBudget` (2 s) is not a finding yet. It is re-run up to
`CaseBudgetConfirmations` (4) more times and judged on the **cheapest** reading, and only a case that
stays over the budget on every one of the five is reported. `FuzzOutcome.Acquitted` counts the ones
that did not; it is on the summary line, and ⚠ **also on stderr**, because a test runner shows a
*passing* test's output to nobody and this number is only interesting on the runs that pass. Silence
means the budget was never tripped. A line means the host was thrashing, and says how badly.

⚠ **The other three oracles measure the decode; this one measures the decode plus everything else the
machine was doing, and on a shared runner the second term is the larger one.** One Windows CI run
(`33038897895`) reported six targets over the budget in a single job — `upgrade` 6,223.6 ms,
`rpc` 5,277.0 ms, `bits` 4,187.5 ms, `stylevalue` 3,909.7 ms, `client` 2,395.7 ms, `packet`
2,059.2 ms. Replayed from the printed seeds on an idle machine, those six inputs cost **0.056 µs,
0.099 µs, 0.115 µs, 0.473 µs, 2.8 µs and 21.2 µs** a case: five to seven orders of magnitude under
what was billed. One of them is **four bytes long**, and nothing is superlinear on four bytes.

**Three runs of the same seed on the same machine then nominated three different inputs as the run's
slowest** — 106.8 ms on 36 B, 14.8 ms on 166 B, 5.1 ms on 300 B. That is the argument in one line: in
this tail the reading is not a function of the input, so a single reading is not evidence about the
input and no threshold can make it one.

The same thing had already happened with a different cast. Run `32897000404`, two days earlier,
accused `upgrade`, `stylevalue`, `meta`, `chunk` and `input` — the last of those **eight bytes** long,
billed 2,380.5 ms. Every target here runs a deterministic case stream from a fixed seed, so two runs
see the same inputs in the same order; the two runs accused almost disjoint sets of them.

**Which is why the answer is not a bigger number.** Measured across all twenty targets and 12.6 M
cases on an idle machine, the worst honest reading is 306 ms (`raven`, which compiles a shader per
case) and every other target stays under 43 ms — while CI's stalls reach 6.2 s and nothing bounds them
there. Any threshold above the noise is one that can no longer see a real blowup. Asking the input
again separates the two populations without moving the line at all: a descheduled thread or a
collector pause does not recur on demand, and a decoder that has genuinely gone quadratic costs the
same seconds every time. Timing noise is one-sided — the machine can only ever make a case look
*slower* — so the minimum is the honest estimator.

It costs the healthy path nothing, because it runs only for a case already over the budget; on a quiet
machine a run is identical to what it was before. ⚠ It also **narrows the property on purpose**: an
input expensive only the *first* time it is seen is now acquitted. `TookTooLong` exists to catch a
decode slow enough to be a weapon, and a cost an attacker cannot make a server pay twice is not one.

**What a genuine blowup looks like now.** A finding that reads
`TookTooLong on 194 bytes (a9e47…) — 2,059.2 ms on 194 B of input, the cheapest of 5 readings — the
first was 2,190.4 ms`. Five independent readings of the same input all over the budget is a property
of the input, and it reproduces from the seed on any machine. `CaseBudgetConfirmations = 0` restores
the old one-reading behaviour exactly, which is how `CaseBudgetTests` shows the same stall failing
without it and passing with it — a budget that can never fire is worse than one that fires when it
should not, and the only way to tell those apart is to run the same target both ways.

## The fifth oracle, which is the only one watched from outside the call

⚠ **All four of the above are computed after `IFuzzTarget.Run` returns, and for a whole class of input
it never does.** A case that loops, or that grows the heap without bound, is never measured by any of
them: the second reading is never taken, no finding is recorded, no result is written, and the run ends
when the operating system decides the machine has had enough. That is not a hypothetical. It happened —
a developer's Mac died of memory pressure with nothing on disk to say which input had done it, which is
the same failure `Corpus.MaxEntries` records one layer down. There it was the *corpus* that was
unbounded; here it is the *case*.

`CaseGuard` watches the case that is running now, from a second thread, and reports
`FuzzFailure.RanAway` against three ceilings:

| Ceiling | Read as | Default | What it is for |
|---|---|---|---|
| wall clock | the run's own `Stopwatch` | 30 s | a case that is not coming back |
| allocation | `GC.GetTotalAllocatedBytes(false)` | 1 GiB | churn: a case allocating in a loop |
| retention | `GC.GetTotalMemory(false)` | 512 MiB | the heap growing, which is what kills a host |

⚠ **The guard's wall clock is the one reading in the harness that cannot be confirmed**, because the
case it is about has not returned and cannot be asked again. It is left at 30 s and left coarse for
that reason: the CI stalls that broke the post-hoc budget top out at 6.2 s, so the ceiling sits five
times clear of the largest one measured — but that is a margin rather than a proof, and a runner that
descheduled a thread for half a minute would abandon a run over nothing. Nothing has been seen doing
that; if it ever is, the fix is the same shape as the budget's, not a bigger ceiling.

**The two allocation figures are two different questions and only one of them describes a dying
machine.** A loop that allocates a kilobyte and drops it never grows the heap, because the collector
keeps up; it costs a core, not the host. A loop that *keeps* what it allocates grows the heap until
there is none left. Churn is a performance finding, retention is the emergency, and the guard treats
them differently for exactly that reason.

**`GC.GetAllocatedBytesForCurrentThread` — the counter the `Allocated` oracle uses — is thread-local
and therefore unreadable by a watchdog**, and making the worker poll its own counter puts the check
back inside the call that never returns. The two process-wide counters are what a second thread can
actually see. They are coarse and they include whatever else the process is doing; that is paid for
with ceilings orders of magnitude above anything healthy, and by requiring a breach to persist across
consecutive samples of the *same* case.

**It costs the healthy path nothing measurable, which was the constraint.** Per case the worker
publishes two release stores, a `long` it had already read off the clock and a reference it already
held. Every measurement — the clock, both counters — is taken on the watchdog's thread. A case that
finishes inside one 16 ms poll is never sampled at all, which is the entire healthy population; the
baselines are taken at the first sample rather than at the call, so the window measured is a subset of
the case and the oracle rounds towards saying nothing.

### What survives a runaway, and what does not

**.NET cannot safely abort a thread**, and a case wedged inside a decoder stays wedged — so the list of
what this buys is short and worth reading rather than assuming. What the guard can do:

- **name the input**, with its length, its fingerprint and the ceiling it went past;
- **write the bytes to disk** — from the watchdog, while the case is still running, because the caller
  that normally writes findings out of a `FuzzOutcome` may never get one;
- **print the same thing to stderr**, for the same reason;
- **stop the run scheduling anything else**, and fail it, if the case ever does return.

What it cannot do is reclaim the thread. A case that keeps growing goes on growing. So for the one
breach that cannot be outlived — retention, over sixteen consecutive samples, while the same case is
still in flight — the guard calls `Environment.FailFast` after the input is on disk. That is not a
recovery and is not dressed up as one: it is the same ending the OOM killer was going to impose, taken
sixteen samples earlier, deliberately, with a culprit named. `FailFast` rather than `Environment.Exit`
because exit waits for foreground threads and one of them is by definition the thread that will not
stop. Set `FuzzSession.AbandonProcessOnRunaway = false` to have the hang instead, which is what a
debugger wants and what the tests below use.

The honest summary: **a machine that dies with a named input on disk is categorically better than one
that dies silently**, and that — not survival — is what this buys.

**And one runaway it cannot report at all: a stack overflow.** The CLR takes the process down at the
overflow, in the time it takes to touch a guard page — no exception, no handler, no finally, nothing
scheduled on any other thread. A watchdog that samples every 16 ms is several thousand samples too
late, and there is no version of it that is not. The second `raven` defect below is exactly that shape,
and the only mechanism that would catch it is running each case in a **child process**, which is what
`SharpFuzz` over libFuzzer does and is already in **Owed** for a different reason. Until then, a deep
recursion in a compiler is a defect this harness can provoke and cannot name.

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
  which counter moved *this case*, where it stopped — and an input producing a number nothing before
  it produced is added to the corpus. That is a weaker signal than libFuzzer's and it is deliberately
  not called coverage; it is enough to walk a decoder into its branches, which random bytes will not
  do.

  **The signature must be about the case, not the run, and getting that wrong is silent.** A
  decoder's counters are lifetime totals, so a signature folded from them strictly increases and
  *every* case looks like a behaviour never seen before — which is not a fuzzer with excellent
  coverage, it is a fuzzer with no guidance and a corpus that keeps everything. Four of these targets
  did exactly that until the ratio was printed and looked at: `rpc` kept 1,027,530 inputs out of
  1,027,508 cases. It now keeps 538 out of 1,500,000, and still finds the same defects — reverting the
  `TickManager` fix, it caught the overflow again from a 45-entry corpus in a 27-byte input.

  The corpus and the signature set are both capped regardless, because a working set that grows
  without bound is a memory leak wearing a hat. Past `MaxSignatures` the guidance has demonstrated
  that nearly everything looks new to it, so it is switched off rather than paid for — which is what
  `packet` and `bits` do, and the printed ratio is where you can see it.

## Structure-aware inputs

**Havoc is the right tool for a decoder and the wrong one for a compiler.** The mutator is aimed at
length prefixes and varints and is very good at those. Pointed at a *language* it spends effectively
all of its budget on text that does not lex, and a shader that fails at its first token has exercised
the tokeniser and nothing behind it — so the binder, the type checker and the backend, which is where a
compiler's defects live, are never reached.

So a target may declare an `IFuzzDomain`: how to read a corpus entry into a value, how to change one,
and how to write one back out. A grammar's version of havoc is replacing a subtree with another of the
same kind, duplicating one, deleting an optional one, grafting one in from a second corpus entry, or
swapping an operator for one the grammar also allows there — each producing something that lexes and
mostly parses, and therefore something that reaches the passes behind the front end.

Three things about it are load-bearing:

- **The four oracles did not change, and that was the constraint rather than the outcome.** They are
  statements about behaviour — nothing threw, nothing amplified, nothing was retained, nothing hung —
  and none of them asks what an input *is*. `IFuzzTarget.Run` still takes a `ReadOnlySpan<byte>`,
  `FuzzSession` still measures around that one call, and a finding still carries the exact bytes. A
  design that had made the oracles care about trees would have thrown away the only reason to grow this
  harness instead of adopting `SharpFuzz`.
- **The corpus format stays bytes, and for a language that costs nothing.** A tree's serialization is
  its source text, which is what a corpus file should hold anyway: readable in a diff, committable as a
  regression, and something a person reproducing a finding can hand to the real compiler. The price is
  a parse per case on the way in and another inside `Run`.
- **Garbage is still generated, and leaving it out is the mistake this is most likely to make.** A tree
  mutator only ever emits text the printer produced, so an unterminated string, a stray byte and a
  nesting depth that runs the parser out of stack stop being reached the moment structured generation
  *replaces* byte havoc rather than joining it. One mutation in `FuzzDomain.GarbageIn` is havoc over
  the serialized form. It is also what keeps a committed regression useful: a crasher found by havoc
  usually is not a tree, so it fails `TryRead`, and without the blend it would be replayed once at
  start-up and never mutated again.

### The validity oracle, and why it is the one worth having

Every other oracle in this harness compares two things Vixen wrote — a parse against its own printer,
an incremental reparse against a full one, a fast scanner against a slow parser. `raven` ends by
handing the emitted module to **`spirv-val`**, which is the only check here that asks somebody else
whether the answer is right.

That is what makes it able to catch a backend emitting something *valid and wrong*. An implicit-LOD
sample in a compute entry point had been silently substituting level zero since July: nothing threw,
nothing was reported, and every golden file matched because they were regenerated from the same
emitter. No crash-finder finds that, and neither does a snapshot test.

Two details are load-bearing. The **target environment is read from the module's own header word**, not
hardcoded — a ray-query module is emitted as SPIR-V 1.4 and validating it as `vulkan1.0` reports the
version rather than the contents, which is a green run that checked nothing. And the module goes down
a **pipe** rather than through a temporary file, which is faster, leaves nothing behind when a case is
killed, and keeps the harness off the host filesystem.

It runs only when the compilation had nothing to report, which is the rarest path in the run — and the
rarity is the point rather than a limitation. A mutant that still compiles cleanly is one edit away
from a shader somebody wrote, which is exactly the population where an emitter quietly substitutes
something, because it is the population the emitter has a path for. A mutant that does not compile
tells the backend nothing it did not already know.

Absence of the validator is **not** a silent skip: `TheSpirvValidatorIsInstalled` fails, for the same
reason `SpirvBackendTests` has that test. CI installs `spirv-tools` on both legs.

**Its first run found two, and they are the reason to have written it.** Two one-token edits of
`Example2.rvn` compiled with *no diagnostic at all* and emitted modules a driver would reject:

- `[Permutation] val UseSoftKnee: bool = true` → `[D] …`. The unknown attribute was accepted in
  silence, so the value stopped being a permutation key and became an ordinary uniform member — and
  SPIR-V forbids `OpTypeBool` in an externally-visible storage class.
- `val over = max(value - threshold, 0f)` → `val over = Vixen(1, 1, 1, 1)`. Calling a *package* was
  accepted, the `val` bound to a void-typed expression, and the emitter materialised
  `OpConstantNull` of `void`.

Both are exactly the shape the oracle exists for — a compile that looks entirely successful and an
output that is not a program. Neither was reachable by anything else here: nothing threw, nothing
amplified, the round-trip held and the reparse agreed.

Both are fixed in the front end, which is where each of them belonged. A binding cannot contain a
boolean (`RVN2137`), an unrecognised attribute is named rather than dropped (`RVN2138`), and a
namespace cannot be called (`RVN2030` — the guard that suppresses a cascade from an already-reported
callee used to swallow it, because a namespace answers `ErrorTypeSymbol` when asked for a type it
does not have). The SPIR-V emitter refuses `OpConstantNull` of `void` as well, since `void` is the
one type with no null value however that request is reached.

The two inputs are committed under `Corpus/raven` and replay on every build. There is no switch to
turn the oracle on: an oracle with an off position is an oracle somebody turns off.

**And a third from the nightly, which is the clearest statement of what this oracle is for: a rule the
whole compiler was written against and nothing enforced.** One token of `Example2.rvn` —
`func Weight(id: uint3): float => float(id.x) * scale.x` becoming `=> Weight(id) * scale.x` — compiled
with no diagnostic at all and emitted a module `spirv-val` refuses with
`[VUID-StandaloneSpirv-None-04634] Entry points may not have a call graph with cycles`. **Raven has
never had recursion and had never said so.** Four places behind the binder carry a visited set with a
comment explaining that the language has none — `CallGraph.InCallOrder`, `Lowerer.CollectStreamUses`,
`LibraryBuilder`'s propagation loop — so every one of them terminated and none of them reported
anything. ⚠ **It is not `RVN2005` and no existing guard could have caught it**: both signatures are
complete before either body is bound, so nothing is ever re-entered and resolution has no opinion.
Fixed in the binder (`RVN2139`), naming the route rather than the function, over the same
`(member, body kind)` nodes the lowerer keys its function table on — so a cycle through a property
accessor or a constructor is the same defect and not a second one. `raven/b3f413d871e6a766.bin`.
#### Then a full nightly, and eleven more that were four defects and a fifth underneath

Runs `31049261231` and `31075211542` produced identical hashes over ~91 M cases, so these were stable
rather than flakes. `spirv-val` sorted them into four complaints, which looked like one fault stated
four ways and was not. **Three of the four were the front end and one was the backend**, and the way
to tell them apart was to ask a question the validator's message does not answer: *did anything
before `spirv-val` know?*

- **`OpCompositeConstruct` whose constituents are the wrong component type** — seven findings, and
  the only class where nothing upstream knew. `EmitConvert` sent a `Splat` conversion straight to the
  broadcast, so an `int` widened to a `float3` became
  `OpCompositeConstruct %v3float %int_0 %int_0 %int_0`. The IR is not at fault and the binder is not:
  `float3 * 0` and an `int` argument to a `float2` parameter are ordinary implicit conversions, and
  `GlslEmitter` writes both halves in one token — `float3(i)` converts *and* widens. SPIR-V has to
  spell it twice and spelled it once. Six of the seven are this; the seventh was a `void` from the
  bullet below.
- **`OpFMul %void` and `OpIMul %void`** — two findings, and the pair that named the fault. Opposite
  errors from the same expression means the opcode was read off the operands and the result type off
  something that disagreed, and what disagreed was `min.y`: a member taken of a *method group*, which
  is the one receiver typed as an error without an error having been reported. Same shape as the
  namespace above, one guard along. `RVN2011` now.
- **`OpStore` of an `i32` through an `f32` pointer** — one finding. `for (i in 3f .. 4)`. `BindRange`
  found the ends' common type and then converted neither of them, so the loop variable was `float`
  and the limit it was compared against stayed `int`. `BindConditional`, ten lines above it, had done
  this correctly all along.
- **`OpCompositeConstruct %void`** — one finding. `[]` in expression-statement position. Every
  position that asks what an empty collection literal *is* already rejected it; the survivor was the
  one that does not ask. `RVN2140` now.

⚠ **A fix can uncover the next one, and one did.** With the splat emitted correctly,
`5cc192ddcce49da6` stopped failing on its constituents and started failing on
`VUID-StandaloneSpirv-Flat-04744`: `func PSMain(uv: int)` declared an undecorated integer fragment
input, because `Flat` was applied where the `stream var`s are declared and not where an entry point's
own parameters are. The GLSL backend had the identical hole and no oracle watching for it. Both are
fixed at the one place every stage variable passes through.

All eleven inputs are in `Corpus/raven`. The two the same runs found that are **not** in this list —
a parse-level input and an entry point whose call graph has a cycle — are somebody else's, and are
deliberately not committed: promotion follows the fix.

#### And a twelfth, five nightlies old before anybody looked, which is a statement about the search

`saturate(dot(sampled.rgb, tint.rgb))` in `Example1.rvn` became `saturate(dot(1, 0f))` — a call the
language *declares*: `Intrinsics` builds `dot` over `float` as well as `float2`–`float4`, in the same
loop as `length`, `distance` and `normalize`, and those four are GLSL.std.450 instructions that take
a scalar happily. `OpDot` is core, requires "a vector of floating-point type" on both operands, and
the emitter reached for it anyway — `Expected float vector as operand: Dot operand index 2`. A dot
product of one lane is the product, which is what GLSL's own `dot` means for a `float` and what
glslang emits for it, so the fix is `OpFMul` in the SPIR-V emitter and nothing in the front end: the
GLSL backend writes `dot(a, b)` for this and is right to. `raven/3a9c4beb4aea379c.bin`.

Nothing shipped was affected — every `dot` in `Raven/Library` is on a vector, and all 66 committed
`.spv` modules validate clean — and the defect had been there since the emitter was written in July.

⚠ **The interesting half is why it appeared on 2026-08-27 and not before.** The night before had run
**26.8 M cases clean**; every night after ran **13–14 M and found it**, at the same fixed seed, from
the same committed corpus. A shorter run found what a run twice its length had not, so the *stream*
changed rather than its depth. It changes whenever the compiler's *reports* change: `Corpus.Offer`
keeps an input whose signature is new, the signature is built from the diagnostics, and the mutator
draws its parents from what was kept. Two new diagnostics landed in that window, and `RVN2054` is the
one that bites this population — three of the twenty fragments `SyntaxDomain` builds a file from are
package-level members (`func G…`, `val k…`, `var v…`), those files used to bind clean and silent, and
one case in sixteen is built from fragments.

So: **a fuzz finding's first appearance dates the search, not the defect.** Bisecting the onset of a
`raven` finding to a compiler change finds the commit that re-signed the corpus, which is almost never
the commit that introduced the fault.

### And guidance, which such a target should turn off

`IFuzzTarget.NoveltyGuides` is true for a decoder and false for a compiler, and the difference is the
size of the behaviour space rather than a preference. A packet reader has a few dozen outcomes, so
"this input did something new" is a strong signal and a corpus selected on it is a set of
representatives. A compiler has a behaviour for every combination of declarations, types and
diagnostics there is: nearly everything looks new, the signature table saturates in seconds, and what
it selected before saturating was whatever the first few thousand cases happened to be.

Declaring it false is **accepting unguided but structured generation**, which is a position rather than
a shortfall. The guidance existed to walk a decoder into branches random bytes never reach; a
grammar-aware domain reaches them by construction instead.

What it buys in exchange is a fix to something that was simply wrong. Saturation used to stop two
things at once: the signature table growing, which is the memory bound it exists for, and *the corpus
growing at all*, which nothing wanted — so a run that saturates in its first second spends the rest of
an hour mutating whatever the first few thousand cases left behind. Past saturation a target that
declared it keeps one input in `Corpus.Sample` regardless of what it did, and the pool goes on turning
over. A target that did *not* declare it still freezes, which is the conservative answer: a decoder
whose signature cannot saturate has a signature that is wrong, and the finding is that.

The whole thing is deterministic. The generator is seeded from the target name, the mutations are a
pure function of it, and the corpus grows in a fixed order — so a failure on a CI runner is reproduced
locally from the seed printed in the message. A fuzzer whose findings cannot be replayed has handed you
a rumour.

## Running it

The gate runs on every build, in `Vixen.Fuzz.Tests` — twelve million cases in about eighteen seconds,
bounded by **case count rather than by the clock**, because a run bounded by time executes a different
number of cases on a loaded machine than on a laptop and a green build then proves nothing in
particular.

**The rows are generated from the registry, not written out.** Three targets were once written,
registered and named, and were simply not among the theory's `[InlineData]` rows — so they existed,
passed the test that checks the names match the constructors, and never ran. A target that exists is
now a target the gate runs; forgetting to give it a case budget fails a test rather than making it
disappear.

For a longer run, give it seconds instead — and, if you want one target rather than twenty, name it:

```bash
VIXEN_FUZZ_SECONDS=600 dotnet test Core/Vixen.Fuzz.Tests -c Release
VIXEN_FUZZ_SECONDS=7200 VIXEN_FUZZ_ONLY=raven dotnet test Core/Vixen.Fuzz.Tests -c Release
```

That is what `.github/workflows/nightly.yml` does at three in the morning, one target per job — the
same harness, the same seeds, the same generator, given between five minutes and two hours rather than
the second or two the gate spends on it. `VIXEN_FUZZ_ONLY` is how a job takes its own target: the other
nineteen rows skip themselves with a reason, which is visible in the results where a `--filter`'d row
would simply be absent. Anything found is written to `artifacts/fuzz-findings` and uploaded under the
target's name, because a finding whose bytes only exist in an assertion message is one somebody has to
retype.

**The numbers above are measured rather than carried forward**, in an isolated Release run: twenty
targets, **12,091,500 cases in about eighteen seconds**. They had been wrong twice — a per-target
budget grows the total, and a target that costs milliseconds a case moves the clock much further than
it moves the count. Five of these twenty are grammars, and they are half the wall time on a tenth of
the cases. Anywhere else the figure appears — `nightly.yml`, `docs/overview.md`,
[docs/plan/14](../../docs/plan/14-roadmap.md) — is a copy of this one and goes stale with it.

### ⚠ `raven` had never had a clean time-bounded run, and now the reason is known

**Nobody had seen this target finish a `VIXEN_FUZZ_SECONDS` run.** One went past 600 s and was killed
without a diagnosis. The shape of that said the cause was an *input* rather than general slowness —
40,000 cases finish in 37 s and a 50 KB shader compiles in 31 ms, neither of which leaves room for a
run that does not end.

It was `var t{[`, above: a parse that never returns, reachable at roughly a quarter of a million cases
and therefore past every run the gate has ever done. The first time-bounded run under the guard named
it in nine minutes, wrote it out, and ended the process deliberately at 678 MB instead of being killed
at whatever the host gave up at.

With that fixed, the next 600 s run got four minutes further and died of the stack overflow above — a
second defect the first had been standing in front of, and one the guard cannot report. So there were
**two reasons a time-bounded run ended early**, and for two nightlies `nightly.yml` set
`VIXEN_FUZZ_SKIP: raven` while the second was open.

**Both are fixed, and `raven` is back in the nightly.** The overflow was
`func F(): float[F()]` — a signature that reaches its own type through an array size, which sent the
binder round `ResolveReturnType → BindType → BindArraySize → BindValue → BindInvocation` and back
until the stack ended. The binder now reports `RVN2005` and names the symbol, the input is
`Corpus/raven/70ae34e20b4880ee.bin`, and `VIXEN_FUZZ_SKIP` is empty.

⚠ **What the skip was for is worth keeping even though the skip is gone.** It was never quarantine
for a target that fails; it was for the one failure that produces *no artifacts*. The CLR ends the
process at the overflow with no thread left to write a finding and no sample early enough to have
taken one, so a night spent on `raven` cost the other nineteen targets their results and left nothing
behind explaining why. That is the only bar a target has to clear to be in here: its runaways have to
be recordable. A target that merely fails is a finding, and the artifacts are how it gets read.

**And the bar is lower now than it was, because the nightly is one job per target.** What made an
unrecordable death expensive was that it was one process running twenty targets; the cost of the same
death today is one job's results, and the other nineteen upload theirs. `VIXEN_FUZZ_SKIP` is the hand
override rather than the mechanism — see *The nightly's budgets*, below.

⚠ **A skip rather than a deleted row, when one is needed.** Filtering a target off the command line
would be one that quietly stops running, which is the silence the generated theory rows were
introduced to end. `VIXEN_FUZZ_SKIP` puts it in the results as a skip with a reason attached, so a
nightly that has stopped fuzzing the compiler says so on its own face. `NothingEscapes` honours it
*only* when the run is bounded by the clock — the per-build gate is bounded by cases, finishes what it
starts, and was unaffected throughout.

Two things follow that the episode did not settle:

- **A time-bounded run is the mode that finds this class and the mode that cannot survive it.** The
  fix here bounded one recursion; it did not make the harness able to record the next one. That is
  still the reason out-of-process execution is owed — with a case in a child process, an overflow is a
  finding with bytes attached rather than a night with nothing in it.
- **The gate's fifteen hundred cases are not a search and were never meant to be.** Two of the `raven`
  findings needed forty thousand cases and the parser hang needed six times that. Depth is the
  nightly's; what the gate owes is that the pipeline still runs.

### The nightly's budgets, and why there is no arithmetic left

**The nightly was one job that ran all twenty targets in series, and it is now one job per target.**
`nightly.yml` builds a `strategy: matrix` over the target list, `fail-fast: false`, and that retires
three separate problems rather than one.

**The cap was arithmetic somebody had to redo.** `timeout-minutes` was the target count times the
per-target seconds plus setup, worked out by hand — 150 for fifteen targets, then 180, then 240, then
255 — and it went stale twice. Once it was twenty minutes *under* the fuzzing time, which is a nightly
guaranteed to end on the clock having reported nothing. Each job now derives its own cap from its own
budget (`seconds / 60 + 30`), so there is no figure that has to be recomputed when a target is added,
and the thirty minutes is the runner's overhead — checkout, restore, a Release build of the engine and
`spirv-tools`, about twelve — plus slack, because a cap that fires on a healthy run stops meaning
anything. It is still not a budget: `VIXEN_FUZZ_SECONDS` bounds the fuzzing and `CaseGuard` bounds a
case, and this is the backstop for the process that stops making progress in a way neither can see.

**A target that ended its own process took the other nineteen with it.** That was the whole reason
`raven` was skipped by name — not the defect, the blast radius: the CLR ends the process at a stack
overflow with no thread left to write a finding, so a night on `raven` cost nineteen targets their
results and left no artifact explaining why. One job per target means the loss is one target's night.
`VIXEN_FUZZ_SKIP` stays as a hand override and is empty; it is no longer the answer to a target that
cannot survive a run.

**And twenty targets shared one wall clock, so the deepest could have no more than the shallowest.**
Ten minutes was what twenty times ten minutes could be afforded to be. Now `raven` takes two hours
while `layerrule` takes five minutes, and the nightly's wall clock is the *largest* budget rather than
the sum — two hours where the single job booked four and a quarter, for three times the fuzzing.
Actions is free on public repositories, so what is being spent is wall clock rather than compute.

**The budgets are `nightly-budgets.json`, and they are chosen from measured rates.** One Release run
of the gate on an arm64 laptop. ⚠ **Read the ratios rather than the figures**: the same run on a
machine with three other builds on it reads three to four times lower across the board, which is the
same reason the gate is bounded by cases and not by the clock. A nightly is slower again, because a
run an hour in is mutating a corpus of larger inputs than a gate's first second is. These are the
order of magnitude a budget was picked from, not a promise about the count.

| Target | Cases/s, measured | Nightly | ≈ cases in that budget | Why that budget |
|---|---:|---:|---:|---|
| `packet` | 1.02 M | 10 min | ~610 M | saturates its signature table; past that the clock buys repetition |
| `bits` | 1.28 M | 10 min | ~770 M | as `packet` |
| `handshake` | 981 k | 10 min | ~590 M | eleven behaviours — shallow, and exhausted long before ten minutes |
| `client` | 1.95 M | 10 min | ~1.2 G | twenty behaviours; its findings were retention, not depth |
| `snapshot` | 1.92 M | 10 min | ~1.2 G | |
| `inspect` | 1.06 M | 10 min | ~640 M | widest behaviour space of the byte targets, at 2 365 |
| `delta` | 2.50 M | 10 min | ~1.5 G | fastest target here |
| `rpc` | 2.38 M | 10 min | ~1.4 G | keeps 538 of 1.5 M — guidance working |
| `synclist` | 540 k | 10 min | ~320 M | |
| `input` | 370 k | 10 min | ~220 M | corpus already at its 4 096 cap in the gate's budget |
| `udp` | 251 k | 10 min | ~150 M | a poll walks a connection table |
| `upgrade` | 344 k | 10 min | ~210 M | seven behaviours |
| `bundle` | 196 k | 10 min | ~120 M | a CRC over the whole payload per case |
| `chunk` | 163 k | 10 min | ~98 M | an LZ4 or Zstd decode per case |
| `heightmap` | 73 k | 10 min | ~44 M | inflate and an unfilter pass per case |
| `meta` | 20 k | **30 min** | ~36 M | four passes per case — decode, line scan, YAML parse, reflected bind — and the malformed tag took three million |
| `stylevalue` | 118 k | **5 min** | ~35 M | fills the 65 536 signature table and the 4 096 corpus in the 3.4 s the gate gives it |
| `layerrule` | 225 k | **5 min** | ~68 M | same, in 1.3 s |
| `vxml` | 15 k | **60 min** | ~55 M | four parses per case; the trailing-escape finding took 1.6 M cases |
| `raven` | 465 | **120 min** | ~3.3 M | a case is a whole compiler, and ten minutes is ~280 k cases — the parser hang lived at a quarter of a million, so the old shared budget was one defect deep |

**⚠ The target list is not written out in the workflow.** Twenty names in a YAML file would be a
second source of truth for a list that is already one, and the way that fails is a twenty-first target
the gate fuzzes on every build and the nightly has never once run — the same class of drift as the cap
that stopped being recomputed. `nightly-budgets.json` is the list CI reads, and
`FuzzGateTests.TheNightlyMatrixIsTheRegistry` fails on every build if it stops being `FuzzTargets.Names`
in that order. A budget also has to be a whole number of minutes, because the workflow divides it by
sixty.

**⚠ And the artifact names carry the target.** Twenty jobs uploading `fuzz-findings` is nineteen
refused uploads and nineteen sets of findings lost with the runner, which is the one thing this leg
exists to produce.

## What it found

Four defects on the first run, all in code that had tests and review and none of which either had
caught — and two more later, both found by building a target rather than by running one.

- **The WebSocket upgrade rebuilt its whole request on every read.** It decoded and split the entire
  accumulated buffer each time bytes arrived, so a client dribbling one byte at a time made the server
  build four thousand strings of up to four kilobytes — about eight megabytes of garbage for four
  kilobytes sent, at no cost to the sender, times however many sockets they cared to open. It had no
  test because it had no seam; giving it one to fuzz is what surfaced it.
- **And it had no timeout.** A client that opened a socket and said nothing held a descriptor and a
  pending task until the listener stopped. Slowloris, in a package with a conformance suite.

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

Then three more from the `.meta` target, all on its first run and all in the same place: the boundary
where `YamlReader` decides what counts as a refusal. Pinned in `Vixen.Core.Yaml.Tests`.

- **YamlDotNet does not always keep to its own exception type.** The boundary caught `YamlException`
  and translated it; a comment ending in a stray byte came back an `EndOfStreamException` from
  `ParserExtensions.Accept`, and a plain scalar the scanner walked off the end of came back an
  `InvalidOperationException` from `Scanner.ScanPlainScalar`. Both reached callers whose `when` filters
  list the documented three — `ContentPipeline`'s and `DoctorRunner`'s — and a filter cannot name a
  type nobody knew was thrown, so the editor crashed on a committed `.meta` instead of quarantining it.
- **A one-byte document containing `:` came out as an `ArgumentException`.** An empty key is legal YAML
  and is not in this dialect, but nothing refused it — so it reached `YamlMapping.Set`, whose
  `ThrowIfNullOrEmpty` guard states a *caller's* contract and named a parameter the caller never
  passed. Refused in the reader now, where a key that came out of a file is a parse error rather than
  somebody's bug. The shortest input in the corpus.

And a fourth from the same boundary, which the nightly found rather than the gate — 3 M cases in,
long after the 60 k the gate runs.

- **A tag that is not a URI came out as an `ArgumentException` from a library constructor.**
  `-torter: !!Te]V 1`: the `!!` shorthand expands to `tag:yaml.org,2002:Te]V`, `TagName`'s constructor
  checks that a global tag is a URI, and it throws `ArgumentException` — from inside
  `Parser.MoveNext`, so the boundary's filter for `YamlException` never saw it and neither did the two
  production `when` filters. Every position a tag can appear in reaches it, `!<>` reaches its
  empty-value sibling, and a `%TAG` directive declaring a bad prefix reaches it for all three handles.
  ⚠ **Translated by a decorator over `IParser` rather than by another type on that filter**, because
  `ArgumentException` is the one type on this seam that is ambiguous — it is also what `YamlMapping.Set`
  throws, so catching it around the whole read would have turned the finding above into "the file is
  bad" instead of the refusal it was fixed into. Nothing of the reader's own runs inside `MoveNext`.
  `meta/5b8b48c830e9132e.bin`, which is the 64-byte prefix the finding printed; the 228-byte input it
  came from is not recoverable from a truncated hex line, and the prefix reproduces it exactly.

And a fifth from the same target, which is the one thing only a *differential* oracle sees: the two
readers answered a question the format had never decided.

- **A sidecar with two `metaVersion:` lines was read as 11 by one reader and 1 by the other.**
  `MetaScanner` scans top-down and stops at the first match, so it is first-wins; `YamlReader` reached
  `YamlMapping.Set`, whose replace-in-place made it last-wins. **Neither was wrong, because nothing
  said which one should be** — the dialect refuses anchors and complex keys and had never mentioned a
  repeated one. So the fix is the definition rather than either reader: a key stated twice in one
  mapping is a `YamlParseException`, alongside the empty key and the complex key, compared as written.
  These files are merged and hand-edited by people, so a repeated key is what a bad conflict
  resolution looks like, and picking one silently is two different compilations of one asset depending
  on which code path looked. ⚠ `Set`'s replace stays, and that is the distinction: it is a *caller's*
  affordance for a migration overwriting a value it computed, and a document is not a caller.
  `meta/4934f8ea81bae860.bin`, 142 bytes; pinned in `Vixen.Core.Yaml.Tests`.

Then one from `vxml`, which is the first finding here that byte havoc could not have reached and the
first that needed more than "nothing threw".

- **A file ending inside an escape threw out of the lexer.** A backslash asks a scanner to take two
  characters; at the end of a file there is one, and the window ended at `Length + 1`. Nothing noticed,
  because `AtEnd` is `>=` and every loop stopped exactly as it should — and then the token that scan
  produced was cut with a range past the end of the string, out of a parser whose entire contract is
  that every file produces a tree. Fixed by clamping `SlidingTextWindow.Advance`, so the property
  belongs to the window rather than to the dozen multi-character skips across two lexers that would
  each have to remember it — the same argument `PacketReader` makes for taking bytes in one place.
  Pinned in `Vixen.Ui.Markup.Tests`. It took 1.6 million cases; the prefix round-trip test next to the
  parser walks a real file and never reaches it, because a real file has no trailing backslash to be
  cut after.

And two from `raven`, both in the same place and both invisible to every oracle that watches for
exceptions — the trees were *identical* each time and only the diagnostics differed.

- **An incremental reparse silently dropped the diagnostics of every member it reused.** A reused
  subtree keeps its nodes and loses its diagnostics, because those were produced by the parse that is
  not being run again — so Raven offered every member declaration for reuse regardless of what its
  parse had reported. An author editing one function watched the errors in the rest of the file
  disappear, and a hot reload — which is what calls `WithChangedText` — bound a tree with fabricated
  tokens in it while reporting nothing to explain them. VXML's front end has had the cleanliness gate
  from the start; Raven's had none. Thirty-two findings in the first four hundred cases.
- **And the gate has to look past the node it is judging.** A member ends by requiring a line break,
  and that check reports where the parser is *standing* — the next real token, with the whitespace
  between them belonging to that token's leading trivia, so it is outside everything the member owns.
  Reuse skips the check, and a span-based gate cannot see the diagnostic it skipped. Five findings
  survived the first fix and led to the second.

`IncrementalParseTests` asserts that the diagnostic counts match and caught neither, because every
shader it edits parses cleanly and zero equals zero. Both are now pinned there by a case that starts
from a broken file — and two of the three rows were confirmed to fail with the fix reverted, which is
the only thing that makes a regression test one.

**And a third, from the nightly, which is the second defect surviving its own fix in the one shape it
did not cover.** The gate that decides whether a member may be reused walks forward past its node to
reach the terminator diagnostic reported at the next real token — and it walked `char.IsWhiteSpace`,
stopping at the first character that was not one. A **comment** between the member and that token ends
the window early, so the member looks clean and its diagnostic disappears again. Thirty-eight against
a full parse's thirty-nine, on `var threshold: float/* ck */tp`: the block comment is the next token's
leading trivia and the window stopped eight characters short. It walks the lexer's own trivia now,
which is where the answer already was — `LexedToken` says which tokens the parser navigates over, and
a second opinion about what counts as trivia is what produced this. Two rows in
`IncrementalParseTests`, both confirmed to fail with the fix reverted; `raven/1ceee2894c870b9f.bin`.

**And a fourth, which was the worse failure of the two shapes and is closed.** At forty thousand cases
— the gate runs fifteen hundred — three inputs made the incremental reparse build a *structurally
different tree*, which is worse than losing a diagnostic. The printed text still agreed, so the
round-trip oracle saw nothing; only the shape comparison caught it. The smallest is forty bytes:
`return 1\nenum E {\n    Off,\n    On = 5\n}\n`, with the enum's name replaced by the keyword
`shader`, which leaves the enum's members lexing identically at what is now a member boundary. The fix
is `ReuseCandidate.Context` — a candidate carries the parse loop that produced it and a reuse site
names the one it is standing in — and both inputs are in the corpus with rows in
`IncrementalParseTests`.

And then the one the fifth oracle was written for, which no other oracle here could have reported.

- **Seven characters hang the Raven parser and take the machine with them.** `var t{[` — a `var`
  member whose type position is an open brace, followed by a bracket — makes `SyntaxTree.ParseText`
  grow the managed heap without bound: 537 MB in 1.9 s, 2.1 GB in 11 s, climbing until the operating
  system decides. An accessor list accepts `[` because an accessor may carry attributes, but whether
  the bracket *is* an attribute list is decided further in by `ScanAttributeList`, which **resets the
  position** when it says no — and every step below it fabricates rather than consumes. So the bracket
  stayed where it was and `ParseAccessorList` added a fabricated accessor for it for ever, keeping
  every one. It was the only loop of its kind in that parser without the no-progress guard the member
  and statement loops all keep; the fix is that guard. Pinned by
  `RecoveryTests.A_bracket_an_accessor_list_cannot_use_still_ends_the_parse`, and both inputs are in
  the corpus — `raven/f3680f7e77d7d18b.bin`, the 2,872-byte mutant of `Example2.rvn` it arrived as,
  and `raven/054deec5ae05ddc8.bin`, the seven characters it reduces to.

  **This is what a fuzz harness with only post-hoc oracles cannot find, and it is not a small class.**
  Nothing threw, nothing was reported, the tree was never built, and no reading was ever taken on the
  far side of `Run` — because there is no far side. It took about a quarter of a million `raven` cases,
  which is why the gate's fifteen hundred never saw it and why `raven` had never once completed a
  `VIXEN_FUZZ_SECONDS` run: every attempt had been hitting this and being killed by whatever ran out
  first. A developer's Mac was one of those.

**And immediately behind it, a second one the first had been hiding — which this harness could provoke
and could not name.** The very next time-bounded run got four minutes further and died of a **stack
overflow** in the binder:

```
package P

shader S {
    func F(): float[F()] {
        return 1f
    }
}
```

`SourceMethodSymbol.ResolveReturnType` → `BindType` → `BindArraySize` → `BindValue` → `BindInvocation`
→ `BoundInvocationExpression.Type` → `SourceMethodSymbol.ReturnType` → and round again. A member
function whose return type is an array sized by a call to itself; nothing on that path asked whether it
was already resolving the symbol it was being asked for. Reproduced in 40 ms from the seven lines above.

⚠ **"Only inside a `shader`" was the wrong reading of it, and chasing that would have found nothing.**
A `struct` does it too. What is true is that the same text at the *top level* does not crash — because
a package-level `func` never becomes a symbol at all, so it is not bound rather than bound fine:
`func G(): Missing` outside a type reports no `RVN2002` either. The real asymmetry was one layer in.
Four source symbols resolve a declared type; `SourceFieldSymbol` and `SourcePropertySymbol` wrap the
whole of it in a per-symbol `resolving` flag that reports `RVN2005`, `SourceMethodSymbol` had that flag
around its *inferred* branch only and left the annotation unguarded, and neither parameter symbol had
one.

**Fixed by giving all four the same guard**, keyed by the symbol, which closes the family rather than
the instance: a return type, a parameter type (`func F(x: float[F(1f)])`), two signatures sizing arrays
by each other, and a `val` parameter sizing its own type (`shader S<val N: int[N]>`) all went to the
guard page before and all now name the symbol they are circular through. A depth bound would have
closed the instance and left the other three.

**And so it is in the corpus** — `raven/70ae34e20b4880ee.bin`. It had been kept out deliberately while
it was open: an input that overflows the stack takes the test host with it on every build, and the rule
here is that promotion follows the fix. It stays the honest edge of the guard — see *What survives a
runaway*: the CLR ends the process at the overflow, so there is no thread left to write a finding and
no sample early enough to have taken one. Bounding this recursion did not change that; a case per child
process still would.

**And one that was thirty-two findings and one defect, which is the more useful half of the story.**
`ParseStatement` threads the attribute lists it has just consumed tokens for into whatever node it
builds — every branch of it except the one that discovers the statement is a block, where
`case OpenBrace: return ParseBlock()` dropped them on the floor. `BlockSyntax` has declared an
`AttributeLists` slot since the grammar was written and the parser never filled it, so `[Unroll] {`
parsed to a block with no attributes and printed back as `{`: the characters left the tree entirely
and the round-trip oracle said so. Reduced, every one of the thirty-two is `func{ …[X] {`. Fixed by
passing the lists in, pinned by `RoundTripTests.An_attributed_block_carries_its_attributes` — which
asserts the *slot* rather than the string, because a fix that put the characters back as trivia would
round-trip and still leave the attributes unreachable to everything that reads the tree.

⚠ **It was thirty-two findings because of this harness, not because of the compiler, and that is what
made it expensive.** The dedup key was the whole detail string, and a detail quotes the input — the
oracle names the byte offset of the first difference and the character either side of it. So one
defect minted a fresh finding per offset, `MaxFindings` filled after five and a half minutes, and the
cap **ends the run**: `raven` has a two-hour nightly budget and had never once spent more than four
per cent of it. The summary line could not say so either — `109,012 cases … 3 FINDING(S)` is what a
run that stopped at four per cent prints, and it is the same line a run that used all of it prints.
What caught it was somebody downloading an artifact and counting the files in it. The key is now the
failure and the detail with the input's own values blanked, the summary says which of the four things
ended the run, and repeats are counted rather than dropped in silence. On the same sixty seconds and
the same seed, with the parser defect put back: **one** finding and sixty-six repeats, and the run
goes the distance.

**And one found and deliberately not fixed**, because the fix is not this harness's to make: the binder
writes `null` into a member declared non-nullable. `subAssets: null` in a sidecar produces an
`AssetMeta` whose `SubAssets` is null although the property is `SubAssetEntry[]` with a non-null
default — nullability is decided from the CLR type, and the C# annotation contradicting it is not in
the descriptor to read. Nothing throws at the parse; the crash lands in whichever consumer dereferences
it first. Refusing a document `null` for a collection member is a decision about every `[DataContract]`
type in the engine, so it belongs to `Vixen.Core.Yaml` rather than here. The input is in the corpus
(`meta/26b80310961881ec.bin`) and the target folds the shape into its signature so it stays reachable.

## The corpus on disk

`Vixen.Fuzz.Tests/Corpus/<target>/<fingerprint>.bin` holds inputs that have broken something. They
are replayed before every run, so a defect found once is a test from then on — the difference between
fuzzing and having fuzzed. The name is an FNV-1a of the bytes rather than a hash code, so two machines
that find the same input write the same file and the second one does not add a duplicate to the review.

The *grown* corpus is deliberately not persisted: it would be a large binary directory whose contents
depend on the machine that produced it, and the seeds plus a fixed generator seed reproduce a run
without it.

## Owed

- **A case per child process, for the one runaway nothing in-process can report.** A stack overflow
  ends the CLR at the overflow — no exception, no handler, no other thread given a chance — so
  `CaseGuard` provokes that class and cannot name it, which the second `raven` finding demonstrates.
  Running a case out of process, writing the input before it starts and reading the child's exit code
  after, catches it for the price of a fork per case. That is affordable only for a *replay* of
  suspect inputs rather than for the twelve million the gate runs, which is the shape the answer
  should take. `SharpFuzz` below is the same machinery arriving for a different reason.
- **`SharpFuzz`, for coverage this cannot have.** The nightly exists; what it runs is still this
  harness, whose guidance is a behaviour signature rather than edge coverage.
  [docs/plan/12](../../docs/plan/12-build-ci-and-testing.md) § Test infrastructure asks for `SharpFuzz`
  over the parsers, and the packet reader belongs in that job: libFuzzer with instrumentation would
  find in an hour what this finds in a week. The targets are already the right shape for it — each is
  `(ReadOnlySpan<byte>) -> outcome` — so the wrapper is a few lines. Worth having *alongside* rather
  than instead: this one runs on every build, which an instrumented one never will.
- **Structure-aware mutation for the *binary* formats.** The seam exists and the grammars use it, but
  the mutator still does not know a snapshot from a handshake. A domain that understood the record
  format could keep the tick and break the payload, rather than spending most of its budget on inputs
  the first field refuses. `IFuzzDomain` is where such a thing would go, and nothing about it is
  specific to text — it was built for trees because that is where the need was sharpest.
- **A generator driven off `Syntax.xml`.** Both grammars describe their node shapes and their child
  slots in a checked-in XML file, which is a machine-readable grammar sitting right there. `Create`
  currently concatenates hand-written fragments — enough to reach combinations no seed is near, and a
  long way short of a generator that could build a well-typed shader nobody wrote.
- **Reuse should not change what is reported, rather than being gated on it.** The `raven` findings
  were fixed by refusing to reuse a member whose parse said anything, which is correct and
  conservative and costs reparses on exactly the files an author is editing — the ones with errors in
  them. The other repair is to make `TryReuseMember` re-run the terminator check that
  `ParseMemberDeclaration` performs, so reuse becomes genuinely transparent. That is a change to the
  parser's member loops and wants the eye of whoever owns which member kinds require a terminator.
