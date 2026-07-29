// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The window's own size and place, which the editor forgot on every launch.</summary>
/// <remarks>
///     ⚠ <b>Everything else about the editor's shape persists — the arrangement, the keymap, the
///     theme.</b> Doc 20's third bar is "the window is theirs", and a window that opens at a fixed
///     1600×1000 however it was closed is the one thing on screen the user has to fix every time.
/// </remarks>
public class WindowPlacementTests : IDisposable {
    readonly string directory =
        Path.Combine(Path.GetTempPath(), "vixen-placement-tests", Guid.NewGuid().ToString("N"));

    public WindowPlacementTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            Directory.Delete(directory, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }

        GC.SuppressFinalize(this);
    }

    void Save(string yaml) => new EditorUserStore(directory).Write(WindowPlacement.File, yaml);

    [Fact]
    public void A_first_run_opens_at_the_default_and_lets_the_platform_place_it() {
        var (size, position) = WindowPlacement.Load(directory);

        Assert.Equal(WindowPlacement.Default, size);
        Assert.Null(position);
    }

    [Fact]
    public void A_saved_size_and_position_come_back() {
        Save("width: 1280\nheight: 720\nx: 40\ny: 90\n");

        var (size, position) = WindowPlacement.Load(directory);

        Assert.Equal(new Int2(1280, 720), size);
        Assert.Equal(new Int2(40, 90), position);
    }

    [Fact]
    public void A_size_with_no_position_is_honoured_on_its_own() {
        // What a platform without window positioning writes: the size is meaningful there and the
        // position is not, so saving a zero would move the window to the corner on the next launch.
        Save("width: 1280\nheight: 720\n");

        var (size, position) = WindowPlacement.Load(directory);

        Assert.Equal(new Int2(1280, 720), size);
        Assert.Null(position);
    }

    /// <summary>
    ///     ⚠ A window restored at 40×12 — which a bad shutdown or a hand-edited file can produce — is
    ///     one the user cannot resize back, because the grips are smaller than the pointer.
    /// </summary>
    [Fact]
    public void A_degenerate_size_falls_back_to_the_default() {
        Save("width: 40\nheight: 12\n");

        var (size, _) = WindowPlacement.Load(directory);

        Assert.Equal(WindowPlacement.Default, size);
    }

    [Fact]
    public void A_file_that_will_not_parse_is_the_default_rather_than_a_failure_to_start() {
        Save("this is not: [a mapping");

        var (size, position) = WindowPlacement.Load(directory);

        Assert.Equal(WindowPlacement.Default, size);
        Assert.Null(position);
    }

    [Fact]
    public void A_missing_number_falls_back_to_the_default_for_that_axis() {
        Save("width: 1280\n");

        var (size, _) = WindowPlacement.Load(directory);

        Assert.Equal(1280, size.X);
        Assert.Equal(WindowPlacement.Default.Y, size.Y);
    }
}
