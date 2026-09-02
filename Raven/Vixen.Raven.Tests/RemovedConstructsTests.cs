// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     Constructs deliberately removed from the language because a GPU has no way to
///     represent them. These are pinned so nobody reintroduces one by accident, and
///     so the reason is written down next to the evidence.
/// </summary>
public class RemovedConstructsTests {
    [Theory]
    // No function pointers or closures on a GPU.
    [InlineData("        val f = x => x")]
    [InlineData("        val f = (x: int) => x")]
    // No null, so nothing that tests for or coalesces it.
    [InlineData("        val x = a ?? b")]
    [InlineData("        x ??= 1")]
    [InlineData("        val x = a!")]
    // No boxing or dynamic dispatch, so no anonymous aggregates.
    [InlineData("        val o = { A = 1 }")]
    // No character type.
    [InlineData("        val c = 'a'")]
    // --- The second pass: constructs that used to compile to the wrong thing ---
    //
    // Each of these parsed, bound and emitted a valid module while meaning something the
    // target cannot do. Silence was the problem: a rejection is recoverable, a wrong answer
    // is not. See docs/plan/07 § J.
    //
    // A `using` statement kept the block and threw away the declaration, so the name was not
    // even in scope. Nothing is disposable on a GPU.
    [InlineData("        using (val x = 1f) {\n        }")]
    [InlineData("        using val x = 1f")]
    // Argument modifiers were parsed and ignored; the IR has no by-reference parameters.
    [InlineData("        val x = Helper(out tint)")]
    // `ref x` used to read as declaring `x` of a type named `ref`, because a bare
    // `type designation` was an expression. That went with the patterns, so it is a parse
    // error now rather than an undefined type.
    [InlineData("        val r = ref tint")]
    [InlineData("        val x = Helper(ref tint)")]
    public void The_construct_no_longer_parses(string body) => AssertDoesNotParse(body);

    /// <summary>
    ///     <c>sizeof</c> and <c>ref</c> are no longer keywords, so they read as ordinary
    ///     names — the same treatment <c>null</c> got in the first pass.
    /// </summary>
    /// <remarks>
    ///     Both used to compile clean and mean the wrong thing. <c>sizeof(T)</c> bound to a
    ///     literal <c>null</c> typed <c>int</c>, so it evaluated to <b>0</b> — never the size of
    ///     anything. <c>ref x</c> was discarded by the binder, which returned the operand, so the
    ///     keyword could only ever have been a lie about a machine with no references.
    /// </remarks>
    [Theory]
    // `sizeof(float4)` now reads as a call to an undefined function.
    [InlineData("        val n = sizeof(float4)", "RVN2010")]
    public void The_keyword_is_gone_so_it_reads_as_an_undefined_name(string body, string id) {
        var diagnostics = Diagnose(
            $"package A\n\nshader S {{\n    var tint: float4\n\n    func Helper(v: float4): float4 => v\n\n"
            + $"    func M() {{\n{body}\n    }}\n}}\n"
        );

        Assert.Contains(id, diagnostics.Select(d => d.Id));
    }

    /// <summary>
    ///     <c>class</c> is gone rather than kept as a synonym. It was treated as a struct
    ///     everywhere — <c>TypeKind.Struct or TypeKind.Class</c> — so it promised reference
    ///     semantics and delivered value semantics, which is the most expensive kind of
    ///     misunderstanding to debug. A GPU has no references; <c>struct</c> says so.
    /// </summary>
    [Fact]
    public void Class_is_no_longer_a_type_keyword() {
        var tree = SyntaxTree.ParseText("package A\n\nclass Widget {\n}\n");

        Assert.NotEmpty(tree.Diagnostics);
    }

