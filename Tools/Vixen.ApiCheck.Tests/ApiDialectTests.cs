// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ A <c>PublicAPI.*.txt</c> line in <c>PublicApiAnalyzers</c>' dialect is named as that, and
///     every committed baseline is checked for one without a Release build (#1340).
/// </summary>
/// <remarks>
///     <para>
///         The analyzer is referenced nowhere, so before this a baseline line was read by exactly one
///         thing: a Release-configured <c>CheckApi</c>, a whole-solution compile away. And what it said
///         about <c>KeyMap! keys</c> was "1 unapproved addition, 1 removal", which is a rename's
///         message. <see cref="EveryCommittedBaselineIsInThisToolsDialect" /> is the cheap half — a
///         second in <c>dotnet test</c> — and <see cref="ApiDialect" /> in <c>Program</c> is the
///         message half.
///     </para>
/// </remarks>
public sealed class ApiDialectTests {
    /// <summary>#650's line, as it was written before d3dd983d9 corrected it.</summary>
    const string Issue650 =
        "Vixen.Ui.Controls.MenuItem.ShowShortcut(Vixen.Ui.Controls.KeyMap! keys, string! commandId) -> Vixen.Ui.Controls.KeyboardShortcut?";

    [Fact]
    public void The_analyzers_non_nullable_bang_is_a_dialect_error_named_by_line() {
        var errors = ApiDialect.Errors(["#nullable enable", "Sample.A -> class", Issue650]);

        var error = Assert.Single(errors);

        Assert.Equal(3, error.Line);
        Assert.Equal(Issue650, error.Entry);

        var message = error.Describe("Core/Vixen.Ui.Controls/PublicAPI.Unshipped.txt");

        Assert.StartsWith("Core/Vixen.Ui.Controls/PublicAPI.Unshipped.txt:3:", message, StringComparison.Ordinal);
        Assert.Contains("dialect", message, StringComparison.Ordinal);
    }

    /// <summary>Every place the analyzer puts one: a generic argument, an array, a return type.</summary>
    [Theory]
    [InlineData("Sample.A.Names.get -> System.Collections.Generic.IReadOnlyList<string!>!")]
    [InlineData("Sample.A.Bytes(byte[]! input) -> void")]
    [InlineData("Sample.A.Name.get -> string!")]
    [InlineData("Sample.A.Pair.get -> (int, string)!")]
    public void Each_position_the_analyzer_marks_is_caught(string line) {
        Assert.Single(ApiDialect.Errors([line]));
    }

    /// <summary>
    ///     ⚠ The negative: the <c>!</c> this tool does write, and one inside a constant's value.
    /// </summary>
    /// <remarks>
    ///     Proved twice. Widening the marker to any <c>!</c> reds the two operator rows, and
    ///     <see cref="EveryCommittedBaselineIsInThisToolsDialect" /> reports every <c>operator !=</c> in
    ///     the tree. Dropping the literal stripping reds the <c>"Hello!"</c> row alone.
    /// </remarks>
    [Theory]
    [InlineData("static Sample.A.operator !=(Sample.A left, Sample.A right) -> bool")]
    [InlineData("static Sample.A.operator !(Sample.A value) -> bool")]
    [InlineData("const Sample.A.Greeting = \"Hello!\" -> string")]
    [InlineData("const Sample.A.Bang = '!' -> char")]
    [InlineData("Sample.A.Name.get -> string?")]
    [InlineData("#nullable enable")]
    public void What_this_tool_writes_is_not_an_error(string line) {
        Assert.Empty(ApiDialect.Errors([line]));
    }

    /// <summary>The whole tree, in <c>dotnet test</c>: no committed baseline carries the analyzer's dialect.</summary>
    [Fact]
    public void EveryCommittedBaselineIsInThisToolsDialect() {
        var root = RepositoryRoot();
        var baselines = CommittedBaselines(root);

        // The instrument first: a sweep over no files agrees with every rule.
        Assert.True(baselines.Count > 200, $"Found {baselines.Count} PublicAPI.*.txt files, which is not this tree.");

        var errors = baselines
            .SelectMany(path => ApiDialect.Errors(File.ReadAllLines(Path.Combine(root, path))).Select(error => error.Describe(path)))
            .ToList();

        Assert.True(errors.Count == 0, string.Join('\n', errors));
    }

    static List<string> CommittedBaselines(string root) {
        using var git = Process.Start(
            new ProcessStartInfo("git", "ls-files -- \"*PublicAPI.Shipped.txt\" \"*PublicAPI.Unshipped.txt\"") {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        );

        Assert.NotNull(git);

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.Equal(0, git.ExitCode);

        return [.. output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
    }

    static string RepositoryRoot() {
        for (var directory = AppContext.BaseDirectory; directory is not null;) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
