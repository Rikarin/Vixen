// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The one modal every desktop application has, and the loop it could have been.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about the <i>second</i> close request, not the first.</b> A
///         prompt is answered frames later, so the only way to close after an answer is to ask
///         again — and asking again re-enters the same handler against a document that "Don't Save"
///         did not clean. The first version of this code had no latch and the prompt reopened for
///         ever; the application could not be quit.
///     </para>
///     <para>
///         The dialogs are pumped by hand rather than by a clock, for <c>DialogTests</c>' reason:
///         the answer is completed from the pump and a test that awaited without one would hang
///         rather than fail.
///     </para>
/// </remarks>
public class DocumentClosePromptTests : IDisposable {
    readonly UiDocument document = new(800f, 600f);
    readonly DialogService dialogs;

    int closed;
    bool? retried;
    readonly List<DocumentCloseAnswer> answers = [];

    /// <summary>What a host's quit does, and the reason every test here uses it.</summary>
    /// <remarks>
    ///     ⚠ <b>A callback that only counted proved nothing.</b> The first version of this suite
    ///     passed <c>() =&gt; closed++</c> — and a sabotage that deleted the latch stayed green,
    ///     because nothing in the test ever made the second close request the latch exists for.
    ///     <c>UiApplication.Quit</c> calls <c>RequestClose</c>, so this does, and
    ///     <see cref="retried" /> is whether that second request was allowed through. A prompt that
    ///     asks again there is an application the user cannot quit.
    /// </remarks>
    void Close() {
        closed++;
        retried = document.RequestClose();
    }

    public DocumentClosePromptTests() {
        ControlTheme.Install(document);
        dialogs = new DialogService(document);
    }

