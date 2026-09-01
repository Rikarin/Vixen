// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The three things the harness does that nothing else in the editor can be asked to.</summary>
/// <remarks>
///     ⚠ <b>A harness with no tests of its own is one whose failures are attributed to whatever it
///     was driving.</b> The rest of this suite exercises <c>Open</c>, <c>ClickRow</c> and <c>Run</c>
///     on every line; what is left is the three capabilities the scenario tests are about to lean
///     their whole weight on — restarting over the same disk, reaching a command through the menu
///     rather than through the registry, and answering a dialog by pressing its buttons.
/// </remarks>
public class HarnessTests {
    /// <summary>
    ///     ⚠ A restart that kept anything alive would let a scenario pass for a scene that was never
    ///     written, which is the one failure "save, reopen, assert" exists to catch.
    /// </summary>
    [Fact]
    public void A_restart_is_a_new_editor_over_the_same_directories() {
        using var session = EditorSession.Start();

        var directory = session.DataDirectory;
        var root = session.ProjectRoot;
        var shell = session.Shell;
        var scene = session.Scene;

        session.Restart();

        Assert.Equal(directory, session.DataDirectory);
        Assert.Equal(root, session.ProjectRoot);

        Assert.NotSame(shell, session.Shell);
        Assert.NotSame(scene, session.Scene);

        // And it is a working editor rather than a set of objects: the scene came back off the disk
        // with what was in it, and the panels are up.
        Assert.Contains("Crate", EditorSession.Labels(session.Hierarchy));
    }

    [Fact]
    public void What_was_saved_before_a_restart_is_there_after_it() {
        using var session = EditorSession.Start();

        session.Scene.Create("Survivor", LocalTransform.Identity);
        session.Run("file.save");

        session.Restart();
        session.ExpandAll(session.Hierarchy);

        Assert.Contains("Survivor", EditorSession.Labels(session.Hierarchy));
    }

    /// <summary>
    ///     ⚠ Through the pointer, which is the point: this is a claim about the <i>menu</i>, and a
    ///     test that called the command would pass on an editor whose File menu was empty.
    /// </summary>
    [Fact]
    public void A_command_can_be_reached_by_opening_a_menu_and_clicking_a_line() {
        using var session = EditorSession.Start();

        session.Open("hierarchy");
        session.Menu("Entity", "Create Empty");

        Assert.Contains("Entity", EditorSession.Labels(session.Hierarchy));
    }

    [Fact]
    public void A_line_inside_a_submenu_is_reached_by_walking_to_it() {
        using var session = EditorSession.Start();

        session.Open("hierarchy");
        session.Menu("Entity", "3D Object", "Cube");

        Assert.Contains("Cube", EditorSession.Labels(session.Hierarchy));
    }

