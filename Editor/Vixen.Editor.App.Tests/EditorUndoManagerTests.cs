// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What a control in the editor finds when it asks for somewhere to record an edit.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first assertion is that there is anything at all.</b> <c>IUndoManager</c>,
///         <c>UiElement.FindUndoManager</c> and <c>CommandStack : IUndoManager</c> all landed in
///         earlier batches, and nothing in the editor ever set a manager — so the walk ran the full
///         height of the tree on every keystroke and answered <see langword="null" />, and every text
///         field in the editor recorded its edits nowhere. That is the shape this repository calls a
///         finished thing nothing calls, sitting inside the issue that built it.
///     </para>
///     <para>
///         ⚠ <b>The second is that it follows the tab.</b> Four sessions read the install as "set
///         <c>UiDocument.UndoManager</c> to the active document's stack" and refused it as a feature
///         rather than a line, because the active document changes. It is a line once the thing in
///         the slot resolves the document per ask instead of holding one, which is exactly what
///         <c>UndoCommands</c> already says about its own lookup.
///     </para>
/// </remarks>
public class EditorUndoManagerTests {
    /// <summary>A document with nothing in it, to be the second tab.</summary>
    /// <remarks>
    ///     A real second <c>SceneDocument</c> would drag a world and a writer in and prove nothing
    ///     extra: what is under test is which <c>Stack</c> the manager reaches, and every
    ///     <c>EditorDocument</c> has one.
    /// </remarks>
    sealed class BlankDocument(EditorProject project, string title) : EditorDocument(project, AssetId.Empty, title) {
        protected override void SaveCore() { }
    }

    [Fact]
    public void A_control_in_the_editor_finds_somewhere_to_record_an_edit() {
        using var fixture = EditorSession.Start();

        Assert.NotNull(fixture.Shell.Document.Root.FindUndoManager());
    }

    [Fact]
    public void What_a_control_records_lands_on_the_active_documents_history() {
        using var fixture = EditorSession.Start();

        var active = fixture.Project.ActiveDocument.Peek();
        Assert.NotNull(active);

        var before = active.Stack.Depth.Value;
        var manager = fixture.Shell.Document.Root.FindUndoManager();
        Assert.NotNull(manager);

        var applied = 1;
        manager.Register("Typing", () => applied--, () => applied++);

        // ⚠ One history, which is the whole reason `CommandStack` implements the interface rather
        // than an adapter sitting beside it: the edit a control made is now on the same stack, in
        // the same order, as the edits the editor's own commands made.
        Assert.Equal(before + 1, active.Stack.Depth.Value);
        Assert.True(manager.CanUndo);

        Assert.True(manager.Undo());
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Switching_the_active_document_switches_which_history_a_control_records_on() {
        using var fixture = EditorSession.Start();

        var first = fixture.Project.ActiveDocument.Peek();
        Assert.NotNull(first);

        var second = new BlankDocument(fixture.Project, "Second");
        fixture.Project.Activate(second);

        var manager = fixture.Shell.Document.Root.FindUndoManager();
        Assert.NotNull(manager);

        var firstDepth = first.Stack.Depth.Value;

        manager.Register("Typing", static () => { }, static () => { });

        // The edit went to the tab the user is on, and the one they left is untouched — the property
        // an install that captured a stack at start-up cannot have.
        Assert.Equal(1, second.Stack.Depth.Value);
        Assert.Equal(firstDepth, first.Stack.Depth.Value);

        fixture.Project.Activate(first);
        Assert.Equal(firstDepth, first.Stack.Depth.Value);
    }

    /// <summary>With no document open at all, an edit still has somewhere to go.</summary>
    /// <remarks>
    ///     ⚠ Not <see langword="null" />. A field edited from a project-settings or asset panel is
    ///     exactly the case with no active document, and answering nothing there would leave it with
    ///     no ⌘Z — the state this whole install exists to end. <c>EditorProject.GlobalStack</c> is
    ///     already where those panels' own commands go.
    /// </remarks>
    [Fact]
    public void With_nothing_active_an_edit_records_on_the_projects_global_stack() {
        using var fixture = EditorSession.Start();

        fixture.Project.Activate(null);

        var manager = fixture.Shell.Document.Root.FindUndoManager();
        Assert.NotNull(manager);

        var before = fixture.Project.GlobalStack.Depth.Value;

        manager.Register("Typing", static () => { }, static () => { });

        Assert.Equal(before + 1, fixture.Project.GlobalStack.Depth.Value);
    }
}