    /// <summary>
    ///     Declaration forms that were C# shapes with nothing behind them on a GPU. Each one
    ///     cost a syntax node, five pieces of generated code, a translator method and a
    ///     permanent round-trip obligation — see docs/plan/07 § J.
    /// </summary>
    [Theory]
    // No object lifetime, so nothing to run at the end of one. This was at least reported
    // (RVN3002) rather than silently accepted.
    [InlineData("package A\n\nstruct F {\n    ~init() {\n    }\n}\n")]
    // Protocol members resolve statically, so there is no diamond to disambiguate. This was
    // silently ignored: the method bound and was callable as an ordinary member.
    [InlineData("package A\n\nstruct F {\n    func P.Q() {\n    }\n}\n")]
    [InlineData("package A\n\nstruct F {\n    var P.Q: int\n}\n")]
    // Primary constructors declared parameters that became neither fields nor a constructor,
    // so the call site failed with RVN2034 while the declaration looked fine.
    [InlineData("package A\n\nstruct Point(x: float, y: float)\n")]
    // `record` promised value equality, ToString and Deconstruct; none of the three existed.
    [InlineData("package A\n\nreadonly record struct Msg(a: int)\n")]
    // A base initializer produced malformed IR (RVN3010) rather than a diagnostic.
    [InlineData("package A\n\nstruct F {\n    init(a: float) : base(a) {\n    }\n}\n")]
    // There is no alias table, so `::` could never resolve to anything.
    [InlineData("package A\n\nshader S {\n    var x: Foo::Bar\n}\n")]
    // Attribute targets were parsed and dropped, so `[property: X]` silently meant `[X]`.
    [InlineData("package A\n\nshader S {\n    [property: Semantic(\"SV_Target\")]\n    var tint: float4\n}\n")]
    public void The_declaration_form_no_longer_parses(string source) {
        var tree = SyntaxTree.ParseText(source);

        Assert.NotEmpty(tree.Diagnostics);
    }

    /// <summary>
    ///     A leading-dot member reference (<c>.Name</c>) only means something inside a
    ///     conditional-access chain, and nullable types went in the first pass — so there is no
    ///     null to guard and nothing for the chain to be. The binder had already given up on it,
    ///     returning an error node with that exact reasoning in a comment.
    /// </summary>
    [Fact]
    public void A_leading_dot_member_reference_no_longer_parses() =>
        AssertDoesNotParse("        val x = .Foo");

    /// <summary>
    ///     Pattern matching and everything built on it. These parsed and bound, and lowering
    ///     rejected them — so nothing miscompiled, but each cost a node, generated code, a
    ///     translator method and a round-trip obligation for a construct that had no route to a
    ///     GPU. Patterns are C# flow-typing: they narrow a static type by testing a value, which
    ///     needs runtime type information that does not exist here.
    /// </summary>
    [Theory]
    // is-patterns, in every shape the grammar had.
    [InlineData("        val b = x is 5")]
    [InlineData("        val b = x is > 5")]
    [InlineData("        val b = x is not 5")]
    [InlineData("        val b = x is > 0 and < 10")]
    [InlineData("        val b = x is var y")]
    [InlineData("        val b = x is _")]
    // A switch *expression*. The switch statement stays and now lowers; the expression form is
    // sugar for it plus an assignment, and neither target has an expression form.
    [InlineData("        val r = x switch {\n            1 => a,\n            _ => b\n        }")]
    // `as` was a reference conversion, and there are no reference types.
    [InlineData("        val y = x as int")]
    // A local function hoists to module scope trivially — which is what a private method is.
    [InlineData("        func Inner(): int {\n            return 1\n        }")]
    public void The_pattern_construct_no_longer_parses(string body) => AssertDoesNotParse(body);