    [Fact]
    public void A_menu_that_is_not_on_the_bar_says_what_is() {
        using var session = EditorSession.Start();

        var failure = Assert.Throws<EditorSessionException>(() => session.Menu("Widgets", "Anything"));

        Assert.Contains("no 'Widgets' menu", failure.Message, StringComparison.Ordinal);
        Assert.Contains("File", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The trail is what makes a scenario's eighth-verb failure legible, and it is worthless if
    ///     it is not actually attached to the exception.
    /// </summary>
    [Fact]
    public void A_failure_carries_the_steps_that_led_to_it() {
        using var session = EditorSession.Start();

        session.Step("open the outliner").Open("hierarchy");
        session.Step("look for something that is not there");

        var failure = Assert.Throws<EditorSessionException>(() => session.Run("nothing.at-all"));

        Assert.Contains("open the outliner", failure.Message, StringComparison.Ordinal);
        Assert.Contains("look for something that is not there", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Interface:", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A registered-but-disabled command is a different mistake from an unregistered one, and the
    ///     message has to be able to tell somebody which they made.
    /// </summary>
    [Fact]
    public void A_disabled_command_is_refused_rather_than_quietly_doing_nothing() {
        using var session = EditorSession.Start();

        // Nothing has been edited, so there is nothing to save and the command says so itself.
        Assert.False(session.CanRun("file.save"));

        var failure = Assert.Throws<EditorSessionException>(() => session.Run("file.save"));

        Assert.Contains("registered but not enabled", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The dialog is drawn rather than native precisely so that this is possible — doc 20's A2
    ///     — and answering it is what save-on-close is made of.
    /// </summary>
    [Fact]
    public void A_dialog_is_answered_by_pressing_one_of_its_buttons() {
        using var session = EditorSession.Start();

        session.Scene.Create("Dirty", LocalTransform.Identity);
        session.Frames(2);

        session.RequestClose();

        Assert.True(session.IsAsking);
        Assert.False(session.IsClosing);

        session.Answer("Cancel");

        Assert.False(session.IsAsking);
        Assert.False(session.IsClosing);
    }

    /// <summary>
    ///     ⚠ <b>The editor draws in the face it ships, and this is the assertion whose absence hid
    ///     the fact that it did not for four weeks.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Fonts.Install</c> tries the embedded Open Sans and then, if it is not there, borrows
    ///         whatever the machine has — Arial on macOS, Segoe UI on Windows, DejaVu Sans on Linux —
    ///         and <b>returns true either way</b>. So when the library split left the <c>.ttf</c>
    ///         embedded in <c>Vixen.Editor.Host</c> while the code reading it stayed in
    ///         <c>Vixen.Editor.App</c>, the lookup answered null and nothing anywhere said so.
    ///     </para>
    ///     <para>
    ///         What that costs is not cosmetic. Three platforms measuring three different faces are
    ///         three platforms that wrap, lay out and hit-test differently, so a suite driving
    ///         synthetic clicks passes on two of them and fails on the third for reasons that read as
    ///         a Windows filesystem bug. Asserting the family — rather than merely that <i>some</i>
    ///         face was found — is what makes the fallback visible when it happens.
    ///     </para>
    ///     <para>
    ///         The semibold is asserted too, because it is registered on the same path: without the
    ///         resource the fallback registers one face under the file's own name, and every bold
    ///         label in the editor silently resolves to the regular.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_editor_draws_in_the_face_it_ships_rather_than_one_off_the_machine() {
        using var session = EditorSession.Start();

        var fonts = session.Document.Fonts;

        Assert.Equal("OpenSans-Regular", fonts.Default?.Name);

        // Through the family the editor's own sheet names, which is the lookup that actually happens.
        Assert.Equal("OpenSans-Regular", fonts.Resolve("OpenSans")?.Name);
        Assert.Equal("OpenSans-SemiBold", fonts.Resolve("OpenSans", 600)?.Name);
    }

    /// <summary>
    ///     ⚠ <b>A row past the bottom of the tree is reached by scrolling to it, not by pressing the
    ///     place it would be if the tree were taller.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The trees virtualise, so a row just off the bottom is realised and has honest bounds —
    ///         and is clipped. A press at its centre reaches the panel behind the scroller, the
    ///         gesture never starts, and nothing throws: the failure arrives several steps later as a
    ///         rename that did not commit or a drag no field ever saw.
    ///     </para>
    ///     <para>
    ///         Seven rows of 24 pixels in 156 of viewport is the case that found it, and it found it
    ///         only after the editor started measuring in its own face — which is why this asserts
    ///         about an overflowing tree rather than about a particular project.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_below_the_fold_is_scrolled_to_before_it_is_clicked() {
        using var session = EditorSession.Start();

        session.Open("project");

        // Enough files that the browser cannot show them all, whatever the face measures.
        var folder = Path.Combine(session.ProjectRoot, "Assets", "Deep");

        Directory.CreateDirectory(folder);

        for (var index = 0; index < 40; index++) {
            File.WriteAllText(Path.Combine(folder, $"file{index:D2}.txt"), "content");
        }

        session.Run("assets.refresh");
        session.ExpandAll(session.Assets);

        var last = session.Row(session.Assets, "file39.txt");
        var viewport = session.Assets.Scroller.Bounds;

        // ⚠ The instrument first: without the scroll this row is below the fold, so a version of
        // `Row` that did nothing would still satisfy the click below by accident on a short list.
        Assert.True(
            last.Bounds.Bottom <= viewport.Bottom && last.Bounds.Top >= viewport.Top,
            $"the row is at {last.Bounds} and the viewport is {viewport}"
        );

        session.ClickRow(session.Assets, "file39.txt");

        Assert.True(session.Project.Assets.TryGetByPath("Assets/Deep/file39.txt", out var entry));
        Assert.Equal([entry.Guid], session.Project.Selection);
    }

    /// <summary>
    ///     ⚠ <b>Two editors at once is not a configuration this assembly may run in.</b> Issue #365:
    ///     every panel here is a live consumer of <c>Strings</c>, which is one process-wide
    ///     <c>Signal</c>, and the signal graph's edge lists are plain arrays with nothing
    ///     interlocked. Two classes standing editors up on two threads did
    ///     <c>--liveConsumerCount</c> on the same producer, the count went negative, and a detach
    ///     indexed <c>liveConsumers[-1]</c> — reported as a flake in
    ///     <c>MilestoneE3Tests.Every_registered_panel_survives_being_closed_and_reopened</c>, which
    ///     passed on its own because running alone is running with nobody to race.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asserted rather than left to the file.</b> <c>AssemblyInfo.cs</c> carries the
    ///     attribute and its reasoning, and an assembly attribute is exactly the kind of thing that
    ///     is deleted by somebody speeding a slow suite up — at which point nothing fails, for a
    ///     while, and then one unrelated test fails once on a loaded machine.
    /// </remarks>
    [Fact]
    public void This_assembly_does_not_run_its_classes_in_parallel() {
        var behaviour = typeof(HarnessTests).Assembly
            .GetCustomAttributes(typeof(CollectionBehaviorAttribute), inherit: false)
            .Cast<CollectionBehaviorAttribute>()
            .SingleOrDefault();

        Assert.True(
            behaviour is { DisableTestParallelization: true },
            "Vixen.Editor.App.Tests must declare [assembly: CollectionBehavior(DisableTestParallelization = "
            + "true)]. Two EditorSessions on two threads share Strings' process-wide signal, and its edge "
            + "list is not thread-safe. See AssemblyInfo.cs."
        );
    }
}
