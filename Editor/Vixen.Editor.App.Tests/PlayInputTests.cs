// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A play session's keyboard, gated on a scene pane having the focus (#470).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The decision is the gate, so the gate is what is asserted, both ways.</b> A session
///         that read every key the editor saw would move the player while somebody typed a name into
///         the inspector; one that read none would be a Play button with no controls. Each test goes
///         from one state to the other inside one run, because "nothing is down" is also what an
///         unwired editor reports.
///     </para>
///     <para>
///         Through the real document's focus and key routing — <c>UiTest.KeyDown</c> delivers to
///         whatever the document has focused, exactly as the host's pump does — and read off the
///         service a game's <c>[GameSystem]</c> is handed, through an action bound the way a game
///         binds one.
///     </para>
/// </remarks>
public class PlayInputTests {
    const string Bindings = """
        name: Probe
        maps:
          - name: Player
            actions:
              - name: Forward
                bindings:
                  - path: <Keyboard>/w
        """;

    /// <summary>W typed into the inspector moves nothing; W in the focused pane does, and Escape lets go.</summary>
    [Fact]
    public void Typing_in_the_inspector_moves_nothing_and_the_focused_pane_drives_the_game() {
        using var session = EditorSession.Start();

        session.Run("play.play");
        session.Frames(2);

        var play = session.Editor.PlayMode.Session;

        Assert.NotNull(play);
        Assert.Contains("input", play.Running);
        Assert.True(play.TryGet<InputService>(out var input));
        Assert.NotNull(input);

        var actions = input.Add(InputActions.Load(Bindings, "Probe"));

        actions.Enable();

        var forward = actions.FindAction("Player/Forward");

        // The inspector has the focus: a key typed there is the editor's, and the game hears nothing.
        FocusIn(session, "inspector");
        session.Ui.KeyDown(InputKey.W);
        session.Frames(2);

        Assert.False(input.Devices.Keyboard.IsDown(InputKey.W));
        Assert.False(forward.IsPressed);

        session.Ui.KeyUp(InputKey.W);
        session.Frames(1);

        // The pane has it: the same key reaches the session's devices and the action the game reads.
        var pane = session.Viewports[0];

        Assert.True(session.Document.Focus(pane.Control));
        session.Settle();

        session.Ui.KeyDown(InputKey.W);
        session.Frames(2);

        Assert.True(input.Devices.Keyboard.IsDown(InputKey.W));
        Assert.True(forward.IsPressed);

        // ⚠ Escape releases the keyboard, and the key still held is let go of with it — the release
        // of W now arrives at an editor that is no longer listening for the game.
        session.Ui.PressKey(InputKey.Escape);
        session.Frames(2);

        Assert.False(input.Devices.Keyboard.IsDown(InputKey.W));
        Assert.False(forward.IsPressed);

        session.Ui.KeyUp(InputKey.W);
        session.Ui.KeyDown(InputKey.W);
        session.Frames(2);

        Assert.False(input.Devices.Keyboard.IsDown(InputKey.W));

        session.Ui.KeyUp(InputKey.W);
        session.Frames(1);
    }

    /// <summary>Focus leaving the pane releases what was held, as a window losing focus does.</summary>
    [Fact]
    public void Moving_the_focus_off_the_pane_releases_a_held_key() {
        using var session = EditorSession.Start();

        session.Run("play.play");
        session.Frames(2);

        Assert.True(session.Editor.PlayMode.Session!.TryGet<InputService>(out var input));

        Assert.True(session.Document.Focus(session.Viewports[0].Control));
        session.Settle();

        session.Ui.KeyDown(InputKey.D);
        session.Frames(1);

        Assert.True(input!.Devices.Keyboard.IsDown(InputKey.D));

        FocusIn(session, "inspector");
        session.Frames(1);

        Assert.False(input.Devices.Keyboard.IsDown(InputKey.D));
    }

    /// <summary>The service a session reads is the one the input debug panel reads, across sessions.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The panel was dark by construction until this</b>: the editor published no
    ///         <see cref="InputService" />. <c>InputDebugPanelTests</c> shows the panel reading the
    ///         published one; this shows that one is what a pane feeds and a session is handed, so a
    ///         key held in the pane is a row in the panel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not asserted through the open panel</b>, because the panel opens as a tab over the
    ///         scene view and a hidden pane loses the focus — which the gate then correctly reads as
    ///         nobody playing.
    ///     </para>
    ///     <para>
    ///         And what a session registered leaves with it: a second Play must not update the first
    ///         session's action assets, held by systems that no longer exist.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_published_service_is_the_one_every_session_reads() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Services.TryGet<InputService>(out var published));

        session.Run("play.play");
        session.Frames(2);

        Assert.True(session.Editor.PlayMode.Session!.TryGet<InputService>(out var first));
        Assert.Same(published, first);

        first!.Add(InputActions.Load(Bindings, "Probe"));

        session.Run("play.stop");
        session.Frames(2);

        Assert.Empty(published!.Assets);

        session.Run("play.play");
        session.Frames(2);

        Assert.True(session.Editor.PlayMode.Session!.TryGet<InputService>(out var second));
        Assert.Same(published, second);
        Assert.Empty(session.Editor.PlayMode.Refused);
    }

    /// <summary>Puts the focus on something inside a tab.</summary>
    static void FocusIn(EditorSession session, string tab) {
        var target = Descendants(session.Panel(tab)).FirstOrDefault(element => element.Focusable)
            ?? throw session.Fail($"Nothing in the '{tab}' tab takes the focus.");

        Assert.True(session.Document.Focus(target));
        session.Settle();
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
