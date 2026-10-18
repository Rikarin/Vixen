// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>
///     Doc 50 § P5 (#1291): a change the surface already applied, put on the pipeline with
///     <see cref="EditProperty.Record" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The first test is the "silently discarded drag" doc 36 § Part 6 measured.</b> A surface
///     that writes live and then calls <see cref="EditProperty.WriteEach" /> with the values it already
///     wrote records nothing: each value equals what the object holds now, so it is skipped. That is
///     asserted here as the baseline the new entry point exists against, and <c>Record</c> written as
///     that call — the sabotage this was checked with — turns the second half red.
/// </remarks>
public class EditRecordTests {
    static EditProperty Size(TestDocument document, params Widget[] widgets) =>
        new EditTarget(widgets, new TestProvider(), document).Find("Size")!;

    [Fact]
    public void A_change_already_applied_is_one_entry_that_undoes_every_object_to_its_own_value() {
        var document = new TestDocument(ModelFixture.Project());
        Widget[] widgets = [new() { Size = 1f }, new() { Size = 2f }, new() { Size = 3f }];
        var property = Size(document, widgets);

        object?[] before = [1f, 2f, 3f];

        // The surface applies the change itself, live — a drag, a script, an animation committing.
        foreach (var widget in widgets) {
            widget.Size += 10f;
        }

        object?[] after = [11f, 12f, 13f];

        // ⚠ The old shape: what the pipeline offered before, and what it recorded.
        Assert.False(property.WriteEach(after));
        Assert.Equal(0, document.Stack.Depth.Value);

        // The new one: one entry, named for what happened and how many it happened to.
        Assert.True(property.Record(before, after, "Move"));
        Assert.Equal(1, document.Stack.Depth.Value);

        var entry = Assert.Single(document.Stack.History);

        Assert.Equal("Move (3)", entry.Name);
        Assert.IsType<RecordedValuesCommand>(entry);

        // Each object back to its own value, not to a shared one.
        document.Stack.Undo();
        Assert.Equal([1f, 2f, 3f], widgets.Select(static widget => widget.Size));

        document.Stack.Redo();
        Assert.Equal([11f, 12f, 13f], widgets.Select(static widget => widget.Size));
    }

    [Fact]
    public void A_change_that_changed_nothing_records_nothing() {
        var document = new TestDocument(ModelFixture.Project());
        var property = Size(document, new Widget { Size = 4f });

        Assert.False(property.Record([4f], [4f], "Move"));
        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void Before_and_after_must_name_every_object() {
        var document = new TestDocument(ModelFixture.Project());
        var property = Size(document, new Widget(), new Widget());

        Assert.Throws<ArgumentException>(() => property.Record([1f], [2f], "Move"));
    }

    [Fact]
    public void A_recorded_change_does_not_merge_into_the_edit_before_it() {
        var document = new TestDocument(ModelFixture.Project());
        var widget = new Widget { Size = 1f };
        var property = Size(document, widget);

        widget.Size = 2f;
        property.Record([1f], [2f], "Move");

        widget.Size = 3f;
        property.Record([2f], [3f], "Move");

        // Two drags are two Ctrl+Zs, whatever the member says about coalescing typed edits.
        Assert.Equal(2, document.Stack.Depth.Value);
    }
}
