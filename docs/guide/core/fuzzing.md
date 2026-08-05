---
title: Fuzzing a format that has a grammar
slug: core/fuzzing
kind: guide
area: Core
summary: How a fuzz target says that bytes are the wrong thing to mutate, what a domain replaces havoc with, and what each of the structured targets asserts beyond "it did not crash".
api: [T:Vixen.Fuzz.IFuzzDomain, T:Vixen.Fuzz.FuzzDomain`1, T:Vixen.Fuzz.SyntaxDomain, T:Vixen.Fuzz.SyntaxDomain.Source, T:Vixen.Fuzz.Targets.RavenTarget, T:Vixen.Fuzz.Targets.Spirv, T:Vixen.Fuzz.Targets.VxmlTarget, T:Vixen.Fuzz.Targets.StyleValueTarget, T:Vixen.Fuzz.Targets.LayerRuleTarget, T:Vixen.Fuzz.Targets.AssetMetaTarget, T:Vixen.Fuzz.Targets.BundleTarget, T:Vixen.Fuzz.Targets.ChunkFormatTarget, T:Vixen.Fuzz.Targets.FuzzImportSettings, T:Vixen.Fuzz.Targets.HeightmapPngTarget]
tags: [core, fuzzing, testing, parsers, grammars, compiler]
since: 0.1
status: preview
related: [core/symbols, assets/content-in-a-game]
---

## What it is

A fuzz target is a decoder with bytes pushed into it: `long Run(ReadOnlySpan<byte> input)`, measured
while it runs. `IFuzzDomain` is the one seam that changes where those bytes come from — instead of
AFL-style havoc over the previous input, a domain reads a corpus entry into a *value*, changes the
value, and writes it back out.

`FuzzDomain<T>` is the class that does that sandwich, so a target author writes only the three
operations. `SyntaxDomain` is the implementation both front ends share: it parses the entry, picks a
node, and splices that node's span in the source text. `SyntaxDomain.Source` is what one of its
inputs is — the text, and the nodes an edit may be chosen from.

Eight of the twenty registered targets read something other than a packet — a file, a stylesheet
fragment, a sidecar, a language — and the two that read a language are the two that declare a domain.
What each asserts, beyond nothing escaping:

| Type | What it reads | The invariant |
|---|---|---|
| `RavenTarget` | a shader, parsed, bound, lowered and emitted | the tree reproduces the file; an incremental reparse equals a full one; every escaping diagnostic id is `RVN[1-5]nnn`; a module that generated is one the validator accepts |
| `Spirv` | the module `RavenTarget` emitted | `spirv-val` accepts it, at the environment the module's own header word asks for |
| `VxmlTarget` | a `.vxml` document | byte-exact print of arbitrary text, and an incremental reparse that matches a full one in text, tree shape and diagnostic count |
| `StyleValueTarget` | a declaration value, or a `var()` substitution ExCSS never saw | nothing escapes, and one parse interns at most 2048 names — a claim about the grammar, not a number picked to pass |
| `LayerRuleTarget` | an `@layer` rule ExCSS handed back whole | never throws; a false return leaves the out-parameter at its default; and the read reaches a fixed point through its own printed form |
| `AssetMetaTarget` | a `.meta` sidecar, read twice | three refusals and nothing else, and the fast line scanner agrees with the full parse wherever both answered |
| `BundleTarget` | a `.bundle`, opened with the checksum on | only `SerializationException`; every id the index enumerates is an id the payload can read |
| `ChunkFormatTarget` | a stored blob, unwrapped back into a chunk | only `SerializationException`, and the allocation is bounded by the bytes present rather than by the length the blob declared |
| `HeightmapPngTarget` | a 16-bit greyscale PNG dropped on an importer | `ArgumentException` is the refusal and nothing else is; the decode stays inside DEFLATE's own 1032× ceiling |
| `FuzzImportSettings` | not a target — the settings record the `meta` seeds carry a `!TextureImporter` tag for | a tag that resolves in a build with no editor in it |

## What it is for

Byte havoc is the right tool for a decoder and the wrong one for a compiler. The mutator aims at
length prefixes and varints and is very good at those; pointed at a *language* it spends effectively
all of its budget on text that does not lex. A shader that dies at its first token has exercised the
tokeniser and nothing else, so the binder, the type checker and the backend — where a compiler's
defects actually live — are never reached at all.

A structured mutation is the grammar's version of the same idea: replace a subtree with another of
the same kind, duplicate one, delete an optional one, graft one in from a second corpus entry. Each
produces something that lexes and mostly parses, and therefore something that arrives at the passes
behind the front end.

The second half of the argument is what a target is allowed to *check*. Four oracles run around
every case — nothing threw, nothing amplified, nothing was retained, nothing took long — and for a
total front end all four are nearly vacuous. The Raven and VXML parsers have no `throw` in them by
design: every file produces a tree, missing tokens are fabricated zero-width, unusable source travels
as trivia. A run watching only for exceptions on such a target would come back clean for ever while
every pass behind it quietly did the wrong thing.

So each structured target carries an invariant of its own, and they come in three shapes:

- **Round-trip equality.** The printed tree is the file, byte for byte, for malformed input too. A
  tree that cannot reproduce its file cannot be formatted, cannot be edited incrementally and cannot
  map a span back to what the author wrote.
- **Differential agreement.** Two readers over one input must say the same thing —
  `AssetMetaTarget` runs the fast line scanner against the full parse, `LayerRuleTarget` runs the
  reader against its own output, and both parsers are checked incrementally against a full reparse.
  A *wrong* answer throws nothing, allocates nothing and retains nothing, so no oracle sees it.
- **Validator-clean.** `Spirv` hands the emitted module to Khronos's own `spirv-val`. Everything
  else in this harness compares two things Vixen wrote; this is the only check that asks somebody
  else whether the answer is right, and therefore the only one that catches a backend emitting
  something *valid and wrong*.

⚠ **The oracles never learn what an input is, and that was the constraint rather than the
outcome.** `Run` still takes a `ReadOnlySpan<byte>`, the corpus is still bytes on disk, and a finding
still carries the exact bytes that broke something. A domain that had made the oracles care about
trees would have thrown away the only reason to grow this harness instead of adopting `SharpFuzz`.

## Using it

The gate runs on every build, bounded by a case count rather than by the clock — every machine runs
the same cases in the same order from the same seed, so a red build is reproduced locally by reading
the seed out of the message. A run bounded by the clock executes a different number of cases on a
loaded CI machine than on a laptop, and a green build then proves nothing in particular. For depth,
give it seconds instead:

```bash
VIXEN_FUZZ_SECONDS=600 dotnet test Core/Vixen.Fuzz.Tests -c Release
```

One target on its own is a session over a name:

```csharp compile
using Vixen.Fuzz;

public static class OneTarget {
    public static void Run() {
        var target = FuzzTargets.Named("vxml");

        var session = new FuzzSession(target, 0x7658_C0DE_2026_0805ul) {
            RegressionDirectory = "Corpus",
            FindingDirectory = "artifacts/fuzz-findings"
        };

        var outcome = session.Run(20_000);

        Console.WriteLine(outcome);

        foreach (var finding in outcome.Findings) {
            // The bytes, not only the message: a finding somebody has to retype is a finding
            // somebody does not reproduce.
            Console.WriteLine($"{finding.Failure}: {finding.Detail}");
            Console.WriteLine(finding.Hex);
        }

        (target as IDisposable)?.Dispose();
    }
}
```

### Declaring a domain

A target declares `IFuzzTarget.Domain` when its input is a language. For anything built on
`Vixen.Core.Syntax` that is one line — `SyntaxDomain` takes a name, a parse delegate, the fragments
`Create` builds from, and a seed for its byte-havoc half — and neither language is named inside it,
so a third grammar on the same tree costs a delegate and a list of snippets.

Everything else about the target is unchanged. `Run` still receives bytes and is still expected to
treat them as bytes it did not write: a domain makes grammatical input *more likely* and promises
nothing at all about any single case.

⚠ **Garbage is still generated, and leaving it out is the mistake this design is most likely to
make.** A tree mutator only ever emits text the printer produced, so an unterminated string, a stray
byte and a nesting depth that runs the parser out of stack stop being reached the moment structured
generation *replaces* havoc rather than joining it. One mutation in `FuzzDomain<T>.GarbageIn` is
havoc over the serialized form, and the two run against the same corpus. It is also what keeps a
committed regression useful: a crasher found by havoc is usually not a tree, so it fails `TryRead`,
and without the blend it would be replayed once at start-up and never mutated again.

⚠ **The corpus stays bytes, and for a language that costs nothing.** A tree's serialization is its
source text, which is what a corpus file should hold anyway — readable in a diff, committable as a
regression, and something a person reproducing a finding can hand to the real compiler. The price is
a parse per case on the way in and another inside `Run`.

⚠ **`FuzzDomain<T>.MaxLength` is 64 KiB where the byte mutator's cap is 1400.** That number is a
transport's payload cap and this is a source file; a shader nobody would call large is already past
1400 bytes, so capping a grammar domain there would mean fuzzing fragments. It is capped all the
same, because a mutation that duplicates a subtree is a doubling and a few hundred unchecked
doublings is an input the size of memory.

⚠ **`SyntaxDomain` applies one edit per case where the byte mutator applies up to six.** A node's
span is an offset into the text it was collected from, and the first splice invalidates every span
after it. A mutant that is kept is mutated again on a later case, which composes edits at no extra
price.

### Turning the guidance off

`IFuzzTarget.NoveltyGuides` is true for a decoder and false for a compiler, and the difference is the
size of the behaviour space rather than a preference. A packet reader has a few dozen outcomes, so
"this input did something new" is a strong signal and a corpus selected on it is a set of
representatives. A compiler has a behaviour for every combination of declarations, types and
diagnostics there is: nearly everything looks new, the signature table saturates within seconds, and
what it selected before saturating was whatever the first few thousand cases happened to be.
Declaring it false is accepting unguided but *structured* generation — the guidance existed to walk a
decoder into branches random bytes never reach, and a domain reaches them by construction.

⚠ **A signature is about the case, not about the run, and getting that wrong is silent.** A decoder's
counters are lifetime totals, so a signature folded from them strictly increases and every case looks
novel — which is not excellent coverage, it is no guidance at all and a corpus that keeps everything.
One target here kept a million inputs in a second before anybody printed the ratio. Fold the
*change* in a counter, or state that is genuinely bounded; never a running total.

⚠ **Every seed a grammar target offers must parse cleanly, and this is the one way a grammar seed
fails that a byte seed does not: quietly.** A malformed seed still produces a tree, still round-trips
and can still be mutated, so it looks exactly like a seed that works while every input descended from
it inherits the same mistake. `EveryGrammarSeedIsWellFormed` beside the gate is what asks. Raven in
particular looks enough like C from a distance that a seed written from memory parses into a tree
full of fabricated tokens — its statements end at a line break and its fields are `var x: float`.

⚠ **A finding is promoted to the corpus after the fix, not before.** A committed input for an open
defect is a permanently red gate, and one that ends the test host — a stack overflow in a binder —
takes the other nineteen targets' results with it on every build.

## Examples

A domain and the target that declares it, in the shape every structured target here has. The domain
does the three operations; the target does the invariant:

```csharp compile
using System.Text;
using Vixen.Fuzz;

// Read, change, write. FuzzDomain does the sandwich around these, blends in byte havoc, and caps
// what comes out.
public sealed class LineDomain(ulong seed) : FuzzDomain<string[]>(seed) {
    static readonly string[] Fragments = [
        "guid: 0123456789abcdef0123456789abcdef", "metaVersion: 1", "importer: !TextureImporter"
    ];

    public override string What => "sidecars, one line at a time";

    // Allowed to fail, and failing is not an error: the corpus holds committed regressions and the
    // empty input, and such an entry is mutated as bytes instead.
    protected override bool TryRead(ReadOnlySpan<byte> bytes, out string[] value) {
        value = Encoding.UTF8.GetString(bytes).Split('\n');

        return !bytes.IsEmpty;
    }

    protected override byte[] Write(string[] value) => Encoding.UTF8.GetBytes(string.Join('\n', value));

    protected override string[] Mutate(string[] value, string[] other, FuzzRandom random) {
        if (value.Length == 0 || other.Length == 0) {
            return value;
        }

        // Grafting a line from the other corpus entry: the unit of the edit is the format's own,
        // which is the whole point of the seam.
        var lines = value.ToArray();
        lines[random.Below(lines.Length)] = other[random.Below(other.Length)];

        return lines;
    }

    protected override string[] Create(FuzzRandom random) => [Fragments[random.Below(Fragments.Length)]];
}

public sealed class SidecarTarget : IFuzzTarget {
    public string Name => "sidecar";

    public string What => "a line-oriented sidecar, read and written back";

    public IFuzzDomain Domain { get; } = new LineDomain(0x5344_4341_5230_0001ul);

    // A behaviour per combination of lines is more than the signature table holds; the domain
    // reaches the branches the guidance was buying.
    public bool NoveltyGuides => false;

    public long AllowanceFor(int inputLength) => 8192 + (128L * inputLength);

    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        corpus.Add(Encoding.UTF8.GetBytes("guid: 0123456789abcdef0123456789abcdef\nmetaVersion: 1\n"));
    }

    public long Run(ReadOnlySpan<byte> input) {
        var text = Encoding.UTF8.GetString(input);
        var lines = text.Split('\n');

        // The invariant, and it is the reason this is a target rather than a smoke test: throwing is
        // how one reports a broken promise, so a reader that cannot reproduce its own input is a
        // finding by the same route an exception would be.
        if (!string.Equals(string.Join('\n', lines), text, StringComparison.Ordinal)) {
            throw new InvalidOperationException($"The read does not reproduce its {text.Length} characters.");
        }

        long signature = 17;

        return (signature * 31) + lines.Length;
    }
}
```

### What the shape has actually found

Worth reading as a description of what these invariants are for rather than as history:

- **A VXML file ending inside an escape threw out of the lexer.** A backslash asks the scanner to
  take two characters and at the end of a file there is one, so the token it produced was cut with a
  range past the end of the string — out of a parser whose whole contract is that every file produces
  a tree. 1.6 million cases; byte havoc could not have reached it.
- **A Raven incremental reparse silently dropped the diagnostics of every member it reused.** The
  trees were identical each time and only the diagnostics differed, so nothing that watches for
  exceptions could have seen it. An author editing one function watched the errors elsewhere in the
  file disappear.
- **Two one-token edits of a shipped shader compiled with no diagnostic at all and emitted modules a
  driver would reject** — a `bool` in a uniform block, and `OpConstantNull` of `void`. Nothing threw,
  nothing amplified, the round-trip held and the reparse agreed. Only the validator saw it.

⚠ **`spirv-val` being absent is not a silent skip.** Without it the Raven target still generates
modules and validates nothing while the run goes on printing "clean", so
`TheSpirvValidatorIsInstalled` fails out loud instead. Install `spirv-tools` — `brew install
spirv-tools`, or `apt-get install spirv-tools`. There is no switch to turn the oracle off: an oracle
with an off position is an oracle somebody turns off.

## See also

- [Symbols](symbols.md) — the other `Vixen.Core` type whose whole argument is that a value must be
  the same on two machines, which is the same reason a fuzz run is seeded rather than random.
- [Getting content into a running game](../assets/content-in-a-game.md) — the bundles and chunks the
  `BundleTarget` and `ChunkFormatTarget` are pointed at, and where their declared lengths come from.
