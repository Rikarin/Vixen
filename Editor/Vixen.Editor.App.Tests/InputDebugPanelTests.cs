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
///         ⚠ <b><see cref="It_reads_the_service_the_editor_publishes_and_keeps_reading_it" /> is the
///         one that matters.</b> Until #470 the shipping editor published no
///         <see cref="InputService" /> and this panel was dark by construction; it publishes the one a
///         play session's focused pane feeds now, so the panel is live from the first frame. The
///         unpublished sentence is still the view's to say, and <c>InputDebugViewTests</c> holds it.
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

    /// <summary>⚠ The panel reads the service the shipping editor publishes, and keeps reading it.</summary>
    [Fact]
    public void It_reads_the_service_the_editor_publishes_and_keeps_reading_it() {
        using var session = EditorSession.Start();

        var view = session.Control<InputDebugView>(AssetEditorsModule.InputDebugPanelId);

        session.Frames(2);

        // The one a play session's focused pane feeds (#470) — the editor's, not one put there by
        // this test, which is what the panel was dark for want of.
        Assert.True(session.Plugins.Services.TryGet<InputService>(out var service));
        Assert.NotNull(service);
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
