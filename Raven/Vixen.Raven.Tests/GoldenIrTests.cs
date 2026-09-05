// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Vixen.Testing;
using Xunit;

namespace Tests;

/// <summary>
///     Golden-file IR tests. Each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is compiled,
///     lowered, verified and dumped with <see cref="IrPrinter" />, then compared
///     against the committed <c>Fixtures/&lt;name&gt;.ir</c> snapshot. This is what
///     makes a change in lowering visible in review rather than silent.
///     To (re)generate snapshots after an intentional change, run the suite with
///     the environment variable <c>UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
/// <remarks>
///     The comparison, the regeneration and the diff are <see cref="GoldenFile" />'s — the shared
///     helper of <c>docs/plan/12</c>, which this suite is one of the four that used to write out by
///     hand.
/// </remarks>
public class GoldenIrTests {
    [Theory]
    [InlineData("lambert")]
    public void Matches_golden(string name) {
        var source = File.ReadAllText(FixturePath(name + ".rvn"));
        var goldenPath = FixturePath(name + ".ir");

        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        Assert.True(
            bag.IsEmpty,
            "Expected clean lowering, got:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        GoldenFile.Matches(IrPrinter.Print(module), goldenPath);
    }

    static string FixturePath(string file) => GoldenFile.InProjectDirectory("Fixtures", file);
}
