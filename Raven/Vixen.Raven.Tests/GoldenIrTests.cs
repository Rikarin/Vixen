// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;

namespace Tests;

/// <summary>
///     Golden-file IR tests. Each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is compiled,
///     lowered, verified and dumped with <see cref="IrPrinter" />, then compared
///     against the committed <c>Fixtures/&lt;name&gt;.ir</c> snapshot. This is what
///     makes a change in lowering visible in review rather than silent.
///     To (re)generate snapshots after an intentional change, run the suite with
///     the environment variable <c>UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
public class GoldenIrTests {
    static bool ShouldUpdate => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";

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

        var actual = Normalize(IrPrinter.Print(module));

        if (ShouldUpdate || !File.Exists(goldenPath)) {
            File.WriteAllText(goldenPath, actual);
            Assert.Fail($"Golden '{name}.ir' was (re)generated. Review the diff and re-run.");
        }

        var expected = Normalize(File.ReadAllText(goldenPath));

        if (expected != actual) {
            File.WriteAllText(goldenPath + ".actual", actual);
        }

        Assert.Equal(expected, actual);
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
