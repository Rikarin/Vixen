// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>
///     <see cref="EditorDocument" /> as <c>Vixen.Ui</c>'s <see cref="IEditableDocument" />, on #656's
///     decision: dirty is the stack's, an edit from outside is a signal of its own, and a save that
///     could not write is reported rather than swallowed.
/// </summary>
public sealed class EditableDocumentPortTests {
    /// <summary>A global operation leaves the document needing a save without calling it dirty.</summary>
    /// <remarks>
    ///     ⚠ <b>The split the decision makes.</b> Before it, <see cref="EditorDocument.IsDirty" /> was
    ///     the stack's answer OR'd with this one, so <c>Vixen.Ui</c>'s <c>document.revert</c>, greyed
    ///     on <see cref="IEditableDocument.IsDirty" />, could not tell "you typed something" from
    ///     "a rename rewrote a reference in here".
    /// </remarks>
    [Fact]
    public void An_edit_from_outside_is_externally_modified_and_not_dirty_and_still_needs_a_save() {
        var project = ModelFixture.Project();
        var document = new TestDocument(project, "Level1");
        var knob = new Knob(document);

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

        Assert.False(document.IsDirty.Value);
        Assert.True(document.ExternallyModified.Value);
        Assert.True(document.HasUnsavedChanges.Value);

        // What saving writes still includes it, which is why the split costs nothing: Save All, the
        // project's close question and the interface's own signal all see it.
        Assert.True(project.HasUnsavedChanges.Value);
        Assert.True(((IEditableDocument)document).ExternallyModified.Value);
        Assert.Equal(1, project.SaveAll());
        Assert.False(document.ExternallyModified.Value);
        Assert.False(project.HasUnsavedChanges.Value);
    }

    /// <summary>Revert takes both answers back, which is what makes it the opposite of Save.</summary>
    [Fact]
    public void Revert_clears_dirty_and_the_edit_from_outside_together() {
        var project = ModelFixture.Project();
        var document = new ReloadingDocument(project);
        var knob = new Knob(document);

        knob.Label.Set("local");

        project.GlobalStack.Execute(
            new DelegateCommand("Rename Asset", context => context.Touch(document), context => context.Touch(document))
        );

        Assert.True(document.IsDirty.Value);
        Assert.True(document.ExternallyModified.Value);

        IEditableDocument editable = document;

        Assert.True(editable.Revert());
        Assert.Equal(1, document.Reloads);
        Assert.False(editable.IsDirty.Value);
        Assert.False(editable.ExternallyModified.Value);
    }

    /// <summary>The interface's name is the tab's title, live.</summary>
    [Fact]
    public void The_name_follows_the_title() {
        IEditableDocument document = new TestDocument(ModelFixture.Project(), "Level1");

        Assert.Equal("Level1", document.Name.Value);

        ((EditorDocument)document).SetTitle("Level2");

        Assert.Equal("Level2", document.Name.Value);
    }

    /// <summary>A save that wrote answers true and is clean.</summary>
    [Fact]
    public void Saving_through_the_interface_writes_and_is_clean() {
        var document = new TestDocument(ModelFixture.Project());
        new Knob(document).Label.Set("edited");

        Assert.True(((IEditableDocument)document).Save());
        Assert.Equal(1, document.Saves);
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>A save that could not write is reported, answers false, and leaves the edit dirty.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported, not swallowed</b> — the decision's words. <see cref="EditorDocument.Save" />
    ///     keeps throwing; the interface's <see langword="bool" /> is reached by catching a write
    ///     failure, handing it to <see cref="EditorProject.SaveFailed" />, and answering
    ///     <see langword="false" /> so <c>DocumentClosePrompt</c> refuses to close.
    /// </remarks>
    [Fact]
    public void A_save_that_could_not_write_is_reported_and_answers_false() {
        var project = ModelFixture.Project();
        var document = new FailingDocument(project);
        new Knob(document).Label.Set("edited");

        var reported = new List<(EditorDocument Document, Exception Failure)>();
        project.SaveFailed += (failed, failure) => reported.Add((failed, failure));

        Assert.False(((IEditableDocument)document).Save());

        var (which, why) = Assert.Single(reported);
        Assert.Same(document, which);
        Assert.IsType<IOException>(why);
        Assert.True(document.IsDirty.Value);
    }

    /// <summary>With nobody to report to, the failure is thrown rather than lost.</summary>
    [Fact]
    public void A_failed_save_nobody_hears_about_is_thrown() {
        var document = new FailingDocument(ModelFixture.Project());

        Assert.Throws<IOException>(() => ((IEditableDocument)document).Save());
    }

    /// <summary>A document whose asset is in the database says where it lives; one with none says null.</summary>
    [Fact]
    public void The_location_is_null_for_a_document_with_no_asset() {
        IEditableDocument document = new TestDocument(ModelFixture.Project(), "Untitled", AssetId.Empty);

        Assert.Null(document.Location.Value);
    }

    sealed class ReloadingDocument(EditorProject project) : EditorDocument(project, AssetId.Empty, "Reloads") {
        public int Reloads { get; private set; }

        public override bool CanReload => true;

        protected override void SaveCore() {
        }

        protected override bool ReloadCore() {
            Reloads++;
            return true;
        }
    }

    sealed class FailingDocument(EditorProject project) : EditorDocument(project, AssetId.Empty, "Full Disk") {
        protected override void SaveCore() => throw new IOException("The disk is full.");
    }
}
