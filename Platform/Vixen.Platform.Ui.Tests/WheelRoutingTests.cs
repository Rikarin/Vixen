// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>Which kind of device turned the wheel survives the trip to the document.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the join, and the join is where the fact was being lost.</b> A mouse wheel,
///         a finger on a trackpad and the momentum AppKit keeps delivering after the fingers leave
///         all arrive as one <c>SDL_MOUSEWHEEL</c> with no phase and no device class — measured on
///         macOS 15 against the SDL this repository ships, and written up on
///         <c>ScrollView.Wheeled</c>. Everything downstream that wants to treat a wheel differently
///         from a flick therefore depends on this one assignment in
///         <see cref="PlatformInput.Dispatch(UiDocument, in PlatformEvent)" />, and an assignment is
///         exactly the kind of line that goes missing without failing to build.
///     </para>
///     <para>
///         ⚠ <b>Both rows, because only one of them is a claim.</b>
///         <see cref="WheelEvent.Notched" /> false means "a continuous device <i>or</i> a backend
///         that could not tell", so a carrier that hard-coded either constant would satisfy half of
///         this and no more.
///     </para>
/// </remarks>
public class WheelRoutingTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 300f);
        document.Load("root { width: 400px; height: 300px; }");
        document.Update();

        return document;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_wheel_reaches_the_document_saying_which_device_produced_it(bool notched) {
        using var document = Documented();

        var seen = new List<WheelEvent>();
        document.Root.AddHandler<WheelEvent>((_, args) => seen.Add(args));

        var wheel = PlatformEvent.MouseWheel(
            1,
            0,
            new Vector2(20f, 20f),
            new Vector2(0f, 1f),
            notched: notched
        );

        Assert.True(PlatformInput.Dispatch(document, wheel));

        var arrived = Assert.Single(seen);
        Assert.Equal(notched, arrived.Notched);

        // And the delta is still the delta: the flag rides in a slot a wheel does not otherwise use,
        // so a carrier that clobbered one with the other would show up here rather than as a scroll
        // that is subtly the wrong distance.
        Assert.True(arrived.DeltaY < 0f, "a wheel turned away from the user scrolls towards the end");
    }
}
