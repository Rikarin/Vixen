// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Input;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>doc 11 § Input system's input debug panel, opened through the real shell.</summary>
/// <remarks>
///     <para>
///         <c>InputDebugViewTests</c> builds the view by hand and asserts what it draws; these go
///         through <see cref="EditorSession" /> and ask the workspace, because a test that constructs
///         the thing it is testing cannot notice that nobody else does — the finding that
///         <c>AgentDebuggerPanelTests</c> was written for and that this panel was written to avoid
///         repeating.
///     </para>
///     <para>
///         ⚠ <b><see cref="It_says_nothing_is_published_and_goes_live_when_something_is" /> is the
///         one that matters.</b> The shipping editor publishes no <see cref="InputService" />, so
///         every other assertion about this panel can be satisfied by a panel that is permanently
///         dark. Proving the sentence *changes* when a host publishes one is what separates "the
///         instrument is honest about not running" from "the instrument does not run".
///     </para>
/// </remarks>
public class InputDebugPanelTests {
    [Fact]
    public void The_input_debug_panel_opens() {
        using var session = EditorSession.Start();

        session.Open(AssetEditorsModule.InputDebugPanelId);
        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.InputDebugPanelId);
    }

    /// <summary>Registering a panel registers its verb, and the View menu is built from those.</summary>
    [Fact]
    public void The_input_debug_panel_has_a_verb_that_opens_it() {
        using var session = EditorSession.Start();

        var id = EditorShell.PanelCommand(AssetEditorsModule.InputDebugPanelId);
        var command = session.Shell.Commands[id];

        Assert.NotNull(command);
        Assert.False(command.IsUnavailable, $"'{id}' is declared-and-disabled.");
        Assert.True(session.Shell.Commands.Execute(id));

        session.Frames(2);

        Assert.Contains(session.Panels, panel => panel.Id == AssetEditorsModule.InputDebugPanelId);
    }

    /// <summary>⚠ The honest-instrument check, both ways.</summary>
    [Fact]
    public void It_says_nothing_is_published_and_goes_live_when_something_is() {
        using var session = EditorSession.Start();

        var view = session.Control<InputDebugView>(AssetEditorsModule.InputDebugPanelId);

        session.Frames(2);

        // The editor routes platform events into its own interface document and never into an
        // InputDeviceSet, so this is what the shipping editor shows — and it says so.
        Assert.Equal(InputDebugView.Unpublished, view.SourceLine);
        Assert.Empty(view.Devices.Children);

        var service = new InputService();

        session.Plugins.Services.Add(service);
        session.Frames(2);

        // ⚠ Resolved again on the frame after the service arrived, not captured when the panel was
        // built. A host publishes late — a play session is the obvious case — and a panel that asked
        // once would stay dark with nothing to say why.
        Assert.Contains("InputService", view.SourceLine, StringComparison.Ordinal);
        Assert.NotEmpty(view.Devices.Children);

        // ⚠ And it keeps reading it, which is a separate claim from having found it. Nothing here
        // calls `Refresh` — a key goes down on the device set and the next editor frame is what has
        // to notice, or the panel is a photograph of the moment the service arrived.
        Assert.Empty(view.Actuated.Children);

        service.Devices.SubmitKey(InputKey.A, true);
        session.Frames(2);

        Assert.NotEmpty(view.Actuated.Children);
    }
}
