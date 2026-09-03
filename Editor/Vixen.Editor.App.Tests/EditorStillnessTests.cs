// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What the editor's shell actually costs on a frame where nothing happened.</summary>
/// <remarks>
///     <para>
///         <b>The instrument for "redraw only when something changed", measured on the real shell
///         rather than argued about.</b> `Vixen.Ui.Desktop`'s README says outright that the editor
///         "draws every frame rather than when something changes", and it is right about the
///         <i>drawing</i> — <c>Record</c>, <c>Upload</c>, <c>Compose</c> and the present are all
///         unconditional. What was never measured is whether the frame would have had anything new to
///         say, and <see cref="UiTest.Redraws" /> is that number: <c>DrawList</c> compares the rebuilt
///         commands against the previous frame's, so it counts frames whose picture actually differs.
///     </para>
///     <para>
///         ⚠⚠ <b>The measured answer refutes the premise the work was deferred on.</b> A settled
///         editor is not "redrawing every frame because everything animates"; over sixty stepped
///         frames it produces <b>no</b> changed drawing at all. The two self-changing readouts are
///         already throttled — <c>EditorShell.StatusInterval</c> and <c>ViewportChrome.StatsInterval</c>,
///         fifteen frames each — and everything else in an idle shell is genuinely still.
///     </para>
///     <para>
///         ⚠ <b><see cref="A_running_background_task_keeps_every_frame_dirty" /> is the half that
///         matters, and it is the anti-freeze test.</b> The reason this work was never attempted is
///         that a surface which forgets to declare itself freezes silently — a progress bar that stops
///         moving looks like a hung import rather than like a bug in a redraw gate. That failure is
///         now a red test rather than a thing somebody notices: while a task is running, every frame
///         must count, and a gate that skipped one would show a number below the frame count here.
///     </para>
///     <para>
///         ⚠ <b>Counted in frames, on the harness's stepped clock.</b> No wall-clock budget, so the
///         numbers are the same on an idle machine and a loaded one — which "the editor got faster"
///         would not be.
///     </para>
/// </remarks>
public class EditorStillnessTests {
    /// <summary>Sixty frames of an editor nobody is touching change nothing at all.</summary>
    /// <remarks>
    ///     ⚠ <b>If this goes red, something in the shell has started rewriting itself every frame, and
    ///     the fix is to throttle that thing rather than to relax this number.</b> That is what
    ///     <c>EditorShell.StatusInterval</c>'s own comment is about: one changed character makes the
    ///     whole window's draw list differ, so the window re-emits every vertex it drew last time
    ///     whatever is on screen. Sixty frames is four crossings of that fifteen-frame throttle, so a
    ///     readout that lost it would be caught here.
    ///     <para>
    ///         ⚠ The frame-time cell is constant in this harness because <c>EditorShell.FrameTime</c>
    ///         is zero with no host driving it, so the throttle is crossed but its <i>value</i> never
    ///         moves. This test therefore proves the shell is still; it does not prove the throttle
    ///         works. That is <c>EditorShell</c>'s own to prove.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_settled_editor_redraws_no_frames_at_all() {
        using var fixture = EditorSession.Start();

        fixture.Settle();

        var updates = fixture.Ui.Updates;
        var redraws = fixture.Ui.Redraws;

        fixture.Ui.Frames(60);

        Assert.Equal(updates, fixture.Ui.Updates);
        Assert.Equal(redraws, fixture.Ui.Redraws);
    }

    /// <summary>⚠ While a background task runs, every single frame has something new to draw.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The progress bar is the case a redraw gate must not get wrong</b>, and this is what
    ///         says so before the gate exists. <c>EditorShell.Tick</c> advances the indeterminate
    ///         phase every frame and <c>RefreshStatus</c> writes it into the bar while anything is
    ///         running, so the picture differs on every one of them — and a gate that decided
    ///         otherwise would stop a spinner dead while an import was still going, which reads as a
    ///         hang rather than as a bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it comes back to still when the task ends</b>, which is the removal half. A
    ///         "busy" flag that was set and never cleared would satisfy the first assertion for ever
    ///         and make the whole gate pointless; asserting only the rise is how that gets missed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_running_background_task_keeps_every_frame_dirty() {
        using var fixture = EditorSession.Start();

        fixture.Settle();

        var task = fixture.Shell.Tasks.Begin("Importing");

        // The bar appearing is itself a change; the claim is about the frames after it is on screen.
        fixture.Ui.Frames(4);

        var redraws = fixture.Ui.Redraws;

        fixture.Ui.Frames(30);

        Assert.Equal(redraws + 30, fixture.Ui.Redraws);

        fixture.Shell.Tasks.Complete(task);
        fixture.Ui.Frames(4);

        Assert.False(fixture.Shell.Tasks.IsBusy);

        redraws = fixture.Ui.Redraws;

        fixture.Ui.Frames(30);

        Assert.Equal(redraws, fixture.Ui.Redraws);
    }
}
