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

    /// <summary>Edit ▸ Undo takes back the active document's edit and leaves the main scene's alone.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The menu item is the half that did not follow the tab.</b> Both
    ///         <c>edit.undo</c> and <c>edit.redo</c> closed over <c>EditorApplication.scene</c> — the
    ///         one <c>SceneDocument</c> set in the constructor — so with an asset editor or an
    ///         additively-opened scene active, ⌘Z stepped back through a history the user was not
    ///         looking at. The method's own summary claimed "over whichever document is active" while
    ///         it did that, which is why this is asserted through the registry rather than through
    ///         the manager the tests above already exercise: <c>ActiveDocumentUndo</c> was right and
    ///         the command beside it was not, so the two disagreed about one verb.
    ///     </para>
    ///     <para>
    ///         The main scene's depth is read as well as the second document's, because "the right
    ///         one moved" and "the wrong one did not" are two failures and only the pair rules out
    ///         a command that undoes on both.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Undo_from_the_menu_takes_back_the_active_documents_edit() {
        using var fixture = EditorSession.Start();

        var main = fixture.Scene;
        var applied = 0;

        ((IUndoManager) main.Stack).Register("Main edit", static () => { }, static () => { });
        var mainDepth = main.Stack.Depth.Value;
        Assert.Equal(1, mainDepth);

        var second = new BlankDocument(fixture.Project, "Second");
        fixture.Project.Activate(second);

        ((IUndoManager) second.Stack).Register("Second edit", () => applied--, () => applied++);
        applied = 1;

        Assert.True(fixture.Shell.Commands.CanExecute("edit.undo"));
        Assert.True(fixture.Shell.Commands.Execute("edit.undo"));

        Assert.Equal(0, applied);
        Assert.Equal(0, second.Stack.Depth.Value);
        Assert.Equal(mainDepth, main.Stack.Depth.Value);
    }

    /// <summary>The item greys itself out from the stack it would actually undo.</summary>
    /// <remarks>
    ///     ⚠ <b>The enablement predicate had the same capture as the run, which is the worse half of
    ///     the two.</b> A menu item that runs on the wrong history at least does something the user
    ///     can see; one that reads <i>enabled</i> because a document they are not looking at has
    ///     something to take back offers an undo that then moves nothing they can see.
    /// </remarks>
    [Fact]
    public void Undo_is_greyed_when_the_active_document_has_nothing_to_take_back() {
        using var fixture = EditorSession.Start();

        ((IUndoManager) fixture.Scene.Stack).Register("Main edit", static () => { }, static () => { });
        Assert.True(fixture.Shell.Commands.CanExecute("edit.undo"));

        fixture.Project.Activate(new BlankDocument(fixture.Project, "Second"));

        Assert.False(fixture.Shell.Commands.CanExecute("edit.undo"));
        Assert.False(fixture.Shell.Commands.Execute("edit.undo"));
    }
}
