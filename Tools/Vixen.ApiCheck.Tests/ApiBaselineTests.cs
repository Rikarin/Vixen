// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The two-file baseline: what it approves, what it reports, and what <c>--update-api</c>
///     writes back.
/// </summary>
public sealed class ApiBaselineTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-api-baseline-tests", Guid.NewGuid().ToString("N"));

    public ApiBaselineTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Fact]
    public void AMatchingSurface_IsNoDifference() {
        var difference = ApiBaseline.Compare(["Sample.A -> class", "Sample.A.B() -> void"], ["Sample.A -> class"], ["Sample.A.B() -> void"]);

        Assert.True(difference.IsEmpty);
    }

    [Fact]
    public void AnEntryNoBaselineApproves_IsAnAddition() {
        var difference = ApiBaseline.Compare(["Sample.A -> class", "Sample.A.New() -> void"], ["Sample.A -> class"], []);

        Assert.Equal(["Sample.A.New() -> void"], difference.Added);
        Assert.Empty(difference.Removed);
    }

    /// <summary>
    ///     The direction the gate exists for at least as much as the other: an approved entry that
    ///     is gone is a break, and nothing else in the build would notice it.
    /// </summary>
    [Fact]
    public void AnApprovedEntryThatIsGone_IsARemoval() {
        var difference = ApiBaseline.Compare(["Sample.A -> class"], ["Sample.A -> class", "Sample.A.Old() -> void"], []);

        Assert.Equal(["Sample.A.Old() -> void"], difference.Removed);
        Assert.Empty(difference.Added);
    }

    [Fact]
    public void ARemovedMarker_WithdrawsAShippedEntry() {
        var difference = ApiBaseline.Compare(
            ["Sample.A -> class"],
            ["Sample.A -> class", "Sample.A.Old() -> void"],
            [ApiBaseline.RemovedPrefix + "Sample.A.Old() -> void"]
        );

        Assert.True(difference.IsEmpty);
    }

    [Fact]
    public void Rebase_LeavesShippedEntriesAlone_AndMarksWhatIsGone() {
        var unshipped = ApiBaseline.Rebase(
            ["Sample.A -> class", "Sample.A.New() -> void"],
            ["Sample.A -> class", "Sample.A.Old() -> void"]
        );

        Assert.Equal(
            [ApiBaseline.RemovedPrefix + "Sample.A.Old() -> void", "Sample.A.New() -> void"],
            unshipped.OrderBy(entry => entry, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void AWrittenBaseline_RoundTrips() {
        var path = Path.Combine(directory, ApiBaseline.UnshippedFileName);

        ApiBaseline.Write(path, ["Sample.B -> class", "Sample.A -> class"]);

        Assert.Equal(["Sample.A -> class", "Sample.B -> class"], ApiBaseline.Read(path));
    }

    /// <summary>
    ///     Written with Unix line endings whatever the operating system, because a baseline that
    ///     rewrites every line when it is regenerated on Windows has diffs that say nothing.
    /// </summary>
    [Fact]
    public void AWrittenBaseline_IsHeadedAndUnixLineEnded() {
        var path = Path.Combine(directory, ApiBaseline.ShippedFileName);

        ApiBaseline.Write(path, ["Sample.A -> class"]);
        var content = File.ReadAllText(path);

        Assert.StartsWith("#nullable enable\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', content);
    }

    [Fact]
    public void AnAbsentBaseline_ReadsAsNothing() =>
        Assert.Empty(ApiBaseline.Read(Path.Combine(directory, "not-written-yet.txt")));

    [Fact]
    public void TheBaselineDirectory_IsTheProjectDirectory() {
        var project = Path.Combine(directory, "Sample");
        var output = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(project, "Sample.csproj"), "<Project />");

        Assert.Equal(project, ApiBaseline.DirectoryFor(Path.Combine(output, "Sample.dll")));
    }

    [Fact]
    public void AnAssemblyOutsideAnyProject_IsAnError() =>
        Assert.Throws<InvalidOperationException>(() => ApiBaseline.DirectoryFor(Path.Combine(directory, "Loose.dll")));
}
