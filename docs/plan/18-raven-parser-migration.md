# 18 — Raven Parser Migration: ANTLR → Roslyn-style hand-written

**✅ Complete.** All eight steps landed, ANTLR is out of the shipping projects, and the `.g4` files
survive as a permanent differential oracle. This document is kept for the finding and the amendment it
makes to [ADR-009](01-technology-decisions.md) — the reasoning is what stays useful, and the ADR points
here.

The migration is [Phase 5b](14-roadmap.md) in the roadmap. It cost its cheapest, for one reason worth
stating: **the language surface settled first.** Doc 07 § J's pruning passes plus a token audit removed
a third of the syntax *before* any parser was written, and migrating into a churning grammar pays the
cost twice.

---

## The finding, in one paragraph

Roslyn's design is **an XML-generated tree plus a hand-written parser.** Raven adopted the first half
verbatim — `Syntax.xml` → generator → green nodes, red nodes, visitors, rewriters, factories, 9 518
generated lines — and then put ANTLR in front of it, joined by a 1 490-line translator. Every friction
point traced back to having two tree representations instead of one. Switching to a hand-written parser
was not adopting a new architecture; it was **finishing the one already chosen.**

**There is no grammar file and no parser generator anywhere in Roslyn.** No ANTLR, no yacc, no table
generation. The XML describes the shape of the tree; a person writes the code that builds it.

## Why it had to change

**1. Incremental reparse is impossible over ANTLR, and the shared tree was built for it.**
`Vixen.Core.Syntax` exists so Raven, VXML and VCSS share one green/red tree, and that tree is *designed*
for reuse: immutable, width-based, position-independent, no parent pointers. ANTLR has no notion of
reusing an existing tree, so `WithChangedText` reparsed whole files — the API landed, the optimisation
could not. That made Raven the one front end unable to benefit from the shared investment, inverting the
point of extracting it.

ADR-009's carve-out was that VXML needs sub-millisecond reparse and Raven does not. That did not survive
[doc 09](09-ui-framework.md), which specifies a `CodeEditor` with syntax highlighting for `.rvn` as well
— people type Raven in that editor with live squiggles. Same requirement, same solution.

**2. "Error recovery is table-driven" was a liability, not a feature.** ADR-009 listed it as a reason
*for* ANTLR. In practice its recovery produced trees the translator could not map, and the mitigation
was to **discard the tree** — the opposite of recovery. An IDE needs a tree with explicit missing and
skipped tokens that binding can still walk, which is exactly what a hand-written parser emits. The
messages matched the machinery: *"no viable alternative at input"* and *"extraneous input '}' expecting
{… 40 tokens …}"* are fine for a CLI and wrong under a squiggle.

**3. The saving was smaller than it looked.** The grammar was 866 lines and the translator 1 490 —
plus `RavenLexerBase`'s three empty hooks that existed only because the grammar's inline actions named
them, and `TerminalOf`/`TerminalOrNull`/`Commas` accessor plumbing for tokens ANTLR does not generate
accessors for.

## What ANTLR was genuinely better at

Recorded honestly, because it is what set the timing.

- **Grammar churn.** While a language is moving, editing a `.g4` alternation beats hand-editing descent
  functions. This premise of ADR-009 held right up to the migration, and is why it was sequenced after
  the shader library rather than before.
- **The grammar as specification.** A `.g4` file is readable, checkable documentation of the syntax —
  and it survived, see below.

## What landed

| Step | Outcome |
|---|---|
| 1 | The corpus frozen. `all_constructs.rvn` exercises every production, and freezing flushed out **six** token-dropping nodes — including `default(T)`'s keyword and parens, and a `val` type parameter's colon — which now carry their tokens and round-trip |
| 2–3 | `SlidingTextWindow`, `LexedToken` and a `SyntaxParser` base in `Vixen.Core.Syntax/Parsing`, internal like the green tree. `Parsing/RavenLexer.cs` (483 lines) pinned to the ANTLR lexer by a token-stream differential |
| 4–5 | `Parsing/RavenParser.cs` (2 232 lines) translates the grammar production by production, pinned byte-exact by `ParserDifferentialTests` — including the ambiguity probes: `(a) + b` is a cast, `a < b` a comparison, `G<int>(y)` a generic invocation, `[Unroll]` on its own line an attributed empty statement |
| 6 | `SyntaxTree.ParseText` runs the new front end. The `catch`-and-discard is gone: an erroneous parse yields a tree with zero-width missing tokens and skipped source as trivia, reproducing the file byte-for-byte. ANTLR left the shipping projects |
| 7 | `Blender` in `Vixen.Core.Syntax/Parsing`. `WithChangedText` is incremental at **member granularity** — editing one function body reparses that member and reuses every other member's green node by reference, with a one-character adjacency margin so an edit gluing onto a boundary invalidates its node. Reuse is verified against the new token stream and falls back to parsing, so it can only skip work, never change the tree |
| 8 | Recovery syncs to line ends: a broken line is one diagnostic naming what was expected, not one per token. `RecoveryTests` pins that error trees reproduce the file byte-for-byte and survive binding |

**The estimate was +1 500…2 500 owned lines. The actual is ~2 715** across the lexer and parser, plus
401 in `Vixen.Core.Syntax/Parsing` — and that last part was already committed by ADR-009's decision to
hand-write VXML and VCSS, which need all three of those types regardless.

## Keeping the grammar as an oracle

**The `.g4` files were not deleted.** They live in `Raven/Vixen.Raven.Tests/Oracle/` with the generated
parser and the old translator, and the differential parse is a permanent test: every corpus file is
parsed by both front ends and the trees compared.

- The grammar stays as executable specification of the syntax.
- Any divergence between the hand-written parser and the declared grammar fails a test rather than
  shipping.
- It turned the migration from an act of faith into a verifiable one, and keeps it verified afterwards.

This mirrors the SPIR-V-vs-`shaderc` differential oracle: **a second implementation earns its keep as a
check even after it stops being the primary.** That is the transferable lesson, and it is why the risk
was low rather than merely believed to be low.

## What this changed in the plan

- **[ADR-009](01-technology-decisions.md)** — amended. "ANTLR is right for Raven" became "ANTLR was
  right for Raven's *bootstrap*".
- **Nothing else.** Binding, lowering, IR, both backends, reflection and the artefact formats are behind
  the tree and never knew which parser built it. That is the whole return on having adopted Roslyn's
  tree design first.

## Still open

Statement-level reuse (today's granularity is the member) and editor-grade message polish. Neither
blocks anything: they ride with the `CodeEditor` work in [doc 11](11-editor.md), and the current
recovery already produces trees an editor can walk.
