// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>The do/undo/redo/merge invariants [11](../../docs/plan/11-editor.md) § Editor testing asks for.</summary>
public sealed class CommandStackTests {
    [Fact]
    public void ACommandRunsWhenItIsExecutedAndIsUndoneAndRedoneExactly() {
        var project = ModelFixture.Project();
        var document = new TestDocument(project);
        var log = new List<string>();

        document.Stack.Execute(Recording(log, "one"));

        Assert.Equal(["do one"], log);
        Assert.True(document.Stack.CanUndo.Value);
        Assert.False(document.Stack.CanRedo.Value);

        Assert.True(document.Stack.Undo());
        Assert.Equal(["do one", "undo one"], log);
        Assert.False(document.Stack.CanUndo.Value);
        Assert.True(document.Stack.CanRedo.Value);

        Assert.True(document.Stack.Redo());
        Assert.Equal(["do one", "undo one", "do one"], log);
    }

    [Fact]
    public void UndoAndRedoOnAnEmptyStackDoNothingAndSaySo() {
        var document = new TestDocument(ModelFixture.Project());

        Assert.False(document.Stack.Undo());
        Assert.False(document.Stack.Redo());
    }

    [Fact]
    public void TheMenuLabelsComeFromTheStackAndFollowIt() {
        var document = new TestDocument(ModelFixture.Project());
        var log = new List<string>();

        Assert.Null(document.Stack.UndoName.Value);

        document.Stack.Execute(Recording(log, "Move Entity"));
        Assert.Equal("Move Entity", document.Stack.UndoName.Value);
        Assert.Null(document.Stack.RedoName.Value);

        document.Stack.Undo();
        Assert.Null(document.Stack.UndoName.Value);
        Assert.Equal("Move Entity", document.Stack.RedoName.Value);
    }

    [Fact]
    public void ExecutingSomethingNewThrowsAwayWhatCouldHaveBeenRedone() {
        var document = new TestDocument(ModelFixture.Project());
        var log = new List<string>();

        document.Stack.Execute(Recording(log, "one"));
        document.Stack.Undo();
        document.Stack.Execute(Recording(log, "two"));

        Assert.False(document.Stack.CanRedo.Value);
        Assert.Equal(1, document.Stack.Depth.Value);
    }

    /// <summary>
    ///     The drag-scrub case the whole merge mechanism exists for, and the property that makes it
    ///     correct rather than merely tidy: three hundred moves are one entry, and undoing it lands
    ///     on the value from before the drag rather than on the one from a frame ago.
    /// </summary>
    [Fact]
    public void AThreeHundredStepDragIsOneUndoEntryThatGoesBackToWhereItStarted() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        for (var step = 1; step <= 300; step++) {
            knob.Amount.Set(step);
        }

        Assert.Equal(300f, knob.Amount.Value);
        Assert.Equal(1, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(0f, knob.Amount.Value);
    }

    [Fact]
    public void SealingEndsTheDragSoTheNextEditIsItsOwnStep() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Amount.Set(1f);
        knob.Amount.Set(2f);
        document.Stack.Seal();
        knob.Amount.Set(3f);

        Assert.Equal(2, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(2f, knob.Amount.Value);
    }

    [Fact]
    public void APropertyThatDoesNotCoalesceGetsAnEntryPerEdit() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Label.Set("a");
        knob.Label.Set("b");

        Assert.Equal(2, document.Stack.Depth.Value);
    }

    [Fact]
    public void NothingMergesIntoAnEntryTheUserHasSteppedPast() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Amount.Set(1f);
        document.Stack.Seal();
        knob.Amount.Set(2f);
        document.Stack.Undo();

        // The redo entry still exists at this point; merging into the entry below it would swallow
        // the step the user just took.
        knob.Amount.Set(5f);

        Assert.Equal(2, document.Stack.Depth.Value);
        Assert.False(document.Stack.CanRedo.Value);
    }

    [Fact]
    public void SettingAPropertyToWhatItAlreadyHoldsRecordsNothing() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        knob.Amount.Set(0f);

        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.False(document.Stack.IsDirty.Value);
    }

    [Fact]
    public void ATransactionIsOneEntryHoweverManyCommandsRanInsideIt() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);
        var other = new Knob(document);

        using (document.Stack.BeginTransaction("Paste")) {
            knob.Amount.Set(3f);
            other.Label.Set("pasted");
        }

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal("Paste", document.Stack.UndoName.Value);

        document.Stack.Undo();
        Assert.Equal(0f, knob.Amount.Value);
        Assert.Equal("none", other.Label.Value);
    }

    [Fact]
    public void ATransactionThatCollectedNothingRecordsNothing() {
        var document = new TestDocument(ModelFixture.Project());

        using (document.Stack.BeginTransaction("Delete Selection")) {
            // Nothing was selected.
        }

        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void CancellingATransactionRollsItBackAndRecordsNothing() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        using (var transaction = document.Stack.BeginTransaction("Drag")) {
            knob.Amount.Set(3f);
            knob.Label.Set("dragging");
            transaction.Cancel();

            Assert.Equal(0f, knob.Amount.Value);
            Assert.Equal("none", knob.Label.Value);
        }

        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void NestedTransactionsProduceOneEntry() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);

        using (document.Stack.BeginTransaction("Outer")) {
            knob.Amount.Set(1f);

            using (document.Stack.BeginTransaction("Inner")) {
                knob.Label.Set("inner");
            }

            Assert.Equal(0, document.Stack.Depth.Value);
        }

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal("Outer", document.Stack.UndoName.Value);
    }

    [Fact]
    public void UndoIsRefusedInsideATransactionBecauseTheEntryDoesNotExistYet() {
        var document = new TestDocument(ModelFixture.Project());

        using var transaction = document.Stack.BeginTransaction("Paste");

        Assert.Throws<InvalidOperationException>(() => document.Stack.Undo());
    }

    [Fact]
    public void TheOldestEntriesFallOffOnceTheStackIsFull() {
        var document = new TestDocument(ModelFixture.Project());
        var knob = new Knob(document);
        document.Stack.Capacity = 4;

        for (var step = 1; step <= 10; step++) {
            knob.Label.Set("v" + step);
        }

        Assert.Equal(4, document.Stack.Depth.Value);

        while (document.Stack.Undo()) {
            // Down to the bottom of what is left.
        }

        // Six edits were dropped, so the earliest value still reachable is the seventh's input.
        Assert.Equal("v6", knob.Label.Value);
    }

    static DelegateCommand Recording(List<string> log, string name) =>
        new(name, _ => log.Add("do " + name), _ => log.Add("undo " + name));
}
