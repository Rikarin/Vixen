// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The shipped example files, held to the contract each one claims.
/// </summary>
/// <remarks>
///     <para>
///         These exist because both files rotted unnoticed for a year: <c>Example1.rvn</c> —
///         the language showcase and the centrepiece of the round-trip corpus — accumulated
///         nine semantic errors, two of them from a diagnostic the language gained *after* the
///         file was written, and <c>Example2.rvn</c> stopped parsing entirely. A round-trip
///         test proved the bytes survived while saying nothing about whether they meant
///         anything.
///     </para>
///     <para>
///         So the contract is asserted rather than assumed, per file, and it is deliberately
///         not the same contract for both — see each test.
///     </para>
/// </remarks>
public class LibraryExampleTests {
    /// <summary>
    ///     <c>Example1.rvn</c> compiles the whole way through, in both backends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The bar this file is held to has moved twice, and each move closed the gap that was
    ///         holding it. It stopped at <em>binding</em> while a generic struct and a spread could
    ///         not reach a backend; at <em>lowering</em> once monomorphisation and sized arrays
    ///         landed; and now at code generation, because the last thing between it and a backend
    ///         — the unsized arrays it declared — is <c>RVN2126</c> at the declaration rather than
    ///         <c>RVN4001</c> twice at the end.
    ///     </para>
    ///     <para>
    ///         That progression is the point of keeping the weaker bar out: a contract that stops
    ///         where the language stops cannot tell you when the language catches up.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Example1CompilesEndToEnd() {
        var source = File.ReadAllText(PathTo("Example1.rvn"));

        var tree = SyntaxTree.ParseText(source, path: "Example1.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Example1", tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "Example1.rvn does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        Assert.True(
            bag.IsEmpty,
            "Example1.rvn does not lower cleanly:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        // Both backends, so neither can be the one that quietly cannot take it.
        CodeGenTestBase.GenerateClean(source);
        CodeGenTestBase.GenerateClean(source, "spirv");
    }

    /// <summary>
    ///     <c>Example2.rvn</c> compiles the whole way through, because it is a compute shader
    ///     rather than a syntax showcase — every construct in it is one a backend supports.
    /// </summary>
    [Fact]
    public void Example2CompilesEndToEnd() {
        var source = File.ReadAllText(PathTo("Example2.rvn"));

        var tree = SyntaxTree.ParseText(source, path: "Example2.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Example2", tree);
        var diagnostics = compilation.GetDiagnostics();
        Assert.True(
            diagnostics.Count == 0,
            "Example2.rvn does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        // Both backends, so neither can be the one that quietly cannot take it.
        CodeGenTestBase.GenerateClean(source);
        CodeGenTestBase.GenerateClean(source, "spirv");
    }

    static string PathTo(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library", file);
}
