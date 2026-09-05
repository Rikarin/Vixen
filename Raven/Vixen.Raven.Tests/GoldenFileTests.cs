// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Xunit;
using Xunit.Sdk;

namespace Tests;

/// <summary>The snapshot helper's own rules, each of them a form that used to be green.</summary>
/// <remarks>
///     Here rather than beside <c>Testing/GoldenFile.cs</c> because the file is linked into the
///     projects that use it and has no assembly of its own — the same arrangement
///     <c>RecordingBackendTests</c> and <c>TestAppTests</c> are in. This is the project that adopted
///     it.
/// </remarks>
public sealed class GoldenFileTests : IDisposable {
    readonly string directory =
        Path.Combine(Path.GetTempPath(), "vixen_golden_" + Guid.NewGuid().ToString("n"));

    /// <summary>Cleans up the scratch directory.</summary>
    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A rendering that matches what is committed is a pass, and writes nothing beside it.</summary>
    [Fact]
    public void AMatchingRenderingPasses() {
        var path = Committed("a.txt", "one\ntwo\n");

        GoldenFile.Matches("one\ntwo", path);

        Assert.False(File.Exists(path + ".actual"), "Nothing should be written beside a golden that matched.");
    }

    /// <summary>Line endings and the trailing newline are not differences.</summary>
    /// <remarks>
    ///     Every hand-rolled copy in the tree normalised exactly these two, and a helper that dropped
    ///     the normalisation would fail six suites on Windows only — the sort of thing a Mac-only run
    ///     never sees.
    /// </remarks>
    [Fact]
    public void LineEndingsAndTheTrailingNewlineAreNotDifferences() {
        GoldenFile.Matches("one\r\ntwo\r\n\r\n", Committed("b.txt", "one\ntwo\n"));
    }

    /// <summary>⚠ A golden that had to be created FAILS rather than passing.</summary>
    /// <remarks>
    ///     A first run that writes the file and reports success is a suite that has pinned whatever
    ///     the code did that day, reviewed by nobody. The file is still written — that is the point
    ///     of the switch — but the run says so.
    /// </remarks>
    [Fact]
    public void AGoldenThatHadToBeCreatedFails() {
        var path = Path.Combine(directory, "new.txt");

        var failure = Assert.Throws<XunitException>(() => GoldenFile.Matches("rendered", path));

        Assert.Contains("(re)generated", failure.Message, StringComparison.Ordinal);
        Assert.Equal("rendered", File.ReadAllText(path));
    }

    /// <summary>⚠ An empty rendering is refused even when the committed golden is empty too.</summary>
    /// <remarks>
    ///     This is the Null-device trap in snapshot form: a printer that returned nothing, a
    ///     generator that emitted no stages or an enumeration that found no fixtures renders as the
    ///     empty string, and an empty golden committed once makes every later run agree with it for
    ///     ever. The naive comparison calls that a pass.
    /// </remarks>
    [Fact]
    public void AnEmptyRenderingIsRefusedAgainstAnEmptyGolden() {
        var failure = Assert.Throws<XunitException>(
            () => GoldenFile.Matches(string.Empty, Committed("empty.txt", string.Empty))
        );

        Assert.Contains("is empty", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ A set that compared no goldens at all fails, where the loop it replaces passed.</summary>
    [Fact]
    public void ASetThatComparedNothingFails() {
        var goldens = GoldenFile.Batch();

        var failure = Assert.Throws<XunitException>(goldens.Done);

        Assert.Contains("No goldens were compared", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A set reports every mismatch it found, not the first.</summary>
    /// <remarks>
    ///     The form this replaces threw on the first <c>Assert.Equal</c>, so a change that moved four
    ///     stages was four runs to see. It also left the remaining goldens unregenerated under the
    ///     switch, which is why each suite had grown a `continue` and a list.
    /// </remarks>
    [Fact]
    public void ASetReportsEveryMismatch() {
        var goldens = GoldenFile.Batch();
        goldens.Matches("changed", Committed("first.txt", "original"));
        goldens.Matches("changed", Committed("second.txt", "original"));

        var failure = Assert.Throws<XunitException>(goldens.Done);

        Assert.Contains("2 golden(s) differ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("first.txt", failure.Message, StringComparison.Ordinal);
        Assert.Contains("second.txt", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The failure carries a hunk with line numbers and both sides of what moved.</summary>
    /// <remarks>
    ///     ⚠ This is the half <c>Assert.Equal</c> cannot do: over a few kilobytes its message is a
    ///     window of characters around an offset, and the reviewer's question is which construct
    ///     changed. The rendering is also left as a <c>.actual</c> file, because the whole new text is
    ///     what a regeneration would commit.
    /// </remarks>
    [Fact]
    public void AMismatchIsReportedAsAHunkAndAnActualFile() {
        var path = Committed("diff.txt", "alpha\nbeta\ngamma\ndelta\n");

        var failure = Assert.Throws<XunitException>(() => GoldenFile.Matches("alpha\nbeta\nGAMMA\ndelta", path));

        Assert.Contains("@@ -1,3 +1,3 @@", failure.Message, StringComparison.Ordinal);
        Assert.Contains("- gamma", failure.Message, StringComparison.Ordinal);
        Assert.Contains("+ GAMMA", failure.Message, StringComparison.Ordinal);
        Assert.Equal("alpha\nbeta\nGAMMA\ndelta", File.ReadAllText(path + ".actual"));
    }

    string Committed(string name, string text) {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);

        return path;
    }
}
