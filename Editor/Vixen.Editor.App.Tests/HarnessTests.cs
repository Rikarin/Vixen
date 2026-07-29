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
}
