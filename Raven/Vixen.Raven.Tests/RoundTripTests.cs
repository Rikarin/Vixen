// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax;

namespace Tests;

/// <summary>
///     Full-fidelity round-trip: <see cref="SyntaxNode.ToFullString" /> must reproduce
///     the original source byte-for-byte (text + all trivia, including whitespace,
///     comments, and newlines) for the fully-wired subset of the grammar.
/// </summary>
public class RoundTripTests {
    [Theory]
    [InlineData("package Vixen.Test\n")]
    [InlineData("package  A.B.C\n")]
    [InlineData("package A.B\nimport X.Y\nimport static X.Z\n")]
    [InlineData("package A.B\n\n// a comment\nimport X.Y\n")]
    [InlineData("package A.B /* inline */ .C\n")]
    [InlineData("package A.B\n\nshader Foo {\n\n}\n")]
    [InlineData("package A.B\n\nshader Empty\n")]
    [InlineData("package A.B\n\nshader First {\n\n}\n\nshader Second {\n\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    val len: int\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    const val Multiplier = 42\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Bar() {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Answer() => 42\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Get(): int => 42\n}\n")]
    // Statements & expressions
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        return 42\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        a.b.c\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        print(x)\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        a.b(x, y)\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        x = 1 + 2\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val y = a < 42\n    }\n}\n")]
    [InlineData(
        "package A.B\n\nshader Foo {\n    func M() {\n        if (a) {\n        } else {\n        }\n    }\n}\n"
    )]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val z = (a + b)\n    }\n}\n")]
    // Separated lists (commas)
    [InlineData("package A.B\n\nshader S : X, Y {\n\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Add(a: int, b: int) {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Three(a: int, b: float, c: bool): int => 42\n}\n")]
    // Declarations: protocol, enum, constructor
    [InlineData("package A.B\n\nprotocol P {\n\n}\n")]
    // Bodiless members: a protocol declares signatures only
    [InlineData("package A.B\n\nprotocol P {\n    func Test()\n}\n")]
    [InlineData("package A.B\n\nprotocol P {\n    func Get(): int\n}\n")]
    [InlineData("package A.B\n\nprotocol P {\n    var Tint: float4\n}\n")]
    [InlineData("package A.B\n\nprotocol P {\n    func First()\n    func Second(a: int): int\n}\n")]
    [InlineData("package A.B\n\nprotocol P { func Test() }\n")]
    // Inline parameter attributes (no newline after the attribute list)
    [InlineData("package A.B\n\nshader Foo {\n    func Pixel([Semantic(\"TEXCOORD0\")] uv: float2) {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Pixel([A] [B] uv: float2) {\n    }\n}\n")]
    [InlineData(
        "package A.B\n\nshader Foo {\n    func Vertex([Semantic(\"POSITION\")] p: float3, [Semantic(\"NORMAL\")] n: float3) {\n    }\n}\n"
    )]
    [InlineData("package A.B\n\nenum E {\n    A, B, C\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    init() {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    init(x: int) => bar\n}\n")]
    // Operator expressions & while
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        x = -a\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        while (a) {\n        }\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val r = a..b\n    }\n}\n")]
    // Properties + accessors
    [InlineData("package A.B\n\nshader Foo {\n    var prop {\n        get => test\n        set => test\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    var count: int => a\n}\n")]
    // Conditional.
    // NOTE: `a[i]` parses as an array type (`type array_rank_specifier`), shadowing
    // element access — a grammar ambiguity like invocation; visitor is wired.
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val x = a ? b : c\n    }\n}\n")]
    // String literals
    [InlineData("package A.B\n\nshader Foo {\n    val name = \"hello\"\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val s = \"a b c\"\n    }\n}\n")]
    [InlineData(
        "package A.B\n\nshader Foo {\n    func M() {\n        print(\"escaped: \\n \\t \\\" done\")\n    }\n}\n"
    )]
    [InlineData("package A.B\n\nshader Foo {\n    val empty = \"\"\n}\n")]
    // Tuple expression & tuple type
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val p = (a, b)\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val p = (a, b, c)\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Swap(p: (int, int)) {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Named(p: (int x, float y)) {\n    }\n}\n")]
    // Collection expressions (+ spread)
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val xs = [a, b, c]\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val xs = []\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val xs = [a, ..b, c]\n    }\n}\n")]
    // is-patterns
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is 5\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is > 5\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is not 5\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is > 0 and < 10\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is var y\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is _\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        val b = x is (5)\n    }\n}\n")]
    // switch expression
    [InlineData(
        "package A.B\n\nshader Foo {\n    func M() {\n        val r = x switch {\n            1 => a,\n            _ => b\n        }\n    }\n}\n"
    )]
    [InlineData(
        "package A.B\n\nshader Foo {\n    func M() {\n        val r = x switch {\n            > 0 when a => b,\n            _ => c\n        }\n    }\n}\n"
    )]
    // Generics
    [InlineData("package A.B\n\nshader Foo {\n    val items: List<int>\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    val m: Map<int, float>\n}\n")]
    [InlineData("package A.B\n\nshader Container : Base<int> {\n\n}\n")]
    // Type parameters & constraints
    [InlineData("package A.B\n\nshader Box<T> {\n\n}\n")]
    [InlineData("package A.B\n\nshader Pair<K, V> {\n\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    func Map<T>(x: T) {\n    }\n}\n")]
    [InlineData("package A.B\n\nshader Box<T> where T : Base {\n\n}\n")]
    [InlineData("package A.B\n\nshader Box<T> where T : Base, Other {\n\n}\n")]
    // Declaration expression (type designation)
    [InlineData("package A.B\n\nshader Foo {\n    func M() => int x\n}\n")]
    // Indexer & operator declarations
    [InlineData("package A.B\n\nshader Foo {\n    int self[i: int] => a\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    Vec operator +(a: Vec, b: Vec) => a\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    bool operator ==(a: Vec, b: Vec) {\n    }\n}\n")]
    // Conversion operators
    [InlineData("package A.B\n\nshader Foo {\n    implicit operator int(v: Vec) => a\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    explicit operator float(v: Vec) {\n    }\n}\n")]
    // Struct declarations
    [InlineData("package A.B\n\nstruct FooBar {\n\n}\n")]
    [InlineData("package A.B\n\nstruct Widget {\n\n}\n")]
    [InlineData("package A.B\n\nreadonly struct Messenger {\n    val userId: int\n}\n")]
    // Array types (empty / jagged rank)
    [InlineData("package A.B\n\nshader Foo {\n    var xs: int[]\n}\n")]
    [InlineData("package A.B\n\nshader Foo {\n    var xs: double[][]\n}\n")]
    // Element access (resolved a[i] ambiguity)
    [InlineData("package A.B\n\nshader Foo {\n    func M() {\n        len = p[42]\n    }\n}\n")]
    public void ToFullString_reproduces_source(string source) {
        var tree = SyntaxTree.ParseText(source);
        Assert.Equal(source, tree.GetRoot().ToFullString());
        // A well-formed program must parse cleanly, with no spurious diagnostics.
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void Fixture_round_trips() {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "package_imports.rvn")
        );
        var tree = SyntaxTree.ParseText(source);
        Assert.Equal(source, tree.GetRoot().ToFullString());
        Assert.Empty(tree.Diagnostics);
    }
}

/// <summary>
///     End-to-end full-fidelity round-trip over the realistic <c>Example1.rvn</c>
///     sample, which exercises attributes (targeted + args), properties with
///     willSet/didSet, tuples, generics, explicit-interface methods, operators and
///     an indexer, element access, patterns and switch expressions, array types, and
///     struct/class/record/enum/protocol declarations.
/// </summary>
public class Example1RoundTripTests {
    [Fact]
    public void Example1_round_trips_byte_for_byte() {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library", "Example1.rvn");
        var text = File.ReadAllText(path);
        var tree = SyntaxTree.ParseText(text);
        Assert.Equal(text, tree.GetRoot().ToFullString());
        Assert.Empty(tree.Diagnostics);
    }
}
