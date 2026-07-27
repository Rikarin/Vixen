// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Dirty tracking, the signal-backed object model, and the two stacks meeting.</summary>
public sealed class EditorDocumentTests {
    [Fact]
    public void ADocumentIsCleanUntilItIsEditedAndCleanAgainOnceItIsSaved() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        Assert.False(document.IsDirty.Value);

        knob.Label.Set("edited");
        Assert.True(document.IsDirty.Value);

        document.Save();
        Assert.Equal(1, document.Saves);
        Assert.False(document.IsDirty.Value);
    }

    [Fact]
    public void UndoingBackToWhatWasSavedIsCleanAndRedoingPastItIsNot() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Label.Set("saved");
        document.Save();
        knob.Label.Set("after");

        Assert.True(document.IsDirty.Value);

        document.Stack.Undo();
        Assert.False(document.IsDirty.Value);

        document.Stack.Redo();
        Assert.True(document.IsDirty.Value);
    }

    /// <summary>
    ///     Undoing past the save point and then editing produces a stack of the same depth holding
    ///     different content. A dirty flag that only counted entries would call that clean.
    /// </summary>
    [Fact]
    public void EditingOnABranchThatDiscardedTheSavedStateStaysDirty() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Label.Set("first");
        knob.Label.Set("second");
        document.Save();

        document.Stack.Undo();
        knob.Label.Set("different");

        Assert.Equal(2, document.Stack.Depth.Value);
        Assert.True(document.IsDirty.Value);
    }

    [Fact]
    public void APropertyIsASignalSoTwoReadersOfItNeverDisagree() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        // What the inspector would bind to. Nothing subscribes; reading re-evaluates.
        var shown = new Vixen.Ui.Reactive.Computed<string>(() => $"Amount: {knob.Amount.Value}");

        Assert.Equal("Amount: 0", shown.Value);

        // What a gizmo drag would do.
        knob.Amount.Set(4f);
        Assert.Equal("Amount: 4", shown.Value);

        document.Stack.Undo();
        Assert.Equal("Amount: 0", shown.Value);
    }

    [Fact]
    public void PropertiesAreListedInTheOrderTheyWereDeclared() {
        var knob = new Knob(null);

        Assert.Equal(["Amount", "Label"], knob.Properties.Select(property => property.Name));
        Assert.True(knob.TryGetProperty("Label", out var label));
        Assert.Equal("none", label.BoxedValue);
    }

    [Fact]
    public void AnObjectWithNoDocumentStillWorksAndIsSimplyNotUndoable() {
        var knob = new Knob(null);

        knob.Amount.Set(2f);

        Assert.Equal(2f, knob.Amount.Value);
    }

    /// <summary>
    ///     The documented interaction between the two stacks: a global operation reaches into open
    ///     documents, and what it leaves behind in them is a discarded redo stack and a dirty flag no
    ///     amount of undoing inside the document clears.
    /// </summary>
    [Fact]
    public void AGlobalOperationMarksTheDocumentsItTouchedAndDiscardsTheirRedo() {
        var project = ModelFixture.Project();
        var document = new TestDocument(project, "Level1");
        var knob = new Knob(document);

        knob.Label.Set("local");
        document.Save();
        document.Stack.Undo();

        Assert.True(document.Stack.CanRedo.Value);
        Assert.True(document.IsDirty.Value);

        project.GlobalStack.Execute(
            new DelegateCommand(
                "Rename Asset",
                context => {
                    knob.Amount.Assign(9f);
                    context.Touch(document);
                },
                context => {
                    knob.Amount.Assign(0f);
                    context.Touch(document);
                }
            )
        );

        Assert.False(document.Stack.CanRedo.Value);
        Assert.True(document.IsDirty.Value);

        // And undoing it inside the document does not clear what came from outside.
        document.Save();
        Assert.False(document.IsDirty.Value);
    }

    [Fact]
    public void UndoingAGlobalOperationPutsTheValueBackAndLeavesTheDocumentMarked() {
        var project = ModelFixture.Project();
        var document = new TestDocument(project);
        var knob = new Knob(document);

        var rename = new DelegateCommand(
            "Rename Asset",
            context => {
                knob.Amount.Assign(9f);
                context.Touch(document);
            },
            context => {
                knob.Amount.Assign(0f);
                context.Touch(document);
            }
        );

        project.GlobalStack.Execute(rename);
        Assert.True(document.IsDirty.Value);
        Assert.Equal(9f, knob.Amount.Value);

        project.GlobalStack.Undo();
        Assert.Equal(0f, knob.Amount.Value);

        // The value is back, and the document is still marked: the editor cannot know whether what
        // was on disk was written before or after the rename, so it says so instead of guessing.
        Assert.True(document.IsDirty.Value);
    }

    [Fact]
    public void EditsInOneDocumentAreInvisibleToAnothersHistory() {
        var project = ModelFixture.Project();
        var first = new TestDocument(project, "First");
        var second = new TestDocument(project, "Second");

        new Knob(first).Label.Set("a");

        Assert.Equal(1, first.Stack.Depth.Value);
        Assert.Equal(0, second.Stack.Depth.Value);
        Assert.False(second.IsDirty.Value);
        Assert.Equal(0, project.GlobalStack.Depth.Value);
    }

    [Fact]
    public void TheProjectKnowsWhenAnyOpenDocumentDiffersFromDisk() {
        var project = ModelFixture.Project();
        var first = new TestDocument(project, "First");
        var second = new TestDocument(project, "Second");

        Assert.False(project.HasUnsavedChanges.Value);

        new Knob(second).Label.Set("a");
        Assert.True(project.HasUnsavedChanges.Value);

        Assert.Equal(1, project.SaveAll());
        Assert.False(project.HasUnsavedChanges.Value);
        Assert.Equal(0, first.Saves);
        Assert.Equal(1, second.Saves);
    }

    [Fact]
    public void OpeningADocumentPutsItInTheProjectAndGivesItFocus() {
        var project = ModelFixture.Project();
        var asset = AssetId.New();
        var document = new TestDocument(project, "Level1", asset);

        Assert.Equal([document], project.Documents);
        Assert.Same(document, project.ActiveDocument.Value);
        Assert.True(project.TryGetDocument(asset, out var found));
        Assert.Same(document, found);
    }

    [Fact]
    public void ClosingADocumentTakesItOutAndMovesFocusToWhatIsLeft() {
        var project = ModelFixture.Project();
        var first = new TestDocument(project, "First");
        var second = new TestDocument(project, "Second");

        Assert.True(second.Close());

        Assert.Equal([first], project.Documents);
        Assert.Same(first, project.ActiveDocument.Value);
        Assert.False(second.IsOpen);

        Assert.True(first.Close());
        Assert.Null(project.ActiveDocument.Value);
    }

    [Fact]
    public void ADocumentWithNoAssetIsNotFoundByTheEmptyGuid() {
        var project = ModelFixture.Project();
        _ = new TestDocument(project, "Untitled");

        Assert.False(project.TryGetDocument(AssetId.Empty, out _));
    }
}
