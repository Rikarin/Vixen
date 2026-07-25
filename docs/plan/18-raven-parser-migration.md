# 18 — Raven Parser Migration: ANTLR → Roslyn-style hand-written

**Status:** planned, not started. Language surface should settle first — see
[§ When](#when-and-what-triggers-it).

Raven's front end is an ANTLR grammar whose parse tree is translated into a Roslyn-shaped green/red
tree. This document records why that arrangement should end, what replaces it, and how to do the
swap without betting the compiler on it.

It amends [ADR-009](01-technology-decisions.md), which chose ANTLR for Raven. That choice was
reasonable and is not being called a mistake — two of its three premises have since weakened, and the
shared-tree work in [§ A of doc 07](07-raven-shader-pipeline.md) changed what "shared infrastructure"
is worth.

---

## The finding, in one paragraph

Roslyn's design is **an XML-generated tree plus a hand-written parser.** Raven adopted the first half
verbatim — `Syntax.xml` → generator → green nodes, red nodes, visitors, rewriters, factories — and
then put ANTLR in front of it, joined by a 1 490-line translator. Every friction point below traces
back to having two tree representations instead of one. Switching to a hand-written parser is not
adopting a new architecture; it is **finishing the one already chosen.**

## What Raven already has, and what it is missing

Raven's generator emits the same three files as Roslyn's `CSharpSyntaxGenerator`, from a `Syntax.xml`
validated by a `Syntax.xsd`:

| Generated file | Lines | Contents |
|---|---|---|
| `Syntax.xml.Internal.Generated.cs` | 3 090 | green nodes |
| `Syntax.xml.Syntax.Generated.cs` | 4 269 | red nodes |
| `Syntax.xml.Main.Generated.cs` | 2 159 | visitors, rewriter, factories |
| | **9 518** | |

That is the Roslyn tree, and it is done. What Roslyn has that Raven does not:

| Roslyn | Purpose | Raven today |
|---|---|---|
| `Lexer.cs` + `SlidingTextWindow.cs` | hand-written lexer over a sliding window | `RavenLexer.g4` (353 lines) |
| `LanguageParser.cs` | hand-written recursive descent, emits green nodes directly | `RavenParser.g4` (513 lines) + `SyntaxAntlrVisitor.cs` (1 490 lines) |
| `Blender.cs` | feeds the parser reusable green nodes from the old tree + change ranges | **nothing — cannot exist over ANTLR** |

**There is no grammar file and no parser generator anywhere in Roslyn.** No ANTLR, no yacc, no table
generation. The XML describes the shape of the tree; a person wrote the code that builds it.

## Why it should change

### 1. Incremental reparse is impossible over ANTLR, and the shared tree was built for it

`Vixen.Core.Syntax` exists so Raven, VXML and VCSS share one green/red tree, and that tree is
*designed* for reuse: immutable, width-based, position-independent, no parent pointers. The node-reuse
blender belongs in that shared layer, where VXML and VCSS would both get it.

Raven cannot use it. ANTLR has no notion of reusing an existing tree, so `SyntaxTree.WithChangedText`
reparses whole files — the API landed with `SourceText.WithChanges`, the optimisation could not. That
makes Raven the one front end unable to benefit from the shared investment, which inverts the point of
extracting it.

ADR-009's carve-out was that VXML needs sub-millisecond reparse and Raven does not. That does not
survive [doc 09](09-ui-framework.md), which specifies a `CodeEditor` with *"syntax highlighting via
`Vixen.Core.Syntax` (Raven/VXML/VCSS/C#)"*. People will type `.rvn` in that editor with live
squiggles. Same requirement, same solution.

### 2. "Error recovery is table-driven" is currently a liability

ADR-009 lists table-driven recovery as a reason **for** ANTLR. In practice `SyntaxTree.ParseText`
carries this:

```csharp
} catch when (!bag.IsEmpty) {
    // ANTLR error recovery can leave the tree with missing/synthetic tokens
    // that the visitor cannot map. […] surface those rather than the downstream NRE.
    syntaxTree.root = null;
}
```

The recovery produces trees the translator cannot handle, and the mitigation is to **discard the
tree**. That is the opposite of recovery: an IDE needs a tree with explicit missing/skipped tokens
that binding can still walk, which is precisely what a hand-written parser emits and what Roslyn does.

The messages match the machinery. Real output:

```
error RVN1001: Syntax error: missing '(' at 'i'
error RVN1001: Syntax error: no viable alternative at input 'const Never:'
error RVN1001: Syntax error: extraneous input '}' expecting {NL, '}', 'global', 'func', … 40 tokens …}
```

Serviceable for a CLI. Not what belongs under a squiggle.

### 3. The saving is smaller than it looks, and the cost is spread over four files

| | Lines |
|---|---|
| `RavenLexer.g4` + `RavenParser.g4` | 866 |
| `SyntaxAntlrVisitor.cs` | 1 490 |
| generated ANTLR (`RavenParser.cs` + `RavenLexer.cs`) | 12 973 |

`SyntaxAntlrVisitor` is a parser's worth of hand-written code that exists *only* to reconcile two tree
representations. A hand-written parser emits green nodes directly and the translator disappears —
along with the `catch` above.

Adding one piece of syntax currently touches **four** places: `RavenLexer.g4`, `RavenParser.g4`,
`Syntax.xml`, `SyntaxAntlrVisitor.cs`. Both `compose` and `val` type parameters were added that way.
A hand-written parser touches two.

The translator is also where the bugs were: a dead `VisitInteger_literal_token` calling
`long.Parse("0x1F")` (never dispatched, would have thrown), and `TerminalOf` throwing on an absent
optional token where `TerminalOrNull` was needed.

### 4. Grammar friction that produces no value

- `AC0125` — `MAT4X4` implicitly defined in the parser, an unreachable alternative. Removed.
- `DIRECTIVE_MODE` — a whole preprocessor apparatus in the lexer routing every directive token to a
  dropped channel, with `DIRECTIVE_IF`/`DIRECTIVE_ELSE` commented out. Vestigial; `#if` is
  [decided against](07-raven-shader-pipeline.md).
- `RavenLexerBase` — three empty hooks that exist only because the grammar's inline actions name them.
- `TerminalOf`/`TerminalOrNull`/`Commas(context)` — accessor plumbing for tokens ANTLR does not
  generate accessors for.

## What ANTLR is genuinely better at

Recorded honestly, because it is the reason for the timing below.

- **Grammar churn.** While the language is moving, editing a `.g4` alternation beats hand-editing
  descent functions. This premise of ADR-009 still holds.
- **The grammar as specification.** A `.g4` file is readable, checkable documentation of the syntax.
  This survives the migration — see [§ Keep the grammar](#keep-the-grammar-as-an-oracle).
- **It works.** 601 Raven tests pass, round-trip fidelity is byte-exact on the sample corpus. Nothing
  here is urgent.

## When, and what triggers it

Not now. In order:

1. **Let [§ C](07-raven-shader-pipeline.md) and [§ F](07-raven-shader-pipeline.md) settle the language
   surface.** Grammar churn is the one thing ANTLR wins, and writing the shader library is what will
   shake out the remaining syntax gaps. Migrating into a moving grammar pays the cost twice.
2. **Migrate before the editor's Raven code editor is built** ([doc 11](11-editor.md)), and before any
   external user depends on diagnostic message text. Both make ANTLR's recovery and messages load-bearing.
3. **Migrate before `.rvnlib`.** That work needs stable, trustworthy trees to serialise.

The trigger to watch for: the first requirement that needs sub-second reparse of a `.rvn` file while
someone is typing it. At that point ANTLR is blocking, not merely suboptimal.

## The migration

### Shape of the target

Into `Vixen.Core.Syntax` (shared, so VXML and VCSS get it):

| New | Purpose |
|---|---|
| `SlidingTextWindow` | character window over `SourceText` with peek/advance/mark-reset |
| `SyntaxParser` | base: token stream, lookahead, `EatToken`, `ExpectToken`, skipped-token handling |
| `Blender` | consumes `SourceText.GetChangeRanges` output and yields reusable green nodes |

Into `Vixen.Raven`:

| New | Replaces |
|---|---|
| `RavenLexer.cs` | `RavenLexer.g4` |
| `RavenParser.cs` | `RavenParser.g4` + `SyntaxAntlrVisitor.cs` |

The green/red tree, `Syntax.xml`, the generator, binding, lowering, IR and both backends are
**untouched**. This is a front-end swap, not a rewrite.

### Steps

1. **Freeze the corpus first.** Extend the golden-tree and round-trip fixtures until every construct in
   the grammar and every file in `Raven/Library/` is covered. This is the safety net, and it is worth
   doing even if the migration never happens.

   **This step has a prerequisite.** Four nodes do not carry their own tokens, so they cannot go into a
   round-trip corpus as they stand: `repeat`/`while`, a cast's parens, and the `self`/`base` keywords
   are all dropped, silently and with no diagnostic — see
   [07 § I](07-raven-shader-pipeline.md#i-gaps-carried-over-from-ravens-retired-implementation-plan).
   A frozen corpus that omits them is not a safety net, and it would let the migration "preserve"
   behaviour that is already wrong.
2. **`SlidingTextWindow` + `RavenLexer`.** Test against the ANTLR lexer: for every corpus file, both
   must produce the same token sequence — kinds, text, trivia. A token-stream differential is a cheap,
   total check.
3. **`SyntaxParser` base** with explicit missing/skipped tokens, so recovery is a first-class concept
   rather than an exception handler.
4. **`RavenParser`, one production at a time**, translating `RavenParser.g4` mechanically. The grammar
   is the specification; keep it open beside the code.
5. **Differential parse.** Parse every corpus file with both front ends and compare serialised trees.
   Byte-exact or the migration is not done. This is the same technique already planned for SPIR-V vs
   `shaderc` in [§ C](07-raven-shader-pipeline.md).
6. **Switch `SyntaxTree.ParseText`** to the new parser. Delete `SyntaxAntlrVisitor`, the ANTLR package
   references, `RavenLexerBase`/`RavenParserBase`, and the `catch when (!bag.IsEmpty)`.
7. **Then, and only then, `Blender`.** Incremental reparse is the payoff, not the migration. Landing it
   separately keeps one hard problem per change.
8. **Improve the diagnostics.** Replace "no viable alternative" with messages naming what was expected
   at that position. This is the user-visible reason for the whole exercise and should not be deferred
   indefinitely.

Steps 1–6 are behaviour-preserving by construction: same trees, same diagnostics IDs, same tests.
Steps 7–8 are the improvements.

### Keep the grammar as an oracle

**Do not delete the `.g4` files.** Keep them building in a test-only project and keep step 5's
differential parse as a permanent test. Benefits:

- The grammar stays as executable specification of the syntax.
- Any divergence between the hand-written parser and the declared grammar fails a test rather than
  shipping.
- It turns the migration from an act of faith into a verifiable one, and keeps it verified afterwards.

This mirrors the SPIR-V-vs-`shaderc` differential oracle: the second implementation earns its keep as
a check even after it stops being the primary.

### Effort and risk

Roslyn's `LanguageParser.cs` is on the order of ten thousand-plus lines, but C# is a far larger
language than Raven and is the wrong yardstick. Against a 513-line parser grammar:

| | Lines |
|---|---|
| new lexer + parser | +3 000 … 5 000 |
| delete `SyntaxAntlrVisitor.cs` | −1 490 |
| delete `.g4` from the shipping project | −866 |
| **net** | **+1 500 … 2 500 owned** |

Plus `SlidingTextWindow`/`SyntaxParser`/`Blender` in Core, which VXML and VCSS were going to need
regardless — so that cost was already committed by ADR-009's decision to hand-write those front ends.

**Risk is low, and specifically because of the oracle.** The token-stream differential (step 2) and the
tree differential (step 5) make correctness mechanically checkable against a working implementation,
on a corpus frozen before any code is written. The realistic failure mode is not a wrong tree; it is
the migration taking longer than expected and stalling half-done — which argues for doing it in one
focused stretch rather than interleaved with feature work.

## What this changes in the plan

- **[ADR-009](01-technology-decisions.md)** — amended. Its statement that "ANTLR is right for Raven"
  becomes "ANTLR was right for Raven's bootstrap".
- **[Doc 07 § A](07-raven-shader-pipeline.md)** — the note that incremental reparse "comes free from
  `Vixen.Core.Syntax`" is already corrected there; this document is the plan it points at.
- **[Doc 14](14-roadmap.md)** — the migration is a discrete phase, sequenced after the shader library
  and before the editor's code editor.
- **Nothing else.** Binding, lowering, IR, both backends, reflection and the artefact formats are
  behind the tree and do not know which parser built it.
