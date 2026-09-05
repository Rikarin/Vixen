// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a window that nobody is touching costs, stated as work.</summary>
/// <remarks>
///     <para>
///         <b>The instrument for doc 49 § 7.3, written before the work it is meant to measure.</b>
///         There is no retained per-element surface and no dirty-rect path — <c>DrawListBuilder</c>
///         reconstructs the whole list every frame of every window — and the gate that change has to
///         pass is a <i>differential</i> in idle-frame work, taken on the same machine at the same
///         moment. A differential needs a number that exists on both sides of it, and this file is
///         that number going in.
///     </para>
///     <para>
///         ⚠ <b>These assertions are written to be <i>wrong</i> the day the retained surface lands,
///         and that is the point rather than a defect.</b> <c>DrawListsBuilt</c> climbing once per
///         frame is today's behaviour, not a promise; when the drawing is retained it stops climbing
///         and this file goes red at exactly the line that says so. A gate that could not tell the
///         two worlds apart would be no gate at all — see the repository's rule about a predicate
///         that cannot be false.
///     </para>
///     <para>
///         ⚠ <b>Work and not time.</b> A rebuild count is the same on an idle laptop and a loaded
///         one; the wall-clock budget that would otherwise be reached for is this repository's
///         largest flake source, and it also cannot express the thing being measured — an interface
///         that redraws a hundred times to produce one picture is wasteful at every frame rate.
///     </para>
/// </remarks>
public class IdleFrameWorkTests {
    const int Frames = 30;

    static UiDocument Still() {
        var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            div { width: 40px; height: 20px; background-color: #345; }
            """);

        for (var index = 0; index < 8; index++) {
            document.Root.Add("div");
        }

        return document;
    }

    /// <summary>
    ///     ⚠ Ninety-seven per cent of the rebuilds on a still window produce a list identical to the
    ///     one before it. That gap is the whole of what a retained surface is for, and until this
    ///     counter existed it could only be described in watts — a unit that depends on the machine,
    ///     on what else is running, and on nothing a test can assert.
    /// </summary>
    [Fact]
    public void A_still_window_rebuilds_its_drawing_every_frame_and_changes_it_once() {
        using var document = Still();

        for (var frame = 0; frame < Frames; frame++) {
            document.Update();
            document.Draw();
        }

        Assert.Equal(Frames, document.Diagnostics.DrawListsBuilt);
        Assert.Equal(1, document.Diagnostics.DrawListsChanged);
    }

    /// <summary>
    ///     The other half of the instrument, and the one that makes the first mean something: a
    ///     change really is counted. A counter that only ever said "nothing changed" would report a
    ///     perfect score for a document that had stopped drawing altogether.
    /// </summary>
    [Fact]
    public void A_window_that_actually_changes_says_so() {
        using var document = Still();

        document.Update();
        document.Draw();

        var changed = document.Diagnostics.DrawListsChanged;

        document.Root.Children[0].AddClass("moved");
        document.Load(".moved { width: 90px; }");

        document.Update();
        document.Draw();

        Assert.Equal(changed + 1, document.Diagnostics.DrawListsChanged);
    }

    /// <summary>
    ///     ⚠ Counted per window and not per frame. A document with a torn-off panel rebuilds two
    ///     lists a frame, and a count that lived in <c>Draw()</c> rather than in <c>Draw(surface)</c>
    ///     would report one — halving the measured cost of exactly the configuration that costs the
    ///     most.
    /// </summary>
    [Fact]
    public void Every_window_is_counted_and_not_every_frame() {
        using var document = Still();

        document.CreateSurface(120f, 80f);

        document.Update();
        document.Draw();

        Assert.Equal(2, document.Diagnostics.DrawListsBuilt);
    }

    /// <summary>The five buffers a frame boundary has to carry over, read as instances.</summary>
    static object[] Buffers(DrawList list) => [list.Commands, list.Glyphs, list.Segments, list.Boxes, list.Masks];

    /// <summary>
    ///     ⚠ <b>The other half of an idle frame's cost, and the one no allocation gate can see.</b>
    ///     Keeping the finished frame for the next frame's diff was five <c>AddRange</c>s — on the
    ///     editor shell's 1 389 commands a 444 KB copy every frame, landing in capacity that already
    ///     existed, so <c>A_settled_frame_allocates_nothing</c> was blind to it. The pairs are a double
    ///     buffer, so the boundary is two references exchanged.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Stated as identity rather than as bytes or milliseconds, because identity is what
    ///         tells the two implementations apart.</b> A copy leaves the same list instance in place
    ///         forever and rewrites its contents; a swap alternates between two. So the assertion is
    ///         that the instance a frame drew into is <i>not</i> the one the next frame draws into, and
    ///         <i>is</i> the one the frame after that returns to — which a copy cannot satisfy at any
    ///         speed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All five pairs, not just the commands.</b> A swap that forgot the masks would be
    ///         silently wrong in only the frames that composite, and the two tests above would not
    ///         see it: a stale <c>previous</c> half makes the diff report a change that did not happen,
    ///         which is a lost optimisation rather than a wrong picture, so it hides.
    ///     </para>
    ///     <para>
    ///         And the content half is guarded by the two tests above rather than repeated here — a
    ///         boundary that lost the finished frame would make a still window report thirty changes
    ///         instead of one, which is exactly what
    ///         <see cref="A_still_window_rebuilds_its_drawing_every_frame_and_changes_it_once" /> reads.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_frame_boundary_exchanges_its_buffers_instead_of_copying_between_them() {
        using var document = Still();

        document.Update();
        document.Draw();

        var first = Buffers(document.Drawing);

        document.Update();
        document.Draw();

        var second = Buffers(document.Drawing);

        document.Update();
        document.Draw();

        var third = Buffers(document.Drawing);

        for (var buffer = 0; buffer < first.Length; buffer++) {
            Assert.NotSame(first[buffer], second[buffer]);
            Assert.Same(first[buffer], third[buffer]);
        }
    }

    /// <summary>
    ///     A document that is never drawn has done no drawing work, which is the answer the instrument
    ///     has to give on the day nothing calls it — the reading that would otherwise be mistaken for
    ///     a perfectly efficient interface.
    /// </summary>
    [Fact]
    public void A_document_that_never_drew_reports_no_work() {
        using var document = Still();

        document.Update();

        Assert.Equal(0, document.Diagnostics.DrawListsBuilt);
        Assert.Equal(0, document.Diagnostics.DrawListsChanged);
    }
}
