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
prefix that decides an allocation, which is the only property this harness has ever cared about. The
name `Vixen.Net.Fuzz` is now narrower than what is in it — see **Naming**, below.

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

**`Vixen.Net.Fuzz` is the wrong name now and renaming it is a separate change.** Nothing in
`FuzzSession`, `Mutator`, `Corpus` or `IFuzzTarget` is network-specific — the harness is a mutation
loop, three oracles and a behaviour signature, and it took a content format without a line of change.
`Vixen.Fuzz`, sitting in `Core` where it already is, is what it should be called.

Not done here because a project rename touches the solution file, every `ProjectReference` to it, the
workflow and the test project beside it, and this branch is one of several in flight. It is a
mechanical change worth doing on its own.

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

**It is switched off in the gate today, and what switched it off is what it found.** Two one-token
edits of `Example2.rvn` compile with *no diagnostic at all* and emit modules a driver would reject:

- `[Permutation] val UseSoftKnee: bool = true` → `[D] …`. The unknown attribute is accepted in
  silence, so the value stops being a permutation key and becomes an ordinary uniform member — and
  SPIR-V forbids `OpTypeBool` in an externally-visible storage class.
- `val over = max(value - threshold, 0f)` → `val over = Vixen(1, 1, 1, 1)`. Calling a *package* is
  accepted, the `val` binds to a void-typed expression, and the emitter materialises
  `OpConstantNull` of `void`.

Both are exactly the shape the oracle exists for — a compile that looks entirely successful and an
output that is not a program. Neither was reachable by anything else here: nothing threw, nothing
amplified, the round-trip held and the reparse agreed.

They are quarantined rather than left red, because a gate that fails on a defect nobody is fixing
today is a gate people learn to ignore, and the next real regression then arrives into a build that
was already failing. The inputs are committed under `Corpus/raven`, so `VIXEN_FUZZ_SPIRV=1` reproduces
both from disk in seconds. Everything up to and including `SpirvBackend.Generate` still runs in the
gate — lowering, verification and emission must still not throw. Deleting `Spirv.Enabled` and
`TheValidityOracleIsQuarantinedNotForgotten` is the last step of fixing them.

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

The gate runs on every build, in `Vixen.Net.Fuzz.Tests` — eleven million cases in about nine seconds,
bounded by **case count rather than by the clock**, because a run bounded by time executes a different
number of cases on a loaded machine than on a laptop and a green build then proves nothing in
particular.

**The rows are generated from the registry, not written out.** Three targets were once written,
registered and named, and were simply not among the theory's `[InlineData]` rows — so they existed,
passed the test that checks the names match the constructors, and never ran. A target that exists is
now a target the gate runs; forgetting to give it a case budget fails a test rather than making it
disappear.

For a longer run, give it seconds instead:

```bash
VIXEN_FUZZ_SECONDS=600 dotnet test Core/Vixen.Net.Fuzz.Tests -c Release
```

That is what `.github/workflows/nightly.yml` does at three in the morning — the same harness, the same
seeds, the same generator, given ten minutes a target rather than a second, which is roughly six
hundred times as many cases. Anything it finds is written to `artifacts/fuzz-findings` and uploaded,
because a finding whose bytes only exist in an assertion message is one somebody has to retype.

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

**And one found and deliberately not fixed**, because the fix is not this harness's to make: the binder
writes `null` into a member declared non-nullable. `subAssets: null` in a sidecar produces an
`AssetMeta` whose `SubAssets` is null although the property is `SubAssetEntry[]` with a non-null
default — nullability is decided from the CLR type, and the C# annotation contradicting it is not in
the descriptor to read. Nothing throws at the parse; the crash lands in whichever consumer dereferences
it first. Refusing a document `null` for a collection member is a decision about every `[DataContract]`
type in the engine, so it belongs to `Vixen.Core.Yaml` rather than here. The input is in the corpus
(`meta/26b80310961881ec.bin`) and the target folds the shape into its signature so it stays reachable.

## The corpus on disk

`Vixen.Net.Fuzz.Tests/Corpus/<target>/<fingerprint>.bin` holds inputs that have broken something. They
are replayed before every run, so a defect found once is a test from then on — the difference between
fuzzing and having fuzzed. The name is an FNV-1a of the bytes rather than a hash code, so two machines
that find the same input write the same file and the second one does not add a duplicate to the review.

The *grown* corpus is deliberately not persisted: it would be a large binary directory whose contents
depend on the machine that produced it, and the seeds plus a fixed generator seed reproduce a run
without it.

## Owed

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
