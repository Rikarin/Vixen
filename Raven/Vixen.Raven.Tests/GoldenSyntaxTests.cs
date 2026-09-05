// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Syntax;
using Vixen.Testing;
using Xunit;

namespace Tests;

/// <summary>
///     Golden-file parser tests. Each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is parsed,
///     dumped with <see cref="SyntaxDumper" />, and compared against the committed
///     <c>Fixtures/&lt;name&gt;.tree</c> snapshot.
///     To (re)generate snapshots after an intentional change, run the suite with
///     the environment variable <c>UPDATE_GOLDEN=1</c>; the <c>.tree</c> files are
///     rewritten from the current output. Review the diff before committing.
/// </summary>
/// <remarks>
///     The comparison, the regeneration and the diff are <see cref="GoldenFile" />'s — the shared
///     helper of <c>docs/plan/12</c>, which this suite is one of the four that used to write out by
///     hand.
/// </remarks>
public class GoldenSyntaxTests {
    [Theory]
    [InlineData("package_imports")]
    [InlineData("expression_precedence")]
    [InlineData("all_constructs")]
    public void Matches_golden(string name) {
        var rvnPath = FixturePath(name + ".rvn");
        var goldenPath = FixturePath(name + ".tree");

        var text = File.ReadAllText(rvnPath);
        var tree = SyntaxTree.ParseText(text);

        GoldenFile.Matches(SyntaxDumper.Dump(tree.GetRoot()), goldenPath);
    }

    static string FixturePath(string file) => GoldenFile.InProjectDirectory("Fixtures", file);
}
