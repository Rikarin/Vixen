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
| IR + code generation (GLSL/SPIR-V) | **Missing** |
| CLI | `Hello, World!` stub |
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
  - **String & char literals** — added `STRING_LITERAL`/`CHARACTER_LITERAL` to the lexer +
    `literal_expression`; new `String/CharacterLiteralExpression` kinds. (Interpolation deferred — see
    below; not present in `Example1.rvn`.)
  - **Lambdas** — grammar `#SimpleLambdaExpression` (`x => e`) + `#ParenthesizedLambdaExpression`
    (`(a: int): ret => e`, reusing `parameter_list`), placed before `#TypeExpression` so ANTLR predicts
    them; no ambiguity warnings.
  - **Tuples** — full-fidelity `TupleType` (parens + separated elements) and `TupleExpression`.
  - **Collection expressions** (+ `..` spread) with brackets/separators.
  - **Patterns & switch** — constant / relational / unary(not) / binary(and/or) / parenthesized / list /
    slice / var / discard patterns, `is`-pattern expression, `switch` expression + arms + `when` clause;
    variable designations (simple / parenthesized / discard). All carry their real tokens.
  - **Generics** — `TypeArgumentList` and `TypeParameterList` made full-fidelity (`<`/`>` + separators);
    type-parameter **constraints** (`where T : Base, Other`, `default`).
  - **Nullable types** (`T?`) — added `type '?'` to the grammar.
  - **Anonymous objects** (`{ a = 1, b = 2 }`) — members parse as (assignment) expressions, so no
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
  properties w/ willSet/didSet, lambdas, anonymous objects, tuples, generics, explicit-interface
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
  `NullableTypeSymbol`, `TupleTypeSymbol`, `FunctionTypeSymbol` (lambdas),
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

These are **Phase 1 grammar bugs**, pinned by
These are **Phase 1 grammar bugs**:

1. ~~**Expression precedence is inverted.**~~ **Fixed.** ANTLR gives a left-recursive rule's
   alternatives *decreasing* precedence in the order written, but `RavenParser2.g4`'s
   `expression` rule listed assignment first (binding tightest) and invocation/indexing/member
   access last (binding loosest), so `1 + f(x)` parsed as `(1 + f)(x)` and `x = a + b` as
   `(x = a) + b`.
2. ~~**All binary operators share one precedence level.**~~ **Fixed.** `1 + 2 * 3` parsed as
   `(1 + 2) * 3`.
3. **`attribute_list` requires a trailing `NL+`**, so parameters cannot carry inline
   attributes (`func f([Semantic("TEXCOORD0")] uv: float2)`).
4. **Method declarations require a body**, so a bodiless `protocol` member (`func Draw()`)
   does not parse — which makes protocols much less useful than intended.

**1 and 2 were fixed by restructuring the `expression` rule** into a proper precedence
ladder, written tightest-first: postfix (invocation, element access, member access, postfix
unary) → prefix unary → cast → multiplicative → additive → shift → range → relational →
`is`/`as` → equality → `&` → `^` → `|` → `&&` → `||` → `??` → switch expression →
conditional `?:` → lambdas → assignment, with the primaries last (their order among
themselves only resolves ambiguity — a collection literal must outrank an implicit element
access, a cast must outrank a parenthesized expression). Assignment, `??` and `?:` are
marked `<assoc=right>`. ANTLR accepts the shared `#BinaryExpression` label across the
operator levels and merges them into one context class, so `SyntaxAntlrVisitor` needed no
change. `Tests/ExpressionPrecedenceTests.cs` pins the resulting shapes and
`Tests/Fixtures/expression_precedence.rvn` gives the golden harness expression coverage;
`package_imports.tree` was unchanged, and the round-trip corpus still passes byte-for-byte.

---

## Phase 3 — Lowering to shared IR

- [ ] Define a **target-independent Raven IR**: SSA-friendly, explicit types, resolved intrinsics, explicit resource/stream bindings, per-stage entry points.
- [ ] Lowering pass: bound tree → IR (desugar control flow, materialize conversions, flatten member access, resolve intrinsics to IR ops).
- [ ] IR verifier + textual dump (for golden tests and debugging).

**Exit criteria:** shaders lower to verifiable IR; IR dump is stable in golden tests.

---

## Phase 4 — GLSL backend *(first target)*

The README's "easiest, just a transpiler" — but built over the IR, not the bound tree.

- [ ] Define the **backend/generator interface** (`ITargetBackend` / `ICodeGenerator`) that consumes IR — the pluggable "generator interface" the README wants.
- [ ] GLSL emitter: type mapping, intrinsic mapping, entry points → GLSL stages, bindings → `uniform`/`in`/`out`/`layout`, one GLSL unit per stage.
- [ ] Golden tests `.rvn → expected .glsl`; optionally validate output with `glslangValidator`.

**Exit criteria:** the README example compiles to valid GLSL that passes glslang.

---

## Phase 5 — CLI (end-to-end)

- [ ] Real `raven` CLI (System.CommandLine): `raven compile --target glsl <input> <output>`.
- [ ] Diagnostic rendering (severity, id, source span, caret), proper exit codes.

**Exit criteria:** the README's documented CLI usage works front to back.

---

## Phase 6 — SPIR-V backend *(second big lift)*

- [ ] SPIR-V module builder: id management, type/constant dedup, capability/decoration/entry-point handling.
- [ ] SPIR-V emitter over the IR.
- [ ] Validate output with `spirv-val`; golden tests `.rvn → .spv` (disassembled).

**Exit criteria:** shaders emit valid SPIR-V passing `spirv-val`.

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

Critical path to a **first working transpile** (`.rvn → GLSL` via CLI): Phases 0 → 1 → 2 → 3 → 4 → 5. Phase 1 was the largest single investment; Phase 2 was the highest-risk research work. Both are done — Phase 3 starts from the bound tree.

## Cross-cutting workstreams

- **Diagnostics catalog:** stable IDs + message templates, grown per phase.
- **Testing:** golden files at every stage (tree, IR, GLSL, SPIR-V) + unit tests for binder/type rules.
- **Docs:** language reference and public-API docs as the surface stabilizes.
