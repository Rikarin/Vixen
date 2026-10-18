// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 45 § Staging step 4: the window's strips follow the state instead of asking for it.</summary>
/// <remarks>
///     <para>
///         <b>Both directions, because either one alone is passed by a defect.</b> A shell that still
///         polled would keep Save right and would be exactly what this exists to remove; a shell that
///         had simply deleted the poll would ask nothing on a quiet frame and would leave Save greyed
///         after the first edit. So the suite asserts that the strip does follow a change, <i>and</i>
///         that a stretch of frames with nothing happening asks no predicate at all.
///     </para>
///     <para>
///         ⚠ <b>The counting is done by a command of this suite's own, put on the strip.</b> The
///         editor's own predicates cannot be counted from outside — and what is being tested is the
///         mechanism, not <c>file.save</c>: if the shell polls, it polls every button on the strip,
///         so a probe button is a faithful instrument and an exact one.
///     </para>
/// </remarks>
public class ToolbarPollTests {
    [Fact]
    public void A_quiet_frame_asks_no_toolbar_predicate_and_a_change_asks_again() {
        using var fixture = EditorSession.Start();

        var asked = 0;

        fixture.Shell.Commands.Add(
            new EditorCommand("test.probe", new StringId("test.probe", "Probe"), static () => { }) {
                Enablement = () => {
                    asked++;
                    return true;
                }
            }
        );

        fixture.Shell.Toolbar.Show(new ToolbarButton("test.probe"));
        fixture.Settle();

        var settled = asked;

        Assert.True(settled > 0, "the probe was never asked at all, so the instrument is not wired");

        // ⚠ Ten frames in which nothing happens. Under the poll this was ten more questions.
        fixture.Frames(10);

        Assert.Equal(settled, asked);

        // And a change still reaches it. `Commands.Executed` is the notification: running any command
        // is a change to what the other commands say.
        fixture.Run("view.toggle-theme").Settle();

        Assert.True(asked > settled, "nothing asked the strip after a command ran");
    }

    /// <summary>Save greys and un-greys from the scene's own dirty signal, with nothing polling.</summary>
    [Fact]
    public void Save_follows_the_scenes_dirty_signal_rather_than_being_asked_every_frame() {
        using var fixture = EditorSession.Start();

        fixture.Settle();

        var save = Button(fixture, "file.save");

        // A freshly opened scene has nothing to write.
        Assert.True(save.Disabled);

        // ⚠ Through the document rather than through a command, deliberately. Running any command
        // invalidates on its own now, so a test that dirtied the scene by pressing a menu line could
        // not tell the dirty signal from `Commands.Executed`.
        fixture.Scene.Create("Poll Test Crate", LocalTransform.Identity);
        fixture.Settle();

        Assert.True(fixture.Scene.IsDirty.Value, "adding an entity did not dirty the scene");
        Assert.False(save.Disabled, "Save stayed greyed after the scene was dirtied");

        fixture.Run("file.save").Settle();

        Assert.False(fixture.Scene.IsDirty.Value);
        Assert.True(save.Disabled, "Save stayed pressable after the scene was written");
    }

    /// <summary>Build greys while content work runs and comes back when it ends, from the task alone.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second predicate #430 named, and the only one of the three notifications with
    ///         no test until this one.</b> <c>assets.build</c> reads <c>ContentTasks.IsBusy</c>, a
    ///         counter a pool thread moves; what tells the strip is <c>BusyChanged</c> reaching
    ///         <c>InvalidateCommands</c>. Deleting that one line left every other test here green.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The un-greying is the half that proves it.</b> Starting the work is a click, and a
    ///         click can invalidate on its own; the work <i>ending</i> happens on the pool with nobody
    ///         touching the editor, so the only thing that can bring the button back is the task's own
    ///         notification. The publish is held on a gate so the busy state is a fact rather than a
    ///         race.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Build_greys_while_content_work_runs_and_comes_back_when_it_ends() {
        using var fixture = EditorSession.Start();
        using var gate = new ManualResetEventSlim();

        File.WriteAllText(Path.Combine(fixture.ProjectRoot, "Game.csproj"), "<Project />");

        fixture.Editor.Content.Publisher = (_, _, _) => {
            // A hang check, not a budget: the test opens the gate itself a few lines down.
            gate.Wait(TimeSpan.FromSeconds(30));
            return Task.FromResult(true);
        };

        fixture.Settle();

        var build = Button(fixture, "assets.build");

        Assert.False(build.Disabled, "Build was greyed before anything was running");

        var view = fixture.Control<BuildSettingsView>(EditorApplication.BuildPanel);

        view.Rebuild();
        fixture.Click(view.BuildButton);

        Until(fixture, () => fixture.Editor.Content.IsBusy, "the build never started");
        fixture.Settle();

        Assert.True(build.Disabled, "Build stayed pressable while content work was running");

        gate.Set();

        Until(fixture, () => !fixture.Editor.Content.IsBusy, "the build never finished");
        fixture.Settle();

        Assert.False(build.Disabled, "Build stayed greyed after the content work ended");
    }

    /// <summary>Pumps frames until a pool thread has done something. The ceiling is a hang check.</summary>
    static void Until(EditorSession fixture, Func<bool> done, string what) {
        for (var frame = 0; frame < 400 && !done(); frame++) {
            fixture.Frame();
            Thread.Sleep(1);
        }

        Assert.True(done(), what);
    }

    /// <summary>And the tick itself no longer names either strip.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted on the behaviour rather than on the source, which is why it is the frame
    ///     count that is measured.</b> A test that grepped <c>EditorShell.cs</c> for
    ///     <c>Toolbar.Refresh</c> would go green the day somebody moved the same call one method
    ///     deeper.
    /// </remarks>
    [Fact]
    public void The_mode_strip_is_not_asked_on_a_quiet_frame_either() {
        using var fixture = EditorSession.Start();

        var asked = 0;

        fixture.Shell.Commands.Add(
            new EditorCommand("test.mode-probe", new StringId("test.mode-probe", "Mode Probe"), static () => { }) {
                Enablement = () => {
                    asked++;
                    return true;
                }
            }
        );

        fixture.Shell.ModeBar.Show(new ToolbarButton("test.mode-probe"));
        fixture.Settle();

        var settled = asked;
        Assert.True(settled > 0, "the mode strip never asked, so the instrument is not wired");

        fixture.Frames(10);

        Assert.Equal(settled, asked);
    }

    static ButtonBase Button(EditorSession fixture, string id) {
        foreach (var button in fixture.Shell.Toolbar.Strip.Children.OfType<ButtonBase>()) {
            if (string.Equals(button.Command, id, StringComparison.Ordinal)) {
                return button;
            }
        }

        throw fixture.Fail($"'{id}' is not a button on the toolbar.");
    }
}
