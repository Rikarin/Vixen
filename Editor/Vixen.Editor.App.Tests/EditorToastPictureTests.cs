// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The editor's own toasts, raised with messages longer than the window — issue #1400.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Where the defect was seen.</b> The batch that fixed the console's rows (#1391) raised two
///         long <c>Notifications.Show</c> messages in a 1600×1000 editor, and both toasts spanned the
///         whole window with their close buttons at x ≈ 1557 and every line beginning mid-sentence at
///         x = 0. <c>ToastWidthTests</c> pins the control on its own theme; this pins it under
///         <c>EditorTheme</c>'s override and in the shell the editor really builds, and draws both
///         renderers' pictures when <c>VIXEN_PANEL_CAPTURE</c> names somewhere to put them.
///     </para>
/// </remarks>
public sealed class EditorToastPictureTests {
    [Fact]
    public void Two_long_toasts_lie_inside_the_editor_s_window() {
        using var fixture = ScrollingPanelPictureTests.Start();

        var sentence = string.Join(" ", Enumerable.Repeat("a message far longer than the window it is shown in", 8));

        fixture.Shell.Notifications.Show("first " + sentence);
        fixture.Shell.Notifications.Show("second " + sentence);
        fixture.Frames(2);

        var width = ScrollingPanelPictureTests.WidthOf(fixture);
        var height = ScrollingPanelPictureTests.HeightOf(fixture);
        var toasts = fixture.Shell.Toasts.Live;

        // Drawn before anything is asserted, so that a red run leaves the picture of what was wrong.
        using var device = ScrollingPanelPictureTests.OpenDevice();
        using var gpu = device is null ? null : new ScrollingPanelPictureTests.GpuPicture(device, width, height);

        ScrollingPanelPictureTests.Draw(fixture, gpu, "editor-long-toasts");

        Assert.Equal(2, toasts.Count);

        foreach (var toast in toasts) {
            var box = toast.Bounds;

            Assert.True(
                box.Left >= 0f && box.Right <= width && box.Top >= 0f && box.Bottom <= height,
                $"a toast is {box} in a {width}×{height} editor (#1400)."
            );

            Assert.True(toast.MessagePart.Block()!.Lines.Length > 1, $"a toast {box.Width} px wide holds its message on one line.");

            var close = toast.CloseButton.Bounds;
            Assert.True(close.Left >= box.Left && close.Right <= box.Right, $"the close button {close} is outside its toast {box}.");
        }

        Assert.True(toasts[0].Bounds.Bottom <= toasts[1].Bounds.Top + 0.5f, "the two toasts overlap.");
    }
}
