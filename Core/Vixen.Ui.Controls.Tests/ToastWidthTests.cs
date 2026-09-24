// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A toast whose message is longer than the window fits inside the window — issue #1400.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The toast was as wide as its message, on one line, and ran off the window's left
///         edge.</b> Measured here before the fix, in a 400×300 window: the host and the toast 1528 px
///         wide at x −1144, the message one 1464 px line, the close button at x 343.
///     </para>
///     <para>
///         ⚠ <b>Both of the issue's candidate causes were real, and both are the layout's.</b> An
///         absolutely positioned box under a row parent is measured at max-content and never capped
///         at its containing block — a plain <c>div</c> with <c>left: 16px</c> in the same window is
///         1464 px wide — and a row flex item measured "at most" a width reports its content size
///         without shrinking its children, so pinning the host on both sides left the toast 1528 px
///         wide inside a 368 px host. The sheet answers each: <c>left</c> and <c>right</c> on the
///         host, <c>max-width: 100%</c> on the toast. The layout rules themselves are left alone
///         here, because code positions a great many absolute boxes by <c>left</c> alone.
///     </para>
///     <para>
///         The oracle is geometric and closed-form: the toast's box inside the window's, less the
///         sheet's 16 px gutter; the message wrapped rather than cut; and the close button the element
///         a pointer finds at its own centre, and dismissing the toast when pressed there.
///     </para>
/// </remarks>
public class ToastWidthTests {
    const float Width = 400f;
    const float Height = 300f;
    const float Gutter = 16f;

    const string Long =
        "The build finished with warnings in forty-two projects, and the longest of them is this sentence, "
        + "which goes on for considerably longer than any window a toast is likely to be shown in.";

    /// <summary>The toast lies inside the window, gutter and all, and the message wraps to fit it.</summary>
    [Fact]
    public void A_toast_longer_than_the_window_fits_inside_it() {
        using var ui = ControlHarness.Open(Width, Height);
        var host = ui.Add<ToastHost>("toasts");
        var toast = host.Show(Long);

        ui.Frame();
        ui.Frame();

        Save(ui, "toast-long");

        var box = toast.Bounds;

        Assert.True(
            box.Left >= Gutter - 0.5f && box.Right <= Width - Gutter + 0.5f,
            $"the toast is {box} in a {Width} px window, outside its {Gutter} px gutters (#1400); host {host.Bounds}, message {toast.MessagePart.Bounds}, close {toast.CloseButton.Bounds}."
        );

        Assert.True(
            toast.MessagePart.Block()!.Lines.Length > 1,
            $"the message is {toast.MessagePart.Block()!.Lines.Length} line in a toast {box.Width} px wide, so it did not wrap."
        );

        var message = toast.MessagePart.Bounds;
        Assert.True(message.Left >= box.Left && message.Right <= box.Right, $"the message {message} runs outside its toast {box}.");
    }

    /// <summary>The close button of a long toast can be reached, and takes the toast away.</summary>
    [Fact]
    public void The_close_button_of_a_long_toast_is_reachable() {
        using var ui = ControlHarness.Open(Width, Height);
        var host = ui.Add<ToastHost>("toasts");
        var toast = host.Show(Long);

        ui.Frame();
        ui.Frame();

        var close = toast.CloseButton.Bounds;
        var (x, y) = (close.X + (close.Width / 2f), close.Y + (close.Height / 2f));

        Assert.True(x is >= 0f and < Width && y is >= 0f and < Height, $"the close button's centre ({x}, {y}) is off the window.");

        var hit = ui.MovePointer(x, y);
        Assert.True(IsInside(hit, toast.CloseButton), $"a pointer at the close button's centre finds <{hit?.Tag}>.");

        ui.PressPointer();
        ui.ReleasePointer();
        ui.Frame();

        Assert.Empty(host.Live);
    }

    /// <summary>A short toast stays in the corner at its own width, and the strip beside it lets the pointer through.</summary>
    /// <remarks>
    ///     ⚠ <b>The host is a strip the width of the window since it is pinned on both sides</b>, so the
    ///     second half is the price of the fix: without <c>pointer-events: none</c> on it, a press
    ///     beside a short toast lands on the host rather than on whatever is under it.
    /// </remarks>
    [Fact]
    public void A_short_toast_stays_in_the_corner_and_the_strip_beside_it_is_not_the_host_s() {
        using var ui = ControlHarness.Open(Width, Height);
        var host = ui.Add<ToastHost>("toasts");
        var toast = host.Show("Saved.");

        ui.Frame();
        ui.Frame();

        var box = toast.Bounds;

        Assert.Equal(Width - Gutter, box.Right, 0.5f);
        Assert.True(box.Width < Width - (2f * Gutter) - 1f, $"a two-word toast is {box.Width} px wide, the whole strip.");

        // Beside the toast, at its own height, inside the host's box.
        var (x, y) = (Gutter + 10f, box.Y + (box.Height / 2f));
        Assert.True(x >= host.Bounds.Left && x < host.Bounds.Right && y >= host.Bounds.Top && y < host.Bounds.Bottom, $"({x}, {y}) is not inside the host {host.Bounds}, so this measures nothing.");

        var hit = ui.MovePointer(x, y);
        Assert.False(IsInside(hit, host), $"a pointer beside the toast finds <{hit?.Tag}>, inside the host.");
    }

    static bool IsInside(UiElement? element, UiElement ancestor) {
        for (var current = element; current is not null; current = current.Parent) {
            if (ReferenceEquals(current, ancestor)) {
                return true;
            }
        }

        return false;
    }

    static void Save(UiTest ui, string name) {
        if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, $"{name}.png"), ui.Capture());
        }
    }
}