    public void Dispose() {
        dialogs.Dispose();
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A document that records whether it was asked to write, and may refuse.</summary>
    sealed class Note(bool writes = true) : EditableDocument("Untitled") {
        public bool Writes { get; set; } = writes;

        public int Saves { get; private set; }

        protected override bool OnSave() {
            Saves++;

            return Writes;
        }

        protected override bool OnRevert() => true;
    }

    [Fact]
    public void A_clean_document_is_not_asked_about_and_does_not_stop_the_close() {
        var (_, prompt) = Installed();

        using (prompt) {
            Assert.True(document.RequestClose());

            dialogs.Pump();
            Assert.False(dialogs.IsOpen);
        }
    }

    /// <summary>
    ///     ⚠ The refusal and the question are one step: cancelling without asking would be an
    ///     application that silently refuses to quit, and asking without cancelling would be one
    ///     that puts a dialog on screen as it exits.
    /// </summary>
    [Fact]
    public void A_dirty_document_refuses_the_request_and_asks() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();

            Assert.False(document.RequestClose());
            Assert.Equal(0, closed);

            dialogs.Pump();

            Assert.True(dialogs.IsOpen);
            Assert.Equal(ControlStrings.DocumentSavePrompt.Text, dialogs.Current?.Title);
        }
    }

    [Fact]
    public void Saving_writes_the_document_and_then_closes() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();
            document.RequestClose();
            dialogs.Pump();

            Press(ControlStrings.DocumentSave.Text);
            dialogs.Pump();

            Assert.Equal(1, note.Saves);
            Assert.False(note.IsDirty.Value);
            Assert.Equal(1, closed);
            Assert.True(retried);
            Assert.Equal([DocumentCloseAnswer.Saved], answers);
        }
    }

    /// <summary>
    ///     ⚠ <b>The latch, asserted as the host's second request being <i>allowed</i>.</b> Discarding
    ///     leaves the document dirty on purpose — marking it clean would be a lie told to every other
    ///     reader of the same signal — so the quit this triggers walks back into the handler with the
    ///     same dirty document. Without the latch that request is refused, which is an application
    ///     the user cannot get out of, and <see cref="retried" /> is the half that says so. The
    ///     dialog counts are the other half: nothing new was queued behind it.
    /// </summary>
    [Fact]
    public void Discarding_closes_without_writing_and_does_not_ask_again() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();
            document.RequestClose();
            dialogs.Pump();

            Press(ControlStrings.DocumentDiscard.Text);
            dialogs.Pump();

            Assert.Equal(0, note.Saves);
            Assert.True(note.IsDirty.Value);
            Assert.Equal(1, closed);
            Assert.True(retried);
            Assert.Equal([DocumentCloseAnswer.Discarded], answers);

            // The second ask never happened: nothing is queued and nothing is on screen.
            Assert.Equal(0, dialogs.Pending);
            Assert.False(dialogs.IsOpen);
        }
    }

    [Fact]
    public void Cancelling_leaves_the_document_alone_and_the_application_open() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();
            document.RequestClose();
            dialogs.Pump();

            Press(ControlStrings.DialogCancel.Text);
            dialogs.Pump();

            Assert.Equal(0, note.Saves);
            Assert.True(note.IsDirty.Value);
            Assert.Equal(0, closed);
            Assert.Equal([DocumentCloseAnswer.Cancel], answers);
        }
    }

    /// <summary>
    ///     ⚠ <b>A save that refused does not close.</b> <c>IEditableDocument.Save</c> returns a bool
    ///     for exactly this case — a full disk, a read-only file — and treating the button press
    ///     rather than the write as the answer is how work is lost silently.
    /// </summary>
    [Fact]
    public void A_save_that_could_not_write_leaves_the_application_open() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.Writes = false;
            note.MarkDirty();

            document.RequestClose();
            dialogs.Pump();

            Press(ControlStrings.DocumentSave.Text);
            dialogs.Pump();

            Assert.Equal(1, note.Saves);
            Assert.True(note.IsDirty.Value);
            Assert.Equal(0, closed);
            Assert.Equal([DocumentCloseAnswer.SaveFailed], answers);
        }
    }

    /// <summary>
    ///     ⚠ <b>Backing out of a question about losing work is "no".</b> Escape and the header's
    ///     close button answer the dialog with its dismissal, and a dismissal that fell through to
    ///     the default arm of a switch would close the application on the keystroke users press to
    ///     get out of things.
    /// </summary>
    [Fact]
    public void Dismissing_the_prompt_is_a_cancel() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();
            document.RequestClose();
            dialogs.Pump();

            dialogs.Current!.Close(CloseReason.Cancelled);
            dialogs.Pump();

            Assert.Equal(0, closed);
            Assert.Equal([DocumentCloseAnswer.Cancel], answers);
        }
    }

    /// <summary>
    ///     ⚠ <b>Asking twice while the first question is on screen stacks nothing.</b> ⌘Q pressed
    ///     twice is ordinary, and a second prompt queued behind the first would be answered by a
    ///     user who has already decided.
    /// </summary>
    [Fact]
    public void A_second_request_while_the_prompt_is_up_is_refused_rather_than_queued() {
        var (note, prompt) = Installed();

        using (prompt) {
            note.MarkDirty();
            document.RequestClose();
            dialogs.Pump();

            Assert.False(document.RequestClose());
            Assert.Equal(0, dialogs.Pending);
        }
    }

    /// <summary>
    ///     The nearest document wins, because the request is raised on the focused element — which
    ///     is what makes this right in a window holding two panels and two documents.
    /// </summary>
    [Fact]
    public void The_document_asked_about_is_the_one_under_the_focus() {
        var left = new Note();
        var right = new Note();

        var leftPanel = document.Root.Add<Panel>();
        var rightPanel = document.Root.Add<Panel>();

        leftPanel.HostedDocument = left;
        rightPanel.HostedDocument = right;

        var field = rightPanel.Add<TextBox>();
        field.Focusable = true;

        using var prompt = DocumentClosePrompt.Install(
            document.Root,
            dialogs,
            Close,
            answers.Add
        );

        document.Update();
        document.Focus(field);

        right.MarkDirty();

        Assert.False(document.RequestClose());

        dialogs.Pump();
        Assert.Equal(right.Name.Value, DialogMessage());

        Press(ControlStrings.DocumentSave.Text);
        dialogs.Pump();

        Assert.Equal(1, right.Saves);
        Assert.Equal(0, left.Saves);
    }

    /// <summary>One panel's document is shut while the other stays open and the head is never asked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The case the framework could not express, and the prompt could already serve.</b>
    ///         <see cref="UiDocument.RequestClose" /> starts at the focus and ends at
    ///         <see cref="UiDocument.CloseRequested" />, because its subject is the application —
    ///         so a tab shutting on its own had no way to ask, and the prompt's own remarks about
    ///         installing on a panel described something no caller could reach.
    ///         <see cref="UiElement.RequestClose" /> raises it from the element that holds the
    ///         document instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The focus is deliberately in the <i>other</i> panel.</b> That is what separates
    ///         "the element asked" from "the focus asked": a request that still started at the focus
    ///         would prompt about the wrong document and this test would name the wrong document in
    ///         its dialog.
    ///     </para>
    ///     <para>
    ///         ⚠ And the head's listener must not fire, because a tab closing is not the application
    ///         going away — a host given that event for both could not tell the two apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_element_can_ask_about_its_own_document_without_asking_the_application() {
        var mine = new Note();
        var neighbour = new Note();

        var panel = document.Root.Add<Panel>();
        var other = document.Root.Add<Panel>();

        panel.HostedDocument = mine;
        other.HostedDocument = neighbour;

        var field = other.Add<TextBox>();
        field.Focusable = true;

        var heads = 0;
        document.CloseRequested += _ => heads++;

        using var prompt = DocumentClosePrompt.Install(panel, dialogs, Close, answers.Add);

        document.Update();
        document.Focus(field);

        mine.MarkDirty();
        neighbour.MarkDirty();

        Assert.False(panel.RequestClose());

        // ⚠ Asserted here rather than at the end, where `Close`'s own document-wide retry would have
        // moved it. Nothing outside the element tree has been told a panel is closing.
        Assert.Equal(0, heads);

        dialogs.Pump();
        Assert.Equal(mine.Name.Value, DialogMessage());

        Press(ControlStrings.DocumentSave.Text);
        dialogs.Pump();

        Assert.Equal(1, mine.Saves);
        Assert.Equal(0, neighbour.Saves);
        Assert.Equal(1, closed);

        // One, from `Close` — which calls `document.RequestClose()` the way a host's quit does, and
        // that one is the application's question and does reach the head. Two would mean the
        // element's request had reached it as well.
        Assert.Equal(1, heads);
    }

    /// <summary>The reason carried is the document's, and the default says so without being written.</summary>
    /// <remarks>
    ///     A handler that treats a quit and a tab close alike is a handler that cannot offer "Save
    ///     All" for one and not the other, so the reason has to survive the raise.
    /// </remarks>
    [Fact]
    public void An_element_request_carries_the_document_reason() {
        var reasons = new List<UiCloseReason>();

        var panel = document.Root.Add<Panel>();
        panel.AddHandler<CloseRequestEvent>((_, args) => reasons.Add(args.Reason));

        document.Update();

        Assert.True(panel.RequestClose());
        Assert.True(panel.RequestClose(UiCloseReason.WindowClosed));

        Assert.Equal([UiCloseReason.DocumentClosed, UiCloseReason.WindowClosed], reasons);
    }

    (Note Note, IDisposable Prompt) Installed() {
        var note = new Note();
        document.HostedDocument = note;

        var prompt = DocumentClosePrompt.Install(document.Root, dialogs, Close, answers.Add);
        document.Update();

        return (note, prompt);
    }

    void Press(string label) {
        var dialog = dialogs.Current ?? throw new InvalidOperationException("nothing is being asked");

        var button = dialog.Footer.Children.OfType<Button>().FirstOrDefault(one => one.Label == label)
            ?? throw new InvalidOperationException($"the dialog has no '{label}' button");

        button.Activate();
    }

    string? DialogMessage() {
        var dialog = dialogs.Current ?? throw new InvalidOperationException("nothing is being asked");

        return Text(dialog.Body);
    }

    static string? Text(UiElement element) {
        if (element is TextBlock block) {
            return block.Text;
        }

        foreach (var child in element.Children) {
            if (Text(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
