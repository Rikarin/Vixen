// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Error recovery (docs/plan/18 steps 3 and 8): a broken file still yields a
///     tree — with zero-width missing tokens and skipped source carried as trivia —
///     that reproduces the input byte-for-byte, reports errors that name what was
///     expected, and stays walkable all the way through binding. The old front end
///     discarded the whole tree; that is the behaviour this pins the exit from.
/// </summary>
public class RecoveryTests {
    [Theory]
    // A missing close paren.
    [InlineData("package A\n\nshader S {\n    func M(] {\n    }\n}\n")]
    // Garbage where a member should start.
    [InlineData("package A\n\nshader S {\n    ??? !!\n    func M() {\n    }\n}\n")]
    // A statement that stops mid-expression.
    [InlineData("package A\n\nshader S {\n    func M() {\n        val x = 1 +\n    }\n}\n")]
    // An unclosed brace at end of file.
    [InlineData("package A\n\nshader S {\n    func M() {\n")]
    // A stray token between members.
    [InlineData("package A\n\nshader S {\n}\n\n)\n\nstruct P {\n    var x: float\n}\n")]
    public void An_erroneous_parse_still_reproduces_the_file(string source) {
        var tree = SyntaxTree.ParseText(source);

        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());
    }

    [Fact]
    public void A_broken_line_is_one_error_not_one_per_token() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    ??? ?? !!! ++ --\n    var x: float\n}\n");

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("RVN1001", diagnostic.Id);

        // Recovery resumes on the next line: the field after the garbage still parses.
        Assert.Contains("var x: float", tree.GetRoot().ToFullString());
    }

    [Fact]
    public void Errors_name_what_was_expected() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    func M( {\n    }\n}\n");

        Assert.Contains(tree.Diagnostics, d => d.GetMessage().Contains("expected"));
    }

    [Fact]
    public void A_missing_token_is_zero_width_and_the_tree_stays_walkable() {
        var tree = SyntaxTree.ParseText("package A\n\nshader S {\n    func M() {\n        x = (1 + 2\n    }\n}\n");

        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal(
            "package A\n\nshader S {\n    func M() {\n        x = (1 + 2\n    }\n}\n",
            tree.GetRoot().ToFullString()
        );

        // Binding an erroneous tree must not throw — the editor's error list, not an
        // exception, is where a broken file surfaces.
        var compilation = Compilation.Create("Broken", tree);
        _ = compilation.GetDiagnostics();
    }

    /// <summary>Seven characters that used to take the machine, not the parse.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Recovery has to make progress, and one loop did not.</b> An accessor list accepts
    ///         <c>[</c> because an accessor may carry attributes — but whether the bracket <i>is</i> an
    ///         attribute list is decided further in, by a scan that resets the position when it says
    ///         no, and every step under that then fabricates rather than consumes. So the bracket
    ///         stayed exactly where it was and the loop added a fabricated accessor for it for ever,
    ///         keeping every one. Half a gigabyte in under two seconds, climbing until the operating
    ///         system took the machine away.
    ///     </para>
    ///     <para>
    ///         Found by the fuzzer at about a quarter of a million <c>raven</c> cases, and only
    ///         findable at all once <c>Vixen.Net.Fuzz</c> grew an oracle that watches a case while it
    ///         is still running — every other one is computed after <c>Run</c> returns, and this parse
    ///         does not return. The 2,872-byte mutant it arrived as is committed as
    ///         <c>Corpus/raven/f3680f7e77d7d18b.bin</c>; the seven characters are the whole of it.
    ///     </para>
    ///     <para>
    ///         The theory rows are the shapes that separate the cause from its neighbours: the loop is
    ///         only entered from <c>var</c>, only on <c>[</c>, and what follows the bracket never
    ///         mattered. Each hangs with the guard removed.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("var t{[")]
    [InlineData("var t {[")]
    [InlineData("var t{ [")]
    [InlineData("var t{[]")]
    [InlineData("var t{[)")]
    [InlineData("package P\nvar t{[")]
    public void A_bracket_an_accessor_list_cannot_use_still_ends_the_parse(string source) {
        var tree = SyntaxTree.ParseText(source);

        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());
    }
}
