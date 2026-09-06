// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 13's UI reading, in the editor, actually being taken.</summary>
/// <remarks>
///     ⚠ <b>The subject is the wiring rather than the panel</b>, and the panel is not what was
///     missing. <c>UiDiagnostics</c>, <c>DiagnosticsPanel</c> and <c>UiApplication.Diagnostics</c>
///     all landed with tests of their own, and nothing in the tree assigned one — a reader with no
///     caller, which is this repository's commonest defect. So what is asserted here is the two ends
///     nobody had joined: that the editor registers the panel, and that the shell refreshes it on the
///     frame.
/// </remarks>
public class UiDiagnosticsPanelTests {
    [Fact]
    public void The_editor_registers_the_panel_and_the_shell_holds_it_while_it_is_open() {
        using var fixture = EditorSession.Start();

        // Nothing is refreshed until somebody asks for the panel: a shell that held one from the
        // start would be doing the work in every session that never opened it.
        Assert.Null(fixture.Shell.Diagnostics);

        fixture.Open(EditorApplication.UiDiagnosticsPanel);

        var panel = fixture.Shell.Diagnostics;
        Assert.NotNull(panel);

        // ⚠ And it is refreshed by the *shell*, on the frame, which is the half a panel cannot do
        // for itself: `Refresh` has to run before the layout pass it reports on, and the top of the
        // editor's frame is inside `EditorShell.Tick`. A panel that had only been created would have
        // no rows at all.
        Assert.True(panel.RowCount > 0);

        // ⚠ And the list's own count beside it, not instead of it. `DiagnosticsPanelTests` records
        // why: `RowCount` is the number the panel's last refresh wrote, so a defect in the pooling
        // moves it and hides itself, where `KeyValueList.Count` is what is on screen.
        Assert.True(panel.Rows.Count > 0);
    }

    [Fact]
    public void Closing_the_panel_stops_the_shell_refreshing_a_torn_out_element() {
        using var fixture = EditorSession.Start();

        fixture.Open(EditorApplication.UiDiagnosticsPanel);
        Assert.NotNull(fixture.Shell.Diagnostics);

        fixture.Close(EditorApplication.UiDiagnosticsPanel);

        // ⚠ Not tidiness. The shell's tick holds the only reference left, so without the clear it
        // would keep writing rows into a panel that is out of the tree, sixty times a second, for
        // the rest of the session — and nothing anywhere would draw the result.
        Assert.Null(fixture.Shell.Diagnostics);

        // The frames after the close are the assertion: they are what a leaked reference would have
        // been running through.
        fixture.Frames(3);
        Assert.Null(fixture.Shell.Diagnostics);
    }

    [Fact]
    public void The_reading_is_of_the_shells_own_document_and_moves_with_it() {
        using var fixture = EditorSession.Start();

        fixture.Open(EditorApplication.UiDiagnosticsPanel);

        var panel = fixture.Shell.Diagnostics!;

        // No `Subject`, so the panel reads the document it is in — which is the shell's, and the one
        // somebody debugging the editor's interface is asking about. The layout-node count is the
        // row that says so: an editor shell is thousands of nodes, and a panel reading a document of
        // its own would report its own handful.
        var nodes = Row(panel, "Layout nodes");

        Assert.NotNull(nodes);
        Assert.True(int.TryParse(nodes, out var count), $"'Layout nodes' read '{nodes}'.");
        Assert.True(count > 100, $"the shell's document has {count} layout nodes.");
    }

    static string? Row(DiagnosticsPanel panel, string key) {
        for (var i = 0; i < panel.Rows.Count; i++) {
            if (string.Equals(panel.Rows.Rows[i].Key, key, StringComparison.Ordinal)) {
                return panel.Rows.Rows[i].Value;
            }
        }

        return null;
    }
}
