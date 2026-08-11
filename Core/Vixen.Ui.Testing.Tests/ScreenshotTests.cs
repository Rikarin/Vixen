// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>The baseline workflow: record once, compare after, and never both at the same time.</summary>
/// <remarks>
///     Against temporary directories rather than committed pictures, deliberately. What these are
///     about is the <i>protocol</i> — that a missing reference fails, that accepting one writes where
///     it can be committed, that a changed interface produces the three files a reviewer opens. A
///     committed PNG would test the rasteriser, and <see cref="RasterizerTests" /> does that by
///     reading pixels, which is a claim a human can check.
/// </remarks>
public sealed class ScreenshotTests : IDisposable {
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vixen-ui-testing-" + Guid.NewGuid().ToString("n")
    );

    UiTest Opened(bool updating = false) {
        var ui = UiTest.Create(
            32,
            32,
            new UiTestOptions {
                Background = new Color4(0f, 0f, 0f, 1f),
                BaselineDirectory = Path.Combine(root, "baselines"),
                SourceBaselineDirectory = Path.Combine(root, "baselines"),
                ArtifactDirectory = Path.Combine(root, "artifacts"),
                UpdateBaselines = updating
            }
        );

        ui.Load("root { width: 32px; height: 32px; } .box { width: 10px; height: 10px; background-color: #ffffff; }");
        return ui;
    }

    [Fact]
    public void A_missing_reference_fails_and_says_how_to_accept_it() {
        using var ui = Opened();

        var failure = Assert.Throws<UiTestException>(() => ui.Screenshot("first"));

        // ⚠ The obvious behaviour — write it and pass — makes the first run of every screenshot
        // green, which means nobody ever looks at the picture everything later is measured against.
        Assert.Contains("no reference", failure.Message, StringComparison.Ordinal);
        Assert.Contains("VIXEN_UPDATE_SCREENSHOTS", failure.Message, StringComparison.Ordinal);

        // And it writes what it drew, so that "look at it" is something somebody can actually do.
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "first.rendered.png")));
        Assert.False(File.Exists(Path.Combine(root, "baselines", "first.png")));
    }

    [Fact]
    public void Accepting_writes_a_reference_that_then_verifies() {
        using (var recording = Opened(updating: true)) {
            recording.Create("div", recording.Document.Root, null, "box");
            recording.Frame();
            recording.Screenshot("box");
        }

        Assert.True(File.Exists(Path.Combine(root, "baselines", "box.png")));

        using var ui = Opened();
        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        // Exact, which is the whole claim: the same interface renders to the same bytes, so nothing
        // has to be tolerated.
        ui.Screenshot("box", ImageTolerance.Exact);
    }

    [Fact]
    public void A_changed_interface_fails_and_writes_the_three_files_a_reviewer_opens() {
        using (var recording = Opened(updating: true)) {
            recording.Create("div", recording.Document.Root, null, "box");
            recording.Frame();
            recording.Screenshot("moved");
        }

        using var ui = Opened();
        ui.Load(".box { margin-left: 8px; }");
        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        var failure = Assert.Throws<UiTestException>(() => ui.Screenshot("moved"));

        Assert.Contains("does not match", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pixels differ", failure.Message, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "artifacts", "moved.rendered.png")));
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "moved.expected.png")));
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "moved.diff.png")));
    }

    [Fact]
    public void A_size_change_is_reported_as_one_rather_than_compared_around() {
        using (var recording = Opened(updating: true)) {
            recording.Screenshot("resized");
        }

        using var ui = UiTest.Create(
            48,
            48,
            new UiTestOptions {
                BaselineDirectory = Path.Combine(root, "baselines"),
                ArtifactDirectory = Path.Combine(root, "artifacts")
            }
        );

        var failure = Assert.Throws<UiTestException>(() => ui.Screenshot("resized"));

        Assert.Contains("changed size", failure.Message, StringComparison.Ordinal);
        Assert.Contains("48×48", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screenshot_appears_in_the_command_log_either_way() {
        using (var recording = Opened(updating: true)) {
            recording.Screenshot("logged");
        }

        using var ui = Opened();
        ui.Screenshot("logged");

        var line = ui.Log.Commands.Single(command => command.Text.Contains("logged", StringComparison.Ordinal));
        Assert.Equal("identical", line.Outcome);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }
}
