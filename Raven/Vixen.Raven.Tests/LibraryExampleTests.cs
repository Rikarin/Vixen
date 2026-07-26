// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
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
    ///     <c>Example1.rvn</c> parses and binds with nothing to report.
    /// </summary>
    /// <remarks>
    ///     Binding, not lowering, is the bar this file is held to. Two of the constructs it
    ///     shows off cannot reach a backend yet — a generic struct needs monomorphisation, and
    ///     a spread element needs an array type that carries a length — and both are recorded
    ///     as open in docs/plan/07. Removing them to turn this test green would make the
    ///     showcase misrepresent the language; the honest version demonstrates what the
    ///     language has and the plan says what does not lower.
    /// </remarks>
    [Fact]
    public void Example1ParsesAndBindsCleanly() {
        var tree = SyntaxTree.ParseText(File.ReadAllText(PathTo("Example1.rvn")), path: "Example1.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Example1", tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "Example1.rvn does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
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
