// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>`vixen frame explode`, driven through the same parser a person uses.</summary>
/// <remarks>
///     Real files in a temporary directory rather than a filesystem seam, because the command's
///     whole behaviour is two file operations and a transform — and the transform itself is tested
///     where it lives. What these assert is the command's contract: where the output lands, what
///     the summary says, and that every way a document can be wrong comes back as words rather than
///     as a stack trace.
/// </remarks>
public sealed class FrameCommandTests : IDisposable {
    readonly StringWriter output = new();
    readonly string directory = Directory.CreateTempSubdirectory("vixen-frame-").FullName;

    /// <summary>The whole point of doc 39: this is a project's entire frame document.</summary>
    const string SevenKnobs =
        """
        version: 2
        game: !StandardFrame
          quality: High
          shadows: Cascades
          gi: Probes
          reflections: Screen
          antialiasing: Taa
          exposure: Automatic
          output: SceneColour
        """;

    public void Dispose() {
        output.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    async Task<int> RunAsync(params string[] args) =>
        await VixenCommand.Create(output, output).Parse(args).InvokeAsync();

    [Fact]
    public async Task Explode_writes_the_expanded_document_beside_the_input() {
        var path = Path.Combine(directory, "Frame.vxcompositor");

        await File.WriteAllTextAsync(path, SevenKnobs, TestContext.Current.CancellationToken);

        Assert.Equal(0, await RunAsync("frame", "explode", path));

        var target = Path.Combine(directory, "Frame.exploded.vxcompositor");

        Assert.True(File.Exists(target));

        var text = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);

        // The node is gone, the graph and the comments are there, and the file says what it is.
        Assert.StartsWith("# Exploded from !StandardFrame", text, StringComparison.Ordinal);
        Assert.DoesNotContain("game: !StandardFrame", text, StringComparison.Ordinal);
        Assert.Contains("game: !Sequence", text, StringComparison.Ordinal);
        Assert.Contains("- name: SceneHdr", text, StringComparison.Ordinal);
        Assert.Contains("# What the scene is drawn into", text, StringComparison.Ordinal);

        // The summary names the output; the input is untouched.
        Assert.Contains("Frame.exploded.vxcompositor", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(SevenKnobs, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task In_place_overwrites_the_document_itself() {
        var path = Path.Combine(directory, "Frame.vxcompositor");

        await File.WriteAllTextAsync(path, SevenKnobs, TestContext.Current.CancellationToken);

        Assert.Equal(0, await RunAsync("frame", "explode", path, "--in-place"));
        Assert.False(File.Exists(Path.Combine(directory, "Frame.exploded.vxcompositor")));

        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("game: !StandardFrame", text, StringComparison.Ordinal);
        Assert.Contains("game: !Sequence", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Not an error to have written by hand, but an error to explode: the honest answer is that
    ///     the file is already the expanded form, not a copy with a new name.
    /// </summary>
    [Fact]
    public async Task A_document_without_the_node_is_refused_with_the_reason() {
        var path = Path.Combine(directory, "Plain.vxcompositor");

        await File.WriteAllTextAsync(path, "version: 2\ngame: !Sequence\n  name: Frame\n", TestContext.Current.CancellationToken);

        Assert.Equal(1, await RunAsync("frame", "explode", path));
        Assert.Contains("no !StandardFrame", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "Plain.exploded.vxcompositor")));
    }

    [Fact]
    public async Task A_missing_file_is_a_usage_error() {
        Assert.Equal(2, await RunAsync("frame", "explode", Path.Combine(directory, "Nowhere.vxcompositor")));
        Assert.Contains("There is no document", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_document_is_reported_rather_than_thrown() {
        var path = Path.Combine(directory, "Broken.vxcompositor");

        await File.WriteAllTextAsync(path, "version: [not: a: version\n", TestContext.Current.CancellationToken);

        Assert.Equal(1, await RunAsync("frame", "explode", path));
        Assert.Contains("Broken.vxcompositor", output.ToString(), StringComparison.Ordinal);
    }
}
