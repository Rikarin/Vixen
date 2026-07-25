# Raven — Implementation Plan

> Universal shader compiler (`.rvn` → GLSL, SPIR-V, later HLSL/Metal), with a
> Roslyn-inspired public API. This document is the roadmap for taking the project
> from "syntax frontend, ~70% wired" to a working compiler.

## Key architectural decisions

These were chosen deliberately and shape the whole plan:

1. **Syntax tree: full Roslyn green/red tree.** We build a true internal *green*
   tree (immutable, width-based, no parent/position) plus a lazily-constructed
   *red* tree (`SyntaxNode` with parent + absolute position). This is more work
   than storing raw offsets, but gives us Roslyn-grade spans, trivia fidelity,
   `WithX` immutability, and incremental reparse later.
2. **Backends share a target-independent IR from the start.** The bound tree is
   lowered to a Raven IR (SSA-friendly); GLSL, SPIR-V, HLSL, and Metal are all
   emitters over that IR. No backend reads the bound tree directly.
3. **ANTLR stays as the parser.** `RavenLexer2`/`RavenParser2` produce an ANTLR
   parse tree; `SyntaxAntlrVisitor` translates it into the green tree. We keep the
   Roslyn-shaped tree as the public API and let ANTLR own tokenizing/parsing.

## Current state (baseline)

| Area | Status |
|------|--------|
| Source generator (`Tools/SyntaxGenerator`, reads `Syntax.xml`) | Working; emits red tree (~3.3k lines, 83 concrete + 18 abstract nodes) |
| ANTLR grammar (`RavenLexer2`/`RavenParser2`) | Working; parses package/imports/example files |
| `SyntaxAntlrVisitor` | ~70% wired — 44 methods still `base.Visit` stubs (Shader, Method, most expressions) |
| `SyntaxToken` | Shell — no real text/value storage, no trivia |
| Spans / `Parent` / `SyntaxTree` back-ref | **Missing** |
| Diagnostics | **Missing** |
| Semantic model (symbols, types, binder) | Done in Phase 2 — see below |
| IR (target-independent) | Done in Phase 3 — see below |
| Code generation — GLSL | Done in Phase 4 — see below |
| Code generation — SPIR-V | Done in Phase 6 — see below |
| CLI | Done in Phase 5 — see below |
| Tests | 1 syntax test; can't run (targets `net8.0`, host has net10) |
| `_old_Antlr/` | Dead code (excluded from compile) |

---

## Phase 0 — Stabilize the foundation *(small)* — ✅ DONE

Goal: green build, running tests, CI.

- [x] Fix framework targeting so `dotnet test` runs — Compiler/Cli/Tests retargeted to `net10.0` (SyntaxGenerator stays `netstandard2.1`).
- [x] Silence generated nullability warnings at the source — fixed the `SyntaxGenerator` Update-method template (`WriteUpdateMethod` now mirrors the factory parameter types), clearing all generated CS8604. Warnings 107 → 4 (remaining are honest CS8618 in the `SyntaxToken`/`SyntaxTree` stubs, addressed in Phase 1). CS3021 is ANTLR-generated noise, suppressed narrowly.
- [x] Delete `Compiler/_old_Antlr/`.
- [x] Add CI (`.github/workflows/ci.yml`, build + test on push/PR to master).
- [x] Stand up a **golden-file parser harness** (`Tests/SyntaxDumper.cs`, `Tests/GoldenSyntaxTests.cs`): `.rvn` → serialized tree snapshot, diffed against `Tests/Fixtures/*.tree`. Regenerate with `UPDATE_GOLDEN=1`.

**Exit criteria met:** `dotnet test` runs green (2 passed, 1 skipped); golden harness in place.

> **Resolved in 1c:** `Test_SyntaxTree` is un-skipped and passing — `Example1.rvn`
> now parses and round-trips byte-for-byte. See Phase 1c below.

---

## Phase 1 — Complete the syntax layer *(critical path)* — **✅ complete**

Everything downstream needs a complete, span-carrying tree. This is the biggest
frontend investment and the chosen green/red design lives here.

> **Status:** 1a (green/red tree), 1b (tokens & trivia), 1c (full ANTLR→tree
> translation), and 1d (diagnostics) are all done. The frontend parses and
> losslessly round-trips real Raven — the full `Example1.rvn` — into a
> span-carrying green/red tree, and surfaces syntax errors as diagnostics.
> **108 tests, zero skipped.** Next: **Phase 2 — semantic model.**

### 1a. Green/red tree in the generator

