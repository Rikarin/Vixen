// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What an animation that never ends costs the value table, frame after frame.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The value table only grows.</b> Every value the animator overlays is interned, because
///         a <c>ComputedStyle</c> holds ids and not text, and <c>NameTable</c> has no way to forget
///         one. A <c>transform-mix(…)</c> carried its progress at full float precision, which is a new
///         string on nearly every frame — so an infinite spinner grew the table by one entry a frame
///         for as long as the document lived: 216,000 an hour at 60 Hz (#1383).
///     </para>
///     <para>
///         A count, not a clock or a byte budget: the property is that the number of distinct values
///         an animation can ever write is bounded by the animation, not by how long it has run.
///     </para>
/// </remarks>
public class AnimationInterningTests {
    /// <summary>
    ///     A frame interval incommensurate with the one-second period, so no two frames land on one
    ///     phase — as a real clock's jitter guarantees and a fixed <c>1/60</c> would not.
    /// </summary>
    static readonly double Step = 1.0 / (60.0 + Math.Sqrt(2.0));

    const int Frames = 25_000;

    /// <summary>
    ///     ⚠ <b>Twenty-five thousand frames of a spinner add at most the 10,001 progresses a mix is
    ///     written at</b>, where they used to add one per frame.
    /// </summary>
    /// <remarks>
    ///     The bound is the mix's own grid — its progress is written to four decimals, as every number
    ///     the animator overlays already was — and is well under the frame count, so an implementation
    ///     that went back to interning each frame's float fails by more than half the frames again.
    /// </remarks>
    [Fact]
    public void A_spinner_writes_a_bounded_set_of_transforms() {
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            #box { width: 100px; height: 40px; }
            @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
            #box { animation-name: spin; animation-duration: 1s; animation-timing-function: linear;
                   animation-iteration-count: infinite; }
            """);

        var box = document.Create("div", document.Root, "box");

        document.Tick(TimeSpan.Zero);
        document.Update();

        var before = document.Styles.Values.Count;

        for (var frame = 1; frame <= Frames; frame++) {
            document.Tick(TimeSpan.FromSeconds(frame * Step));
            document.Update();
        }

        // The instrument: it did spin, so the frames were not all one value.
        Assert.NotNull(box.Transform);

        Assert.InRange(document.Styles.Values.Count - before, 1000, 10_001);
    }
}
