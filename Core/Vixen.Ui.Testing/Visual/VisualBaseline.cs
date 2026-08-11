// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;

namespace Vixen.Ui.Testing.Visual;

/// <summary>A committed picture, and what happens when the interface stops matching it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A missing reference is a failure, not a recording.</b> The obvious behaviour — write
///         the picture and pass — makes the first run of every screenshot green, which means nobody
///         ever looks at the picture that every later run is measured against. So a missing reference
///         writes what it drew where a human can open it, says exactly how to accept it, and fails.
///         The same argument the graphics golden suite makes, and it is the argument for the whole
///         technique: a suite that rewrites its own expectations when they fail is a suite that
///         always passes.
///     </para>
///     <para>
///         Accepting a change is <c>VIXEN_UPDATE_SCREENSHOTS=1</c>, deliberately an environment
///         variable and not an option a test can set for itself.
///     </para>
/// </remarks>
public static class VisualBaseline {
    /// <summary>Where references live when nothing says otherwise.</summary>
    public const string DirectoryName = "__screenshots__";

    /// <summary>Checks a picture against its reference, or records it as the new one.</summary>
    /// <param name="test">The test taking the picture, for its options and its log.</param>
    /// <param name="name">What the picture is called, which is also its file's name.</param>
    /// <param name="rendered">What was drawn.</param>
    /// <param name="tolerance">How far it may drift.</param>
    /// <returns>How it went, for the command log.</returns>
    /// <exception cref="UiTestException">It differs, or there is nothing to compare it with.</exception>
    public static string Verify(UiTest test, string name, in Bitmap rendered, ImageTolerance tolerance) {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var baselines = test.Options.BaselineDirectory
            ?? Path.Combine(AppContext.BaseDirectory, DirectoryName);

        var reference = Path.Combine(baselines, $"{name}.png");

        if (test.Options.UpdateBaselines) {
            // ⚠ The source tree, not the output directory. Rewriting the copy beside the binary makes
            // the run pass and changes nothing anybody can commit — the next clean build restores the
            // old picture and the fix evaporates.
            var accepted = Path.Combine(SourceDirectory(test), $"{name}.png");
            PngCodec.Save(accepted, rendered);
            return $"recorded {accepted}";
        }

        var artifacts = test.Options.ArtifactDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "artifacts", "screenshot-diff");

        if (!File.Exists(reference)) {
            var written = Path.Combine(artifacts, $"{name}.rendered.png");
            PngCodec.Save(written, rendered);

            throw Fail(
                test,
                $"screenshot \"{name}\" has no reference to compare against",
                $"what it drew is at {written}. Look at it, and if it is right, accept it with "
                + "VIXEN_UPDATE_SCREENSHOTS=1"
            );
        }

        var expected = PngCodec.Load(reference);

        if (rendered.Width != expected.Width || rendered.Height != expected.Height) {
            PngCodec.Save(Path.Combine(artifacts, $"{name}.rendered.png"), rendered);

            throw Fail(
                test,
                $"screenshot \"{name}\" changed size",
                $"drew {rendered.Width}×{rendered.Height}, the reference is "
                + $"{expected.Width}×{expected.Height}"
            );
        }

        var comparison = ImageComparer.Compare(rendered, expected, tolerance);

        if (comparison.Matches) {
            return comparison.ToString();
        }

        PngCodec.Save(Path.Combine(artifacts, $"{name}.rendered.png"), rendered);
        PngCodec.Save(Path.Combine(artifacts, $"{name}.expected.png"), expected);
        PngCodec.Save(Path.Combine(artifacts, $"{name}.diff.png"), ImageComparer.Diff(rendered, expected, tolerance));

        throw Fail(
            test,
            $"screenshot \"{name}\" does not match its reference",
            $"{comparison}. The rendering, the reference and a diff are in {artifacts}"
        );
    }

    /// <summary>Where an accepted picture is written so that it can be committed.</summary>
    /// <param name="test">Whose options to read.</param>
    /// <returns>The directory.</returns>
    /// <remarks>
    ///     Configured, or found by walking up from the binary until a directory holding a project
    ///     file turns up. The walk is a convenience and not a guarantee: a layout it cannot make
    ///     sense of falls back to the directory the references were read from, which at least keeps
    ///     the run working and is visible in the message the update prints.
    /// </remarks>
    public static string SourceDirectory(UiTest test) {
        ArgumentNullException.ThrowIfNull(test);

        if (test.Options.SourceBaselineDirectory is { Length: > 0 } configured) {
            return configured;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (directory.GetFiles("*.csproj").Length > 0) {
                return Path.Combine(directory.FullName, DirectoryName);
            }
        }

        return test.Options.BaselineDirectory ?? Path.Combine(AppContext.BaseDirectory, DirectoryName);
    }

    static UiTestException Fail(UiTest test, string what, string found) =>
        UiTestException.Build(what, found, 0, test.Log, test.Tree());
}