    /// <summary>
    ///     Member forms that went with them: an indexer is a call with different spelling, and a
    ///     conversion operator is an implicit call that hides where the work happens. Neither
    ///     lowered.
    /// </summary>
    [Theory]
    [InlineData("package A\n\nstruct F {\n    var v: float\n\n    float self[i: int] => v\n}\n")]
    [InlineData("package A\n\nstruct F {\n    var v: float\n\n    implicit operator float(x: F) => x.v\n}\n")]
    [InlineData("package A\n\nstruct F {\n    var v: float\n\n    explicit operator int(x: F) => 1\n}\n")]
    public void The_member_form_no_longer_parses(string source) {
        var tree = SyntaxTree.ParseText(source);

        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void Null_is_no_longer_a_keyword_so_it_reads_as_an_undefined_name() =>
        AssertDiagnostics("package A\n\nshader S {\n    func M() {\n        val x = null\n    }\n}\n", "RVN2010");

    [Fact]
    public void Nullable_type_annotations_no_longer_parse() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    val x: int?\n}\n");
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Theory]
    // `string`, `char`, `long` and `object` are gone from the type table, so a
    // reference to one is an ordinary "type not found".
    [InlineData("string")]
    [InlineData("char")]
    [InlineData("long")]
    [InlineData("object")]
    public void The_type_no_longer_exists(string name) =>
        AssertDiagnostics($"package A\n\nshader S {{\n    val x: {name}\n}}\n", "RVN2002");

    [Fact]
    public void A_string_literal_still_parses_because_attributes_need_it() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            shader S {
                [Semantic("SV_Target")]
                var tint: float4
            }

            """
        );

        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void An_attribute_keeps_reading_its_string_argument() {
        var compilation = CompileClean(
            """
            package A

            shader S {
                [Semantic("SV_Target")]
                var tint: float4
            }

            """
        );

        Assert.Equal(
            "SV_Target",
            GetMember<FieldSymbol>(FindType(compilation, "S"), "tint")
                .SemanticName
        );
    }

    [Fact]
    public void A_string_literal_in_expression_position_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                func M() {
                    val s = "text"
                }
            }

            """,
            "RVN2025"
        );

    /// <summary>
    ///     <b>String interpolation is refused, and belongs on this list rather than on a gap list.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>docs/plan/07</c> disagrees with itself about this. § I carries it as an open
    ///         syntax-fidelity gap — "needs lexer modes for embedded expressions" — while the
    ///         paragraph retiring <c>Library/Example2.rvn</c> lists it among the <em>deliberately
    ///         removed</em> constructs that made that file unfixable, beside <c>class</c>,
    ///         <c>string</c> as a type, <c>long</c>, <c>null</c>, <c>int?</c> and <c>!</c>. The
    ///         second is the one that follows from the language: an interpolation is an expression
    ///         whose value is a <c>string</c>, there is no such type, and
    ///         <see cref="A_string_literal_in_expression_position_is_rejected" /> is <c>RVN2025</c>
    ///         saying so about the simpler case. A lexer mode would buy the syntax for a value
    ///         nothing could hold.
    ///     </para>
    ///     <para>
    ///         So this pins what the compiler does now rather than proposing what it should. The
    ///         <c>$</c> is <c>RVN1002</c> from the lexer, which carries it as trivia — and that
    ///         second half is what makes the attribute case worth an assertion of its own below.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_interpolated_string_in_expression_position_is_rejected_twice_over() {
        var tree = SyntaxTree.ParseText(
            """
            package A

            shader S {
                var tint: float4

                func M() {
                    val s = $"tint is {tint}"
                }
            }

            """,
            path: "Test.rvn"
        );

        Assert.Equal("RVN1002", Assert.Single(tree.Diagnostics).Id);

        Assert.Contains(
            Vixen.Raven.Compilation.Create("Test", tree).GetDiagnostics(),
            d => d.Id == "RVN2025"
        );
    }

    /// <summary>
    ///     ⚠ And in the one position a string literal <em>is</em> legal, the interpolation is not
    ///     merely refused — the text is read as though the holes were literal characters.
    /// </summary>
    /// <remarks>
    ///     An attribute argument is compile-time metadata, so it is the one place an interpolation
    ///     could plausibly be asked for: <c>[Semantic($"TEXCOORD{Index}")]</c> over a permutation
    ///     key. What happens instead is that the lexer reports the <c>$</c> and carries it as
    ///     <em>trivia</em>, so the token stream the parser sees is an ordinary string literal and
    ///     <c>SemanticName</c> comes back holding the braces. Only <c>RVN1002</c> being an error
    ///     stops that reaching a backend, which is a thin thing to rest on — and it is the argument
    ///     for the syntax staying refused rather than half-understood.
    /// </remarks>
    [Fact]
    public void An_interpolated_attribute_argument_is_read_as_its_own_braces() {
        const string Source = """
                              package A

                              shader S {
                                  [Semantic($"SV_Target{0}")]
                                  var tint: float4
                              }

                              """;

        var tree = SyntaxTree.ParseText(Source, path: "Test.rvn");

        Assert.Equal("RVN1002", Assert.Single(tree.Diagnostics).Id);

        var compilation = Vixen.Raven.Compilation.Create("Test", tree);

        Assert.Equal(
            "SV_Target{0}",
            GetMember<FieldSymbol>(FindType(compilation, "S"), "tint").SemanticName
        );
    }

    [Fact]
    public void An_integer_literal_too_large_for_int_takes_the_unsigned_shape() {
        var (compilation, tree, model) = Compile(
            """
            package A

            shader S {
                func M() {
                    val big = 3000000000
                }
            }

            """
        );

        Assert.Empty(compilation.GetDiagnostics());

        var declaration = FindNode<VariableDeclarationSyntax>(tree, d => d.Identifier.ValueText == "big");
        var local = Assert.IsType<LocalSymbol>(model.GetDeclaredSymbol(declaration));
        Assert.Equal("uint", local.Type.ToDisplayString());
    }

    // --- The third pass: surface that parsed with nothing behind it ---
    //
    // Each of these was accepted and then ignored: no lookup, no lowering, no emitter
    // ever read it. Silent acceptance teaches the author something untrue, so the
    // surface is gone. See the audit in docs/plan/07 § J.

    [Theory]
    // `#` swallowed the directive line and compiled every branch in — `#if X` was a
    // silent no-op that included both sides.
    [InlineData("package A\n\nshader S {\n    func M() {\n        var s = 1f\n#if UseDetail\n        s = 2f\n#endif\n    }\n}\n")]
    // `@name` parsed and the `@` silently vanished from the tree, breaking round-trip.
    [InlineData("package A\n\nshader S {\n    func M(@pos: float4) {\n    }\n}\n")]
    // Accessibility was parsed into a symbol property no code ever read: a `private`
    // field was readable from any other type. `abstract` and `partial` likewise
    // reached properties with zero consumers.
    [InlineData("package A\n\nshader S {\n    public func M() {\n    }\n}\n")]
    [InlineData("package A\n\nshader S {\n    private var x: float\n}\n")]
    [InlineData("package A\n\nshader S {\n    protected func M() {\n    }\n}\n")]
    [InlineData("package A\n\nabstract shader S {\n}\n")]
    [InlineData("package A\n\npartial struct P {\n    var x: float\n}\n")]
    // `global import` was stored in the tree and never read by the binder.
    [InlineData("package A\n\nglobal import B.C\n\nshader S {\n}\n")]
    // Type-parameter variance means nothing in a language with only value types.
    [InlineData("package A\n\nstruct Box<in T> {\n    var item: T\n}\n")]
    [InlineData("package A\n\nstruct Box<out T> {\n    var item: T\n}\n")]
    // `operator true`/`operator false` declared methods no expression could invoke.
    [InlineData("package A\n\nstruct F {\n    var v: float\n\n    bool operator true(x: F) => true\n}\n")]
    public void The_ignored_surface_no_longer_parses(string source) {
        var tree = SyntaxTree.ParseText(source);

        Assert.NotEmpty(tree.Diagnostics);
    }

    [Theory]
    // Statements end at the newline; `;` never had a grammar rule and now has no token.
    [InlineData("        val x = 1;")]
    // `^i` (index from end) bound as an int and then had nothing to index: no sized
    // arrays, and ranges are not values.
    [InlineData("        val x = ^1")]
    // A tuple's close paren was optional and a missing one was fabricated, so
    // `(1f, 2f` round-tripped to `(1f, 2f)` with no diagnostic.
    [InlineData("        val t = (1f, 2f")]
    // The switch parens were independently optional, so `switch x) {` parsed.
    [InlineData("        switch x) {\n        case 1:\n            break\n        }")]
    public void The_loose_expression_surface_no_longer_parses(string body) => AssertDoesNotParse(body);

    static void AssertDoesNotParse(string body) {
        var tree = SyntaxTree.ParseText($"package A\n\nshader S {{\n    func M() {{\n{body}\n    }}\n}}\n");
        Assert.NotEmpty(tree.Diagnostics);
    }
}