**Design decisions (locked in):**
- **Token as class.** Red `SyntaxToken` stays a `SyntaxNode`-derived class wrapping a
  green token — keeps `GetSlot`/traversal/the dumper working with minimal ripple.
  (Deviation from Roslyn's value-type tokens; documented.)
- **Key insight that bounds the churn:** keep the *red-facing* helper/factory API
  stable — `SyntaxFactory.X(...)`, `SyntaxFactory.Identifier(...)`, `SyntaxList.List(...)`
  keep their signatures and only change *internals*. The ~60 `SyntaxAntlrVisitor`
  call sites then barely change; churn concentrates in the generator templates and
  hand-written base types.

**Progress:**
- [x] **Green foundation (`Compiler/Syntax/InternalSyntax/`)** — `GreenNode` (widths,
  slots, trivia-aware `Width`, `WriteTo`/round-trip), green `SyntaxToken`/`SyntaxTrivia`/
  `SyntaxList` + `SyntaxListBuilder`, and `Text/TextSpan`. Unit-tested (`GreenNodeTests`):
  width accounting, trivia separation, byte-for-byte round-trip. **Build green.**
- [x] `GreenNode.CreateRed(parent, position)`; implemented on hand green types + generated green nodes.
- [x] Generator: new `WriteInternal` emits green node classes (`InternalSyntax`) based on
  `GreenNode`, children typed as `GreenNode`, ctor `AdjustWidth`, `GetSlot`, `CreateRed`
  (`Syntax.xml.Internal.Generated.cs`).
- [x] Generator: rewrote `WriteSyntax` (red) to wrap green — typed accessors via a cached
  `GetRed(slot)`, `GetSlot` → red child, `Update`/`With` unchanged.
- [x] Generator: rewrote `WriteFactory` to build the green node from red params' `.Green`
  and return `(RedType)green.CreateRed(null, 0)`.
- [x] Hand red base: `SyntaxNode` = green + `Parent` + `Position` + lazy `GetRed` cache +
  `Span`/`FullSpan`/`ToFullString` + `SyntaxTree` back-ref; red `SyntaxToken` (green-backed);
  red `SyntaxListNode`; `SyntaxList.List` builds green + wraps red; `SyntaxFactory` token
  helpers green-backed. Visitor churn was minimal (only `new(kind)` token constructions).
- [x] Defined the missing `TypeParameterConstraintClauseSyntax` node properly in `Syntax.xml`
  (was a broken hand stub extending `SyntaxToken`).

**1a status: DONE.** Build + tests green (`RedTreeTests` proves lazy child caching + identity,
parent linkage to root, and ordered absolute positions; golden snapshot updated — empty lists
now project to `null` like Roslyn instead of empty list nodes). Generated green/red code is
warning-clean. **Note:** byte-for-byte source round-trip is deferred to **1b** — keyword/
punctuation tokens currently carry no text and trivia isn't captured yet, so `ToFullString()`
only reproduces identifier text so far. That is the next task.

### 1b. Real tokens & trivia — **infrastructure DONE, per-node rollout ongoing**

Decision: **full-fidelity CST** — every node carries all its tokens (keywords,
punctuation, terminators) as slots; newlines are modeled as `EndOfLineTrivia`
(the parser still consumes `NL` structurally, the tree demotes it to trivia).

- [x] Green `SyntaxToken` stores kind + exact text + typed value; leading trivia as a green node/list.
- [x] `SyntaxFacts.GetText(kind)` for canonical fixed-text tokens.
- [x] Stream-driven trivia in the ANTLR bridge: `SyntaxAntlrVisitor.Token(IToken, kind)` reads exact
  text and gathers preceding whitespace/comment/newline tokens as leading trivia
  (`GatherLeadingTrivia`/`MapTrivia`); `SyntaxTree.ParseText` fills the token stream and passes it in.
- [x] **Byte-for-byte round-trip proven** (`RoundTripTests`): the fully-wired subset
  (`CompilationUnit`/`PackageDirective`/`ImportDirective`/`QualifiedName`/`IdentifierName`, now
  carrying `PackageKeyword`/`ImportKeyword`/`DotToken`/`EndOfFileToken`) reproduces source exactly —
  multi-level names, multiple imports, `static`, blank lines, line + inline block comments.
- [ ] **Rollout:** apply the same pattern (add token slots in `Syntax.xml` + wire `Token(...)` in the
  visitor) to the remaining ~75 nodes. This merges with **1c** — each visitor gets un-stubbed and made
  full-fidelity together. Note: verbatim `@`-identifiers and left-recursion token accessors handled
  case-by-case (`TerminalOf` helper for tokens ANTLR doesn't expose).

### 1c. Finish the ANTLR → tree translation — **✅ done**

**Bigger than "un-stub 44 methods":** several nodes referenced by the grammar
aren't even defined in `Syntax.xml` yet (e.g. `ShaderDeclarationSyntax` was
missing). Each construct needs: define/extend its node (full-fidelity token
slots), then wire its visitor with `Token(...)`. Grammar note: the language uses
`shader`/`protocol` (no `class`), so `Example2.rvn`'s `class` won't parse as-is.

**Recipe proven end-to-end at all three tree levels** (compilation-unit, name,
and now member/type declaration):
- [x] `ShaderDeclarationSyntax` defined + `VisitShader_declaration` wired (attributes,
  modifiers, `shader` keyword, identifier, optional type-params/params/base-list/constraints,
  optional `{ … }` body, members). Added `VisitMember_declaration` to drop trailing `NL*`.
  Added kinds `ShaderKeyword`/`OpenBraceToken`/`CloseBraceToken`/`ShaderDeclaration` + `SyntaxFacts` text.
- [x] Round-trip proven for shaders (empty body, no body, multiple shaders).
- [x] **Field declarations**: `VisitField_declaration`/`VisitVariable_declaration` made full-fidelity
  (val/var keyword, `:` colon, `=` equals); `LiteralExpressionSyntax` defined + `VisitLiteralExpression`
  wired (numeric/true/false/null/default); `VisitModifier` + `VisitPredefinedType` made trivia-aware.
  Round-trip proven for `val x: int`, `const val M = 42`, bool/null/real literals.
- [x] **Method declarations**: `MethodDeclarationSyntax` defined + `VisitMethod_declaration` wired
  (`func` keyword, identifier, param list, optional return type, block or `=>` body). Made
  `ParameterList`/`Block`/`ArrowExpressionClause` full-fidelity (parens, braces, `=>`). Round-trip
  proven for block-body, arrow-body, and return-typed methods (empty param lists).
- [x] **Statements & expressions**: defined `Invocation`/`MemberAccess`/`Binary`/`AssignmentExpression`
  nodes; wired `VisitBinary/Assignment/Invocation/MemberAccess/Parenthesized/TypeExpression/NameType`.
  Made `ArgumentList`/`ParenthesizedExpression`/`ReturnStatement`/`IfStatement`/`ElseClause`/`ForStatement`
  full-fidelity. Round-trip proven for `return`, member access (`a.b.c`), assignment, binary, `if/else`,
  parenthesized, local decls. `RoundTripTests` now 29 cases.
- [x] **Separated lists**: added `SeparatedSyntaxList<T>` (green interleaves element/separator; red struct
  exposes elements at even slots) + generator support (`IsSeparatedNodeList`, rewriter pass-through) +
  visitor helpers (`Commas`, `SeparatedList<T>`). Converted `ArgumentList`/`ParameterList`/`BaseList`
  (and their abstract bases) to separated; made `Parameter`/`BaseList` carry `:`. Round-trip proven for
  multi-param methods, multi-base shaders (`shader S : X, Y`). *(Bracketed arg/param lists still build the
  non-interleaved form — commas lost there until wired.)*
- [x] **Grammar fix**: moved `#DeclarationExpression` below `#TypeExpression` so `name(args)` parses as an
  invocation (the `type` primary now outranks `type variable_designation`). `print(x)` and multi-arg
  `a.b(x, y)` now parse as invocations and round-trip. `RoundTripTests` now 34 cases.
- [x] **Declarations**: `protocol`, `enum` (+ members, separated), `init` constructor, `func` method,
  fields, `var` properties + `get`/`set`/`willSet`/`didSet` accessors — all defined + wired + round-tripped.
- [x] **Statements**: `return`, `if/else`, `for`, `while`, expression, local-declaration.
- [x] **Expressions**: literal, name, invocation, member access, element access, binary, assignment,
  prefix/postfix unary, range (`..`), ref, conditional (`?:`), null-coalescing (`??`), parenthesized.
- [x] **The hard/specialized set — all landed & round-tripping:**
  - **String & char literals** *(char later removed — see "Pruning the language")* — added
    `STRING_LITERAL`/`CHARACTER_LITERAL` to the lexer +
    `literal_expression`; new `String/CharacterLiteralExpression` kinds. (Interpolation deferred — see
    below; not present in `Example1.rvn`.)
  - **Lambdas** *(later removed)* — grammar `#SimpleLambdaExpression` (`x => e`) + `#ParenthesizedLambdaExpression`
    (`(a: int): ret => e`, reusing `parameter_list`), placed before `#TypeExpression` so ANTLR predicts
    them; no ambiguity warnings.
  - **Tuples** — full-fidelity `TupleType` (parens + separated elements) and `TupleExpression`.
  - **Collection expressions** (+ `..` spread) with brackets/separators.
  - **Patterns & switch** — constant / relational / unary(not) / binary(and/or) / parenthesized / list /
    slice / var / discard patterns, `is`-pattern expression, `switch` expression + arms + `when` clause;
    variable designations (simple / parenthesized / discard). All carry their real tokens.
  - **Generics** — `TypeArgumentList` and `TypeParameterList` made full-fidelity (`<`/`>` + separators);
    type-parameter **constraints** (`where T : Base, Other`, `default`).
  - **Nullable types** (`T?`) *(later removed)* — added `type '?'` to the grammar.
  - **Anonymous objects** (`{ a = 1, b = 2 }`) *(later removed)* — members parse as (assignment) expressions, so no
    `name_equals`/assignment ambiguity.
  - **Attributes** — full-fidelity `AttributeList` (brackets + separated), targeted specifiers
    (`[property: …]`), argument lists (`FooBar(test: "x")`) with `NameColon` (fixed to carry its `:`).
  - **Declarations** — `destructor` (`~init`), `indexer` (`self[…]`), `operator` overloads,
    `conversion operator` (`implicit`/`explicit`), explicit-interface method specifiers
    (`func Generic<int>.Test<Asd>()`), local functions (`func` keyword + `:` return type), and
    `struct`/`class`/`record` type declarations (new lexer keywords + grammar, mirroring `shader`;
    `record` added as a modifier).
- [x] **Grammar ambiguity resolved:** `a[i]` now parses as **element access**, not an array type —
  restricted `array_rank_specifier` to empty/comma-only rank (`T[]`, `T[,]`), so bracketed content
  can only be indexing.
- [x] **`Test_SyntaxTree` un-skipped and passing** — the full realistic `Example1.rvn` (attributes,
  properties w/ willSet/didSet, tuples, generics, explicit-interface
  methods, local functions, element access, string/char literals, nullable/array types,
  struct/class/record) round-trips **byte-for-byte**.
- [x] **100 tests pass, zero skipped**; clean rebuild, no generated-code warnings.
- [ ] **Deferred (not in `Example1.rvn`):** string **interpolation** (needs lexer modes for embedded
  expressions); sized array types as *type syntax* (dropped in favour of unambiguous element access).

Original priority list retained below:
  - **Expressions:** `Binary`, `Assignment`, `Invocation`, `MemberAccess`, `ElementAccess`, `Literal`, `Conditional`, `ConditionalAccess`, `Tuple`, `Collection`, `Range`, `Ref`, `TypeExpression`, `DeclarationExpression`.
  - **Members:** `Shader`, `Method`, `Constructor`, `Destructor`, `Property`, `Indexer`, `Operator`, `Conversion`, `Accessor`, `Enum`, `Protocol`, `Variable`, `Type`.
  - **Patterns/misc:** `ConstantPattern`, `BinaryPattern`, `ListPattern`, `SlicePattern`, `ParenthesizedPattern`, variable designations, `Switch`/`SwitchExpression`.
- [ ] Map ANTLR token offsets into green-node widths + trivia during construction.

### 1d. Diagnostics infrastructure — **✅ done**
- [x] **Text services:** `SourceText` (immutable snapshot + precomputed line index, `\r\n`/`\n`/`\r`
  aware) with `GetLinePosition`/`GetLinePositionSpan`; `LinePosition`/`LinePositionSpan` (zero-based,
  one-based `ToString`). `TextSpan` already existed.
- [x] **Core types** (`Compiler/Diagnostics/`): `DiagnosticSeverity` (Hidden/Info/Warning/Error),
  `Location` (file path + `TextSpan`, resolves to a `LinePositionSpan` via `SourceText`; `Location.None`),
  `DiagnosticDescriptor` (stable id/title/message-format/category/severity), `Diagnostic`
  (descriptor + location + args, `GetMessage()`, Roslyn-style `ToString`), `DiagnosticBag`
  (`Add`/`AddRange`/`HasErrors`/`ToArray`). Syntax descriptors in the `RVN1xxx` range
  (`RVN1001` syntax error, `RVN1002` invalid character) — kept clear of the generator analyzer's
  `RVN000x` ids.
- [x] **Error routing:** `RavenSyntaxErrorListener` implements `IAntlrErrorListener<int>` (lexer) and
  `IAntlrErrorListener<IToken>` (parser); `SyntaxTree.ParseText` removes ANTLR's console listeners,
  installs it, and exposes `SyntaxTree.Text` + `SyntaxTree.Diagnostics`. Parser errors take the
  offending token's offsets; lexer errors map line/column via `SourceText`. `ParseText` is now
  robust to ANTLR error-recovery trees (visitor exceptions are swallowed **only** when the bag
  already holds errors, so genuine visitor bugs on well-formed input still surface).
- [x] **Grammar hardening (surfaced by diagnostics):** the newline handling around braces was
  over-constrained (`'{' NL+ … NL+ '}'` clashed with members' own trailing `NL+`), so *valid*
  programs silently produced error-recovered parses. Relaxed type-decl/enum/switch bodies to
  `NL*` (+ `NL*` after separators). Now every valid program parses with **zero** diagnostics.
- [x] **Tests:** `DiagnosticsTests` (valid → none; parser error → `RVN1001`/Error/correct span;
  invalid char → error at its span; one-based line/col; `SourceText` offset→line/col). The full
  round-trip corpus **and** `Example1.rvn` now additionally assert `Diagnostics` is empty.
  **108 tests pass, zero skipped**; clean rebuild, no warnings.

**Exit criteria — met:** `Example1.rvn` and the round-trip corpus fully parse into a complete tree
with **no diagnostics**; round-trip tests pass; syntax errors surface as diagnostics with correct
spans and stable ids. *(`Example2.rvn` still uses constructs beyond the current grammar — tracked
separately, not a 1d blocker.)*

---

## Phase 2 — Semantic model *(the core)* — **✅ complete**

Model the public shape on Roslyn: `Compilation` → `SemanticModel` → symbols.
This is the "semantic passes" the README flags as the hard, fun part.

> **Status:** 2a, 2b and 2c are done. A well-formed shader — fields, uniforms,
> textures/samplers, intrinsics, entry points — binds with **zero diagnostics**
> and exposes correct symbol/type info; malformed programs produce targeted
> `RVN2xxx` errors. **257 tests, zero skipped.** Next: **Phase 3 — lowering to IR.**
>
> **Deviation from the plan (documented):** the public symbol API is the abstract
> class hierarchy (`Symbol`, `TypeSymbol`, `MethodSymbol`, …) rather than a split
> of `ISymbol` interfaces over internal implementations. Roslyn's split exists to
> keep its internal model private across assembly boundaries; Raven has one
> compiler assembly, and the split would have doubled the surface for no gain.
> Interfaces remain a mechanical wrapper if the public API ever needs them.

### 2a. Symbols & type system — **✅ done**
- [x] `Symbol` hierarchy (`Compiler/Symbols/`): `NamespaceSymbol` (packages), `NamedTypeSymbol`
  (shader/struct/class/protocol/enum), `MethodSymbol`, `FieldSymbol`, `PropertySymbol`,
  `ParameterSymbol`, `LocalSymbol`, `TypeParameterSymbol` — plus `Synthesized*` variants for
  compiler-supplied members and `Substituted*` views for generic instantiation.
- [x] `TypeSymbol` + built-ins (`BuiltInTypes`): scalars (`bool`/`int`/`uint`/`long`/`float`/
  `double`/`char`), vectors (`bool2..4`, `int2..4`, `uint2..4`, `float2..4`, `double2..4`),
  matrices (`mat2` … `mat4x3`), `string`/`object`, and the resource types `Texture2D`/
  `Texture3D`/`TextureCube`/`Sampler`. Structural types: `ArrayTypeSymbol`,
  `TupleTypeSymbol`,
  `SequenceTypeSymbol` (ranges), `ErrorTypeSymbol`, `NullTypeSymbol`.
- [x] **Vector swizzles** as synthesized members: `v.x`, `v.xy`, `c.rgb`; a swizzle over
  distinct lanes is assignable, `v.xx` is not.
- [x] Generics: type parameters with `where` constraints, `ConstructedNamedTypeSymbol` +
  `TypeMap` so `Box<int>.value` really is `int`. Explicit type arguments only — **no
  inference** (`Identity<int>(x)` works, `Identity(x)` does not).
- [x] Conversions (`Conversions`/`Conversion`): identity, widening numeric, scalar→vector
  splat, constant-literal fit, nullable wrap/unwrap, null literal, reference/boxing, and the
  explicit counterparts. Each carries a `Cost` that ranks overload candidates.
- [x] Member lookup walks the type, its base type and its protocols, nearest first.

### 2b. Compilation & binder pipeline — **✅ done**
- [x] `Compilation`: owns the syntax trees, builds the symbol table, hands out one
  `SemanticModel` per tree, and aggregates syntax + declaration + binding diagnostics
  ordered by file and position.
- [x] **Declaration pass:** package directives build the namespace chain; type declarations
  become `SourceNamedTypeSymbol`s. Everything past a name is lazy — members are created
  without signatures, and each signature resolves on first read. That ordering is what lets
  a type refer to its own members while its members refer back to the type.
- [x] **Binder chain:** `global → imports → type → member → block…` (`Binder` + `GlobalBinder`,
  `ImportBinder`, `NamedTypeBinder`, `MemberBinder`, `BlockBinder`, `ContextBinder`). Imports
  resolve lazily, after every declaration exists.
- [x] **Binding pass:** a full bound tree (`BoundNode`, 24 expression kinds + 13 statement
  kinds) with resolved symbols, types, cost-based overload resolution (positional, named and
  defaulted arguments), built-in operator resolution including `mat * vec` / `mat * mat`, and
  every conversion materialized as a `BoundConversionExpression`.
- [x] **Semantic diagnostics** (`SemanticDiagnostics`, `RVN2xxx`): undefined names and members,
  unresolved types, arity and type-argument errors, duplicate declarations, conversion and
  operator failures, non-`bool` conditions, `return` mismatches, assignment to read-only,
  non-indexable and non-iterable values, circular and cyclic definitions. The error type
  absorbs downstream uses, so one mistake reports once.
- [x] `SemanticModel` API: `GetSymbolInfo`, `GetTypeInfo` (declared **and** converted type),
  `GetDeclaredSymbol`, plus `GetBoundNode` for inspection.

### 2c. Shader-specific semantics — **✅ done (bar the parts that need grammar work)**
- [x] Entry points / stages: `[VertexShader]`, `[PixelShader]`, `[GeometryShader]`,
  `[ComputeShader]` → `MethodSymbol.Stage`, surfaced by `Compilation.GetEntryPoints()`.
  Diagnostics for duplicate stages, generic entry points and stage attributes outside a shader.
- [x] Resource bindings: `FieldSymbol.ResourceKind` classifies shader fields as
  `Uniform`/`Texture`/`Sampler`; a resource declared outside a shader is an error.
  `Texture2D.Sample(sampler, uv)` and friends are built-in members.
- [x] Input/output semantics: `[Semantic("POSITION")]` → `SemanticName` on fields, methods
  (the return semantic) and parameters.
- [x] **Intrinsic function library** (`Intrinsics`): element-wise maths over float scalars and
  vectors, integer `abs`/`min`/`max`/`clamp`, `dot`/`cross`/`length`/`distance`/`normalize`/
  `reflect`/`refract`, `lerp`/`mix`/`smoothstep`/`step`, `all`/`any`, and a generated
  `mul`/`transpose` table over every matrix shape. They are global `MethodSymbol`s, so calls
  go through ordinary overload resolution.
- [ ] **Deferred (needs grammar support):** stream I/O declarations between stages, and
  `Buffer<T>`-style resources (the built-in named types are not generic yet).
- [ ] *(Optional, later)* flow analysis: definite assignment, reachability.

**Exit criteria — met:** `ShaderSemanticsTests.A_realistic_shader_binds_with_no_diagnostics`
binds a Lambert shader (uniforms, texture + sampler, intrinsics, two entry points) with zero
diagnostics and correct types; `SemanticDiagnosticsTests` covers 24 targeted error cases.

### Defects in earlier phases found while building Phase 2

These are **Phase 1 grammar bugs**:

1. ~~**Expression precedence is inverted.**~~ **Fixed.** ANTLR gives a left-recursive rule's
   alternatives *decreasing* precedence in the order written, but `RavenParser2.g4`'s
   `expression` rule listed assignment first (binding tightest) and invocation/indexing/member
   access last (binding loosest), so `1 + f(x)` parsed as `(1 + f)(x)` and `x = a + b` as
   `(x = a) + b`.
2. ~~**All binary operators share one precedence level.**~~ **Fixed.** `1 + 2 * 3` parsed as
   `(1 + 2) * 3`.
3. ~~**`attribute_list` requires a trailing `NL+`**, so parameters cannot carry inline
   attributes (`func f([Semantic("TEXCOORD0")] uv: float2)`).~~ **Fixed:** the newline is no
   longer part of `attribute_list`; a declaration spells its attributes
   `(attribute_list NL*)*` and a parameter `attribute_list*`, so parameter semantics are
   readable (`ShaderSemanticsTests.Stage_io_semantics_are_read_off_declarations`).
4. ~~**Method declarations require a body**, so a bodiless `protocol` member (`func Draw()`)
   does not parse — which makes protocols much less useful than intended.~~ **Fixed:** the
   body of `method_declaration` and the accessors of `property_declaration` are optional
   (`SymbolTests.Protocol_members_declare_a_signature_without_a_body`). A bodiless `init` is
   still not accepted — protocol initialiser requirements are a language question, not a
   grammar oversight.

**1 and 2 were fixed by restructuring the `expression` rule** into a proper precedence
ladder, written tightest-first: postfix (invocation, element access, member access, postfix
unary) → prefix unary → cast → multiplicative → additive → shift → range → relational →
`is`/`as` → equality → `&` → `^` → `|` → `&&` → `||` → `??` → switch expression →
conditional `?:` → assignment, with the primaries last (their order among
themselves only resolves ambiguity — a collection literal must outrank an implicit element
access, a cast must outrank a parenthesized expression). Assignment, `??` and `?:` are
marked `<assoc=right>`. ANTLR accepts the shared `#BinaryExpression` label across the
operator levels and merges them into one context class, so `SyntaxAntlrVisitor` needed no
change. `Tests/ExpressionPrecedenceTests.cs` pins the resulting shapes and
`Tests/Fixtures/expression_precedence.rvn` gives the golden harness expression coverage;
`package_imports.tree` was unchanged, and the round-trip corpus still passes byte-for-byte.

---

## Phase 3 — Lowering to shared IR — **✅ complete**

> **Status:** the IR, the lowering pass, the verifier and the textual dump are
> done. A realistic shader lowers to IR that verifies clean and round-trips
> through a golden snapshot. **314 tests, zero skipped.** Next: **Phase 4 — GLSL
> backend**, which is the first consumer of `IrModule`.

### 3a. The Raven IR *(`Compiler/IR/`)* — **✅ done**
- [x] **Type model** (`IrType`): a deliberately small closed set — `void`/`bool`/`i32`/`u32`/
  `f32`/`f64`, vectors, matrices, arrays, nominal structs, textures and samplers. Everything
  else is rejected during lowering rather than in an emitter, so a backend can switch on
  `IrTypeKind` exhaustively.
- [x] **SSA values** (`IrValue`): every instruction defines one value, numbered per function
  and never reassigned. What mutates is memory.
- [x] **Variables and places** (`IrVariable`, `IrPlace`, `IrAccess`): storage is a variable
  plus an access chain of field / index / swizzle steps — SPIR-V's access chain, which also
  emits directly as `a.b[i].xy` in a source-level target. `IrExtractInstruction` covers the
  non-addressable case (reading a field out of a call result).
- [x] **Instructions**: constant, load, store, unary, binary, convert, intrinsic, call,
  composite construct, extract, select.
- [x] **Structured control flow** (`IrIfStatement`, `IrLoopStatement`) rather than a basic-block
  graph: SPIR-V requires structured merges in shaders and the source-level targets want the
  same shape. A loop carries its condition block, the value to test, the body, and the step a
  `for` runs before re-testing.
- [x] **Shader shape** (`IrShader`): bindings with per-kind slots, an initializer block for
  bindings with declared defaults, entry points with their stage IO, and the functions.

**Deviation from the plan, documented:** the plan said "SSA-friendly", and this is the
LLVM-pre-mem2reg reading of that — instructions are SSA, locals stay in memory behind
explicit loads and stores. That is what both target families want to consume, and a mem2reg
pass can promote later if a backend prefers registers.

### 3b. Lowering pass *(`Compiler/Lowering/`)* — **✅ done**
- [x] **Erasure:** a shader's fields have no runtime object, so `self.scale` becomes a global
  binding and `self` disappears. A struct's methods take the receiver explicitly, and a
  struct's constructor returns the value it builds (the IR has no by-reference parameters).
- [x] **Explicitness:** every conversion is an `IrConvertInstruction`, every read a load,
  every write a store. Scalar-to-vector widening becomes `convert.splat`.
- [x] **Desugaring:** `for … in` over a range becomes a counted loop with the bound hoisted;
  over an array, a counted loop over indices. Compound assignment and `++`/`--` become load,
  operate, store — reading the target before the right-hand side, as the source language does.
- [x] **Flattening:** member access and swizzles become access chain steps.
- [x] **Intrinsic resolution:** an overload of the intrinsic library becomes a single
  `IrIntrinsic` opcode, and `mul` becomes the `matrixMultiply` operator.
- [x] **Constant folding** of `const` fields and of literals used at another numeric type.
- [x] **Diagnostics** (`LoweringDiagnostics`, `RVN3xxx`): `RVN3001` for a type with no GPU
  representation (tuples, for now), `RVN3002` for a construct lowering does not implement
  (local functions, user-defined operators, switch expressions, patterns), `RVN3003` for an
  unaddressable assignment target, `RVN3004` for a member with no body. The list is short
  because most of what used to land here was removed from the language instead — see
  "Pruning the language" below.

### 3c. Verifier and dump — **✅ done**
- [x] **`IrVerifier`**: values defined once and used only where they are in scope (a value
  defined inside an `if` branch does not escape it), operand and result types on every
  instruction, well-formed access chains, call arity and argument types, `break`/`continue`
  only inside a loop, a value-returning function that cannot fall off the end, one entry point
  per stage, and no two bindings sharing a slot.
- [x] **`IrPrinter`**: a stable, deterministic textual dump — no hash codes, no dictionary
  ordering, invariant number formatting — used by the golden test and for debugging.

**Exit criteria — met:** `Tests/Fixtures/lambert.rvn` (struct, uniforms, a texture and
sampler, a binding initializer, a counted loop, a conditional, swizzles, an intrinsic call and
two entry points) lowers to IR that the verifier accepts with zero diagnostics, and its dump
is pinned in `Tests/Fixtures/lambert.ir`.

### Not lowered yet

Deliberate gaps, each of which reports rather than miscompiling:

- **Short-circuit `&&`/`||`.** They lower to `logicalAnd`/`logicalOr`, which evaluate both
  operands. Sound for the side-effect-free expressions shaders are made of; making them
  branch is Phase 4 work if a backend needs it.
- **`?:` lowers to `select`,** which also evaluates both arms, matching SPIR-V's `OpSelect`.
- **Stream I/O declarations between stages** and **`Buffer<T>`** — still blocked on grammar
  and generic built-in types respectively (see Phase 2c).
- **Local functions, user-defined operators, conversion operators, indexers, destructors,
  patterns, switch and tuples** — reported as `RVN3002`/`RVN3001`. All of these *are*
  implementable on a GPU (hoisting, plain calls, `switch`, synthesized structs); they are
  simply not lowered yet.

---

## Pruning the language *(after Phase 3)*

Phase 3 made the boundary concrete: lowering had to reject a list of constructs the binder
happily accepted. Rather than carry them as permanent `RVN300x` errors, the ones that can
**never** work on a GPU were removed from the language outright — grammar, syntax tree,
symbols, binder and lowerer.

**Removed:**

| Construct | Why |
|-----------|-----|
| Lambdas (`x => e`, `(a: int) => e`) | No function pointers, no closures |
| Nullable types `T?`, `null`, `??`, `??=`, postfix `!` | There are no null references |
| Anonymous objects (`{ a = 1 }`) | No boxing, no dynamic dispatch |
| `char` and character literals | No character type |
| `long` (and the `l`/`L` literal suffix) | No 64-bit integers |
| `object` | No common base, no boxing |
| `string` **as a type** | No string values on a GPU |

String *literals* survive as syntax: attribute arguments such as `[Semantic("SV_Target")]`
are compile-time metadata read straight off the syntax, never bound. Using one as a value is
`RVN2025`. An integer literal too large for `int` now takes the `uint` shape instead of
widening to a type that no longer exists.

**Deliberately kept**, because they *are* implementable on a GPU even though lowering does
not do them yet: local functions (hoist to module scope), user-defined and conversion
operators (plain calls — vector maths on user types wants these), indexers (calls), tuples
(synthesized structs), and `switch`/patterns (both GLSL and HLSL have `switch`).

`Tests/RemovedConstructsTests.cs` pins every removal, and
`Tests/ReadmeExampleTests.cs` compiles the README's language example so the docs cannot
drift from the language.

### Full-fidelity gaps found while rewriting the sample

`Library/Example1.rvn` must round-trip byte-for-byte, which surfaced three nodes that do not
carry all their tokens. They were never in the round-trip corpus, so nothing caught them:

- `RepeatStatementSyntax` — no `repeat`/`while` keywords or parens
- `CastExpressionSyntax` — no parentheses
- `SelfExpressionSyntax` / `BaseExpressionSyntax` — no keyword token

Each needs token slots in `Syntax.xml` plus visitor wiring, the same recipe Phase 1b used.

---

## Phase 4 — GLSL backend *(first target)* — **✅ complete**

The README's "easiest, just a transpiler" — but built over the IR, not the bound tree.

> **Status:** the backend interface, the GLSL emitter and the golden tests are done.
> `Tests/Fixtures/lambert.rvn` generates a vertex and a fragment unit, both pinned as
> goldens, and the README's language example reaches GLSL. **356 tests, zero skipped.**
> Next: **Phase 5 — CLI**, which wires this to a command line.

- [x] **Backend interface** (`Compiler/CodeGen/ITargetBackend.cs`): a backend takes an
  `IrModule` and a `DiagnosticBag` and returns one `GeneratedSource` per entry point. It
  never sees the bound tree or the syntax tree, so a new target is one new implementation and
  nothing else. `TargetBackends.Create("glsl")` resolves one by name.
- [x] **GLSL emitter** (`Compiler/CodeGen/Glsl/`):
  - **Types** (`GlslTypes`): scalars, `vec`/`ivec`/`uvec`/`bvec`/`dvec`, matrices, structs,
    combined samplers. Matrices flip — Raven's `matRxC` is R rows by C columns, GLSL's
    `matCxR` is C columns by R rows — which is exactly what keeps `m * v` meaning the same
    thing in both languages. Identifiers that collide with GLSL keywords or `gl_` are mangled.
  - **Intrinsics** (`GlslIntrinsics`): a name table plus the handful that need a shape change
    — `saturate` expands to `clamp(x, 0, 1)`, `atan2` folds into GLSL's two-argument `atan`,
    `Sample` becomes `texture`, `Load` becomes `texelFetch`, `ArrayLength` becomes `.length()`.
  - **Bindings**: uniforms go into one `layout(std140, binding = 0)` block; textures follow at
    the next binding indices. Vector comparisons become GLSL's componentwise functions
    (`lessThan` and friends), because `<` on a vector is not GLSL.
  - **Entry points**: each becomes its own translation unit containing only the functions that
    stage reaches. Stage inputs are `layout(location = N) in` globals, a vertex `vec4` result
    goes to `gl_Position`, and anything else to a located `out`. `main()` threads the globals
    into the user's function.
  - **Constants are inlined** at every use rather than named — they are pure, so it is always
    safe, and it removes most of the noise from an SSA-shaped emission.
- [x] **Golden tests** (`Tests/GoldenGlslTests.cs`, `Tests/Fixtures/lambert.{vert,frag}.glsl`)
  plus 36 unit tests over the mapping, and `Tests/ReadmeExampleTests.cs` runs the README's
  example through the whole pipeline into GLSL.

### What GLSL cannot mirror

Each of these is reported rather than silently mishandled:

- **Standalone samplers** (`RVN4003`, info). GLSL outside Vulkan has no separate sampler
  object, so a texture binding becomes a combined `sampler2D` and the sampler binding folds
  into it. Nothing of the shader's meaning is lost, but the binding table changes shape, so
  it is said out loud.
- **Binding defaults.** A GLSL uniform cannot carry an initializer, so a binding's declared
  default stays host-side data on `IrShader.Initializer`; the generated unit carries a comment
  saying so.
- **Unsized arrays** (`RVN4001`, error). GLSL only allows a runtime-sized array as the last
  member of a storage block, and the IR has no way to say that yet.
- **The compute stage** (`RVN4002`, error). It needs a workgroup size and nothing in the
  language declares one.

**Loops** deserve a note. GLSL's `continue` jumps to the top of the loop body, so a counted
loop's step has to live there rather than after the body. The emitter hoists the step — and,
for `repeat`, the condition — behind a first-iteration flag, which is what makes `continue`
land in the right place in every form.

**Exit criteria — met, with one caveat:** the README example generates a vertex and a
fragment unit with no errors, and both goldens are pinned. `glslangValidator` is **not
installed on this machine**, so `GoldenGlslTests.Passes_glslang` reports that it skipped
validation rather than pretending to have run it. Install glslang (`brew install glslang`) and
re-run to close that last gap.

---

## Phase 5 — CLI (end-to-end) — **✅ complete**

> **Status:** `raven compile --target glsl <input> <output>` runs the whole pipeline and
> writes GLSL. Diagnostics render with the source line and a caret under the span.
> **381 tests, zero skipped.** Next: **Phase 6 — SPIR-V**, the second backend over the
> same IR.

- [x] **Real `raven` CLI** (`Cli/`, System.CommandLine 2.0, assembly name `raven`):
  - `RavenCommand` builds the command surface: the two positional arguments plus
    `--target`/`-t` (constrained to `TargetBackends.Names`, so an unknown target is a parse
    error with the known ones listed), `--emit-ir`, `--verbose`/`-v`, `--no-color`.
  - `CompileDriver` is the pipeline — parse, bind, lower, verify, generate, write — and takes
    its two `TextWriter`s rather than touching the console, so the tests drive it exactly as
    the command does.
  - **Each stage stops on its own errors.** A parse failure never cascades into a wall of
    semantic noise, which is the whole reason the stages report separately.
  - **Output resolution.** A path with an extension names a file, and then the shader must
    produce exactly one unit; anything else is a directory and gets one file per stage
    (`Lambert.vert.glsl`, `Lambert.frag.glsl`). A two-stage shader aimed at a single file is
    an error rather than a guess at a second name.
  - `--verbose` names the files as they are written; a successful run is otherwise silent.
    Diagnostics go to stderr, so stdout stays clean.
- [x] **Diagnostic rendering** (`Compiler/Diagnostics/DiagnosticFormatter.cs`) — in the
  compiler, not the CLI, because a library consumer wants the same output:

  ```
  Lambert.rvn(6,16): error RVN2010: The name 'nrmalize' does not exist in the current context

    6 |     return nrmalize(v)
      |            ^^^^^^^^
  ```

  A span running past its line underlines what fits; an empty span still gets one caret; the
  caret row copies the source's leading whitespace verbatim, so a tab-indented line lines up
  in any terminal. Colour is opt-in and the CLI turns it on only for a real terminal —
  `--no-color`, a redirected stderr, `NO_COLOR` or `TERM=dumb` each turn it off. This needed
  `Location.SourceText` and `SourceText.GetLineText`, neither of which existed.
- [x] **Exit codes** (`Cli/ExitCode.cs`): `0` success, `1` the input produced errors, `2` the
  command line or a path was wrong. A build script can tell "you invoked me wrong" from "the
  shader is wrong", which one code for both would hide.
- [x] **Tests** — `Tests/CliTests.cs` (15, each in its own scratch directory, driving the real
  command through `RavenCommand`) and `Tests/DiagnosticFormatterTests.cs` (9).

**A one-input command line.** `<input>` takes one file, not a list: a variadic argument
followed by another positional one cannot be split unambiguously, and `<output>` is
positional in the documented usage. `CompileDriver` still takes a list, because a compilation
is many trees — the day multi-file compilation matters, it arrives as an option rather than
by making the positional arguments ambiguous.

**Found and fixed while wiring this up:** the GLSL backend reported the folded-sampler
`RVN4003` once per *entry point*, so a two-stage shader said it twice. It is a property of the
shader, not of any one stage, so it moved from `GlslEmitter` up to `GlslBackend`. The unit
tests had been papering over it with `.Distinct()`.

**Exit criteria — met:** the README's documented invocation works front to back, pinned by
`CliTests.The_documented_invocation_works_front_to_back`.

---

## Phase 6 — SPIR-V backend *(second big lift)* — **✅ complete**

> **Status:** `raven compile -t spirv Lambert.rvn out/` writes `.spv` that
> `spirv-val --target-env vulkan1.0` accepts. **465 tests, zero skipped**, and every SPIR-V
> module any test produces is put through the reference validator. Next: **Phase 7 —
> interaction classes, HLSL and Metal**.

The second target over the same `IrModule`, and the one that proves the boundary was worth
drawing: the emitter never looks at the bound tree, and nothing in Phases 1–3 had to move.

- [x] **Module builder** (`Compiler/CodeGen/Spirv/SpirvModule.cs`, `SpirvInstruction.cs`):
  instructions are held as opcode + result type + result + operands rather than as raw words,
  so **the same objects both encode the binary and render the listing** — a golden file over
  the listing cannot say one thing while the bytes hold another. Instructions go into the ten
  sections the spec's logical layout demands, and ids, types, constants and pointer types are
  interned (SPIR-V makes two `OpTypeFloat 32` instructions *invalid*, not merely wasteful).
- [x] **Types and layout** (`SpirvTypes.cs`, `Std140Layout.cs`): SPIR-V has no implicit memory
  layout, so a uniform block carries `Offset` on every member, `MatrixStride` and `ColMajor`
  on every matrix and `ArrayStride` on every array — all computed from Vulkan's standard
  uniform layout rules. Types come in two flavours, plain and explicitly laid out, because
  Vulkan will not accept one type serving both roles; the flag propagates down through
  members.
- [x] **Emitter** (`SpirvEmitter.cs`, `SpirvEmitter.Instructions.cs`): structured control flow
  becomes basic blocks with `OpSelectionMerge`/`OpLoopMerge`, locals become `OpVariable`s
  reached through access chains, and an entry point gets a generated `main` that threads the
  stage globals into the user's function. Functions are emitted **callee-first**, so a call
  never points forward.
- [x] **Validation** (`Tests/SpirvValidationTests.cs`, 23 shapes; `Tests/SpirvBackendTests.cs`,
  35 cases; `Tests/GoldenSpirvTests.cs`): everything goes through `spirv-val`. The golden test
  also **cross-checks the listing against `spirv-dis`** — if the two agree on the whole opcode
  sequence, the words that were encoded are the words the listing claims, which is the one
  thing a hand-written encoder can get wrong without anything else noticing.

### Where SPIR-V is the easier target

GLSL outside Vulkan has no standalone sampler, so the GLSL backend folds a `Sampler` binding
into the texture and says so (`RVN4003`). **SPIR-V has `OpTypeSampler`**: the texture and the
sampler stay two descriptors and meet only at `OpSampledImage`, so nothing is dropped.

Loops are the other one. GLSL's `continue` jumps to the top of the loop *body*, so a counted
loop's step has to be hoisted there behind a first-iteration flag. **SPIR-V names the continue
target**, so the step simply goes in that block and `continue` branches straight to it.

### What SPIR-V will not take

- **A boolean in a uniform** (`RVN4001`). `OpTypeBool` has no size and no memory layout, so it
  cannot live anywhere the host can see. GLSL hides this by giving it four bytes in a `std140`
  block; SPIR-V does not, and the validator says so.
- **A boolean or an aggregate as stage I/O** (`RVN4001`) — same reason for the boolean; an
  aggregate would need a location for every leaf.
- **Unsized arrays** (`RVN4001`) and **the compute stage** (`RVN4002`), exactly as in GLSL.
- **Binding defaults** (`RVN4003`, info) — a descriptor-backed variable cannot carry an
  initializer, so the declared default stays host-side data.
- **Reading a whole struct out of a uniform block** (`RVN4002`). Its laid-out type is a
  different type from the plain one, so it needs a member-by-member copy that is not built
  yet. Field-by-field reads, which is what the lowerer actually produces, are unaffected.

### Defect found while building Phase 6

**`m[i]` means a row in the IR and a column in every target.** `IrIndexAccess.ResultType`
hands back `IrMatrixType.RowType` — a vector of `Columns` lanes — and the binder agrees
(`Binder.BindElementAccess` types `m[i]` as `Vector(component, matrix.Columns)`). But GLSL and
SPIR-V both index a matrix by *column*, so for `var m: mat2x3` the GLSL backend emits
`vec3 _1 = m[0];` against a `mat3x2` — which glslang rejects — and the SPIR-V backend built an
access chain whose result type did not match its base, which `spirv-val` rejects. The same
convention question affects **constructing a matrix from a flat run of scalars**: the IR
documents `mat3(a…i)` as rows, GLSL fills columns.

This is a **Phase 3 convention bug, not a Phase 6 one**, and settling it is a language
decision (HLSL indexes rows, GLSL indexes columns) rather than an emitter fix. Phase 6 does
not paper over it: the SPIR-V backend now **refuses** to index a matrix (`RVN4002`,
`SpirvBackendTests.Indexing_a_matrix_is_refused_because_the_conventions_disagree`) rather than
emit a module the validator would reject. The GLSL backend still emits the wrong thing
silently. Matrix *construction* is emitted the way GLSL does it in both backends, so the two
targets at least agree with each other.

**Exit criteria — met:** `Tests/GoldenSpirvTests.Passes_spirv_val` puts both stages of the
README example through `spirv-val --target-env vulkan1.0`, and
`ReadmeExampleTests.The_readme_language_example_reaches_valid_spirv` does the same for the
README's own listing. SPIR-V Tools v2026.2 is installed on this machine, and
`SpirvBackendTests.The_validator_is_installed_so_these_tests_mean_something` fails loudly if
it ever is not — a silent skip would make every other SPIR-V assertion vacuous.

---

## Phase 7 — Interaction classes & extensibility

- [ ] **Stride-style interaction/effect class generation**: read the semantic model, emit C# classes exposing shader parameters/resources for host-engine binding.
- [ ] Formalize the backend plugin surface; add **HLSL** and **Metal** emitters over the IR.
- [ ] **Package manager: deferred** — the README itself calls it overkill; revisit only after multi-target codegen is solid.

---

## Sequencing & dependencies

```
Phase 0 ─▶ Phase 1 ─▶ Phase 2 ─▶ Phase 3 ─▶ Phase 4 ─▶ Phase 5
                                    │
                                    └──────▶ Phase 6 (SPIR-V, parallel to 4/5 once IR exists)
                                                   │
                                                   └─▶ Phase 7 (interaction classes, HLSL, Metal)
```

Critical path to a **first working transpile** (`.rvn → GLSL` via CLI): Phases 0 → 1 → 2 → 3 → 4 → 5. Phase 1 was the largest single investment; Phase 2 was the highest-risk research work. **Phases 0–6 are done** — `raven compile Lambert.rvn out/` writes GLSL and `-t spirv` writes validated SPIR-V. Phase 6 is the evidence that the boundary was drawn in the right place: a second target that consumes `IrModule` and nothing else needed no change anywhere in Phases 1–3. Phase 7 adds HLSL and Metal the same way.

## Cross-cutting workstreams

- **Diagnostics catalog:** stable IDs + message templates, grown per phase.
- **Testing:** golden files at every stage (tree, IR, GLSL, SPIR-V) + unit tests for binder/type rules.
- **Docs:** language reference and public-API docs as the surface stabilizes.
